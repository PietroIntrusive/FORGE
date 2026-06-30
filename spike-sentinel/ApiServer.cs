using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeSentinel;

// Local control-plane HTTP server for the UI.
//
// Threat model: this endpoint can change Windows services and power state, so a
// CSRF from any web page the user happens to have open would be a real attack.
// Defenses, in layers:
//   1. Bind to loopback only (127.0.0.1) — never a wildcard prefix, so no admin /
//      netsh urlacl is needed and the socket isn't reachable off-box.
//   2. No "Access-Control-Allow-Origin: *". CORS headers are echoed ONLY for our
//      own origin, so a foreign tab's fetch can't read any response (incl. the
//      token) cross-origin.
//   3. Host + Origin allowlist on every request — reject DNS-rebinding / foreign
//      origins outright.
//   4. A per-process CSRF token: mutating routes (/api/restore) require it in a
//      Security-Token header. The token is only obtainable same-origin, so a
//      foreign page can't forge the call even if it guesses the URL.
//   5. The UI is served same-origin from this server with the token injected, so
//      the browser treats it as first-party.
static class ApiServer
{
    const int Port = 5172;
    static readonly string Origin = $"http://127.0.0.1:{Port}";

    // 256-bit random token, regenerated every process start. Never persisted.
    static readonly string CsrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    static readonly string[] AllowedHosts =
        { $"127.0.0.1:{Port}", $"localhost:{Port}" };

    // Lazy-initialised baseline cache. Shared across requests; benign race on first
    // set (worst case two requests both seed it from an equivalent snapshot).
    static Snapshot? _baseline;

    public static async Task Run()
    {
        _baseline = SnapshotEngine.LoadBaseline();
        // Turnkey single-click: the daemon may only ever run `serve` (the watcher's
        // `run` verb, which normally seeds the baseline, might never fire). Without a
        // baseline, restore-to-baseline has nothing to compare against. Seed one from
        // the current state. Read-only snapshot + encrypted write to the user profile —
        // needs no admin. Best-effort; status just reports "sem baseline" on failure.
        if (_baseline is null)
        {
            try
            {
                var seed = SnapshotEngine.Take();
                SnapshotEngine.SaveBaseline(seed);
                _baseline = seed;
                Console.WriteLine("Baseline inicial capturado (primeiro serve).");
            }
            catch { /* non-fatal */ }
        }
        bool isAdmin = Elevation.IsAdmin();

        using var listener = new HttpListener();
        // Loopback IP literal, not "+"/"*": binds without elevation on Win7+.
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

        Console.WriteLine($"Forge API → {Origin}/");
        Console.WriteLine($"Abra {Origin}/ no browser. Ctrl+C para parar.");
        if (!isAdmin)
            Console.WriteLine("Aviso: sem admin — botão Aplicar vai retornar 403. Rode como Administrador.");

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (HttpListenerException) { break; } // listener stopped

            // Fire-and-forget per request; the engines are independent per call.
            _ = Task.Run(() => Handle(ctx, isAdmin));
        }
    }

    static async Task Handle(HttpListenerContext ctx, bool isAdmin)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        try
        {
            // ---- Layer 3: Host / Origin gate -------------------------------
            if (!HostAllowed(req))
            {
                res.StatusCode = 421; // Misdirected Request
                Close(res, "{\"error\":\"bad host\"}");
                return;
            }

            var origin = req.Headers["Origin"];
            bool sameOrigin = origin is null || string.Equals(origin, Origin, StringComparison.OrdinalIgnoreCase);
            if (!sameOrigin)
            {
                // Foreign origin: refuse and send NO CORS headers, so the caller
                // can't read anything back.
                res.StatusCode = 403;
                Close(res, "{\"error\":\"forbidden origin\"}");
                return;
            }

            // ---- Layer 2: CORS only for our own origin ---------------------
            if (origin is not null)
            {
                res.Headers["Access-Control-Allow-Origin"] = Origin;
                res.Headers["Vary"] = "Origin";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Security-Token";
            }
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.OutputStream.Close(); return; }

            var path = req.Url?.AbsolutePath ?? "/";

            if (req.HttpMethod == "GET" && path == "/")
                await ServeUi(res);
            else if (req.HttpMethod == "GET" && path == "/api/status")
                ServeStatus(res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/restore")
                ServeRestore(req, res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/apply")
                ServeApply(req, res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/quick")
                ServeQuick(req, res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/gamemode")
                ServeGameMode(req, res, isAdmin);
            else if (req.HttpMethod == "GET" && path == "/api/ram")
                ServeRam(res);
            else if (req.HttpMethod == "GET" && path == "/api/gpu")
                ServeGpu(res);
            else if (req.HttpMethod == "POST" && path == "/api/gpu/power")
                ServeGpuPower(req, res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/gpu/power/confirm")
                ServeGpuPowerConfirm(req, res, isAdmin);
            else if (req.HttpMethod == "POST" && path == "/api/gpu/power/reset")
                ServeGpuPowerReset(res, isAdmin);
            else if (req.HttpMethod == "GET" && path == "/api/monitor")
                ServeMonitor(res);
            else if (req.HttpMethod == "GET" && path == "/api/games")
                ServeGames(res);
            else if (req.HttpMethod == "POST" && path == "/api/baseline")
                ServeBaseline(req, res, isAdmin);
            else
            {
                res.StatusCode = 404;
                Close(res, "{}");
            }
        }
        catch
        {
            try { res.StatusCode = 500; Close(res, "{\"error\":\"internal\"}"); } catch { }
        }
    }

    static bool HostAllowed(HttpListenerRequest req)
    {
        var host = req.Headers["Host"];
        return host is not null && Array.Exists(AllowedHosts,
            h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
    }

    static void ServeStatus(HttpListenerResponse res, bool isAdmin)
    {
        var current = SnapshotEngine.Take();
        var baseline = _baseline ??= current;
        var r = ScoreEngine.Calculate(baseline, current);
        var json = JsonSerializer.Serialize(new
        {
            global      = r.Global,
            tier        = r.Tier,
            tier_color  = r.TierColor,
            categories  = new
            {
                sistema  = r.System.Score,
                hardware = r.Hardware.Score,
                sentinel = r.Sentinel.Score,
                gpu      = r.Gpu.Score,
                jogos    = r.Games.Score,
                monitor  = r.Monitoring.Score,
            },
            regressions = SnapshotEngine.Compare(baseline, current).Count,
            power_plan  = current.PowerPlanName,
            captured_at = current.CapturedAt,
            is_admin    = isAdmin,
            game_mode   = GameMode.Enabled,
            active_game = GameMode.ActiveGame,
            // First-run signal for the onboarding wizard: true once a baseline is on disk.
            has_baseline = SnapshotEngine.BaselineExists(),
            // Real fixable tasks for THIS machine. Each carries `needed` computed from the
            // live snapshot — the UI shows only the ones whose target the system doesn't
            // already meet. No more static "3 itens" fiction.
            tasks       = BuildTasks(current),
        });
        res.ContentType = "application/json; charset=utf-8";
        Close(res, json);
    }

    // Maps each forward action to whether the real system still needs it, plus the
    // human "from → to" labels read off the live snapshot. The id/target stay anchored
    // to the ApplyActions allowlist; this only *reports* state, never widens what can
    // be written.
    static List<object> BuildTasks(Snapshot s)
    {
        const string GuidHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string GuidUltimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        var guid = s.PowerPlanGuid.ToLowerInvariant();
        bool powerOk = guid == GuidHighPerf || guid == GuidUltimate;

        static string StartLbl(int v) => v switch
        {
            4 => "desabilitado",
            3 => "manual",
            2 => "automático",
            _ => "ativo",
        };

        var rows = new (string Id, bool Needed, string From, string Why, string Sev)[]
        {
            ("powerplan-highperf", !powerOk, s.PowerPlanName,
                "O plano atual reduz o clock da CPU para economizar energia — ruim em jogos. Alto Desempenho mantém a CPU em frequência máxima sob carga.", "warn"),
            ("diagtrack-off", s.DiagTrackStartType != 4, StartLbl(s.DiagTrackStartType),
                "Serviço da Microsoft que coleta dados de uso e os envia em segundo plano. Desativar libera CPU e rede, sem afetar Windows Update ou jogos.", "crit"),
            ("wsearch-off", s.WSearchStartType != 4, StartLbl(s.WSearchStartType),
                "Indexação do Windows Search consome disco e CPU em segundo plano. Em SSD o ganho de busca não compensa o custo sob carga.", "warn"),
            ("hibernation-off", s.HibernationEnabled != 0, s.HibernationEnabled == 1 ? "ativa" : "desativada",
                "Hibernação reserva um hiberfil.sys do tamanho da RAM e gera escrita constante no SSD. Desativar libera disco.", "warn"),
            // needed só quando confirmadamente ligada (==1); -1 = não lido não enche o saco.
            ("mouseaccel-off", s.MouseAccel == 1, s.MouseAccel == 1 ? "ligada" : "desligada",
                "A aceleração (\"melhorar precisão do ponteiro\") faz o cursor andar mais ou menos conforme a velocidade do movimento — quebra a memória muscular da mira. Desligar dá resposta 1:1, o padrão de todo jogador de FPS. Não muda FPS, muda a mira.", "warn"),
            ("accesskeys-off", s.AccessKeyHotkeys == 1, s.AccessKeyHotkeys == 1 ? "ativadas" : "desativadas",
                "Apertar Shift 5 vezes abre o popup das Sticky Keys e rouba o foco do jogo — alt-tab na hora errada perde a partida. Desligar o atalho mantém o recurso disponível, mas sem o gatilho acidental. Zero impacto em FPS.", "warn"),
        };

        var list = new List<object>();
        foreach (var r in rows)
        {
            var a = ApplyActions[r.Id];
            list.Add(new
            {
                id          = r.Id,
                title       = a.Detail,
                why         = r.Why,
                from_label  = r.From,
                to_label    = a.AfterLabel,
                sev         = r.Sev,
                needed      = r.Needed,
            });
        }
        return list;
    }

    // POST /api/baseline — capture current state as the Sentinel baseline (wizard finale).
    // Overwrites the trusted reference the score/regression engine compares against, so it
    // is CSRF + admin gated like every other write.
    static void ServeBaseline(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;
        var snap = SnapshotEngine.Take();
        SnapshotEngine.SaveBaseline(snap);
        _baseline = snap;
        Close(res, JsonSerializer.Serialize(new
        {
            ok = true,
            captured_at = snap.CapturedAt,
            power_plan = snap.PowerPlanName,
        }));
    }

    // GET /api/ram — physical-memory read for the wizard's XMP/RAM step. Read-only and
    // educational: reports the rated (SPD/XMP label) clock vs the clock actually running,
    // so the UI can tell whether XMP/DOCP is active. Shells PowerShell's CIM provider — the
    // same out-of-process idiom as powercfg — to avoid pulling in System.Management.
    static void ServeRam(HttpListenerResponse res)
    {
        res.ContentType = "application/json; charset=utf-8";
        try
        {
            var raw = RunPs(
                "Get-CimInstance Win32_PhysicalMemory | " +
                "Select-Object Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber,DeviceLocator | " +
                "ConvertTo-Json -Compress").Trim();
            // CIM emits a bare object (not an array) when there's a single module; normalize.
            if (raw.Length == 0) { Close(res, "{\"modules\":[],\"error\":\"sem dados\"}"); return; }
            if (!raw.StartsWith("[")) raw = "[" + raw + "]";

            using var doc = JsonDocument.Parse(raw);
            var modules = new List<object>();
            long totalBytes = 0; int rated = 0, running = 0;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                long cap  = GetLong(m, "Capacity");
                int speed = (int)GetLong(m, "Speed");                // rated label (SPD/XMP)
                int conf  = (int)GetLong(m, "ConfiguredClockSpeed");  // actually running
                totalBytes += cap;
                if (speed > rated)  rated  = speed;
                if (conf  > running) running = conf;
                modules.Add(new
                {
                    capacity_gb  = (int)Math.Round(cap / 1073741824.0),
                    speed,
                    configured   = conf,
                    manufacturer = GetStr(m, "Manufacturer"),
                    part         = GetStr(m, "PartNumber"),
                    slot         = GetStr(m, "DeviceLocator"),
                });
            }
            // XMP/DOCP "active" heuristic: running clock meets the rated label (within 2%).
            bool xmpActive = rated > 0 && running >= rated * 0.98;
            Close(res, JsonSerializer.Serialize(new
            {
                modules,
                total_gb     = (int)Math.Round(totalBytes / 1073741824.0),
                module_count = modules.Count,
                dual_channel = modules.Count >= 2,
                rated_mhz    = rated,
                running_mhz  = running,
                xmp_active   = xmpActive,
            }));
        }
        catch (Exception e)
        {
            // Soft-fail: the wizard step degrades to "não consegui ler" rather than breaking.
            Close(res, JsonSerializer.Serialize(new { modules = Array.Empty<object>(), error = e.Message }));
        }
    }

    static string RunPs(string script)
    {
        var psi = new ProcessStartInfo("powershell",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // PS 5.1 writes console-codepage bytes; read as UTF-8 so game names / paths with
            // accents don't come back as mojibake. Scripts set [Console]::OutputEncoding=UTF8.
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(8000);
        return outp;
    }

    // GET /api/gpu — live GPU telemetry (temp, clocks, util, vram, power, fan) straight
    // from the NVIDIA driver via nvidia-smi. Read-only; no gating. Replaces the dashboard's
    // old hard-coded placeholder numbers. available=false when there's no NVIDIA GPU/driver,
    // so the UI shows "—" instead of a made-up temperature.
    static void ServeGpu(HttpListenerResponse res)
    {
        res.ContentType = "application/json; charset=utf-8";
        var g = GpuReader.Read();
        var p = GpuTuner.Read();
        var json = JsonSerializer.Serialize(new
        {
            available    = g.Available,
            name         = g.Name,
            temp_c       = g.TempC,
            core_mhz     = g.CoreMhz,
            mem_mhz      = g.MemMhz,
            util_pct     = g.UtilPct,
            mem_used_mb  = g.MemUsedMb,
            mem_total_mb = g.MemTotalMb,
            power_w      = g.PowerW,
            fan_pct      = g.FanPct,
            // Power-limit tuning surface (Fase 1). UI mostra o slider só quando available.
            power = new
            {
                available = p.Available,
                current   = (int)Math.Round(p.Current),
                @default  = (int)Math.Round(p.Default),
                min       = (int)Math.Round(p.Min),
                max       = (int)Math.Round(p.Max),
                pending   = p.Pending,
            },
        });
        Close(res, json);
    }

    // POST /api/gpu/power {watts} — apply provisório do limite (CSRF+admin, clampeado,
    // auto-reverte se não confirmar). O watts é a única exceção ao id-only; GpuTuner
    // clampeia pro [min,max] do driver antes de qualquer escrita.
    static void ServeGpuPower(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;
        double watts;
        try
        {
            using var doc = JsonDocument.Parse(ReadBody(req));
            watts = doc.RootElement.GetProperty("watts").GetDouble();
        }
        catch
        {
            res.StatusCode = 400;
            Close(res, "{\"error\":\"expected {\\\"watts\\\":number}\"}");
            return;
        }
        var r = GpuTuner.Apply(watts);
        Close(res, JsonSerializer.Serialize(new
        {
            ok = r.Ok, applied = (int)r.Applied, revert_to = (int)r.RevertTo,
            min = (int)r.Min, max = (int)r.Max, message = r.Message, revert_seconds = 15,
        }));
    }

    static void ServeGpuPowerConfirm(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;
        bool ok = GpuTuner.Confirm();
        Close(res, JsonSerializer.Serialize(new { ok, kept = ok }));
    }

    static void ServeGpuPowerReset(HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        // Reset só restaura o padrão SEGURO do driver → gate só por admin, sem token.
        if (!isAdmin)
        {
            res.StatusCode = 403;
            Close(res, "{\"error\":\"admin required\"}");
            return;
        }
        var r = GpuTuner.Reset();
        Close(res, JsonSerializer.Serialize(new { ok = r.Ok, applied = (int)r.Applied, message = r.Message }));
    }

    // One PowerShell/CIM pass for the live system monitor: CPU (name/cores/threads/load),
    // RAM (total/free), physical disks (model/media/bus/size/used/health/temp), and the top
    // processes by working set (with per-process CPU%). CIM perf classes are used instead of
    // Get-Counter so the property names are locale-independent — Get-Counter's paths are
    // TRANSLATED on a pt-BR Windows and would silently break. Single-quoted throughout: it's
    // passed through RunPs's double-quoted -Command, so no embedded double quotes allowed.
    const string MonitorScript =
        "$ErrorActionPreference='SilentlyContinue';" +
        "$os=Get-CimInstance Win32_OperatingSystem;" +
        "$cp=Get-CimInstance Win32_Processor|Select-Object -First 1;" +
        "$prc=Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor;" +
        "$ld=($prc|Where-Object {$_.Name -eq '_Total'}).PercentProcessorTime;" +
        // Per-core load: instâncias numeradas (0,1,2…), ordenadas, viram array de %.
        "$cores=@($prc|Where-Object {$_.Name -ne '_Total'}|Sort-Object {[int]$_.Name}|ForEach-Object {[int]$_.PercentProcessorTime});" +
        // Throughput agregado de disco (_Total): bytes/s lidos e escritos.
        "$iot=Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk|Where-Object {$_.Name -eq '_Total'}|Select-Object -First 1;" +
        "$lp=[int]$cp.NumberOfLogicalProcessors;if($lp -lt 1){$lp=1};" +
        "$pf=@{};Get-CimInstance Win32_PerfFormattedData_PerfProc_Process|ForEach-Object {$pf[[int]$_.IDProcess]=[int]$_.PercentProcessorTime};" +
        "$dk=Get-PhysicalDisk|ForEach-Object {" +
            "$rt=$null;try{$rt=($_|Get-StorageReliabilityCounter).Temperature}catch{};" +
            "$dn=[int]$_.DeviceId;$us=$null;" +
            "try{$vs=Get-Partition -DiskNumber $dn|Get-Volume;$us=([int64](($vs|Measure-Object -Property Size -Sum).Sum))-([int64](($vs|Measure-Object -Property SizeRemaining -Sum).Sum))}catch{};" +
            "[pscustomobject]@{model=$_.FriendlyName;media=[string]$_.MediaType;bus=[string]$_.BusType;size=[int64]$_.Size;used=$us;health=[string]$_.HealthStatus;temp=$rt}};" +
        "$pr=Get-Process|Sort-Object -Descending WorkingSet64|Select-Object -First 6|ForEach-Object {" +
            "$c=0;if($pf.ContainsKey($_.Id)){$c=[int][math]::Round($pf[$_.Id]/$lp)};" +
            "[pscustomobject]@{name=$_.ProcessName;ram=[int64]$_.WorkingSet64;cpu=$c}};" +
        "[pscustomobject]@{" +
            "cpu=[pscustomobject]@{name=[string]$cp.Name;cores=[int]$cp.NumberOfCores;threads=$lp;load=[int]$ld;per_core=$cores};" +
            "ram=[pscustomobject]@{total_kb=[int64]$os.TotalVisibleMemorySize;free_kb=[int64]$os.FreePhysicalMemory};" +
            "io=[pscustomobject]@{read_bps=[int64]$iot.DiskReadBytesPersec;write_bps=[int64]$iot.DiskWriteBytesPersec};" +
            "disks=@($dk);procs=@($pr)}|ConvertTo-Json -Depth 5 -Compress";

    // GET /api/monitor — live CPU/RAM/disk/process telemetry. Read-only; no gating. The UI
    // polls this; available=false (or any field absent) makes the UI show "—" rather than
    // invent numbers.
    static void ServeMonitor(HttpListenerResponse res)
    {
        res.ContentType = "application/json; charset=utf-8";
        try
        {
            var raw = RunPs(MonitorScript).Trim();
            if (raw.Length == 0) { Close(res, "{\"available\":false}"); return; }
            using var _ = JsonDocument.Parse(raw); // reject malformed output
            Close(res, "{\"available\":true,\"sys\":" + raw + "}");
        }
        catch { Close(res, "{\"available\":false}"); }
    }

    // Real game detection: Steam (registry SteamPath → libraryfolders.vdf → appmanifest_*.acf)
    // and Epic (ProgramData manifests). Each install path is mapped to its drive's media type
    // (SSD/HDD) so the UI can flag "no HDD" honestly instead of inventing it. No literal double
    // quotes in the script (uses [char]34) so it survives RunPs's double-quoted -Command wrap.
    // ONE LINE (statements joined by ';'): powershell.exe -Command breaks on a multi-line
    // argument, so this mirrors MonitorScript's single-line idiom. UTF-8 console + UTF-8 .acf
    // reads kill the mojibake; final dedupe by lowercased path drops the Steam-default-library
    // double count; redistributables/shared runtimes are filtered out.
    const string GamesScript =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ErrorActionPreference='SilentlyContinue';$q=[char]34;" +
        "$dm=@{};Get-PhysicalDisk|ForEach-Object{$pd=$_;Get-Partition -DiskNumber $pd.DeviceId|Where-Object{$_.DriveLetter}|ForEach-Object{$dm[([string]$_.DriveLetter).ToUpper()]=[string]$pd.MediaType}};" +
        "function Med($p){if(-not $p){return 'Unknown'};$d=($p.Substring(0,1)).ToUpper();if($dm.ContainsKey($d)){$m=$dm[$d];if($m -match 'SSD'){return 'SSD'};if($m -match 'HDD'){return 'HDD'};if($m -match 'NVMe'){return 'SSD'};return $m};return 'Unknown'};" +
        "$out=New-Object System.Collections.ArrayList;" +
        "$sp=(Get-ItemProperty 'HKCU:\\Software\\Valve\\Steam' -Name SteamPath).SteamPath;" +
        "if($sp){$libs=New-Object System.Collections.ArrayList;[void]$libs.Add($sp);" +
        "$lf=Join-Path $sp 'steamapps\\libraryfolders.vdf';" +
        "if(Test-Path $lf){Get-Content $lf -Encoding UTF8|ForEach-Object{$parts=$_ -split $q;if($parts.Count -ge 4 -and $parts[1] -eq 'path'){[void]$libs.Add(($parts[3] -replace '\\\\\\\\','\\'))}}};" +
        "foreach($lib in $libs){$cm=Join-Path $lib 'steamapps';if(Test-Path $cm){Get-ChildItem $cm -Filter 'appmanifest_*.acf'|ForEach-Object{" +
        "$nm=$null;$idir=$null;Get-Content $_.FullName -Encoding UTF8|ForEach-Object{$p=$_ -split $q;if($p.Count -ge 4){if($p[1] -eq 'name'){$nm=$p[3]};if($p[1] -eq 'installdir'){$idir=$p[3]}}};" +
        "if($nm -and $idir -and $nm -notmatch 'Redistributable|Steamworks Shared|Proton|Steam Linux'){$full=Join-Path (Join-Path $cm 'common') $idir;[void]$out.Add([pscustomobject]@{name=$nm;launcher='Steam';path=$full;media=(Med $full)})}}}}};" +
        "$em=Join-Path $env:ProgramData 'Epic\\EpicGamesLauncher\\Data\\Manifests';" +
        "if(Test-Path $em){Get-ChildItem $em -Filter '*.item'|ForEach-Object{$j=Get-Content $_.FullName -Raw -Encoding UTF8|ConvertFrom-Json;" +
        "if($j.DisplayName -and $j.InstallLocation){[void]$out.Add([pscustomobject]@{name=$j.DisplayName;launcher='Epic';path=$j.InstallLocation;media=(Med $j.InstallLocation)})}}};" +
        "$seen=@{};$final=New-Object System.Collections.ArrayList;foreach($g in $out){$k=([string]$g.path).ToLower();if(-not $seen.ContainsKey($k)){$seen[$k]=1;[void]$final.Add($g)}};" +
        "[pscustomobject]@{games=@($final|Sort-Object name)}|ConvertTo-Json -Depth 4 -Compress";

    // GET /api/games — installed-game list with real SSD/HDD per game. Read-only; no gating.
    // available=false on any failure → UI shows the honest "detecção em breve" empty state.
    static void ServeGames(HttpListenerResponse res)
    {
        res.ContentType = "application/json; charset=utf-8";
        try
        {
            var raw = RunPs(GamesScript).Trim();
            if (raw.Length == 0) { Close(res, "{\"available\":true,\"games\":[]}"); return; }
            using var _ = JsonDocument.Parse(raw); // reject malformed
            Close(res, "{\"available\":true," + raw.TrimStart('{'));
        }
        catch { Close(res, "{\"available\":false,\"games\":[]}"); }
    }

    static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    static void ServeRestore(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;

        var current = SnapshotEngine.Take();
        var bl = _baseline ?? current;
        var plan = RestoreEngine.BuildPlan(bl, current);

        if (plan.Count == 0) { Close(res, "{\"applied\":0,\"total\":0,\"results\":[]}"); return; }

        // Safety net before mutating the machine.
        var rp = RestorePointEngine.Create("Forge antes de restaurar ao baseline");

        var batchId = Guid.NewGuid().ToString("N");
        int ok = 0;
        var results = new List<object>();
        foreach (var a in plan)
        {
            bool applied = RestoreEngine.ApplyRaw(a.Kind, a.RawTarget);
            if (applied)
            {
                ok++;
                FixLog.Append(new FixLogEntry(DateTime.UtcNow, batchId, "restore", a.Kind,
                    a.Setting, a.RawCurrent, a.RawTarget, a.BeforeLabel, a.AfterLabel, a.Detail));
            }
            results.Add(new { kind = a.Kind, setting = a.Setting, ok = applied });
        }

        if (ok > 0)
            ToastHelper.Show("Forge — restauração aplicada", $"{ok}/{plan.Count} configuração(ões) restaurada(s).");

        var json = JsonSerializer.Serialize(new
        {
            applied = ok,
            total = plan.Count,
            restore_point = new { created = rp.Created, message = rp.Message },
            results
        });
        Close(res, json);
    }

    // One forward optimization action. SECURITY principle 1: the request body carries
    // ONLY an id from this allowlist; the (kind,target) it resolves to is hardcoded.
    // No GUID, service name, or value is ever taken from the request — so neither a
    // foreign page nor a tampered UI can drive the daemon to write an arbitrary value.
    record ApplyAction(string Kind, string TargetRaw, string Setting, string AfterLabel, string Detail);

    static readonly Dictionary<string, ApplyAction> ApplyActions = new(StringComparer.Ordinal)
    {
        // Targets chosen to match the values ScoreEngine rewards, so applying one
        // visibly raises the score (the forge heats up).
        ["powerplan-highperf"] = new("powerplan", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            "Plano de energia", "Alto desempenho", "Ativar plano de Alto desempenho"),
        ["diagtrack-off"] = new("service:DiagTrack", "4",
            "Telemetria (DiagTrack)", "desabilitado", "Desabilitar serviço de telemetria DiagTrack"),
        ["wsearch-off"] = new("service:WSearch", "4",
            "Windows Search", "desabilitado", "Desabilitar indexação do Windows Search"),
        ["hibernation-off"] = new("hibernation", "0",
            "Hibernação", "desativada", "Desativar hibernação (libera disco, menos escrita em SSD)"),
        // QoL de gameplay: zero FPS, mira 1:1. Não move o score de performance (não é
        // métrica de perf) — o feedback é o card sumir da Central quando aplicado.
        ["mouseaccel-off"] = new("mouseaccel", "0",
            "Precisão do ponteiro", "desligada", "Desligar aceleração do mouse (mira 1:1, padrão FPS)"),
        ["accesskeys-off"] = new("accesskeys", "0",
            "Teclas de acessibilidade", "desativadas", "Desligar atalhos Sticky/Filter/Toggle (sem popup do Shift x5)"),
    };

    // POST /api/apply — the "forge ahead" rail, mirror of /api/restore. Same gating
    // (CSRF + admin), same safety net (restore point + audit log), reversible via undo.
    static void ServeApply(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;

        // Resolve the id against the server-side allowlist. Unknown id = 400, never a
        // passthrough of caller-supplied values.
        string id;
        try
        {
            using var doc = JsonDocument.Parse(ReadBody(req));
            id = doc.RootElement.GetProperty("id").GetString() ?? "";
        }
        catch { id = ""; }

        if (!ApplyActions.TryGetValue(id, out var act))
        {
            res.StatusCode = 400;
            Close(res, "{\"error\":\"unknown action\"}");
            return;
        }

        var current = SnapshotEngine.Take();
        var (beforeRaw, beforeLabel) = CurrentValue(current, act.Kind);

        // Idempotent: already in the target state → no write, no restore point.
        if (string.Equals(beforeRaw, act.TargetRaw, StringComparison.OrdinalIgnoreCase))
        {
            Close(res, JsonSerializer.Serialize(new
            {
                applied = 0, total = 1, already = true,
                results = new[] { new { kind = act.Kind, setting = act.Setting, ok = true } }
            }));
            return;
        }

        // Safety net before mutating the machine.
        var rp = RestorePointEngine.Create($"Forge antes de aplicar: {act.Setting}");

        var batchId = Guid.NewGuid().ToString("N");
        bool ok = RestoreEngine.ApplyRaw(act.Kind, act.TargetRaw);
        if (ok)
            FixLog.Append(new FixLogEntry(DateTime.UtcNow, batchId, "apply", act.Kind,
                act.Setting, beforeRaw, act.TargetRaw, beforeLabel, act.AfterLabel, act.Detail));

        if (ok)
            ToastHelper.Show("Forge — otimização aplicada", $"{act.Setting}: {act.AfterLabel}.");

        var json = JsonSerializer.Serialize(new
        {
            applied = ok ? 1 : 0,
            total = 1,
            restore_point = new { created = rp.Created, message = rp.Message },
            results = new[] { new { kind = act.Kind, setting = act.Setting, ok } }
        });
        Close(res, json);
    }

    // ---- Layer 4 gate, shared by every mutating endpoint ------------------------
    // Valid CSRF token + admin. Writes the 403 and returns false when refused; the
    // caller must already have set res.ContentType. One place to audit the rule.
    static bool MutationAllowed(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        var token = req.Headers["Security-Token"];
        if (token is null || !FixedEquals(token, CsrfToken))
        {
            res.StatusCode = 403;
            Close(res, "{\"error\":\"missing or invalid security token\"}");
            return false;
        }
        if (!isAdmin)
        {
            res.StatusCode = 403;
            Close(res, "{\"error\":\"admin required\",\"hint\":\"Reinicie em terminal elevado.\"}");
            return false;
        }
        return true;
    }

    // POST /api/quick — "Sopro da Forja". Applies the whole safe optimization set in a
    // single pass: every allowlisted action not already in its target state, under ONE
    // restore point and ONE audit batch — so a single `undo` reverts the entire sopro.
    // Same gating and safety net as /api/apply; no caller value ever reaches the system
    // (the request body is ignored — the action set is the server-side allowlist).
    static void ServeQuick(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;

        var current = SnapshotEngine.Take();

        // Partition the allowlist: what still needs applying vs. what's already done.
        var todo = new List<ApplyAction>();
        var results = new List<object>();
        int already = 0;
        foreach (var act in ApplyActions.Values)
        {
            var (beforeRaw, _) = CurrentValue(current, act.Kind);
            if (string.Equals(beforeRaw, act.TargetRaw, StringComparison.OrdinalIgnoreCase))
            {
                already++;
                results.Add(new { kind = act.Kind, setting = act.Setting, ok = true, already = true });
            }
            else todo.Add(act);
        }

        // Nothing drifted from the optimized target → no write, no restore point.
        if (todo.Count == 0)
        {
            Close(res, JsonSerializer.Serialize(new
            {
                applied = 0, total = ApplyActions.Count, already, results
            }));
            return;
        }

        // One safety net + one audit batch for the whole sopro. Logging "apply" with the
        // pre-sopro raw value as RawBefore is what makes the batch undoable in one shot.
        var rp = RestorePointEngine.Create("Forge antes do Sopro da Forja (otimização rápida)");
        var batchId = Guid.NewGuid().ToString("N");
        int ok = 0;
        foreach (var act in todo)
        {
            var (beforeRaw, beforeLabel) = CurrentValue(current, act.Kind);
            bool applied = RestoreEngine.ApplyRaw(act.Kind, act.TargetRaw);
            if (applied)
            {
                ok++;
                FixLog.Append(new FixLogEntry(DateTime.UtcNow, batchId, "apply", act.Kind,
                    act.Setting, beforeRaw, act.TargetRaw, beforeLabel, act.AfterLabel, act.Detail));
            }
            results.Add(new { kind = act.Kind, setting = act.Setting, ok = applied });
        }

        if (ok > 0)
            ToastHelper.Show("Forge — Sopro da Forja", $"{ok} otimização(ões) aplicada(s). Reversível pelo histórico.");

        Close(res, JsonSerializer.Serialize(new
        {
            applied = ok,
            total = ApplyActions.Count,
            already,
            skipped = todo.Count - ok,
            restore_point = new { created = rp.Created, message = rp.Message },
            results
        }));
    }

    // POST /api/gamemode — enable/disable automatic Game Mode. The body carries a single
    // bool {enabled}; no system value is taken from the request. GameMode itself only
    // drives the same hardcoded, reversible knobs (see GameMode.cs).
    static void ServeGameMode(HttpListenerRequest req, HttpListenerResponse res, bool isAdmin)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (!MutationAllowed(req, res, isAdmin)) return;

        bool enabled;
        try
        {
            using var doc = JsonDocument.Parse(ReadBody(req));
            enabled = doc.RootElement.GetProperty("enabled").GetBoolean();
        }
        catch
        {
            res.StatusCode = 400;
            Close(res, "{\"error\":\"expected {\\\"enabled\\\":bool}\"}");
            return;
        }

        if (enabled) GameMode.Enable();
        else GameMode.Disable();

        Close(res, JsonSerializer.Serialize(new
        {
            enabled = GameMode.Enabled,
            active_game = GameMode.ActiveGame
        }));
    }

    // Current machine value + human label for a kind, read from a fresh snapshot.
    // Recorded as the undo target in the audit log.
    static (string raw, string label) CurrentValue(Snapshot s, string kind) => kind switch
    {
        "powerplan"         => (s.PowerPlanGuid, s.PowerPlanName),
        "service:DiagTrack" => (s.DiagTrackStartType.ToString(), StartLabel(s.DiagTrackStartType)),
        "service:WSearch"   => (s.WSearchStartType.ToString(), StartLabel(s.WSearchStartType)),
        "hibernation"       => (s.HibernationEnabled.ToString(), s.HibernationEnabled == 1 ? "ativa" : "desativada"),
        "mouseaccel"        => (s.MouseAccel.ToString(), s.MouseAccel == 1 ? "ligada" : "desligada"),
        "accesskeys"        => (s.AccessKeyHotkeys.ToString(), s.AccessKeyHotkeys == 1 ? "ativadas" : "desativadas"),
        _                   => ("", ""),
    };

    static string StartLabel(int v) => v switch
    {
        0 => "boot", 1 => "sistema", 2 => "automático", 3 => "manual", 4 => "desabilitado", _ => $"({v})"
    };

    static string ReadBody(HttpListenerRequest req)
    {
        using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        return r.ReadToEnd();
    }

    // GET / — serve the dashboard same-origin with the CSRF token injected so the
    // page can authenticate its POSTs. We inject a <meta> tag the JS reads.
    static async Task ServeUi(HttpListenerResponse res)
    {
        var file = LocateUi();
        if (file is null)
        {
            res.StatusCode = 404;
            res.ContentType = "text/plain; charset=utf-8";
            Close(res, "UI não encontrada (design/index.html).");
            return;
        }

        var html = await File.ReadAllTextAsync(file);
        var meta = $"<meta name=\"forge-token\" content=\"{CsrfToken}\">";
        // Inject right after <head>; fall back to prepend if no <head>.
        var idx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        html = idx >= 0
            ? html.Insert(idx + 6, "\n  " + meta)
            : meta + html;

        res.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.OutputStream.Close();
    }

    // Resolve design/index.html relative to the exe, walking up a few levels so it
    // works from bin/Debug/... during the spike.
    static string? LocateUi()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "design", "index.html");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    static void Close(HttpListenerResponse res, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    // Constant-time compare so the token can't be recovered by timing.
    static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

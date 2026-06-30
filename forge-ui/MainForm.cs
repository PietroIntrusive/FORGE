using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace ForgeUi;

// The Forge window. Hosts the daemon-served dashboard in a WebView2 and lives in the
// system tray. Closing the window hides it (the app keeps running); real exit is only
// via the tray's "Encerrar". All system changes happen inside the WebView, through the
// daemon's audited IPC — this shell never writes to the machine itself.
sealed class MainForm : Form
{
    const string DaemonRoot   = "http://127.0.0.1:5172/";
    const string DaemonStatus = "http://127.0.0.1:5172/api/status";

    readonly WebView2 _web = new();
    readonly NotifyIcon _tray = new();
    readonly ToolStripMenuItem _gameMode;
    readonly ToolStripMenuItem _sentinel;
    readonly System.Windows.Forms.Timer _poll = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    // Completes once the dashboard has loaded in the WebView. The tray actions drive
    // the page's own authenticated JS (it holds the CSRF token; this process doesn't),
    // so they must wait on this before calling ExecuteScript.
    readonly TaskCompletionSource _webReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _reallyExit;

    public MainForm()
    {
        Text = "Forge";
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x0a, 0x0a, 0x0b);
        Icon = ForgeIcon.Create(256);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        // ---- system tray ----
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir A Forja", null, (_, _) => ShowWindow());
        menu.Items.Add("Sopro da Forja (otimização rápida)", null, async (_, _) => await QuickOptimize());
        _gameMode = new ToolStripMenuItem("Modo Jogo Automático") { CheckOnClick = true };
        _gameMode.Click += (_, _) => ToggleGameMode();
        menu.Items.Add(_gameMode);
        _sentinel = new ToolStripMenuItem("Sentinel: —") { Enabled = false };
        menu.Items.Add(_sentinel);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Encerrar Forja", null, (_, _) => ExitApp());

        _tray.Icon = ForgeIcon.Create(32);
        _tray.Text = "Forge";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        FormClosing += OnFormClosing;
        Load += async (_, _) => await Init();

        _poll.Interval = 3000;
        _poll.Tick += async (_, _) => await PollStatus();
        _poll.Start();
    }

    async Task Init()
    {
        await EnsureDaemon();
        await _web.EnsureCoreWebView2Async();
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) _webReady.TrySetResult(); };
        _web.CoreWebView2.Navigate(DaemonRoot);
        await PollStatus();
    }

    // True once we've had to fall back to a non-elevated daemon: the dashboard renders
    // (status/score/monitor are read-only and bind fine without admin) but any mutation
    // returns 403. QuickOptimize/ToggleGameMode check this to warn instead of silently
    // failing.
    bool _daemonUnprivileged;

    // Bring the daemon up if it isn't already answering. The daemon is the *privileged*
    // half — it writes services and power state — so it must run elevated. This process
    // never does (asInvoker, by design: an elevated WebView2 is a bigger attack surface).
    // So we spawn the daemon with the "runas" verb: a single UAC prompt, for the daemon
    // alone, the first time the Forge opens in a session.
    //
    // In an installed setup the daemon is already running (logon task / service), so
    // DaemonAlive() short-circuits here and the user never sees a prompt at all.
    async Task EnsureDaemon()
    {
        if (await DaemonAlive()) { _daemonUnprivileged = false; return; }
        var exe = FindDaemonExe();
        if (exe is null) return;

        // 1) Preferred: elevated daemon — full optimization power, one UAC prompt.
        if (TryStartDaemon(exe, elevated: true) && await WaitDaemonAlive())
        {
            _daemonUnprivileged = false;
            return;
        }

        // 2) UAC declined or the elevated launch failed. Start unprivileged so the
        //    dashboard still opens (read-only). Mutations will 403; we flag that.
        if (TryStartDaemon(exe, elevated: false) && await WaitDaemonAlive())
        {
            _daemonUnprivileged = true;
            _tray.ShowBalloonTip(4000, "Forge",
                "Aberta em modo leitura. Para otimizar, reabra a Forja e aceite o aviso do Windows (UAC).",
                ToolTipIcon.Info);
        }
    }

    // Launches the daemon's `serve` verb. elevated=true adds the runas verb (UAC).
    // Returns false on UAC cancel (Win32 1223) or any launch failure — caller falls back.
    static bool TryStartDaemon(string exe, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "serve") { UseShellExecute = true };
            if (elevated) psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // 1223 = UAC cancelled
        catch { return false; }
    }

    async Task<bool> WaitDaemonAlive()
    {
        for (int i = 0; i < 16; i++)
        {
            if (await DaemonAlive()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    async Task<bool> DaemonAlive()
    {
        try { return (await _http.GetAsync(DaemonStatus)).IsSuccessStatusCode; }
        catch { return false; }
    }

    static string? FindDaemonExe()
    {
        var b = AppContext.BaseDirectory;
        const string tfm = "net10.0-windows10.0.19041.0";
        string[] candidates =
        {
            Path.Combine(b, "ForgeSentinel.exe"),
            Path.GetFullPath(Path.Combine(b, "..", "..", "..", "..", "spike-sentinel", "bin", "Debug",   tfm, "ForgeSentinel.exe")),
            Path.GetFullPath(Path.Combine(b, "..", "..", "..", "..", "spike-sentinel", "bin", "Release", tfm, "ForgeSentinel.exe")),
        };
        return Array.Find(candidates, File.Exists);
    }

    async Task PollStatus()
    {
        try
        {
            var json = await _http.GetStringAsync(DaemonStatus);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int global = r.GetProperty("global").GetInt32();
            int regr = r.GetProperty("regressions").GetInt32();
            string tier = r.GetProperty("tier").GetString() ?? "";
            _sentinel.Text = regr > 0
                ? $"Sentinel: {regr} regressão(ões) — score {global}"
                : $"Sentinel: OK — {global}/100 ({tier})";

            // Reflect the daemon's real Game Mode state. Setting Checked here doesn't fire
            // the menu's Click, so this can't loop back into ToggleGameMode.
            bool gameMode = r.TryGetProperty("game_mode", out var gm) && gm.GetBoolean();
            _gameMode.Checked = gameMode;
            string? activeGame = r.TryGetProperty("active_game", out var ag) && ag.ValueKind == JsonValueKind.String
                ? ag.GetString() : null;
            _gameMode.Text = activeGame is not null ? $"Modo Jogo: {activeGame}" : "Modo Jogo Automático";
        }
        catch
        {
            _sentinel.Text = "Sentinel: daemon offline";
        }
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit) return;
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2000, "Forge", "Rodando na bandeja. Clique no ícone pra reabrir.", ToolTipIcon.None);
        }
    }

    // "Sopro da Forja" — drives the page's own forgeQuick() (POST /api/quick): the whole
    // safe optimization set in one atomic, reversible batch. We trigger the UI's authed
    // JS rather than POST from here so the CSRF token never has to leave the WebView. The
    // window is shown so the user sees the forge heat up and the result banner.
    async Task QuickOptimize()
    {
        ShowWindow();
        await EnsureDaemon();
        if (!await WaitWebReady())
        {
            _tray.ShowBalloonTip(2500, "Forge", "Não consegui abrir a Forja a tempo. Tente de novo.", ToolTipIcon.Warning);
            return;
        }
        if (_daemonUnprivileged)
        {
            _tray.ShowBalloonTip(3500, "Forge", "Modo leitura: otimizar exige elevação. Reabra a Forja e aceite o UAC.", ToolTipIcon.Warning);
            return;
        }
        await _web.CoreWebView2.ExecuteScriptAsync("window.forgeQuick && window.forgeQuick()");
    }

    // Forge.Games auto mode. CheckOnClick has already flipped the menu's checkmark, so the
    // post-click state is the desired one. Drives the page's forgeGameMode() — no need to
    // surface the window; the WebView is live in the tray. Reverts the checkmark on failure.
    async void ToggleGameMode()
    {
        bool desired = _gameMode.Checked;
        await EnsureDaemon();
        if (!await WaitWebReady())
        {
            _tray.ShowBalloonTip(2500, "Forge", "Inicie A Forja para usar o Modo Jogo.", ToolTipIcon.Warning);
            _gameMode.Checked = !desired; // undo the optimistic flip
            return;
        }
        var js = $"window.forgeGameMode && window.forgeGameMode({(desired ? "true" : "false")})";
        await _web.CoreWebView2.ExecuteScriptAsync(js);
    }

    // True once the dashboard has loaded and forgeQuick/forgeGameMode are callable.
    async Task<bool> WaitWebReady()
    {
        try { await _webReady.Task.WaitAsync(TimeSpan.FromSeconds(15)); return _web.CoreWebView2 is not null; }
        catch { return false; }
    }

    void ExitApp()
    {
        _reallyExit = true;
        _poll.Stop();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _http.Dispose();
            _tray.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}

// Tray/window icon drawn in code — an incandescent forge dot (cold ring → forge core →
// hot center). Avoids shipping a binary .ico for the scaffold; a real branded icon can
// replace this later.
static class ForgeIcon
{
    // Loads the embedded multi-size forge.ico and hands back the frame closest to the
    // requested size (window: 256, tray: 32). Beats the old code-drawn ellipse — this is
    // the same anvil-over-magma mark stamped on the exe.
    public static Icon Create(int size = 256)
    {
        var asm = typeof(ForgeIcon).Assembly;
        var name = asm.GetManifestResourceNames()
                      .First(n => n.EndsWith("forge.ico", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return new Icon(s, new Size(size, size));
    }
}

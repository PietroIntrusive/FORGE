using System.Text.Json;

namespace ForgeSentinel;

// Histórico de score: JSONL local, uma linha por ponto {ts, global, categorias}.
// É o que transforma a tela "Evolução do seu PC" de demonstração em dado real.
//
// Decisões:
//   - %LOCALAPPDATA%\Forge (mesma casa do user-data do WebView2): local-only,
//     nunca roaming, nunca rede. Zero telemetria continua valendo — o arquivo
//     nasce e morre no PC do usuário.
//   - Dedup: só grava quando o score global MUDA, ou a cada 6h como batimento.
//     O poll da UI (5s) chama Record a cada status; o cache em memória do último
//     ponto faz o caso comum custar zero I/O.
//   - Rotação: 2000 pontos (~anos de uso real). Acima disso, descarta os mais
//     antigos.
//   - Linha corrompida é ignorada na leitura; falha de escrita nunca derruba o
//     daemon (histórico é conforto, não é função vital).
static class ScoreHistory
{
    static readonly object Gate = new();
    const int MaxPoints = 2000;
    const int HeartbeatSecs = 6 * 3600;

    static Point? _last;          // cache do último ponto gravado (evita reler o arquivo a cada poll)
    static bool _lastLoaded;

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge");
    static string FilePath => Path.Combine(Dir, "history.jsonl");

    // Nomes minúsculos de propósito: serializam direto no shape que a UI consome.
    sealed record Point(long ts, int global, int sistema, int hardware, int sentinel,
        int gpu, int jogos, int monitor);

    public static void Record(ScoreResult r)
    {
        try
        {
            lock (Gate)
            {
                if (!_lastLoaded)
                {
                    var existing = ReadAll();
                    _last = existing.Count > 0 ? existing[^1] : null;
                    _lastLoaded = true;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (_last is not null && _last.global == r.Global && now - _last.ts < HeartbeatSecs)
                    return;   // nada mudou e o batimento ainda não venceu

                var p = new Point(now, r.Global, r.System.Score, r.Hardware.Score,
                    r.Sentinel.Score, r.Gpu.Score, r.Games.Score, r.Monitoring.Score);

                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(p) + Environment.NewLine);
                _last = p;
                TrimIfNeeded();
            }
        }
        catch { /* histórico nunca derruba o daemon */ }
    }

    // JSON pronto pro GET /api/history.
    public static string Json()
    {
        try
        {
            List<Point> pts;
            lock (Gate) pts = ReadAll();
            return JsonSerializer.Serialize(new { available = true, points = pts });
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { available = false, error = e.Message });
        }
    }

    static List<Point> ReadAll()
    {
        var list = new List<Point>();
        if (!File.Exists(FilePath)) return list;
        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<Point>(line);
                if (p is not null) list.Add(p);
            }
            catch { /* linha corrompida: pula */ }
        }
        return list;
    }

    static void TrimIfNeeded()
    {
        var pts = ReadAll();
        if (pts.Count <= MaxPoints) return;
        pts.RemoveRange(0, pts.Count - MaxPoints);
        File.WriteAllLines(FilePath, pts.Select(p => JsonSerializer.Serialize(p)));
    }
}

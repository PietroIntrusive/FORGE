using System.Text.Json;

namespace ForgeSentinel;

// One audited change. Stores both the raw machine value (for replay/undo) and a
// human label (for display). BatchId groups all actions applied in a single run
// so `undo` can revert a whole restore at once.
record FixLogEntry(
    DateTime AppliedAt,
    string BatchId,
    string Action,       // "restore" | "apply" | "undo"
    string Kind,         // powerplan | service:DiagTrack | service:WSearch | hibernation
    string Setting,      // display name
    string RawBefore,    // machine value before this action (the undo target)
    string RawAfter,     // machine value written by this action
    string BeforeLabel,
    string AfterLabel,
    string Detail
);

// Append-only audit trail. SECURITY principle 4: every system change is logged
// with before/after and is reversible.
static class FixLog
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ForgeSentinel");

    // Encrypted + authenticated (AES-GCM): the audit trail can't be silently edited
    // to hide a change — a tampered blob fails the auth tag and is rejected.
    static readonly string LogPath       = Path.Combine(Dir, "fix_log.dat");
    static readonly string LegacyLogPath = Path.Combine(Dir, "fix_log.json");

    public static string Location => LogPath;

    public static List<FixLogEntry> Load()
    {
        // Encrypted store is authoritative once it exists. A null read here means a
        // tampered blob (GCM auth fail) — never fall back to cleartext, or the audit
        // trail could be downgraded by dropping a plaintext fix_log.json.
        if (File.Exists(LogPath))
        {
            var list = SecureStore.ReadJson<List<FixLogEntry>>(LogPath);
            if (list is null && File.Exists(LegacyLogPath))
                try { File.Delete(LegacyLogPath); } catch { }
            return list ?? new();
        }

        // One-time import of a pre-encryption log, then drop the cleartext file.
        if (File.Exists(LegacyLogPath))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<List<FixLogEntry>>(File.ReadAllText(LegacyLogPath));
                if (legacy is not null)
                {
                    SecureStore.WriteJson(LogPath, legacy);
                    File.Delete(LegacyLogPath);
                    return legacy;
                }
            }
            catch { /* ignore — treat as empty log */ }
        }
        return new();
    }

    public static void Append(FixLogEntry entry)
    {
        var all = Load();
        all.Add(entry);
        SecureStore.WriteJson(LogPath, all);
    }
}

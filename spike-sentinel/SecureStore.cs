using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeSentinel;

// Authenticated-encryption at rest for local state (baseline snapshot, fix log).
//
// Why: a competitor could forge a baseline to inflate their score, or scrub the
// fix log to hide changes. AES-256-GCM gives confidentiality AND integrity — any
// edit to the blob fails the auth tag and the read is rejected, so tampering is
// detected, not silently trusted.
//
// Key handling (respects "no static keys in the binary"): a random 256-bit key is
// generated once and stored under %APPDATA%, itself wrapped with Windows DPAPI
// (CurrentUser scope). The key never lives in source and never leaves the user's
// profile in cleartext. There is nothing to extract from the assembly.
//
// Blob layout:  [0x01 version][12-byte nonce][16-byte tag][ciphertext]
static class SecureStore
{
    const byte Version = 0x01;
    const int NonceLen = 12;   // AES-GCM standard nonce
    const int TagLen   = 16;   // 128-bit auth tag

    static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ForgeSentinel", "key.bin");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // ---- public JSON helpers -------------------------------------------------

    public static void WriteJson<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts);
        var blob = Protect(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, blob);
    }

    // Reads an encrypted blob. If the file is legacy cleartext JSON (pre-encryption),
    // it's transparently parsed and re-written encrypted on the spot (one-time migration).
    public static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var raw = File.ReadAllBytes(path);
        if (raw.Length == 0) return default;

        // Encrypted path
        if (raw[0] == Version && raw.Length >= 1 + NonceLen + TagLen)
        {
            try
            {
                var plain = Unprotect(raw);
                return JsonSerializer.Deserialize<T>(plain);
            }
            catch
            {
                // Tampered or key mismatch — refuse rather than trust garbage.
                return default;
            }
        }

        // Legacy cleartext migration: looks like JSON ('{' or '[')
        if (raw[0] is (byte)'{' or (byte)'[')
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(raw);
                if (value is not null) WriteJson(path, value); // upgrade to encrypted
                return value;
            }
            catch { return default; }
        }

        return default;
    }

    // ---- core crypto ---------------------------------------------------------

    static byte[] Protect(byte[] plaintext)
    {
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagLen];

        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, plaintext, cipher, tag);

        var blob = new byte[1 + NonceLen + TagLen + cipher.Length];
        blob[0] = Version;
        Buffer.BlockCopy(nonce, 0, blob, 1, NonceLen);
        Buffer.BlockCopy(tag, 0, blob, 1 + NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, blob, 1 + NonceLen + TagLen, cipher.Length);
        return blob;
    }

    static byte[] Unprotect(byte[] blob)
    {
        var key = GetKey();
        var nonce = new byte[NonceLen];
        var tag = new byte[TagLen];
        var cipher = new byte[blob.Length - 1 - NonceLen - TagLen];
        Buffer.BlockCopy(blob, 1, nonce, 0, NonceLen);
        Buffer.BlockCopy(blob, 1 + NonceLen, tag, 0, TagLen);
        Buffer.BlockCopy(blob, 1 + NonceLen + TagLen, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Decrypt(nonce, cipher, tag, plain); // throws on auth failure
        return plain;
    }

    // ---- key ring (DPAPI-wrapped 256-bit key) --------------------------------

    static byte[]? _cachedKey;

    static byte[] GetKey()
    {
        if (_cachedKey is not null) return _cachedKey;

        if (File.Exists(KeyPath))
        {
            try
            {
                var wrapped = File.ReadAllBytes(KeyPath);
                _cachedKey = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);
                if (_cachedKey.Length == 32) return _cachedKey;
            }
            catch { /* fall through and regenerate */ }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var wrappedNew = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, wrappedNew);
        _cachedKey = key;
        return key;
    }
}

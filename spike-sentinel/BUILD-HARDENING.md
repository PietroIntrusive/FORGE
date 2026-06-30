# Build Hardening — ForgeSentinel

Defense layers, from build flags to anti-reversal. Read before shipping a release.

## TL;DR

```bash
# Normal dev build — no anti-debug, no obfuscation. Debug freely.
dotnet build -c Debug

# Hardened release — anti-debug ON, obfuscation if the CLI is installed.
dotnet build -c Release -p:Hardened=true
```

`-p:Hardened=true` defines the `FORGE_HARDENED` compile symbol. Without it, the
whole anti-tamper subsystem (`IntegrityGuard`) compiles to nothing — so your own
debugging is never sabotaged.

---

## Layer 1 — Source-level (always on)

| Defense | Where | Notes |
|---|---|---|
| AES-256-GCM at rest | `SecureStore.cs` | `snapshot.dat`, `fix_log.dat`. Auth tag = tamper-evident. |
| DPAPI-wrapped key | `SecureStore.cs` | 256-bit key under `%APPDATA%`, wrapped CurrentUser. No static key in the binary. |
| Downgrade block | `SnapshotEngine`, `FixLog` | If the encrypted file exists but fails auth, we refuse — never fall back to a planted cleartext `.json`. |
| Input allowlist | `RestoreEngine.cs` | Fixed action set, GUID regex, bounded ints. No runtime-supplied command/path/value ever executes. |
| Loopback + CSRF + Host/Origin | `ApiServer.cs` | See SECURITY model in that file's header. |

## Layer 2 — Anti-debug (hardened builds only)

`IntegrityGuard.cs`, armed from `Program.cs` via `[Conditional("FORGE_HARDENED")]`.

- `IsDebuggerPresent` + `CheckRemoteDebuggerPresent` + `NtQueryInformationProcess(ProcessDebugPort)` on a background thread.
- **Late crash, not instant reaction.** On detection it silently taints `Trust.Healthy`; the score engine reads that flag later and skews the result far from the check site. No exit, no dialog — nothing that points the attacker at the guard.

## Layer 3 — Obfuscation (ConfuserEx, optional)

> [!warning] net10 caveat
> ConfuserEx2's **anti-tamper** and **compressor/packer** rewrite the PE and break
> on modern .NET runtimes. They are **deliberately disabled** in `forge.confuser.xml`.
> Only runtime-agnostic IL transforms are enabled: rename (overload induction),
> control flow, constants (string encryption), reference proxy, resources.

### Install the CLI

1. Grab ConfuserEx2 from the maintained fork: <https://github.com/mkaring/ConfuserEx/releases>
2. Unzip into `spike-sentinel/tools/confuser/` so that `tools/confuser/Confuser.CLI.exe` exists.
3. Build hardened release:
   ```bash
   dotnet build -c Release -p:Hardened=true
   ```
   The `ForgeObfuscate` MSBuild target runs automatically **after** the build and
   writes the protected assembly to `bin/Release/obfuscated/`.

If the CLI is absent, the build **warns and continues** — obfuscation is skipped,
never fatal. Paths in `forge.confuser.xml` assume the standard
`net10.0-windows10.0.19041.0` output folder; adjust the TFM if yours differs.

## Layer 4 — Native AOT (recommended for true anti-reversal)

For net10 this beats any IL obfuscator: AOT compiles to native machine code and
strips IL/metadata, so dnSpy/ILSpy have nothing to open. Tradeoff: reflection-heavy
paths need care, and the generic Host builder may emit trim warnings.

Starting point (validate before relying on it):

```bash
dotnet publish -c Release -r win-x64 -p:Hardened=true -p:PublishAot=true --self-contained
```

If AOT proves clean for the CLI surface, it should become the primary release path
and ConfuserEx drops to a secondary pass. Until validated, ship the obfuscated
JIT assembly from Layer 3.

---

## Verification done (2026-06-24)

- AES-GCM round-trip: `snapshot.dat` is binary (`0x01` header), no plaintext leak, reads back correct.
- Tamper: flipping 1 byte → GCM auth fail → "Sem baseline" (rejected, not trusted).
- Downgrade: planted forged `snapshot.json` + corrupted `.dat` → refused **and** the forged file quarantined.
- HTTP gates: no/bad token → 403, foreign Origin → 403, bad Host → 421, valid same-origin → 200.
- Both `Debug` and `Release -p:Hardened=true` compile with 0 errors.

# Security Policy

Forge runs with high privileges: it installs a background service (`forge-daemon`) and modifies Windows services, scheduled tasks, registry values, power settings, and GPU state. We take its security posture seriously, because a tool people trust with admin rights must earn that trust.

This document covers both **how Forge is built to be safe for the user** and **how to report a vulnerability**.

---

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately via GitHub Security Advisories:
**Repository → Security → Report a vulnerability** (private disclosure).

If you cannot use that channel, email the maintainer listed in the project README.

What to expect:
- Acknowledgment within **72 hours**
- An initial assessment within **7 days**
- Coordinated disclosure: we fix, then credit you (if you wish) in the release notes

Please include: affected version, OS build, reproduction steps, and impact.

---

## Supported versions

| Version | Supported |
|---|---|
| Latest stable (`main` releases) | ✅ |
| Pre-1.0 / nightly | ⚠️ best effort |
| Older minor versions | ❌ |

---

## Security design principles

These are non-negotiable rules the codebase must uphold. A PR that violates one will not be merged.

### 1. No arbitrary code execution — ever
The daemon **never** executes commands, scripts, or paths supplied at runtime by config files, the UI, or (future) community presets. Every system modification maps to a **hardcoded, reviewed action** in a C# function. Community presets (v2) are **declarative data only** — they select from a fixed allowlist of known-safe actions; they can never introduce a new command.

> This is the single most important rule. A PC optimizer that runs as `LocalSystem` and executes community-supplied strings would be a malware distribution platform. Forge is designed so that is structurally impossible.

### 2. Least privilege
- The **UI** (`forge-ui`) runs as the normal user — no elevation.
- Only the **daemon** holds privilege, and only the daemon touches the system.
- The daemon exposes a narrow, typed IPC surface — not a general-purpose command runner.

### 3. Hardened IPC
- Named pipe `\\.\pipe\forge` is created with an ACL restricting access to **authenticated local users** only — no remote, no anonymous.
- Every message is validated against a strict schema. Unknown actions are rejected.
- The daemon trusts **nothing** from the pipe: all inputs are bounds-checked and matched against allowlists before any action runs.

### 4. Everything is reversible and logged
- Every change writes a `fix_log` row: module, action, value before, value after, timestamp.
- The user can revert any change from the UI.
- Nothing is changed silently — the UI shows the exact before/after value before applying.

### 5. No kernel driver
- GPU tuning uses **NVAPI in user space**. Forge installs **no kernel-mode driver**, which removes an entire class of privilege-escalation and BSOD risks and keeps distribution clean (no driver signing chain to compromise).

### 6. No telemetry
- Forge makes **zero network calls** in v1.0, with one exception: an **opt-in** GPU driver version check that contacts only NVIDIA's public endpoint. It is off by default and sends no user data.
- There is no analytics, no crash phone-home, no account.

### 7. Transparency by default
- The README enumerates **every category of system change** Forge can make.
- The source is the spec: if it's not in the code, Forge doesn't do it. The repo is the audit.

---

## Supply-chain & repository security

The build pipeline and repository are part of the attack surface. Hardening here protects every user who installs a release.

### Dependencies
- NuGet vulnerability audit (`dotnet list package --vulnerable`) runs in CI; builds fail on known-vulnerable packages.
- Dependencies are pinned via a committed lockfile (`packages.lock.json`).
- Dependabot enabled for NuGet and GitHub Actions.

### Code integrity
- **Signed commits** required on `main` (branch protection).
- **Branch protection**: no direct pushes to `main`; PR review required.
- **CodeQL** static analysis on every PR.
- Release binaries are **code-signed** (Authenticode) so Windows SmartScreen and users can verify authenticity and detect tampering.
- Release artifacts publish **SHA-256 checksums**; ideally builds are reproducible.

### CI/secrets
- GitHub Actions use least-privilege `GITHUB_TOKEN` scopes.
- Signing certificates and any tokens live in encrypted repository secrets — never in the tree.
- No secret is ever committed; `gitleaks` (or equivalent) scans PRs.

### Releases
- Only tagged, signed releases are distributed.
- The installer (NSIS) ships only the two signed binaries; it does not download executable code at install time.

---

## Threat model (summary)

| Threat | Mitigation |
|---|---|
| Malicious community preset runs code as SYSTEM | Presets are declarative; only allowlisted actions exist (Principle 1) |
| Local non-admin process abuses the daemon via pipe | Pipe ACL + strict schema validation + allowlists (Principle 3) |
| Tampered/fake "Forge" binary tricks users | Code signing + published checksums |
| Compromised dependency | NuGet vulnerability audit + pinned lockfile + Dependabot |
| Stolen signing key in CI | Encrypted secrets, least-privilege tokens, key rotation |
| Silent harmful change | Every change logged, reversible, shown before apply (Principle 4) |
| Data exfiltration | No telemetry; no network except opt-in driver check (Principle 6) |

---

## For contributors

Before submitting a PR that touches the daemon or any system-modifying code:

1. Does it add any path where runtime input becomes a command/script/path? → **Reject the approach.** Map it to a hardcoded action instead.
2. Is every new system change logged to `fix_log` and reversible?
3. Does the UI show the user what will change before it happens?
4. Is new IPC strictly typed and validated?

If you answer "no" to 2–4 or "yes" to 1, the PR is not ready.

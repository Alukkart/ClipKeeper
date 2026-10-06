# Security policy

## Supported versions

Only the latest release gets fixes. ClipKeeper checks for updates once a day and updates itself from
**Settings → General → Update**, so please make sure you're on the latest version before reporting.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately instead:
[Security → Report a vulnerability](https://github.com/Alukkart/ClipKeeper/security/advisories/new).
English or Russian is fine.

Please include what an attacker could do, the steps to reproduce it, and the ClipKeeper and Windows versions.

What to expect: a first answer within a few days, and a fixed release as soon as the fix is ready and checked.
You'll be credited in the release notes unless you'd rather not be.

## What is in scope

ClipKeeper runs on your computer with your rights, so the most important things are:

- **Updates** — the downloaded exe must come only from this repository's releases and match `SHA256SUMS.txt`.
- **The OBS WebSocket password** — stored encrypted with Windows DPAPI in `settings.json`.
- **Changes to OBS files** — the "start with OBS" script and the scene collection backups.
- **Files it runs or deletes** — ffmpeg, OBS restarts, moving clips to the Recycle Bin, cleaning old source clips.
- **Data from the internet** — game covers and banners from Steam and Wikipedia, release info from GitHub.

Out of scope: antivirus or SmartScreen warnings about an unsigned exe (see the README), bugs in OBS or ffmpeg themselves,
and anything that needs an attacker who already controls your Windows account.

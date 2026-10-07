# Changelog

What changed in each version of ClipKeeper, for the people who use it. The newest is on top.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). The release workflow copies a version's section into its GitHub release.

## [Unreleased]

## [1.1.0] - 2026-10-07

The library for hundreds of clips: search, names, selecting and joining clips, going through new ones; sharing with
even loudness and as a GIF; a first-run setup that checks and fixes OBS.

### Added
- A search in the library by name, game or date ("bridge", "yesterday", "5 october"; Ctrl+F). From the library's home,
  or with "Search all folders" from a game, it looks through Sources, Ready and the collection at once.
- Filters above the clips — Favorites, Not trimmed, Older than a month — and the order: newest, oldest, longest, largest.
- Clips sorted by date fall into days: Today, Yesterday, 5 October…
- Name a clip right on its card: F2 or a double click on the title. The name goes into the file name, so Discord and
  Telegram show it too; a source keeps its game and time around it.
- Select several clips (Ctrl+click, Shift+click, Ctrl+A) and add them to favorites, copy them, move ready clips to the
  collection or send them to the Recycle Bin at once.
- "Go through new clips": the clips of the last two weeks play one by one, and a key keeps each, marks it to trim later
  or for the Recycle Bin. Nothing is deleted until the end.
- The first-run setup checks what OBS needs for clips — the WebSocket server, the replay buffer, a Save Replay key — and
  fixes what is missing, restarting OBS when it has to and showing each step of that; it ends with a test clip.
- The Recording page says when OBS later loses what clips need (the replay buffer turned off, no Save Replay key, a fix
  waiting for OBS to close), with a link to fix it.
- Join selected clips into one: in the order they were selected (drag a card or press its ‹ › to move it), each whole
  or only a part of it, back to back or with a short fade, at full quality or fitted for Discord, Nitro or Telegram,
  saved to Ready, next to the first clip or a folder you choose.
- Even loudness when sharing (off by default): the clip comes out at −14 LUFS, as YouTube plays it.
- A GIF target in the editor: an animation without sound, GIF or WebP, with its width and frame rate.

### Changed
- Sharing minutes of video into a small limit (Discord's 10 MB) now fits: the sound drops to 64 kbps and the picture to
  480p instead of going over the limit.
- Drop-down menus have rounded corners.
- The first-run setup takes the whole window instead of a small card over the app, with its steps listed on the
  left; a passed step opens again with a click.
- A day's header in the library ("Today · 3 clips · 7 min") counts the whole day, and a folder's in the search the
  whole folder, not only the clips loaded so far.
- After the library is first opened, ready and collection clips not read yet are read quietly in the background, so
  their games and the search come up at once later.

## [1.0.2] - 2026-10-07

### Added
- Built-in sounds: four for the alarm (Fall, Motif, Monitor, Bell) and four for "Clip saved" (Marimba, Glass,
  Shutter, Drop), picked with a click that also plays them.
- Your own alarm or "Clip saved" sound from any audio file — MP3, OGG, M4A, WAV… (the first 10 seconds are kept).
  Right click on it to delete it.

### Changed
- Settings are now 15 small sections in six groups (Basics, OBS, Monitoring, Clips, Library, Editor) instead of seven
  long tabs. On the settings page the sidebar turns into the list of sections, with the search on top; each section
  opens with a short description. The arrow on top or Esc goes back to the page you came from.
- The Favorites and All clips cards in the library show the frames of up to six latest clips, fanned out.
- If you never changed the alarm or "Clip saved" sound, it switches from the Windows sound to the new built-in one.
  A sound you chose stays.
- Release notes link a VirusTotal scan of `ClipKeeper.exe`.

### Fixed
- Editor: in Russian, the hint next to "Send in one click" was drawn over the caption.

## [1.0.1] - 2026-10-06

### Fixed
- Share → Telegram could make a file larger than Telegram accepts. It now keeps to 2 GB, Telegram's upload limit:
  the quality is as good as before, and only a range too long for 2 GB is squeezed under the limit.

### Changed
- `ClipKeeper.exe` names its author and license in the file properties.

## [1.0.0] - 2026-10-05

The first public release.

### Added
- Recording guard over obs-websocket: keeps OBS sources on the right audio devices and monitors after driver or
  audio software updates, restarts a crashed replay buffer and OBS itself, notices audio that never reaches OBS and
  a black or frozen picture, restores mixer tracks, watches dropped frames, disk space and graphics driver failures,
  backs up the OBS settings daily.
- An alarm over the game that doesn't steal focus, with a sound, and a green window when things fixed themselves.
- A check of every saved clip (length, tracks, dropped frames) and a "Clip saved" card with Open, Trim, Copy and
  Folder; the press of the OBS save key is confirmed at once.
- Sorting clips into game folders, with no OBS script.
- A library by game with covers from Steam and Wikipedia, favorites, Ready and Collection folders, cleanup of old clips.
- A trim editor with Premiere keys (remappable): cuts from the middle, per-track volume and solo, lossless,
  frame-exact and share (Discord 10 MB / Nitro / Telegram / your own size) saving, and a check of every saved file.
- Hotkeys in game for the last clip: trim, favorite, copy, show its card again.
- Statistics by month and game, and a month recap to share as a picture.
- First-run setup, starting together with OBS, updates from GitHub releases, English and Russian interface.

[Unreleased]: https://github.com/Alukkart/ClipKeeper/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Alukkart/ClipKeeper/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/Alukkart/ClipKeeper/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Alukkart/ClipKeeper/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Alukkart/ClipKeeper/releases/tag/v1.0.0

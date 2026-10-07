# Contributing to ClipKeeper

Thanks for wanting to help! Bug reports, ideas, fixes and screenshots of odd setups are all welcome.
Issues, discussions and pull requests can be in **English or Russian** · Можно писать по-русски.

## Ways to help

- **Report a bug.** In ClipKeeper: **Settings → General → Report a problem** — it opens the issue form with the versions
  filled in and shows the log in Explorer. Look the log over before attaching it: it has folder and device names.
- **Share an idea or ask a question** in [Discussions](https://github.com/Alukkart/ClipKeeper/discussions).
  Issues are for bugs and concrete feature requests.
- **Unusual setups help the most.** Voicemeeter, SteelSeries Sonar, several monitors, several audio tracks, AMD / Intel
  encoders, OBS portable — if something breaks there, a log is worth a lot.
- **Code.** Look for issues labeled [`good first issue`](https://github.com/Alukkart/ClipKeeper/labels/good%20first%20issue).
  For anything bigger than a small fix, open an issue first, so we agree on the idea before you spend time on it.

## Build

Nothing to install: ClipKeeper is built with the C# compiler that ships with Windows (.NET Framework 4.8).
No Visual Studio, no .NET SDK, no NuGet.

```bash
build.cmd
```

`build.cmd ClipKeeper.test.exe` builds under another name, so a running ClipKeeper is not touched.

To trim clips and see previews locally, put `ffmpeg.exe` and `ffprobe.exe` from a
[BtbN build](https://github.com/BtbN/FFmpeg-Builds/releases) (`ffmpeg-master-latest-win64-gpl.zip`, the `bin` folder)
into `ffmpeg\` next to the exe. They are not in git.

Any editor works. Rider and Visual Studio can open the folder, but there is no project file: `build.cmd` is the build.

## Test

```bash
ClipKeeper.test.exe --selftest
```

It checks the logic without OBS and builds every window off screen; the report goes to `selftest.txt`, and the exit code
is not 0 if something failed. The same runs in GitHub Actions on every pull request.

More checks for the editor and the screens (`--preview`, `--trimtest`, `--keytest`, `--playtest`) are listed in the
README, section **Build and release → Command line**.

If you fix a bug in the logic, add a `check(...)` for it to `SelfTest` in `src/Program.cs`.

## Where things are

| Area | Files |
|---|---|
| Start, command line, self-test | `Program.cs` |
| Tray icon, windows, UI thread | `App.cs`, `TrayMenu.cs`, `Wpf.cs` |
| obs-websocket client | `ObsClient.cs`, `ObsFind.cs`, `ObsScript.cs` |
| Recording guard (own thread) | `Guard.cs`, `GuardExtras.cs`, `GuardPerf.cs`, `GuardUi.cs`, `Matcher.cs`, `Audio.cs` |
| Saved clip check, alarm | `ClipCard.cs`, `Media.cs`, `AlertWindow.cs`, `Sound.cs`, `SaveKey.cs` |
| Library, sorting, statistics | `Library.cs`, `Gallery.cs`, `Sorter.cs`, `Cleanup.cs`, `ClipStats.cs`, `Charts.cs` |
| Main window | `MainWindow*.cs`, markup in `ui/MainWindow.xaml`, styles in `ui/Styles.xaml` |
| Trim editor | `TrimWindow*.cs`, `Trimmer.cs`, `Encoders.cs`, `Ffmpeg.cs`, `KeyMap.cs`, markup in `ui/TrimWindow.xaml` |
| Updates, problem report | `Updates.cs`, `Report.cs` |
| Interface language | `Lang.cs` |

Every file starts with a comment saying what it is for — read that first.

## Code style

- Match the code around you: the same naming, brace style and comment density.
- Comments, logs and test reports are in **English**. Comments say *why*, not *what*.
- Every string the user sees is in both languages: `L.T("Save", "Сохранить")` in code, `"Save¦Сохранить"` in XAML.
  Plurals go through `L.N` / `L.W`. If you don't speak Russian, write the English text and leave the Russian part
  in English — it will be translated in review.
- No new dependencies: ClipKeeper is one exe and a folder with ffmpeg, and it stays that way.
- The guard must never make a recording worse. Anything that changes OBS or deletes files is careful by default,
  makes a copy first and is written to the log.
- Never send anything anywhere. The only network calls are the update check against this repository's releases and
  cover art lookups (see **Privacy** in the README).

## Pull requests

1. Fork, make a branch, keep the change focused on one thing.
2. `build.cmd` and `--selftest` pass.
3. For a visible change, add a screenshot (both languages if you can, `--preview` renders them).
4. If the change affects what the user does or sees, update `README.md` and `README.ru.md`.
5. Add a line to `## [Unreleased]` in [`CHANGELOG.md`](CHANGELOG.md), in English, written for the user: what they get or
   what was broken — not which files changed. The release notes are made from it.

Releases are made from `v*` tags by GitHub Actions; contributors don't need to do anything for that.

## License

By contributing you agree that your contribution is released under the [MIT license](LICENSE) of this project.

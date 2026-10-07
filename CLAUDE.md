# ClipKeeper — notes for Claude

## Git: git flow, always

The work goes through branches; nothing is committed straight to `main` or `develop`.

| Branch | What it holds | Made from → merged into |
|---|---|---|
| `main` | Released code only. Every commit on it is a release with a `vX.Y.Z` tag | — |
| `develop` | What the next release will be | — |
| `feature/<short-name>` | One feature or change | `develop` → `develop` |
| `fix/<short-name>` | A bug fix that can wait for the next release | `develop` → `develop` |
| `release/X.Y.Z` | Getting a release ready: the CHANGELOG section, last fixes | `develop` → `main` and `develop` |
| `hotfix/X.Y.Z` | An urgent fix of the released version | `main` → `main` and `develop` |

- **Start of a task:** `git checkout develop`, then `git checkout -b feature/<name>` (or `fix/<name>`). A task the user
  asks for in one message is one branch, even if it takes several commits; unrelated tasks get their own branches.
- **Commits on the branch:** small, one meaning each, message style as in `git log` (`Area: what changed` + a body that
  says why). Commit when a piece works, without asking.
- **Finishing a branch:** the checks below pass → `git checkout develop` → `git merge --no-ff feature/<name>` →
  delete the branch. Tell the user it is merged into `develop`.
- **Release** (only when the user asks for one): `git checkout -b release/X.Y.Z develop`; in `CHANGELOG.md` rename
  `## [Unreleased]` to `## [X.Y.Z] - <date>`, add an empty `## [Unreleased]` above it and fix the links at the bottom;
  commit. Then `git checkout main && git merge --no-ff release/X.Y.Z && git tag vX.Y.Z`, `git checkout develop &&
  git merge --no-ff release/X.Y.Z`, delete the branch. Version numbers: features → minor (1.1.0), only fixes → patch.
- **Hotfix:** `git checkout -b hotfix/X.Y.Z main`, fix, CHANGELOG section `[X.Y.Z]`, merge into `main` (tag) and
  `develop` the same way.
- **Pushing is the user's call.** Never push, push a tag or open a pull request without being asked in this chat:
  a pushed `v*` tag publishes a GitHub release. When asked to push: the branches that changed and, after a release,
  the tag (`git push origin main develop vX.Y.Z`).
- If the working tree has someone else's uncommitted changes when a task starts, ask before moving them to a branch.

## Checks before merging into develop

1. `build.cmd ClipKeeper.test.exe` builds (cmd in this environment does not run programs from the current folder:
   call `.\build.cmd`).
2. `ClipKeeper.test.exe --selftest <report>` — `RESULT: ALL PASSED`. Add a check there for new logic.
3. Visible changes: look at them with `--preview <folder> --lang ru` (and `en`), the screens are PNGs.
4. Saving or encoding changes: `--trimtest` / `--mergetest` on a real clip from `D:\Sources`.
5. Something the user sees or does differently: a line in `CHANGELOG.md` → `## [Unreleased]` (English, written for
   the user) and both `README.md` and `README.ru.md`.

The running ClipKeeper.exe keeps one copy: the test build can't be clicked through while it runs — say so instead of
claiming a manual check.

## The code in short

- C# for .NET Framework 4.8 and WPF, built by the compiler that ships with Windows (`build.cmd`); no NuGet.
- Every string is in both languages: `L.T("English", "Русский")` in code, `"English¦Русский"` in XAML.
- `--showcase` / `showcase.cmd` render the README GIF; `--preview-settings` the settings screenshots.
- UI changes: show the user mockups first; they choose between variants.

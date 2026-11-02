# Privacy policy

_Last updated: 2 November 2026_

Fav Explorer does not collect, store, or transmit any personal information.

- **No network access, unless you turn on update checks.** The app has no
  telemetry, analytics, crash reporting or accounts.
- **Update checks are off by default.** If you turn on *Check for updates once a
  week* in Settings, or press *Check now*, Fav Explorer asks GitHub's public API
  which release of Fav Explorer is the latest
  (`api.github.com/repos/tashreefshareef/fav-explorer/releases/latest`). The
  request carries no identifier and nothing about you or your files; GitHub sees
  only what any web request shows, such as your IP address, under
  [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
  Nothing is downloaded or installed automatically. See `src/tray/UpdateCheck.cs`.
- **What it stores, locally only:**
  - A small `favexplorer` alternate data stream on each file you favourite,
    containing the time you favourited it.
  - In `%LOCALAPPDATA%\FavExplorer\`: the list of favourited file paths, a folder
    index used to draw the badge, your settings, and a diagnostic log.
- **None of this leaves your computer.** You can delete it at any time by
  running `uninstall.ps1 -RemoveAllTags -PurgeData`.
- **Keyboard shortcut.** Fav Explorer watches for its one shortcut
  (Ctrl+Shift+F by default) while File Explorer is in front. It does not record,
  store, or send any other keystrokes — see `src/tray/HotkeyHook.cs`.

If this ever changes, this policy will be updated before the release that
changes it. Questions: open an issue on the project's GitHub repository.

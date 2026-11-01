# Privacy policy

_Last updated: 1 October 2026_

Fav Explorer does not collect, store, or transmit any personal information.

- **No network access.** The app makes no network connections — no telemetry,
  analytics, crash reporting, update checks, or accounts.
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

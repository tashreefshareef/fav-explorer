# Fav Explorer

A heart you can stamp on any file from File Explorer with one keystroke, that stays
visible on the thumbnail — so favourites are obvious while you scroll past a hundred
photos, without opening any of them.

Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> on a selection in Explorer. A heart
appears in the corner of each thumbnail. Press it again to take it off.

It also works inside the Photos viewer: open a folder's photos, arrow through them,
and press the same shortcut on any that deserve it. The heart lands on that file and
is waiting on its thumbnail when you go back to Explorer.

![Explorer showing a folder of photos, four of them with a small red heart in the corner of the thumbnail](docs/images/explorer-hearts.png)

Double-click the tray icon for every favourite in one list, wherever it lives:

![The Fav Explorer favourites window listing four hearted photos with their folder and the time each was favourited](docs/images/favourites-list.png)

## Download

Get `FavExplorerSetup-<version>.exe` from
[Releases](https://github.com/tashreefshareef/fav-explorer/releases). Windows 10 1809
or later, x64. See the [code signing policy](#code-signing-policy) for how release
binaries are signed.

## How it works

Two pieces, deliberately kept apart.

**`HeartOverlay.dll`** is a native shell icon overlay handler — the same Windows
mechanism Dropbox and Google Drive use to badge files with sync status. Windows loads
it into `explorer.exe` and asks it, for every icon it draws, whether the heart applies.
Because it runs on threads that paint the desktop, it does no blocking work: it checks
a memory-mapped table of *folders that contain favourites* and rejects almost every
file with a couple of memory reads and no disk access at all.

**`FavExplorer.exe`** is a tray app that owns everything else — the keyboard shortcut,
reading the Explorer selection, writing the tag, and maintaining that table. It is a
normal unelevated process, so when it goes wrong it takes nothing else with it.

In Photos there is no selection to read, so the file on screen is worked out from two
things the OS exposes about any window: the viewer's command line names the file it
was opened on (each viewer window is its own process), and its title is the bare name
of whatever it is showing now. Next/Previous never leave the opening folder, so the
folder from one plus the name from the other is the path — checked against the disk
before it is trusted. Newer Photos builds often hand the file to an idle spare process
whose command line names nothing; then the folder comes from the open Explorer windows
instead, nearest the viewer first, and then the Desktop. `SelfTest photos <hwnd>` reports each of those steps for a
window, for when that inference stops matching what Photos does.

The tag itself is an **NTFS alternate data stream** named `favexplorer`, attached to the
file. Nothing inside the photo is touched — not a pixel, not the EXIF. Because the
stream is part of the file, renaming or moving it within NTFS carries the heart along.

Timestamps take one extra step. NTFS stores them per *file*, not per stream, so writing
the tag restamps `LastWriteTime` and sets the archive bit — which reorders folders
sorted by Date modified, and makes every sync client on the machine think the file
changed and re-upload it. Hearting a file is not a modification to it, so `AdsTag`
captures the timestamps and attributes before writing and puts them back afterwards,
once the stream handle is closed.

```
Ctrl+Shift+F ─→ FavExplorer.exe ──writes──→ photo.jpg:favexplorer   (the truth)
                     │                            ↑
                     │                            │ probed only when the
                     ├──publishes──→ dirs.bin ────┘ folder is a known hit
                     │                  ↑
                     └──SHChangeNotify──┤ read by
                            ↓           │
                        explorer.exe ─→ HeartOverlay.dll
```

`docs/index-format.md` specifies the shared table byte for byte. The two halves of the
app must hash paths identically; `tools/probe` exists to prove they still do.

## Building

Needs the MSVC C++ toolset, the Windows SDK, CMake, and the .NET 10 SDK. Inno Setup 6
is optional; with it, the build also produces the installer.

```powershell
.\build.ps1
```

Everything lands in `dist\`.

## Installing

```powershell
.\dist\install.ps1
```

It asks for administrator rights once. That is needed for exactly one thing: Windows
reads the list of overlay handlers from `HKEY_LOCAL_MACHINE` and nowhere else, so there
is no such thing as a per-user install of an icon overlay. Everything afterwards runs
unelevated.

The installer prints where Fav Explorer ranks among the overlay handlers on your machine
and restarts Explorer, which has to happen for the new handler to be noticed.

To remove it:

```powershell
.\dist\uninstall.ps1                 # leaves the tags on your files
.\dist\uninstall.ps1 -RemoveAllTags  # strips them first
```

## Things worth knowing

**Windows only honours about eleven icon overlay handlers.** There are fifteen slots
system-wide, the shell reserves several, and they are handed out by sorting the
registry key names. This is why Fav Explorer's key is called `   FavExplorer` with three
leading spaces — it is a real technique, and Google Drive beats it with four. If
Fav Explorer ever falls outside the limit the heart silently stops being drawn, so
Settings shows the live ranking and the full list of competitors rather than leaving
you to guess.

**The badge size is a transparent margin, not a setting Windows offers.** Windows
picks whichever frame is closest to the overlay size it wants, pins it bottom-left,
and gives the handler no say in either. The only lever is how much of each frame you
leave transparent.

There are three ways to spend that margin, and two of them are wrong:

- **A constant fraction of the frame.** Google Drive does this — a flat ~47% — and
  it is heavy-handed, planting a badge across half of every large thumbnail.
- **A constant pixel size.** Tried it; 10 px is fine on a 16 px Details row and
  effectively invisible at 4% of a 256 px thumbnail.
- **Sub-linear growth.** What OneDrive does, and what `tools/make-icons.ps1` now
  copies outright.

The numbers below are measured out of OneDrive's own `FileSyncShell64.dll` with
`PrivateExtractIcons`, not guessed. They track roughly `frame^0.7`:

| frame | 16 | 24 | 32 | 48 | 64 | 96 | 128 | 256 |
|-------|----|----|----|----|----|----|-----|-----|
| badge |  9 | 13 | 15 | 16 | 16 | 24 |  25 |  48 |

Legible on a Details row and on a jumbo thumbnail, without swamping either. To make
it louder or quieter than OneDrive, scale the whole curve:

```powershell
$env:FAVEXPLORER_BADGE_BOOST = '1.25'   # 1.0 matches OneDrive exactly
.\build.ps1
.\dist\install.ps1
```

Whatever you change, verify it against the **installed** DLL rather than the asset —
`PrivateExtractIcons` on `C:\Program Files\Fav Explorer\HeartOverlay.dll` reports what
Explorer will actually draw. Checking the source `.ico` proves nothing about what
got installed.

**Hearts keep drawing when the tray app is not running.** The overlay handler reads
only two things, both on disk: the folder index and the stream on the file. Neither
needs a live process. What stops while Fav Explorer is closed is *changing* tags, and
keeping the index current as favourites move — so a folder that gains its first
favourite after that will not badge until the app runs again.

**The index can go stale, and it used to fail silently.** If the folder index loses
track of a folder, the handler rejects everything in it with no disk access — which is
exactly what it is supposed to do, and looks identical to "no favourites here". Three
things now guard it: the write is read back through a separate handle and retried on a
remap if it did not land, a five-minute watchdog compares the on-disk folder count with
what the favourites list implies, and **Settings shows the verdict in plain words**
with a Repair button. `%LOCALAPPDATA%\FavExplorer\favexplorer.log` records all of it.

**Renames and moves between known folders now look after themselves.** A
`FileSystemWatcher` sits on each folder that holds favourites — a handful, never
recursive — so renaming a photo updates its entry, and moving a tagged photo *into*
one of those folders picks it up automatically, stream and all.

**Moving a file into a folder Fav Explorer has never seen still needs a nudge.** The
watcher only covers folders that already hold favourites, so a photo moved somewhere
entirely new keeps its tag but gets no badge until *Rescan a folder…* in the tray menu
finds it. Watching the whole filesystem to close that gap would cost far more than it
is worth.

**The tag doesn't survive leaving NTFS.** ZIP, email attachments, most uploads, and
FAT32 or exFAT drives all drop alternate data streams. This is the cost of not
modifying the photo; the trade was made deliberately.

**`Ctrl+D` is not the shortcut, on purpose.** Browsers taught everyone that Ctrl+D
means "bookmark", but in File Explorer it deletes the selection. Settings refuses to
bind it, along with the other Ctrl+letter combinations Explorer already claims.

## Roadmap

Planned:

- [Signed releases through SignPath](https://github.com/tashreefshareef/fav-explorer/issues/1), so SmartScreen stops warning
- [Install with winget and Scoop](https://github.com/tashreefshareef/fav-explorer/issues/2)
- [Search and filter in the favourites list](https://github.com/tashreefshareef/fav-explorer/issues/3)
- [Copy or export all favourites to a folder](https://github.com/tashreefshareef/fav-explorer/issues/4)
- [Keep hearts when files are copied to drives that can't store them](https://github.com/tashreefshareef/fav-explorer/issues/5)

Under consideration:

- [Shortcut support in other photo viewers](https://github.com/tashreefshareef/fav-explorer/issues/6) such as IrfanView and ImageGlass

Give an issue a 👍 to vote for it, or
[open a new one](https://github.com/tashreefshareef/fav-explorer/issues/new) with an idea.

## Layout

```
src/overlay/     the C++ overlay handler that loads into explorer.exe
src/tray/        the C# tray app
tools/probe/     asks the handler's own code what it would answer, from a console
tools/selftest/  drives the C# half so probe can be checked against it
tools/           icon generation
docs/            the shared index format
```

## Checking it still works

`tools/probe` compiles the real `index.cpp` — not a copy — and reports the verdict the
overlay handler would give for any path, along with which stage produced it:

```
> probe.exe C:\Pictures\iceland.jpg
  parent hash        0x42FB6B9AC2A1925B
  folder in index    yes - will probe the stream
  stream present     yes
  VERDICT            S_OK (heart drawn)
```

To confirm the C++ and C# hashes still agree, which is the one failure that would be
otherwise invisible:

```powershell
.\tools\selftest\bin\Release\net10.0\SelfTest.exe hash C:\Pictures
.\build\probe\Release\probe.exe --hash C:\Pictures
```

The two hex values must match.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Release binaries are built from this repository by the
[`build` workflow](.github/workflows/build.yml) on GitHub Actions, and each release is
approved for signing by hand. Nothing built on a developer machine is signed.

Team roles:

- Committers and reviewers: [MOHAMMED Tashreef](https://github.com/tashreefshareef)
- Approvers: [MOHAMMED Tashreef](https://github.com/tashreefshareef)

Privacy: Fav Explorer makes no network connections and transfers no information to any
other system. See [PRIVACY.md](PRIVACY.md).

# The shared folder-hint index

`HeartOverlay.dll` runs inside `explorer.exe` and is asked "is this file a favourite?"
for **every icon Explorer draws**. It cannot afford to touch the disk for each one.

So the tray app maintains a small memory-mapped set of *folders that contain at least
one favourite*. The DLL's hot path is:

1. Take the parent folder of the item.
2. Hash it, look it up in the set. **Miss → `S_FALSE` immediately, zero I/O.**
   This is the answer for essentially every file on the machine.
3. Hit → probe the NTFS alternate data stream `<path>:favexplorer` to get the real answer.

The set is a hint, never the truth. A false positive costs one file open. The truth is
always the alternate data stream on the file itself, which is why renaming or moving a
photo inside NTFS keeps its heart.

## File

`%LOCALAPPDATA%\FavExplorer\dirs.bin` — fixed size, 524,352 bytes, never grows or shrinks.

Fixed capacity means the DLL maps it once at load and never has to remap, which removes
the only real source of complexity on the Explorer side.

## Layout

All values little-endian.

```
offset  size    field
------  ------  ---------------------------------------------------------------
0       4       magic      = 0x58444D48  ('HMDX')
4       4       version    = 1
8       8       generation   see "Torn reads" below
16      4       capacity   = 65536 slots
20      4       count        occupied slots (informational)
24      40      reserved     zero
------  ------  ---------------------------------------------------------------
64      8*65536 slots        uint64 hashes; 0 means empty
```

Header is padded to 64 bytes so the slot array starts on a cache line.

## Hashing

FNV-1a, 64-bit, over the **UTF-16 code units** of the folder path, two bytes per unit,
low byte first.

The path is normalised first:

- full absolute path, no trailing backslash (except a drive root, which keeps it: `C:\`)
- **ASCII-only lowercasing**: `A`–`Z` map to `a`–`z`, every other code unit is left alone

The ASCII-only rule matters. `towlower` in C++ and `ToLowerInvariant` in C# disagree on
some non-ASCII characters, and a hash that disagrees across the two halves of the app is
a bug that only shows up on someone else's photo folder. Restricting the fold to ASCII
makes both sides trivially identical.

```
h = 0xCBF29CE484222325
for each utf-16 unit u in normalised path:
    h ^= (u & 0xFF);  h *= 0x100000001B3
    h ^= (u >> 8);    h *= 0x100000001B3
if h == 0: h = 1        // 0 is reserved for "empty slot"
```

## Probing

Open addressing, linear probe, wrapping at `capacity`:

```
i = h & (capacity - 1)
while slots[i] != 0 and slots[i] != h:
    i = (i + 1) & (capacity - 1)
```

65,536 slots against a realistic few hundred folders keeps the load factor near zero, so
lookups are one cache miss. The tray app refuses to exceed a 0.5 load factor.

Deletion cannot just zero a slot — that would break probe chains for other keys. The tray
app rewrites the whole table instead, which at 512 KB is instant and happens only when a
folder loses its last favourite.

## Torn reads

The tray app writes in place while the DLL may be reading. `generation` is a seqlock:

- **Writer:** make `generation` odd, write slots, then set it to the next even number.
- **Reader:** read `generation` before and after the lookup. If either read is odd, or the
  two differ, the table was mid-write — fall back to probing the stream.

Falling back is always safe: the worst case is one unnecessary `CreateFile` on a path that
turns out not to have the stream. The DLL never blocks and never returns a wrong answer.

The DLL also uses `generation` to invalidate its small result cache.

## The stream itself

Stream name `favexplorer`, so the full path is `C:\photos\iceland.jpg:favexplorer`.

Contents are a single UTF-8 line, for anyone who goes looking with
`more < "file.jpg:favexplorer"`:

```
FE1 <unix-millis-when-favourited>\n
```

Files hearted before the app was renamed carry a `heartmark` stream with an `HM1` line
instead. The app moves those onto `favexplorer` on first run (for everything in the old
list) and during *Rescan a folder…* (for anything moved since), keeping the line as is.

Nothing reads the timestamp for correctness — presence of the stream *is* the flag. It is
there so the favourites list can sort by when you hearted something even if the cache is
lost.

### Consequences of this choice

- Survives rename and move within an NTFS volume. The stream is part of the file.
- Costs zero bytes of the photo itself. No pixel and no EXIF is touched, and the file's
  timestamps and archive bit are captured and restored around the write — see `AdsTag`.
- **Stripped** by ZIP, email attachment, most uploads, and any copy to FAT32/exFAT.
- Requires NTFS or ReFS. The tray app checks the filesystem before tagging and says so
  plainly if the volume can't hold the tag.

## Verifying the write

The generation counter is written, then read back **through a separate file handle**
before the write is believed. Reading back through the same mapping we just wrote
would prove nothing — the failure this guards against is precisely the one where our
mapping and the file have parted company, and the DLL in Explorer reads the file, not
our mapping.

If the read-back disagrees, the mapping is torn down, rebuilt, and the write retried
once. Both outcomes are recorded in `%LOCALAPPDATA%\FavExplorer\favexplorer.log`.

This is not hypothetical. The index went stale in the field: the app carried on
saving `favorites.tsv` while the index sat on a folder set from hours earlier. Nothing
reported a problem, because a folder the index has forgotten is answered with a
perfectly legitimate "no favourites here" — the same answer it gives for the millions
of folders that genuinely hold nothing. A silent wrong answer that is indistinguishable
from the correct one is the worst failure shape this design can produce, so the write
is now proved rather than assumed.

A five-minute watchdog also compares the on-disk folder count against what the
favourites list implies, and republishes on a mismatch. Note that the index file is
held with an exclusive write lock while Fav Explorer runs, so nothing outside the app can
corrupt it — the watchdog exists purely to bound the damage from the app's own writes
failing.

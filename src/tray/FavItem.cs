using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FavExplorer;

// Smallest to largest, which is also the order Ctrl+mouse wheel steps through.
internal enum FavView { Details, Tiles, Medium, Large, ExtraLarge }

internal enum FavSort { Added, Name, Folder, Size }

internal enum FavGroup { None, Date, Folder }

/// <summary>One favourite as the favourites window shows it.</summary>
internal sealed class FavItem : INotifyPropertyChanged
{
    public FavItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
    }

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }

    private DateTimeOffset _added;
    public DateTimeOffset Added
    {
        get => _added;
        set { if (Set(ref _added, value)) Raise(nameof(AddedText)); }
    }
    public string AddedText => _added.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    private long? _size;
    public long? Size
    {
        get => _size;
        set { if (Set(ref _size, value)) Raise(nameof(SizeText)); }
    }
    // As Explorer shows it: whole kilobytes rounded up, so a small file never reads 0 KB.
    public string SizeText => _size is long n ? $"{(n + 1023) / 1024:N0} KB" : "";

    // A file that has gone missing is worth showing rather than hiding — otherwise it
    // just silently disappears and you wonder if you imagined it.
    private bool _missing;
    public bool Missing
    {
        get => _missing;
        set => Set(ref _missing, value);
    }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        private set { if (Set(ref _thumb, value)) Raise(nameof(HasThumb)); }
    }
    public bool HasThumb => _thumb is not null;

    /// <summary>The size the current thumbnail was asked for; 0 when there is none.</summary>
    public int ThumbPixels { get; private set; }
    public long ThumbBytes { get; private set; }

    /// <summary>When the item was last near the screen, for choosing what to evict.</summary>
    public long LastSeen { get; set; }

    // Read by the group description; worked out again whenever grouping changes.
    public string GroupName { get; set; } = "";
    public int GroupRank { get; set; }

    public void SetThumb(BitmapSource? bmp, int px)
    {
        Thumb = bmp;
        ThumbPixels = px;
        ThumbBytes = bmp is null ? 0 : (long)bmp.PixelWidth * bmp.PixelHeight * 4;
    }

    public void DropThumb() => SetThumb(null, 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

internal static class FavGrouping
{
    public static void Assign(FavItem item, FavGroup group, DateTime now)
    {
        switch (group)
        {
            case FavGroup.Date:
                (item.GroupRank, item.GroupName) = DateBucket(item.Added.LocalDateTime, now);
                break;
            case FavGroup.Folder:
                item.GroupRank = 0;
                item.GroupName = item.Folder;
                break;
            default:
                item.GroupRank = 0;
                item.GroupName = "";
                break;
        }
    }

    /// <summary>
    /// Explorer's own date groups, except that anything before this year is grouped
    /// by year rather than lumped into "A long time ago".
    /// </summary>
    public static (int Rank, string Name) DateBucket(DateTime when, DateTime now)
    {
        DateTime today = now.Date;
        DayOfWeek first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        DateTime week = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));
        DateTime month = new(today.Year, today.Month, 1);

        if (when >= today) return (0, "Today");
        if (when >= today.AddDays(-1)) return (1, "Yesterday");
        if (when >= week) return (2, "Earlier this week");
        if (when >= week.AddDays(-7)) return (3, "Last week");
        if (when >= month) return (4, "Earlier this month");
        if (when >= month.AddMonths(-1)) return (5, "Last month");
        if (when.Year == today.Year) return (6, "Earlier this year");
        return (7 + today.Year - when.Year, when.Year.ToString());
    }
}

/// <summary>
/// Groups first, then the chosen order inside each group. Sorting by the field the
/// groups come from flips the groups too, so oldest-first starts with the oldest year.
/// </summary>
internal sealed class FavComparer(FavSort sort, bool descending, FavGroup group) : IComparer
{
    public int Compare(object? x, object? y)
    {
        var a = (FavItem)x!;
        var b = (FavItem)y!;

        int c = group switch
        {
            FavGroup.Date => a.GroupRank.CompareTo(b.GroupRank) * (sort == FavSort.Added && !descending ? -1 : 1),
            FavGroup.Folder => Native.StrCmpLogicalW(a.Folder, b.Folder) * (sort == FavSort.Folder && descending ? -1 : 1),
            _ => 0,
        };
        if (c != 0) return c;

        c = sort switch
        {
            FavSort.Name => Native.StrCmpLogicalW(a.Name, b.Name),
            FavSort.Folder => Native.StrCmpLogicalW(a.Folder, b.Folder),
            FavSort.Size => (a.Size ?? -1).CompareTo(b.Size ?? -1),
            _ => a.Added.CompareTo(b.Added),
        };
        if (descending) c = -c;
        return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
    }
}

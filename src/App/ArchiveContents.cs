using System.Windows.Media;
using SevenZipper.Core;

namespace SevenZipper.App;

internal sealed class ArchiveContents
{
    private readonly ArchiveRow[] _rows;
    private readonly ILookup<string, ArchiveRow> _folders;

    public ArchiveContents(IReadOnlyList<ArchiveEntry> entries)
    {
        var directories = new Dictionary<string, ArchiveRow>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveRow>();
        ulong totalSize = 0, totalPacked = 0;
        foreach (var entry in entries)
        {
            var path = entry.Path.Replace('\\', '/').TrimEnd('/');
            if (entry.IsDirectory) directories[path] = new ArchiveRow(path, entry, true);
            else
            {
                files.Add(new ArchiveRow(path, entry, false));
                totalSize += entry.Size;
                totalPacked += entry.PackedSize;
            }
            for (var parent = Parent(path); parent.Length > 0; parent = Parent(parent))
                if (!directories.TryAdd(parent, new ArchiveRow(parent, null, true))) break;
        }
        _rows = directories.Values.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(files.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        _folders = _rows.ToLookup(row => Parent(row.Path), StringComparer.OrdinalIgnoreCase);
        TotalCount = entries.Count;
        FileCount = files.Count;
        TotalSize = totalSize;
        TotalPackedSize = totalPacked;
    }

    public int TotalCount { get; }
    public int FileCount { get; }
    public ulong TotalSize { get; }
    public ulong TotalPackedSize { get; }

    public ArchiveRow[] Browse(string folder, string search) => search.Length == 0
        ? _folders[folder].ToArray()
        : _rows.Where(row => row.Path.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();

    public uint[] Selection(IEnumerable<ArchiveRow> rows)
    {
        var indices = new HashSet<uint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ArchiveRow>(rows);
        while (pending.TryPop(out var row))
        {
            if (row.Entry != null) indices.Add(row.Entry.Index);
            if (!row.IsDirectory || !seen.Add(row.Path)) continue;
            foreach (var child in _folders[row.Path]) pending.Push(child);
        }
        return indices.Order().ToArray();
    }

    public ulong SelectionSize(IEnumerable<ArchiveRow> rows)
    {
        ulong total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ArchiveRow>(rows);
        while (pending.TryPop(out var row))
        {
            if (!seen.Add(row.Path)) continue;
            if (!row.IsDirectory) total += row.Size;
            else foreach (var child in _folders[row.Path]) pending.Push(child);
        }
        return total;
    }

    public static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }
}

internal sealed record ArchiveRow(string Path, ArchiveEntry? Entry, bool IsDirectory)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string AccessibleName => $"{Path}, {(IsDirectory ? "폴더" : Entry?.IsEncrypted == true ? "암호화된 파일" : "파일")}";
    public ulong Size => Entry?.Size ?? 0;
    public string SizeText => IsDirectory ? "" : FormatBytes(Size);
    public string Method => (Entry?.IsEncrypted == true ? "암호화 · " : "") + (Entry?.Method ?? "");
    public DateTimeOffset? Modified => Entry?.Modified;
    public string ModifiedText => Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Glyph => IsDirectory ? "\uE8B7" : Entry?.IsEncrypted == true ? "\uE72E" : "\uE8A5";
    public Brush GlyphBrush => IsDirectory ? Brushes.Khaki : Brushes.LightSteelBlue;

    public static string FormatBytes(ulong size)
    {
        if (size < 1024) return $"{size:N0} B";
        if (size < 1024UL * 1024) return $"{size / 1024d:0.#} KiB";
        if (size < 1024UL * 1024 * 1024) return $"{size / (1024d * 1024):0.#} MiB";
        return $"{size / (1024d * 1024 * 1024):0.##} GiB";
    }
}

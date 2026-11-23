using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SevenZipper.Core;

public static partial class PathSafety
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    public static string ResolveExistingFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new ArchiveException("파일의 실제 경로를 확인하지 못했습니다.");
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetVolumeInformationW(string root, IntPtr volumeName, uint volumeNameLength,
        IntPtr serialNumber, IntPtr maximumComponentLength, out uint fileSystemFlags, IntPtr fileSystemName, uint fileSystemNameLength);
    // FAT, exFAT and some network shares have no alternate data streams. An unknown volume counts as
    // capable, so a real stream failure there still surfaces.
    public static bool SupportsNamedStreams(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return true;
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        const uint namedStreams = 0x00040000;
        return !GetVolumeInformationW(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out uint flags, IntPtr.Zero, 0)
            || (flags & namedStreams) != 0;
    }
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    public static bool IsDeviceName(string name) => !string.IsNullOrEmpty(name) && DeviceName().IsMatch(name);

    public static string ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\'))
            throw new ArchiveException($"올바르지 않은 압축 항목 경로: {name}");
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')
                || part.Any(c => c < 32) || DeviceName().IsMatch(part))
                throw new ArchiveException($"안전하지 않은 압축 항목 경로: {name}");
        }
        return normalized;
    }

    public static string SafeCombine(string root, string relative)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, ValidateEntryName(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveException("해제 경로가 대상 폴더를 벗어납니다.");
        RejectReparseAncestors(path);
        return path;
    }

    public static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArchiveException($"링크 또는 재분석 지점은 지원하지 않습니다: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string UniquePath(string desired, bool directory = false)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var parent = Path.GetDirectoryName(desired)!;
        var extension = directory ? "" : Path.GetExtension(desired);
        var stem = directory ? Path.GetFileName(desired) : Path.GetFileNameWithoutExtension(desired);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(parent, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    internal static string CreateStage(string parent)
    {
        RejectReparseAncestors(parent);
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".7zipper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        return stage;
    }

    internal static void DeleteStage(string stage)
    {
        if (!Path.GetFileName(stage).StartsWith(".7zipper-", StringComparison.Ordinal)) return;
        DeleteTreeWithoutLinks(stage);
    }

    public static void DeleteOwnedDirectory(string directory, string root)
    {
        var full = Path.GetFullPath(directory);
        var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), expected, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new ArchiveException("임시 폴더의 소유권을 확인할 수 없습니다.");
        DeleteTreeWithoutLinks(full);
    }

    private static void DeleteTreeWithoutLinks(string directory)
    {
        if (!Directory.Exists(directory)) return;
        RejectReparseAncestors(directory);
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArchiveException("임시 폴더에 링크가 있습니다.");
            if ((attributes & FileAttributes.Directory) != 0) DeleteTreeWithoutLinks(item);
            else { File.SetAttributes(item, FileAttributes.Normal); File.Delete(item); }
        }
        var dirAttributes = File.GetAttributes(directory);
        if ((dirAttributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(directory, dirAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(directory);
    }
}

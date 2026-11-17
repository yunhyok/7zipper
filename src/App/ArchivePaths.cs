using System.IO;

namespace SevenZipper.App;

internal sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

internal static class ArchivePaths
{
    public const string OpenFilter =
        "압축 파일 (*.7z, *.zip, *.001, *.z01)|*.7z;*.zip;*.001;*.z01|모든 파일 (*.*)|*.*";

    public static string AbsolutePath(string text, string label)
    {
        var path = text.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"{label}의 드라이브 또는 공유 경로부터 입력하세요.");
        return Path.GetFullPath(path);
    }

    public static string SuggestOutput(IReadOnlyList<string> inputs, string extension, bool perFolder = false)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputs[0]));
        var parent = Path.GetDirectoryName(first) ?? first;
        if (perFolder) return parent;
        var name = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrWhiteSpace(name)) name = "압축 파일";
        var output = Path.Combine(parent, name + extension);
        for (var number = 2; File.Exists(output) || Directory.Exists(output); number++)
            output = Path.Combine(parent, $"{name} ({number}){extension}");
        return output;
    }
}

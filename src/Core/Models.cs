namespace SevenZipper.Core;

public enum ArchiveFormat { SevenZip, Zip, ZipZstandard }
public enum ExtractionMode { Here, Folder, Smart }
public enum CollisionPolicy { Ask, Skip, Rename, Overwrite }

public sealed record ArchiveEntry(uint Index, string Path, ulong Size, ulong PackedSize,
    bool IsDirectory, bool IsEncrypted, string Method, DateTimeOffset? Modified);
public sealed record ArchiveProgress(string Stage, ulong CompletedBytes, ulong TotalBytes, string? CurrentFile, string? JobId = null,
    double? OverallPercent = null, int CompletedJobs = 0, int TotalJobs = 0);
public sealed record ShellRequest(string Operation, string[] Paths);

public sealed class CompressionOptions
{
    public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;
    public int Level { get; set; } = 5;
    public int Threads { get; set; } = Environment.ProcessorCount;
    public ulong MemoryLimitBytes { get; set; }
    public ulong VolumeBytes { get; set; }
    public string? Password { get; set; }
    public bool EncryptHeaders { get; set; }
    public bool PerFolder { get; set; }
    public string OutputPath { get; set; } = "";
}

public sealed class ExtractionOptions
{
    public string DestinationDirectory { get; set; } = "";
    public ExtractionMode Mode { get; set; } = ExtractionMode.Smart;
    public string? Password { get; set; }
    public uint[]? SelectedIndices { get; set; }
    public CollisionPolicy Collision { get; set; } = CollisionPolicy.Ask;
    public int Threads { get; set; } = Environment.ProcessorCount;
    public ulong MemoryLimitBytes { get; set; }
}

public sealed class ConversionOptions
{
    public int Level { get; set; } = 9;
    public int Threads { get; set; } = Environment.ProcessorCount;
    public ulong MemoryLimitBytes { get; set; }
    public ulong VolumeBytes { get; set; }
    public string? OutputDirectory { get; set; }
    public string? SourcePassword { get; set; }
    public string? OutputPassword { get; set; }
    public bool EncryptHeaders { get; set; }
    public bool DeleteSourceAfterSuccess { get; set; }
}

public sealed class ArchiveException(string message, int status = 2) : Exception(message)
{
    public int Status { get; } = status;
    public bool IsPasswordError => Status == 3;
}

public sealed class DragPreparation(string directory, IReadOnlyList<string> paths) : IDisposable
{
    public string Directory { get; } = directory;
    public IReadOnlyList<string> Paths { get; } = paths;
    public void Dispose() => PathSafety.DeleteOwnedDirectory(Directory, AppPaths.DragRoot);
}

public static class AppPaths
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7Zipper");
    public static string DragRoot => Path.Combine(DataRoot, "Drag");
}

internal sealed record NativeInput(string Source, string Name, bool IsDirectory);
internal sealed class WorkerRequest
{
    public string Operation { get; set; } = "list";
    public string Archive { get; set; } = "";
    public string? Destination { get; set; }
    public string? Password { get; set; }
    public uint[]? Indices { get; set; }
    public List<NativeInput>? Inputs { get; set; }
    public CompressionOptions? Create { get; set; }
    public int Threads { get; set; } = Environment.ProcessorCount;
    public ulong MemoryLimitBytes { get; set; }
}
internal sealed class WorkerMessage
{
    public string Type { get; set; } = "progress";
    public ArchiveEntry? Entry { get; set; }
    public ArchiveProgress? Progress { get; set; }
    public int Status { get; set; }
    public string? Error { get; set; }
}

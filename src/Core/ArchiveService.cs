using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SevenZipper.Core;

public sealed partial class ArchiveService
{
    private static readonly SemaphoreSlim Queue = new(1, 1);
    public Func<string, Task<CollisionPolicy>>? ResolveCollisionAsync { get; set; }
    public Func<string, bool, Task<string?>>? ResolvePasswordAsync { get; set; }
    private readonly SemaphoreSlim passwordGate = new(1, 1);

    [GeneratedRegex(@"\.z\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipVolume();
    [GeneratedRegex(@"\.7z\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipVolume();

    public static bool IsArchivePath(string path) => path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || ZipVolume().IsMatch(path) || SevenZipVolume().IsMatch(path);

    public static string NormalizeArchivePath(string path)
    {
        path = Path.GetFullPath(path);
        if (ZipVolume().IsMatch(path)) path = Path.ChangeExtension(path, ".zip");
        else if (SevenZipVolume().IsMatch(path)) path = path[..(path.LastIndexOf('.') + 1)] + "001";
        if (!File.Exists(path)) throw new ArchiveException($"압축파일 또는 시작 볼륨을 찾을 수 없습니다: {path}");
        PathSafety.RejectReparseAncestors(path);
        return PathSafety.ResolveExistingFile(path);
    }

    private static string ArchiveStem(string path)
    {
        var name = Path.GetFileName(path);
        if (SevenZipVolume().IsMatch(name)) name = name[..name.LastIndexOf('.')];
        return Path.GetFileNameWithoutExtension(name);
    }

    public async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string path, string? password = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        try { return await ListInternalAsync(NormalizeArchivePath(path), password, MemoryBudget.Resolve(0), cancellation); }
        finally { Queue.Release(); }
    }

    private static Task<List<ArchiveEntry>> ListInternalAsync(string path, string? password, ulong memory, CancellationToken cancellation)
        => WorkerHost.ExecuteAsync(new() { Operation = "list", Archive = path, Password = password,
            MemoryLimitBytes = memory, Threads = 1 }, null, cancellation);

    public async Task<IReadOnlyList<string>> CreateAsync(IReadOnlyList<string> inputs, CompressionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        try
        {
            var paths = NormalizeInputs(inputs, !options.PerFolder);
            ValidateOptions(options);
            var memory = MemoryBudget.Resolve(options.MemoryLimitBytes);
            int threads = Math.Clamp(options.Threads, 1, Environment.ProcessorCount);
            if (!options.PerFolder) return await CreateOneAsync(paths, options, memory, threads, progress, cancellation);
            if (paths.Any(x => !Directory.Exists(x))) throw new ArchiveException("폴더별 압축에는 폴더만 선택하세요.");
            if (string.IsNullOrWhiteSpace(options.OutputPath)) throw new ArchiveException("출력 폴더를 선택하세요.");
            var outputDirectory = Path.GetFullPath(options.OutputPath);
            if (paths.Any(path => outputDirectory.Equals(path, StringComparison.OrdinalIgnoreCase)
                || outputDirectory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new ArchiveException("폴더별 압축의 출력 위치는 원본 폴더 바깥으로 지정하세요.");
            PathSafety.RejectReparseAncestors(options.OutputPath);
            Directory.CreateDirectory(options.OutputPath);
            var outputs = new ConcurrentBag<string>();
            int parallel = Math.Min(paths.Count, Math.Min(4, Math.Min(threads, (int)Math.Min(int.MaxValue, memory / (256UL * 1024 * 1024)))));
            parallel = Math.Max(1, parallel);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = paths.Select(path =>
            {
                var output = PathSafety.UniquePath(Path.Combine(options.OutputPath, Path.GetFileName(path) + Extension(options.Format)));
                while (!reserved.Add(output)) output = NextReserved(output, reserved);
                return (path, output);
            }).ToArray();
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellation }, async (item, token) =>
            {
                var local = CopyOptions(options, item.output);
                var completed = await CreateOneAsync([item.path], local, memory / (ulong)parallel, Math.Max(1, threads / parallel), progress, token);
                foreach (var output in completed) outputs.Add(output);
            });
            return outputs.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Queue.Release(); }
    }

    private static string NextReserved(string output, HashSet<string> reserved)
    {
        string parent = Path.GetDirectoryName(output)!, stem = Path.GetFileNameWithoutExtension(output), ext = Path.GetExtension(output);
        for (int n = 2; ; n++) { var candidate = Path.Combine(parent, $"{stem} ({n}){ext}"); if (!reserved.Contains(candidate) && !File.Exists(candidate)) return candidate; }
    }
    // Reserve the entire volume basename before publishing; never overwrite an older archive or split set.
    private static string UniqueOutput(string desired)
    {
        string parent = Path.GetDirectoryName(desired)!, stem = Path.GetFileNameWithoutExtension(desired), extension = Path.GetExtension(desired);
        for (int n = 2; File.Exists(desired) || Directory.Exists(desired) || File.Exists(desired + ".001")
            || File.Exists(Path.ChangeExtension(desired, ".z01")); n++)
            desired = Path.Combine(parent, $"{stem} ({n}){extension}");
        return desired;
    }
    private static string Extension(ArchiveFormat format) => format == ArchiveFormat.SevenZip ? ".7z" : ".zip";
    private static CompressionOptions CopyOptions(CompressionOptions x, string output) => new() { Format = x.Format, Level = x.Level,
        Threads = x.Threads, MemoryLimitBytes = x.MemoryLimitBytes, VolumeBytes = x.VolumeBytes, Password = x.Password,
        EncryptHeaders = x.EncryptHeaders, PerFolder = false, OutputPath = output };
    private static void ValidateOptions(CompressionOptions options)
    {
        if (!Enum.IsDefined(options.Format) || options.Level < 0 || options.Level > 9)
            throw new ArchiveException("압축 형식 또는 수준이 올바르지 않습니다.");
        if (options.VolumeBytes != 0 && options.VolumeBytes < 64 * 1024) throw new ArchiveException("분할 크기는 64 KiB 이상이어야 합니다.");
        if (options.EncryptHeaders && string.IsNullOrEmpty(options.Password)) throw new ArchiveException("파일명 암호화에는 암호가 필요합니다.");
        if (string.IsNullOrWhiteSpace(options.OutputPath)) throw new ArchiveException("출력 경로를 선택하세요.");
    }

    private static List<string> NormalizeInputs(IReadOnlyList<string> inputs, bool collapseDescendants)
    {
        if (inputs.Count == 0) throw new ArchiveException("압축할 파일 또는 폴더를 선택하세요.");
        var paths = inputs.Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Length).ToList();
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new ArchiveException($"파일을 찾을 수 없습니다: {path}");
            PathSafety.RejectReparseAncestors(path);
            if (collapseDescendants && result.Any(parent => Directory.Exists(parent) && path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            if (string.IsNullOrEmpty(Path.GetFileName(path))) throw new ArchiveException("드라이브 전체 대신 하위 폴더를 선택하세요.");
            result.Add(path);
        }
        return result;
    }

    private static List<NativeInput> EnumerateInputs(IReadOnlyList<string> roots, string output, CancellationToken cancellation)
    {
        var result = new List<NativeInput>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var pending = new Stack<(string path, string name)>();
            pending.Push((root, Path.GetFileName(root)));
            while (pending.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                if (string.Equals(item.path, output, StringComparison.OrdinalIgnoreCase)) continue;
                PathSafety.RejectReparseAncestors(item.path);
                var name = PathSafety.ValidateEntryName(item.name);
                if (!names.Add(name)) throw new ArchiveException($"같은 압축 경로를 갖는 입력이 있습니다: {name}");
                bool directory = Directory.Exists(item.path);
                result.Add(new(item.path, name, directory));
                if (directory)
                    foreach (var child in Directory.EnumerateFileSystemEntries(item.path).OrderDescending(StringComparer.OrdinalIgnoreCase))
                        pending.Push((child, name + "/" + Path.GetFileName(child)));
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<string>> CreateOneAsync(IReadOnlyList<string> roots, CompressionOptions options,
        ulong memory, int threads, IProgress<ArchiveProgress>? progress, CancellationToken cancellation)
    {
        string desired = Path.GetFullPath(options.OutputPath);
        if (!desired.EndsWith(Extension(options.Format), StringComparison.OrdinalIgnoreCase)) desired += Extension(options.Format);
        if (roots.Any(x => string.Equals(x, desired, StringComparison.OrdinalIgnoreCase))) throw new ArchiveException("원본 파일을 압축 출력으로 지정할 수 없습니다.");
        PathSafety.RejectReparseAncestors(desired);
        desired = UniqueOutput(desired);
        progress?.Report(new("파일 확인", 0, 0, roots[0]));
        var items = await Task.Run(() => EnumerateInputs(roots, desired, cancellation), cancellation).ConfigureAwait(false);
        var snapshots = await Task.Run(() => items.Where(x => !x.IsDirectory).ToDictionary(x => x.Source,
            x => { var f = new FileInfo(x.Source); return (f.Length, f.LastWriteTimeUtc); }, StringComparer.OrdinalIgnoreCase), cancellation).ConfigureAwait(false);
        var stage = PathSafety.CreateStage(Path.GetDirectoryName(desired)!);
        var published = new List<string>();
        try
        {
            var stagedArchive = Path.Combine(stage, Path.GetFileName(desired));
            await WorkerHost.ExecuteAsync(new() { Operation = "create", Archive = stagedArchive, Inputs = items,
                Create = options, Threads = threads, MemoryLimitBytes = memory }, progress, cancellation);
            foreach (var (path, before) in snapshots)
            {
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
                    throw new ArchiveException($"압축 중 원본 파일이 변경되었습니다: {path}");
            }
            var artifacts = Directory.GetFiles(stage).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (artifacts.Length == 0) throw new ArchiveException("압축 엔진이 출력 파일을 생성하지 않았습니다.");
            foreach (var file in artifacts)
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.Combine(Path.GetDirectoryName(desired)!, Path.GetFileName(file));
                PathSafety.RejectReparseAncestors(target);
                File.Move(file, target, false);
                published.Add(target);
            }
            return published;
        }
        catch
        {
            foreach (var path in published) try { File.Delete(path); } catch (IOException) { }
            throw;
        }
        finally { PathSafety.DeleteStage(stage); }
    }

    public async Task<IReadOnlyList<string>> ExtractAsync(string path, ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        try { return await ExtractOneAsync(path, options, MemoryBudget.Resolve(options.MemoryLimitBytes),
            Math.Clamp(options.Threads, 1, Environment.ProcessorCount), ProtectedSources([path]), progress, cancellation); }
        finally { Queue.Release(); }
    }

    public async Task<IReadOnlyList<string>> ExtractManyAsync(IReadOnlyList<string> paths, ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        try
        {
            var archives = paths.Select(NormalizeArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (archives.Length == 0) return [];
            var protectedSources = ProtectedSources(archives);
            var memory = MemoryBudget.Resolve(options.MemoryLimitBytes);
            int threads = Math.Clamp(options.Threads, 1, Environment.ProcessorCount);
            int count = Math.Max(1, Math.Min(archives.Length, Math.Min(4, Math.Min(threads, (int)Math.Min(int.MaxValue, memory / (256UL * 1024 * 1024))))));
            var results = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(archives, new ParallelOptions { MaxDegreeOfParallelism = count, CancellationToken = cancellation }, async (archive, token) =>
            {
                var local = new ExtractionOptions { DestinationDirectory = options.DestinationDirectory, Mode = options.Mode,
                    Password = options.Password, SelectedIndices = options.SelectedIndices, Collision = options.Collision };
                bool retried = false;
                while (true)
                {
                    try
                    {
                        foreach (var output in await ExtractOneAsync(archive, local, memory / (ulong)count, Math.Max(1, threads / count), protectedSources, progress, token)) results.Add(output);
                        break;
                    }
                    catch (ArchiveException error) when (error.IsPasswordError && ResolvePasswordAsync != null)
                    {
                        await passwordGate.WaitAsync(token);
                        try { local.Password = await ResolvePasswordAsync(archive, retried || local.Password != null); }
                        finally { passwordGate.Release(); }
                        if (local.Password == null) throw new OperationCanceledException(token);
                        retried = true;
                    }
                }
            });
            return results.ToArray();
        }
        finally { Queue.Release(); }
    }

    private async Task<IReadOnlyList<string>> ExtractOneAsync(string path, ExtractionOptions options, ulong memory,
        int threads, HashSet<string> protectedSources, IProgress<ArchiveProgress>? progress, CancellationToken cancellation)
    {
        path = NormalizeArchivePath(path);
        var entries = await ListInternalAsync(path, options.Password, memory, cancellation);
        var selected = SelectEntries(entries, options.SelectedIndices);
        if (selected.Count == 0) return [];
        var parent = string.IsNullOrWhiteSpace(options.DestinationDirectory) ? Path.GetDirectoryName(path)! : Path.GetFullPath(options.DestinationDirectory);
        PathSafety.RejectReparseAncestors(parent);
        bool newFolder = options.Mode == ExtractionMode.Folder || options.Mode == ExtractionMode.Smart && NeedsFolder(selected);
        var target = newFolder ? Path.Combine(parent, string.IsNullOrEmpty(ArchiveStem(path)) ? "압축 해제" : ArchiveStem(path)) : parent;
        Directory.CreateDirectory(parent);
        var stage = PathSafety.CreateStage(parent);
        try
        {
            await WorkerHost.ExecuteAsync(new() { Operation = "extract", Archive = path, Destination = stage, Password = options.Password,
                Indices = options.SelectedIndices == null ? null : selected.Select(x => x.Index).ToArray(), Threads = threads, MemoryLimitBytes = memory }, progress, cancellation);
            // Moving the staged tree is blocking file I/O; keep it off the caller's UI thread.
            var files = await Task.Run(() => CommitAsync(stage, target, options.Collision, path, protectedSources, newFolder, cancellation), cancellation);
            progress?.Report(new("완료", 1, 1, target));
            return files;
        }
        finally { PathSafety.DeleteStage(stage); }
    }

    public static bool NeedsFolder(IReadOnlyList<ArchiveEntry> entries)
    {
        if (entries.Count == 1) return false;
        var roots = entries.Select(x => PathSafety.ValidateEntryName(x.Path).Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return roots.Length != 1 || entries.Any(x => !x.IsDirectory && !PathSafety.ValidateEntryName(x.Path).Contains('/'));
    }

    private static List<ArchiveEntry> SelectEntries(List<ArchiveEntry> entries, uint[]? indices)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = PathSafety.ValidateEntryName(entry.Path);
            if (!names.Add(name)) throw new ArchiveException($"중복된 압축 항목: {name}");
        }
        if (indices == null) return entries;
        var wanted = indices.ToHashSet();
        var available = entries.Select(x => x.Index).ToHashSet();
        if (!wanted.IsSubsetOf(available)) throw new ArchiveException("선택한 압축 항목이 더 이상 존재하지 않습니다.");
        var directories = entries.Where(x => wanted.Contains(x.Index) && x.IsDirectory)
            .Select(x => PathSafety.ValidateEntryName(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(x => wanted.Contains(x.Index) || InsideSelectedFolder(x.Path)).ToList();
        bool InsideSelectedFolder(string name)
        {
            name = name.Replace('\\', '/');
            for (int slash = name.LastIndexOf('/'); slash >= 0; slash = name.LastIndexOf('/'))
            {
                name = name[..slash];
                if (directories.Contains(name)) return true;
            }
            return false;
        }
    }

    private readonly SemaphoreSlim commitGate = new(1, 1);
    private async Task<IReadOnlyList<string>> CommitAsync(string stage, string destination, CollisionPolicy policy,
        string archive, HashSet<string> protectedSources, bool newFolder, CancellationToken cancellation)
    {
        await commitGate.WaitAsync(cancellation);
        try
        {
            if (newFolder) destination = PathSafety.UniquePath(destination, true);
            PathSafety.RejectReparseAncestors(destination);
            Directory.CreateDirectory(destination);
            var outputs = new List<string>();
            var zone = PathSafety.SupportsNamedStreams(stage) ? ReadZone(archive) : null;
            await MoveDirectoryAsync(stage, destination);
            return outputs;

            async Task MoveDirectoryAsync(string sourceDirectory, string targetDirectory)
            {
                foreach (var source in Directory.EnumerateFileSystemEntries(sourceDirectory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    PathSafety.RejectReparseAncestors(source);
                    bool isDirectory = Directory.Exists(source);
                    var target = PathSafety.SafeCombine(targetDirectory, Path.GetFileName(source));
                    bool existingDirectory = Directory.Exists(target), existingFile = File.Exists(target);
                    if (existingDirectory && isDirectory) { await MoveDirectoryAsync(source, target); continue; }
                    if (existingDirectory || existingFile)
                    {
                        var action = policy;
                        if (action == CollisionPolicy.Ask) action = ResolveCollisionAsync != null ? await ResolveCollisionAsync(target) : CollisionPolicy.Rename;
                        if (action == CollisionPolicy.Skip) continue;
                        if (action == CollisionPolicy.Ask) throw new OperationCanceledException(cancellation);
                        if (action == CollisionPolicy.Rename || isDirectory || existingDirectory)
                            target = PathSafety.UniquePath(target, isDirectory);
                        else if (action == CollisionPolicy.Overwrite)
                        {
                            PathSafety.RejectReparseAncestors(target);
                            if (protectedSources.Contains(PathSafety.ResolveExistingFile(target))) throw new ArchiveException("원본 압축파일 또는 분할 볼륨을 덮어쓸 수 없습니다.");
                            if (zone != null) WriteZone(source, zone);
                            File.Move(source, target, true);
                            outputs.Add(target);
                            continue;
                        }
                    }
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(target);
                        await MoveDirectoryAsync(source, target);
                        outputs.Add(target);
                    }
                    else { if (zone != null) WriteZone(source, zone); File.Move(source, target, false); outputs.Add(target); }
                }
            }
        }
        finally { commitGate.Release(); }
    }

    private static HashSet<string> ProtectedSources(IEnumerable<string> archives)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in archives)
        {
            var path = NormalizeArchivePath(input);
            result.Add(path);
            var directory = Path.GetDirectoryName(path)!;
            if (SevenZipVolume().IsMatch(path))
            {
                var prefix = Path.GetFileName(path[..path.LastIndexOf('.')]) + ".";
                foreach (var volume in Directory.EnumerateFiles(directory, prefix + "*"))
                    if (Path.GetFileName(volume)[prefix.Length..].All(char.IsAsciiDigit)) result.Add(PathSafety.ResolveExistingFile(volume));
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = Path.GetFileNameWithoutExtension(path) + ".z";
                foreach (var volume in Directory.EnumerateFiles(directory, prefix + "*"))
                    if (Path.GetFileName(volume)[prefix.Length..].All(char.IsAsciiDigit)) result.Add(PathSafety.ResolveExistingFile(volume));
            }
        }
        return result;
    }

    private static string? ReadZone(string archive)
    {
        if (!PathSafety.SupportsNamedStreams(archive)) return null;
        string zone;
        try { zone = File.ReadAllText(archive + ":Zone.Identifier"); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Only the zone number is propagated; untrusted ADS text is never copied verbatim.
        var match = Regex.Match(zone, @"(?m)^ZoneId=([0-4])\s*$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void WriteZone(string output, string zone)
        => File.WriteAllText(output + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=" + zone + "\r\n");

    public async Task TestAsync(string path, string? password = null, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        try { await WorkerHost.ExecuteAsync(new() { Operation = "test", Archive = NormalizeArchivePath(path), Password = password,
            Threads = Environment.ProcessorCount, MemoryLimitBytes = MemoryBudget.Resolve(0) }, progress, cancellation); }
        finally { Queue.Release(); }
    }

    public async Task<DragPreparation> PrepareDragAsync(string path, uint[] selected, string? password,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellation = default)
    {
        await Queue.WaitAsync(cancellation);
        string directory = Path.Combine(AppPaths.DragRoot, Guid.NewGuid().ToString("N"));
        try
        {
            path = NormalizeArchivePath(path);
            ulong memory = MemoryBudget.Resolve(0);
            var entries = SelectEntries(await ListInternalAsync(path, password, memory, cancellation), selected);
            if (entries.Count == 0) throw new ArchiveException("끌어낼 항목을 선택하세요.");
            PathSafety.RejectReparseAncestors(directory);
            Directory.CreateDirectory(directory);
            // shortcut: orphaned drag data survives a crash; reclaim only after a reboot or explicit user cleanup.
            await WorkerHost.ExecuteAsync(new() { Operation = "extract", Archive = path, Destination = directory, Password = password,
                Indices = entries.Select(x => x.Index).ToArray(), Threads = Environment.ProcessorCount, MemoryLimitBytes = memory }, progress, cancellation);
            if ((PathSafety.SupportsNamedStreams(directory) ? ReadZone(path) : null) is { } zone)
                await Task.Run(() => { foreach (var entry in entries.Where(x => !x.IsDirectory)) WriteZone(PathSafety.SafeCombine(directory, entry.Path), zone); }, cancellation);
            var selectedRoots = entries.Where(x => selected.Contains(x.Index)).Select(x => PathSafety.ValidateEntryName(x.Path))
                .OrderBy(x => x.Length).ToList();
            var paths = new List<string>();
            foreach (var root in selectedRoots)
            {
                var full = PathSafety.SafeCombine(directory, root);
                if (!paths.Any(p => full.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) paths.Add(full);
            }
            return new(directory, paths);
        }
        catch { PathSafety.DeleteOwnedDirectory(directory, AppPaths.DragRoot); throw; }
        finally { Queue.Release(); }
    }
}

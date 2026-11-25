using System.IO.Compression;
using System.Security.Cryptography;
using SevenZipper.Core;

if (args.Length == 1 && args[0] == "--worker") return await WorkerHost.RunAsync();
int assertions = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); assertions++; }
void Reject(Action action, string label) { try { action(); } catch (ArchiveException) { assertions++; return; } throw new Exception("FAIL accepted: " + label); }
ArchiveEntry Entry(string name, bool folder = false) => new(0, name, 0, 0, folder, false, "", null);
foreach (var bad in new[] { "../evil", "a/../../b", "/root", @"C:\evil", @"\\server\file", "a:secret", "NUL.txt", "con", "a/COM1", "a/../b", "a//b", "x. ", "a/.", "a\0b" })
    Reject(() => PathSafety.ValidateEntryName(bad), bad);
Check(PathSafety.ValidateEntryName("한글/emoji 😀.txt") == "한글/emoji 😀.txt", "unicode name");
Check(!ArchiveService.NeedsFolder([Entry("one.txt")]), "single file smart extract");
Check(!ArchiveService.NeedsFolder([Entry("photos/a.txt"), Entry("photos/b.txt")]), "implicit single root");
Check(!ArchiveService.NeedsFolder([Entry("empty", true)]), "empty folder smart extract");
Check(ArchiveService.NeedsFolder([Entry("a.txt"), Entry("b.txt")]), "multiple root files");
Check(ArchiveService.NeedsFolder([Entry("dir/a.txt"), Entry("b.txt")]), "mixed roots");

void CheckBatch(IReadOnlyList<ArchiveProgress> reports, int jobs, string label, bool completed = true)
{
    Check(reports.Count > 0 && reports.All(p => p.OverallPercent is >= 0 and <= 100 && p.TotalJobs == jobs), label + " known batch size");
    Check(reports[0].OverallPercent == 0 && reports[0].CompletedJobs == 0, label + " starts at zero including queued jobs");
    Check(reports.Zip(reports.Skip(1)).All(p => p.First.OverallPercent <= p.Second.OverallPercent
        && p.First.CompletedJobs <= p.Second.CompletedJobs), label + " serialized monotonic progress");
    Check(reports.All(p => p.CompletedJobs == jobs || p.OverallPercent < 100), label + " waits for every commit");
    Check(completed ? reports[^1].OverallPercent == 100 && reports[^1].CompletedJobs == jobs
        : reports[^1].OverallPercent < 100 && reports[^1].CompletedJobs < jobs, label + " completion state");
    if (completed) Check(reports.Where(p => p.JobId != null).Select(p => p.JobId).Distinct().Count() == jobs, label + " stable logical job IDs");
}

var aggregateReports = new List<ArchiveProgress>();
var aggregateType = typeof(ArchiveService).Assembly.GetType("SevenZipper.Core.BatchProgress")!;
var aggregate = Activator.CreateInstance(aggregateType, 3, new ImmediateProgress(aggregateReports.Add))!;
var aggregateJobs = Enumerable.Range(0, 3).Select(i => (IProgress<ArchiveProgress>)aggregateType.GetProperty("Item")!.GetValue(aggregate, [i])!).ToArray();
void CompleteAggregate(int index) => aggregateJobs[index].GetType().GetMethod("Complete")!.Invoke(aggregateJobs[index], null);
aggregateJobs[0].Report(new("압축", 100, 100, "first", "worker-a"));
Check(aggregateReports[^1].OverallPercent is > 33 and < 34 && aggregateReports[^1].CompletedJobs == 0,
    "one finished worker cannot fill a three-job batch before commit");
var logicalId = aggregateReports[^1].JobId;
aggregateJobs[0].Report(new("파일 확인", 0, 0, "first", "worker-retry"));
Check(aggregateReports[^1].JobId == logicalId && aggregateReports[^1].CompletedBytes == 0 && aggregateReports[^1].TotalBytes == 0,
    "retry keeps logical ID and raw byte counters");
aggregateJobs[1].Report(new("압축", 0, 0, "empty"));
Check(aggregateReports[^1].OverallPercent == aggregateReports[^2].OverallPercent, "unknown and empty totals do not invent progress");
Parallel.For(0, 100, i => aggregateJobs[i % 3].Report(new("압축", (ulong)i, 100, "parallel")));
CompleteAggregate(0);
CompleteAggregate(1);
Check(aggregateReports[^1].OverallPercent < 100 && aggregateReports[^1].CompletedJobs == 2, "empty job completes only when committed");
CompleteAggregate(2);
CheckBatch(aggregateReports, 3, "deterministic batch");

Check(PathSafety.IsDeviceName("CON") && PathSafety.IsDeviceName("NUL.txt") && !PathSafety.IsDeviceName("console.txt"), "device name check");
Check(new ConversionOptions().Level == 9, "ZIP to 7z conversion defaults to maximum compression");
Check(ArchiveService.IsZipArchivePath("a.zip") && ArchiveService.IsZipArchivePath("a.z01") && ArchiveService.IsZipArchivePath("a.Z12")
    && !ArchiveService.IsZipArchivePath("a.7z") && !ArchiveService.IsZipArchivePath("a.7z.001") && !ArchiveService.IsZipArchivePath("a.txt"),
    "IsZipArchivePath distinguishes ZIP and split ZIP volumes");
var stemMethod = typeof(ArchiveService).GetMethod("ArchiveStem", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
Check((string)stemMethod.Invoke(null, [@"C:\temp\report..zip"])! == "report", "archive stem trims trailing dots");
Check((string)stemMethod.Invoke(null, [@"C:\temp\CON.zip"])! == "CON_", "archive stem avoids Windows device names");
var managedTemp = Path.Combine(Path.GetTempPath(), "7zipper-managed-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(managedTemp);
try
{
    var splitCheck = Path.Combine(managedTemp, "demo.7z");
    File.WriteAllText(splitCheck + ".001", "part1");
    Check(ArchiveService.OutputExists(splitCheck), "OutputExists detects existing 7z split volume");
    var zipSplitCheck = Path.Combine(managedTemp, "demo.zip");
    File.WriteAllText(Path.Combine(managedTemp, "demo.z01"), "part1");
    Check(ArchiveService.OutputExists(zipSplitCheck), "OutputExists detects existing ZIP split volume");

    var ownedParent = Path.Combine(managedTemp, "owned");
    var ownedId = Guid.NewGuid().ToString("N");
    var ownedDir = Path.Combine(ownedParent, ownedId);
    var roSub = Path.Combine(ownedDir, "readonly-sub");
    Directory.CreateDirectory(roSub);
    File.WriteAllText(Path.Combine(roSub, "f.txt"), "data");
    File.SetAttributes(roSub, FileAttributes.Directory | FileAttributes.ReadOnly);
    PathSafety.DeleteOwnedDirectory(ownedDir, ownedParent);
    Check(!Directory.Exists(ownedDir), "DeleteOwnedDirectory deletes read-only subdirectories");
}
finally { if (Directory.Exists(managedTemp)) Directory.Delete(managedTemp, true); }

if (args.Contains("--managed")) { Console.WriteLine($"PASS {assertions} managed assertions"); return 0; }

var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-work"));
Directory.CreateDirectory(parent);
var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "자료 😀");
Directory.CreateDirectory(Path.Combine(source, "empty"));
Directory.CreateDirectory(Path.Combine(source, "nested"));
await File.WriteAllTextAsync(Path.Combine(source, "한글.txt"), string.Concat(Enumerable.Repeat("7Zipper 테스트 데이터 abcdefghijklmnopqrstuvwxyz\n", 20000)));
await File.WriteAllTextAsync(Path.Combine(source, "nested", "zero.txt"), "");
byte[] random = new byte[1024 * 1024]; RandomNumberGenerator.Fill(random);
await File.WriteAllBytesAsync(Path.Combine(source, "random.bin"), random);
var service = new ArchiveService();
var hashes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(source, p), Hash);
var tests = new[] {
    (ArchiveFormat.SevenZip, false, false, false), (ArchiveFormat.SevenZip, true, true, false),
    (ArchiveFormat.SevenZip, true, false, true), (ArchiveFormat.Zip, false, false, false),
    (ArchiveFormat.Zip, true, false, false), (ArchiveFormat.Zip, false, false, true),
    (ArchiveFormat.ZipZstandard, false, false, false), (ArchiveFormat.ZipZstandard, true, false, true)
};
foreach (var (format, encrypted, headers, split) in tests)
{
    string label = $"{format}-{encrypted}-{headers}-{split}";
    Console.WriteLine("TEST " + label);
    string output = Path.Combine(root, label + (format == ArchiveFormat.SevenZip ? ".7z" : ".zip"));
    var results = await service.CreateAsync([source], new() { Format = format, OutputPath = output,
        Threads = 4, MemoryLimitBytes = 512UL * 1024 * 1024, Password = encrypted ? "비밀 pass123!" : null,
        EncryptHeaders = headers, VolumeBytes = split ? 128UL * 1024 : 0 });
    Check(results.Count > 0 && results.All(File.Exists), label + " archive created");
    var open = format == ArchiveFormat.SevenZip && split ? output + ".001" : output;
    var password = encrypted ? "비밀 pass123!" : null;
    var entries = await service.ListAsync(open, password);
    Check(entries.Count >= 6, label + " all entries");
    await service.TestAsync(open, password);
    var destination = Path.Combine(root, "extract-" + label);
    var extracted = await service.ExtractAsync(open, new() { DestinationDirectory = destination,
        Password = password, Mode = ExtractionMode.Smart, Threads = 4, MemoryLimitBytes = 512UL * 1024 * 1024 });
    foreach (var (relative, hash) in hashes) Check(Hash(Path.Combine(destination, Path.GetFileName(source), relative)) == hash, label + " hash " + relative);
    Check(Directory.Exists(Path.Combine(destination, Path.GetFileName(source), "empty")), label + " empty directory");
    Check(!Directory.EnumerateDirectories(destination).Any(x => Path.GetFileName(x).StartsWith(".7zipper-")), label + " staging cleanup");
    var one = entries.Single(x => x.Path.EndsWith("한글.txt"));
    var selective = Path.Combine(root, "selected-" + label);
    await service.ExtractAsync(open, new() { DestinationDirectory = selective, Password = password, Mode = ExtractionMode.Here, SelectedIndices = [one.Index] });
    Check(Directory.GetFiles(selective, "*", SearchOption.AllDirectories).Length == 1, label + " selection");
    if (encrypted)
    {
        bool rejected = false;
        try { await service.ExtractAsync(open, new() { DestinationDirectory = Path.Combine(root, "bad-" + label), Password = "wrong", Mode = ExtractionMode.Here }); }
        catch (ArchiveException) { rejected = true; }
        Check(rejected, label + " wrong password rejected");
    }
    if (split) Check(results.Count > 1, label + " multiple volumes");
}

string malicious = Path.Combine(root, "traversal.zip");
using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("unsafe"); }
bool blocked = false;
try { await service.ExtractAsync(malicious, new() { DestinationDirectory = Path.Combine(root, "attack"), Mode = ExtractionMode.Here }); }
catch (ArchiveException) { blocked = true; }
Check(blocked && !File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal blocked");

var archivePlain = Path.Combine(root, "Zip-False-False-False.zip");
var collisionDir = Path.Combine(root, "collisions");
await service.ExtractAsync(archivePlain, new() { DestinationDirectory = collisionDir, Mode = ExtractionMode.Here });
var preserved = Path.Combine(collisionDir, Path.GetFileName(source), "한글.txt");
await File.WriteAllTextAsync(preserved, "keep me");
await service.ExtractAsync(archivePlain, new() { DestinationDirectory = collisionDir, Mode = ExtractionMode.Here, Collision = CollisionPolicy.Rename });
Check(await File.ReadAllTextAsync(preserved) == "keep me", "collision preserves original");
Check(File.Exists(Path.Combine(Path.GetDirectoryName(preserved)!, "한글 (2).txt")), "collision renamed");

var perFolder = Path.Combine(root, "per-folder");
var folders = await service.CreateAsync([source, Path.Combine(source, "nested")], new() { PerFolder = true, OutputPath = perFolder, Format = ArchiveFormat.Zip, Threads = 4 });
Check(folders.Count == 2, "each selected folder gets an archive");
var batchInputs = Enumerable.Range(0, 6).Select(i => Path.Combine(root, "progress-source", "folder-" + i)).ToArray();
foreach (var folder in batchInputs) Directory.CreateDirectory(folder);
await File.WriteAllBytesAsync(Path.Combine(batchInputs[1], "zero.bin"), []);
await File.WriteAllBytesAsync(Path.Combine(batchInputs[2], "random.bin"), random);
await File.WriteAllTextAsync(Path.Combine(batchInputs[3], "text.txt"), new string('a', 65536));
Directory.CreateDirectory(Path.Combine(batchInputs[4], "nested-empty"));
await File.WriteAllTextAsync(Path.Combine(batchInputs[5], "small.txt"), "small");
foreach (var format in Enum.GetValues<ArchiveFormat>())
{
    Console.WriteLine("TEST batch-progress-" + format);
    var output = Path.Combine(root, "progress-create-" + format);
    var creationReports = new List<ArchiveProgress>();
    bool committedBeforeComplete = true;
    var archives = await service.CreateAsync(batchInputs, new() { PerFolder = true, OutputPath = output, Format = format,
        Threads = 2, MemoryLimitBytes = 512UL * 1024 * 1024 }, new ImmediateProgress(p =>
        {
            creationReports.Add(p);
            if (p.CompletedJobs > 0) committedBeforeComplete &= Directory.GetFiles(output).Length >= p.CompletedJobs;
        }));
    CheckBatch(creationReports, 6, format + " queued create");
    Check(committedBeforeComplete && archives.Count == 6, format + " reports completed archives after publishing");
    var emptyArchive = Path.Combine(output, "no-entries.zip");
    using (ZipFile.Open(emptyArchive, ZipArchiveMode.Create)) { }
    var extractionReports = new List<ArchiveProgress>();
    var destination = Path.Combine(root, "progress-extract-" + format);
    await service.ExtractManyAsync([.. archives, emptyArchive, archives[0]], new() { DestinationDirectory = destination,
        Mode = ExtractionMode.Here, Threads = 2, MemoryLimitBytes = 512UL * 1024 * 1024 }, new ImmediateProgress(extractionReports.Add));
    CheckBatch(extractionReports, 7, format + " queued extract including empty archive and duplicate input");
    Check(batchInputs.All(p => Directory.Exists(Path.Combine(destination, Path.GetFileName(p)))), format + " mixed and empty folders extracted");
    Check(Hash(Path.Combine(destination, "folder-2", "random.bin")) == Hash(Path.Combine(batchInputs[2], "random.bin")), format + " progress batch data intact");
}
var noJobsReports = new List<ArchiveProgress>();
Check((await service.ExtractManyAsync([], new(), new ImmediateProgress(noJobsReports.Add))).Count == 0
    && noJobsReports.Single().OverallPercent == 100 && noJobsReports[0].TotalJobs == 0, "empty extraction batch finishes without workers");
var firstFolder = Path.Combine(root, "A");
var secondFolder = Path.Combine(root, "A (2)");
Directory.CreateDirectory(firstFolder);
Directory.CreateDirectory(secondFolder);
await File.WriteAllTextAsync(Path.Combine(firstFolder, "content.txt"), "first folder");
await File.WriteAllTextAsync(Path.Combine(secondFolder, "content.txt"), "second folder");
foreach (var (format, extension, volume) in new[]
    { (ArchiveFormat.SevenZip, ".7z", "A.7z.001"), (ArchiveFormat.Zip, ".zip", "A.z01") })
{
    var output = Path.Combine(root, "per-folder-" + format);
    Directory.CreateDirectory(output);
    var existingVolume = Path.Combine(output, volume);
    await File.WriteAllTextAsync(existingVolume, "existing volume");
    var results = await service.CreateAsync([firstFolder, secondFolder], new() { PerFolder = true, OutputPath = output,
        Format = format, Threads = 2, MemoryLimitBytes = 512UL * 1024 * 1024 });
    var firstArchive = Path.Combine(output, "A (2)" + extension);
    var secondArchive = Path.Combine(output, "A (2) (2)" + extension);
    Check(results.Count == 2 && results.Contains(firstArchive) && results.Contains(secondArchive), format + " per-folder split name collision resolved");
    var firstExtracted = Path.Combine(output, "first");
    var secondExtracted = Path.Combine(output, "second");
    await service.ExtractAsync(firstArchive, new() { DestinationDirectory = firstExtracted, Mode = ExtractionMode.Here });
    await service.ExtractAsync(secondArchive, new() { DestinationDirectory = secondExtracted, Mode = ExtractionMode.Here });
    Check(await File.ReadAllTextAsync(Path.Combine(firstExtracted, "A", "content.txt")) == "first folder", format + " first archive contents");
    Check(await File.ReadAllTextAsync(Path.Combine(secondExtracted, "A (2)", "content.txt")) == "second folder", format + " second archive contents");
    Check(await File.ReadAllTextAsync(existingVolume) == "existing volume", format + " existing per-folder split volume preserved");
}
var encryptedA = Path.Combine(root, "SevenZip-True-True-False.7z");
var encryptedB = Path.Combine(root, "Zip-True-False-False.zip");
int prompts = 0;
service.ResolvePasswordAsync = (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult<string?>("비밀 pass123!"); };
var passwordReports = new List<ArchiveProgress>();
await service.ExtractManyAsync([encryptedA, encryptedB], new() { DestinationDirectory = Path.Combine(root, "batch"), Collision = CollisionPolicy.Rename }, new ImmediateProgress(passwordReports.Add));
CheckBatch(passwordReports, 2, "password retry batch");
Check(prompts == 2, "batch password prompts once per archive");
Check(Directory.GetFiles(Path.Combine(root, "batch"), "*", SearchOption.AllDirectories).Length == 6, "batch retry commits each archive once");
var twinA = Path.Combine(root, "twin-a", "logs.zip");
var twinB = Path.Combine(root, "twin-b", "logs.zip");
Directory.CreateDirectory(Path.GetDirectoryName(twinA)!); Directory.CreateDirectory(Path.GetDirectoryName(twinB)!);
using (var zip = ZipFile.Open(twinA, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("a.txt").Open()); w.Write("a"); }
using (var zip = ZipFile.Open(twinB, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("b.txt").Open()); w.Write("b"); }
var twinOutput = Path.Combine(root, "twin-out");
await service.ExtractManyAsync([twinA, twinB], new() { DestinationDirectory = twinOutput, Mode = ExtractionMode.Folder, Threads = 2 });
Check(Directory.GetDirectories(twinOutput).Length == 2, "parallel same-stem archives keep separate folders");
var protectedDir = Path.Combine(root, "protected"); Directory.CreateDirectory(protectedDir);
var victim = Path.Combine(protectedDir, "victim.zip"); File.Copy(twinA, victim);
var trigger = Path.Combine(protectedDir, "trigger.zip");
using (var zip = ZipFile.Open(trigger, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("victim.zip").Open()); w.Write("would corrupt source"); }
string victimHash = Hash(victim); bool sourceProtected = false;
var failedReports = new List<ArchiveProgress>();
try { await service.ExtractManyAsync([trigger, victim], new() { DestinationDirectory = protectedDir, Mode = ExtractionMode.Here, Collision = CollisionPolicy.Overwrite, Threads = 1 }, new ImmediateProgress(failedReports.Add)); }
catch (ArchiveException) { sourceProtected = true; }
Check(sourceProtected && Hash(victim) == victimHash, "batch extraction cannot overwrite another source archive");
CheckBatch(failedReports, 2, "failed extraction batch", completed: false);
var volumeSource = Path.Combine(root, "volume-source"); Directory.CreateDirectory(volumeSource);
await File.WriteAllBytesAsync(Path.Combine(volumeSource, "self.7z.002"), random);
var splitSelfBase = Path.Combine(protectedDir, "self.7z");
await service.CreateAsync([Path.Combine(volumeSource, "self.7z.002")], new() { OutputPath = splitSelfBase, VolumeBytes = 65536, Threads = 2 });
var protectedVolume = splitSelfBase + ".002";
string volumeHash = Hash(protectedVolume); bool volumeProtected = false;
try { await service.ExtractAsync(splitSelfBase + ".001", new() { DestinationDirectory = protectedDir, Mode = ExtractionMode.Here, Collision = CollisionPolicy.Overwrite }); }
catch (ArchiveException) { volumeProtected = true; }
Check(volumeProtected && Hash(protectedVolume) == volumeHash, "extract cannot overwrite source companion volume");
var dragEntries = await service.ListAsync(archivePlain);
using (var drag = await service.PrepareDragAsync(archivePlain, [dragEntries.Single(x => x.IsDirectory && x.Path.Replace('\\','/').TrimEnd('/') == Path.GetFileName(source)).Index], null))
{
    Check(drag.Paths.Count == 1 && Directory.Exists(drag.Paths[0]), "folder drag prepared");
    foreach (var (relative, hash) in hashes) Check(Hash(Path.Combine(drag.Paths[0], relative)) == hash, "drag content " + relative);
}
var taken = Path.Combine(root, "taken"); Directory.CreateDirectory(taken);
await service.CreateAsync([Path.Combine(source, "random.bin")], new() { OutputPath = Path.Combine(taken, "set.7z"), VolumeBytes = 65536, Threads = 2 });
await File.WriteAllTextAsync(Path.Combine(taken, "set (2).7z"), "occupied");
var renamedOutput = await service.CreateAsync([Path.Combine(source, "random.bin")], new() { OutputPath = Path.Combine(taken, "set.7z"), Threads = 2 });
Check(renamedOutput.Count == 1 && Path.GetFileName(renamedOutput[0]) == "set (3).7z", "output name skips both a split set and an existing file");
Check(await File.ReadAllTextAsync(Path.Combine(taken, "set (2).7z")) == "occupied", "existing output left untouched");
bool streams;
var probe = Path.Combine(root, "stream-probe.txt"); await File.WriteAllTextAsync(probe, "");
try { await File.WriteAllTextAsync(probe + ":probe", "x"); streams = true; } catch (IOException) { streams = false; }
Check(PathSafety.SupportsNamedStreams(root) == streams, "named stream support detected");
var marked = Path.Combine(root, "marked.zip"); File.Copy(archivePlain, marked);
if (streams)
{
    await File.WriteAllTextAsync(marked + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/\r\n");
    await service.ExtractAsync(marked, new() { DestinationDirectory = Path.Combine(root, "marked-out"), Mode = ExtractionMode.Here });
    Check(await File.ReadAllTextAsync(Path.Combine(root, "marked-out", Path.GetFileName(source), "한글.txt") + ":Zone.Identifier") == "[ZoneTransfer]\r\nZoneId=3\r\n", "zone propagated without untrusted text");
    using var markedDrag = await service.PrepareDragAsync(marked, [dragEntries.Single(x => x.Path.EndsWith("한글.txt")).Index], null);
    if (PathSafety.SupportsNamedStreams(markedDrag.Directory))
        Check((await File.ReadAllTextAsync(markedDrag.Paths[0] + ":Zone.Identifier")).Contains("ZoneId=3"), "zone propagated to drag files");
}
// Optional: the root of a FAT32 or exFAT volume, which has no alternate data streams.
var plainRoot = Environment.GetEnvironmentVariable("SEVENZIPPER_TEST_FAT_ROOT");
if (string.IsNullOrEmpty(plainRoot)) Console.WriteLine("SKIP stream-less volume: SEVENZIPPER_TEST_FAT_ROOT is not set");
else
{
    Console.WriteLine("TEST stream-less volume " + plainRoot);
    var plainWork = Path.Combine(plainRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(plainWork);
    Check(!PathSafety.SupportsNamedStreams(plainWork), "volume reports no named streams");
    await service.ExtractAsync(marked, new() { DestinationDirectory = Path.Combine(plainWork, "out"), Mode = ExtractionMode.Here });
    foreach (var (relative, hash) in hashes) Check(Hash(Path.Combine(plainWork, "out", Path.GetFileName(source), relative)) == hash, "marked archive extracts to stream-less volume " + relative);
    var plainArchive = Path.Combine(plainWork, "plain.zip"); File.Copy(archivePlain, plainArchive);
    await service.ExtractAsync(plainArchive, new() { DestinationDirectory = Path.Combine(root, "from-plain"), Mode = ExtractionMode.Here });
    foreach (var (relative, hash) in hashes) Check(Hash(Path.Combine(root, "from-plain", Path.GetFileName(source), relative)) == hash, "archive on stream-less volume extracts " + relative);
    var plainCreated = await service.CreateAsync([source], new() { OutputPath = Path.Combine(plainWork, "created.7z"), Threads = 2 });
    Check(plainCreated.Count == 1 && File.Exists(plainCreated[0]), "archive created on stream-less volume");
    Directory.Delete(plainWork, true);
}
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel(); bool wasCanceled = false;
    try { await service.CreateAsync([source], new() { OutputPath = Path.Combine(root, "canceled.7z") }, cancellation: canceled.Token); }
    catch (OperationCanceledException) { wasCanceled = true; }
    Check(wasCanceled && !File.Exists(Path.Combine(root, "canceled.7z")), "pre-cancel no output");
}
var cancelSource = Path.Combine(root, "cancel-source.bin");
using (var file = File.Create(cancelSource)) for (int i = 0; i < 96; i++) file.Write(random);
using (var canceled = new CancellationTokenSource())
{
    var cancelFolder = Path.Combine(root, "cancel-batch-source"); Directory.CreateDirectory(cancelFolder);
    File.Copy(cancelSource, Path.Combine(cancelFolder, "large.bin"));
    var cancellationReports = new List<ArchiveProgress>();
    bool wasCanceled = false;
    var output = Path.Combine(root, "cancel-batch-output");
    var reporting = new ImmediateProgress(p => { cancellationReports.Add(p); if (p.CompletedBytes > 0) canceled.Cancel(); });
    try { await service.CreateAsync([cancelFolder, batchInputs[0]], new() { PerFolder = true, OutputPath = output, Threads = 1 }, reporting, canceled.Token); }
    catch (OperationCanceledException) { wasCanceled = true; }
    Check(wasCanceled && Directory.GetFiles(output).Length < 2, "batch cancel leaves queued or running jobs incomplete");
    CheckBatch(cancellationReports, 2, "canceled create batch", completed: false);
    Check(!Directory.GetDirectories(output).Any(p => Path.GetFileName(p).StartsWith(".7zipper-")), "batch cancel staging cleanup");
}
using (var canceled = new CancellationTokenSource())
{
    bool wasCanceled = false;
    var reporting = new ImmediateProgress(p => { if (p.Stage != "파일 확인" && p.CompletedBytes > 0) canceled.Cancel(); });
    try { await service.CreateAsync([cancelSource], new() { OutputPath = Path.Combine(root, "active-cancel.7z"), Threads = 1 }, reporting, canceled.Token); }
    catch (OperationCanceledException) { wasCanceled = true; }
    Check(wasCanceled && !File.Exists(Path.Combine(root, "active-cancel.7z")), "active cancel removes unfinished output");
    Check(!Directory.GetDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".7zipper-")), "active cancel staging cleanup");
}
Console.WriteLine("TEST zip-to-7z conversion");
var convertDir = Path.Combine(root, "convert-work");
Directory.CreateDirectory(convertDir);
var zipOne = Path.Combine(convertDir, "alpha.zip");
var zipTwo = Path.Combine(convertDir, "beta.zip");
var zipEncrypted = Path.Combine(convertDir, "secret.zip");
File.Copy(Path.Combine(root, "Zip-False-False-False.zip"), zipOne);
File.Copy(Path.Combine(root, "Zip-False-False-False.zip"), zipTwo);
File.Copy(Path.Combine(root, "Zip-True-False-False.zip"), zipEncrypted);
var existingTarget = Path.Combine(convertDir, "alpha.7z");
await File.WriteAllTextAsync(existingTarget, "existing-7z");
var convertReports = new List<ArchiveProgress>();
service.ResolvePasswordAsync = (_, _) => Task.FromResult<string?>("비밀 pass123!");
var converted = await service.ConvertZipTo7zAsync([zipOne, zipTwo, zipEncrypted], new ConversionOptions
{
    Threads = 2,
    MemoryLimitBytes = 512UL * 1024 * 1024
}, new ImmediateProgress(convertReports.Add));
CheckBatch(convertReports, 3, "multi-zip to 7z conversion");
Check(converted.Count == 3 && converted.All(p => p.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && File.Exists(p)),
    "each selected ZIP converts to an individual 7z archive");
Check(await File.ReadAllTextAsync(existingTarget) == "existing-7z"
    && converted.Contains(Path.Combine(convertDir, "alpha (2).7z")),
    "conversion preserves existing 7z archive using unique name");
Check(File.Exists(zipOne) && File.Exists(zipTwo) && File.Exists(zipEncrypted),
    "source ZIP files remain intact by default");
foreach (var output7z in converted)
{
    await service.TestAsync(output7z);
    var verifyDir = Path.Combine(convertDir, "verify-" + Path.GetFileNameWithoutExtension(output7z));
    await service.ExtractAsync(output7z, new() { DestinationDirectory = verifyDir, Mode = ExtractionMode.Here });
    foreach (var (relative, hash) in hashes)
        Check(Hash(Path.Combine(verifyDir, Path.GetFileName(source), relative)) == hash, "converted 7z hash " + relative);
    Check(Directory.Exists(Path.Combine(verifyDir, Path.GetFileName(source), "empty")), "converted 7z preserves empty directory");
}
var disposableZip = Path.Combine(convertDir, "remove-me.zip");
File.Copy(zipOne, disposableZip);
var deletedOutputs = await service.ConvertZipTo7zAsync([disposableZip], new ConversionOptions
{
    DeleteSourceAfterSuccess = true,
    Threads = 2,
    MemoryLimitBytes = 512UL * 1024 * 1024
});
Check(deletedOutputs.Count == 1 && File.Exists(deletedOutputs[0]) && !File.Exists(disposableZip),
    "DeleteSourceAfterSuccess removes source ZIP only after 7z is created");

Console.WriteLine($"PASS {assertions} assertions; fixtures: {root}");
return 0;

static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

sealed class ImmediateProgress(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
{
    public void Report(ArchiveProgress value) => callback(value);
}

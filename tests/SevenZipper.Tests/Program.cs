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
var encryptedA = Path.Combine(root, "SevenZip-True-True-False.7z");
var encryptedB = Path.Combine(root, "Zip-True-False-False.zip");
int prompts = 0;
service.ResolvePasswordAsync = (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult<string?>("비밀 pass123!"); };
await service.ExtractManyAsync([encryptedA, encryptedB], new() { DestinationDirectory = Path.Combine(root, "batch"), Collision = CollisionPolicy.Rename });
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
try { await service.ExtractManyAsync([trigger, victim], new() { DestinationDirectory = protectedDir, Mode = ExtractionMode.Here, Collision = CollisionPolicy.Overwrite, Threads = 1 }); }
catch (ArchiveException) { sourceProtected = true; }
Check(sourceProtected && Hash(victim) == victimHash, "batch extraction cannot overwrite another source archive");
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
    bool wasCanceled = false;
    var reporting = new ImmediateProgress(p => { if (p.Stage != "파일 확인" && p.CompletedBytes > 0) canceled.Cancel(); });
    try { await service.CreateAsync([cancelSource], new() { OutputPath = Path.Combine(root, "active-cancel.7z"), Threads = 1 }, reporting, canceled.Token); }
    catch (OperationCanceledException) { wasCanceled = true; }
    Check(wasCanceled && !File.Exists(Path.Combine(root, "active-cancel.7z")), "active cancel removes unfinished output");
    Check(!Directory.GetDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".7zipper-")), "active cancel staging cleanup");
}
Console.WriteLine($"PASS {assertions} assertions; fixtures: {root}");
return 0;

static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

sealed class ImmediateProgress(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
{
    public void Report(ArchiveProgress value) => callback(value);
}

# 7Zipper implementation contract

Windows x64, .NET 10 WPF UI, native MSVC C++ engine. All modules use the project directory as their working root. UI namespace `SevenZipper.App`, shared managed namespace `SevenZipper.Core`. No shell parsing of archive output. No passwords in argv or logs.

## Managed UI contract (owned by coordinator)

`ArchiveFormat { SevenZip, Zip, ZipZstandard }`; `ExtractionMode { Here, Folder, Smart }`; `CollisionPolicy { Ask, Skip, Rename, Overwrite }`.

`ArchiveEntry(uint Index, string Path, ulong Size, ulong PackedSize, bool IsDirectory, bool IsEncrypted, string Method, DateTimeOffset? Modified)`.

`CompressionOptions`: `Format` (SevenZip), `Level` (5), `Threads` (Environment.ProcessorCount), `MemoryLimitBytes` (0 = auto 50% available), `VolumeBytes` (0), `Password` (null), `EncryptHeaders` (false), `PerFolder` (false), `OutputPath` (empty). OutputPath is full archive filename for normal create, output directory for PerFolder.

`ExtractionOptions`: `DestinationDirectory`, `Mode` (Smart), `Password`, `SelectedIndices` (null = all), `Collision` (Ask), `Threads`, `MemoryLimitBytes`.

`ArchiveProgress(string Stage, ulong CompletedBytes, ulong TotalBytes, string? CurrentFile, string? JobId = null)`.

`ArchiveService` has async ListAsync(path,password,cancellation), CreateAsync(IReadOnlyList<string> inputs,CompressionOptions, IProgress<ArchiveProgress>?,cancellation), ExtractAsync(path,ExtractionOptions,IProgress<ArchiveProgress>?,cancellation), TestAsync(path,password,IProgress<ArchiveProgress>?,cancellation). Create/Extract return IReadOnlyList<string> final output paths. `Func<string,Task<CollisionPolicy>>? ResolveCollisionAsync` property for Ask. List returns IReadOnlyList<ArchiveEntry>. `PrepareDragAsync(path,uint[] selected,string? password,IProgress<ArchiveProgress>?,cancellation)` returns `DragPreparation` with `Directory` and `Paths`; ownership handed to native drag lifetime, never delete while a copy is alive.

UI App startup handles `--shell-request <inherited pipe handle>`, request wire format below; coordinator owns App.xaml.cs/bootstrap, worker host and shell handoff. UI agent owns MainWindow + dialogs/theme, references Core project; may create App.xaml resources but not App.xaml.cs. MainWindow public method `HandleRequest(ShellRequest request)`; `ShellRequest(string Operation,string[] Paths)` in Core. Operations open, compress-7z, compress-zip, compress-options, extract-here, extract-smart. MainWindow constructor parameterless. All expensive UI calls await ArchiveService; cancellation supported.

## Native engine ABI (engine agent owns actual header)

DLL `SevenZipper.Native.dll`, cdecl exports with UTF-16 strings and 8-byte default struct alignment. Functions `sz_list`, `sz_create`, `sz_extract`, `sz_test`, `sz_last_error`, `sz_version`. Engine agent must publish exact header promptly; coordinator generates matching interop. Callbacks are synchronous to the export lifetime, may originate on native workers. Entry callback receives index/path/sizes/flags/method/FILETIME. Progress callback returns 0 to cancel, 1 to continue. Status 0 success, 1 canceled, other failures carry UTF-16 sz_last_error. Create inputs include explicit safe relative archive names. Extraction always targets an empty private staging directory supplied by managed code. Native still validates all entry paths, rejects links/reparse/ADS/devices/traversal and checks CRC. All entry names untrusted. No native overwrite of an existing file. Restrict allocation and codec threads to job options. ZIP split is standard PKWARE .z01+last .zip, 7z split .001. Partial output cleanup belongs only to this job.

## Shell and drag contracts (shell agent owns native shell module)

`SevenZipper.Shell.dll` IExplorerCommand x64, classic registry installation first. Classes/installer exact GUIDs owned by shell agent. Shell Invoke launches `7Zipper.exe --shell-request <handle>` using explicit application path, only read pipe handle inherited. Pipe bytes: UTF-8 JSON `{"operation":"compress-7z","paths":["C:\\..."]}` followed by EOF (max 16 MiB input). No password or arbitrary operation executable. IShellItemArray full selection, no archive parsing in Explorer.

Separate exported drag helper in same DLL: `int sz_drag_files(void* ownerHwnd,const wchar_t* const* paths,int count,const wchar_t* stagingDirectory)` on UI STA; returns Win32 HRESULT. Native owns cleanup only once drop+async copy+COM references are done; for canceled/no drop cleanup after files closed. Does not destroy archived originals. Only COPY allowed. Unknown outcome or crash: leave temp for next boot cleanup, never time-only deletion. App can query `int sz_drag_active()` to postpone exit during async transfer. UI prepares paths in worker before entering native drag; no password prompts inside OLE.

Installer Inno Setup: default `{autopf}\\7Zipper`, directory page shown, supports custom drive/path. Package source directory `artifacts/publish`, generated setup under `dist`. Installer version 0.1.5. Icon at `assets/7zipper.ico`, master `assets/7zipper.png`. Include LICENSE, THIRD-PARTY-NOTICES.md, and runtime licenses. No forced default file association. No automatic certificate trust changes.

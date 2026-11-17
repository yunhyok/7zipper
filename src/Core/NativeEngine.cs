using System.Runtime.InteropServices;

namespace SevenZipper.Core;

internal static class NativeEngine
{
    private const string Library = "SevenZipper.Native.dll";
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public uint Index, Flags;
        public IntPtr Path, Method;
        public ulong Size, PackedSize, Modified;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Input
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Source;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CreateOptions
    {
        public uint Format, Level, Threads, EncryptHeaders;
        public ulong MemoryLimit, VolumeSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReadOptions
    {
        public uint Threads;
        public ulong MemoryLimit;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EntryCallback(IntPtr context, in Entry entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int ProgressCallback(IntPtr context, [MarshalAs(UnmanagedType.LPWStr)] string stage,
        ulong completed, ulong total, [MarshalAs(UnmanagedType.LPWStr)] string? currentFile);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sz_list(string archive, in ReadOptions options, EntryCallback entry, ProgressCallback progress, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sz_create(string archive, [In] Input[] inputs, uint count, in CreateOptions options, ProgressCallback progress, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sz_extract(string archive, string destination, [In] uint[]? indices, uint count, in ReadOptions options, ProgressCallback progress, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sz_test(string archive, in ReadOptions options, ProgressCallback progress, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sz_last_error();

    public static int Run(WorkerRequest request, Action<WorkerMessage> send, CancellationToken cancellation)
    {
        // No managed exception may cross a native callback boundary.
        Exception? callbackError = null;
        ProgressCallback progress = (_, stage, done, total, file) =>
        {
            if (cancellation.IsCancellationRequested) return 0;
            try { send(new() { Progress = new(stage, done, total, file) }); return 1; }
            catch (Exception error) { callbackError = error; return 0; }
        };
        EntryCallback entry = (IntPtr _, in Entry e) =>
        {
            if (cancellation.IsCancellationRequested) return 0;
            try
            {
                string name = Marshal.PtrToStringUni(e.Path) ?? "";
                if ((e.Flags & 4) != 0) throw new ArchiveException($"안전하지 않은 압축 항목: {name}");
                PathSafety.ValidateEntryName(name);
                DateTimeOffset? modified = e.Modified > 0 && e.Modified <= 2650467743999999999UL
                    ? new DateTimeOffset(DateTime.FromFileTimeUtc((long)e.Modified)) : null;
                send(new() { Type = "entry", Entry = new(e.Index, name, e.Size, e.PackedSize,
                    (e.Flags & 1) != 0, (e.Flags & 2) != 0, Marshal.PtrToStringUni(e.Method) ?? "", modified) });
                return 1;
            }
            catch (Exception error) { callbackError = error; return 0; }
        };
        var read = new ReadOptions { Threads = checked((uint)Math.Max(1, request.Threads)),
            MemoryLimit = request.MemoryLimitBytes, Password = request.Password };
        int status;
        if (request.Operation == "create")
        {
            var options = request.Create ?? throw new ArchiveException("압축 설정이 없습니다.");
            var native = new CreateOptions { Format = (uint)options.Format, Level = (uint)options.Level,
                Threads = read.Threads, EncryptHeaders = options.EncryptHeaders ? 1u : 0,
                MemoryLimit = read.MemoryLimit, VolumeSize = options.VolumeBytes, Password = options.Password };
            var inputs = (request.Inputs ?? []).Select(x => new Input { Source = x.Source, Name = x.Name, Flags = x.IsDirectory ? 1u : 0 }).ToArray();
            status = sz_create(request.Archive, inputs, (uint)inputs.Length, in native, progress, IntPtr.Zero);
        }
        else status = request.Operation switch
        {
            "list" => sz_list(request.Archive, in read, entry, progress, IntPtr.Zero),
            "extract" => sz_extract(request.Archive, request.Destination ?? throw new ArchiveException("해제 위치가 없습니다."),
                request.Indices, (uint)(request.Indices?.Length ?? 0), in read, progress, IntPtr.Zero),
            "test" => sz_test(request.Archive, in read, progress, IntPtr.Zero),
            _ => throw new ArchiveException("알 수 없는 작업입니다.")
        };
        GC.KeepAlive(entry); GC.KeepAlive(progress);
        if (callbackError != null) throw callbackError;
        var errorText = status > 1 ? Marshal.PtrToStringUni(sz_last_error()) : null;
        send(new() { Type = "result", Status = status, Error = errorText });
        return status;
    }
}

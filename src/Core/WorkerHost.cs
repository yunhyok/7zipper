using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SevenZipper.Core;

public static class WorkerHost
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static async Task<int> RunAsync()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        var sendLock = new object();
        long lastProgress = 0;
        void Send(WorkerMessage message)
        {
            lock (sendLock)
            {
                long now = Environment.TickCount64;
                if (message.Type == "progress" && now - lastProgress < 80
                    && message.Progress?.CompletedBytes != message.Progress?.TotalBytes) return;
                if (message.Type == "progress") lastProgress = now;
                Console.Out.WriteLine(JsonSerializer.Serialize(message));
                Console.Out.Flush();
            }
        }
        try
        {
            var line = await Console.In.ReadLineAsync() ?? throw new ArchiveException("작업 요청이 없습니다.");
            var request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions) ?? throw new ArchiveException("작업 요청 오류");
            _ = Task.Run(async () =>
            {
                try { while (await Console.In.ReadLineAsync() is { } input) if (input == "cancel") cancellation.Cancel(); }
                catch (IOException) { cancellation.Cancel(); }
            });
            return NativeEngine.Run(request, Send, cancellation.Token);
        }
        catch (OperationCanceledException) { Send(new() { Type = "result", Status = 1 }); return 1; }
        catch (Exception error)
        {
            Send(new() { Type = "result", Status = error is ArchiveException ae ? ae.Status : 2, Error = error.Message });
            return 2;
        }
    }

    internal static async Task<List<ArchiveEntry>> ExecuteAsync(WorkerRequest request,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath ?? throw new ArchiveException("프로그램 경로를 찾을 수 없습니다.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--worker");
        using var process = Process.Start(start) ?? throw new ArchiveException("압축 엔진을 시작하지 못했습니다.");
        using var job = WorkerJob.Create(process, request.MemoryLimitBytes);
        var stderr = process.StandardError.ReadToEndAsync();
        var entries = new List<ArchiveEntry>();
        var jobId = Guid.NewGuid().ToString("N");
        ulong metadataBytes = 0;
        WorkerMessage? result = null;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            // Register only after the request: cancellation must never become the first protocol line.
            using var cancelRegistration = cancellation.Register(() =>
            {
                try { process.StandardInput.WriteLine("cancel"); process.StandardInput.Flush(); } catch { }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                });
            });
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var message = JsonSerializer.Deserialize<WorkerMessage>(line, JsonOptions)
                    ?? throw new ArchiveException("압축 엔진의 응답 오류");
                if (message.Type == "entry" && message.Entry != null)
                {
                    metadataBytes = checked(metadataBytes + 256UL + (ulong)(message.Entry.Path.Length + message.Entry.Method.Length) * 2);
                    if (metadataBytes > request.MemoryLimitBytes / 4) throw new ArchiveException("압축 목록이 메모리 예산을 초과했습니다.");
                    entries.Add(message.Entry);
                }
                else if (message.Type == "progress" && message.Progress != null) progress?.Report(message.Progress with { JobId = jobId });
                else if (message.Type == "result") result = message;
            }
            await process.WaitForExitAsync();
            cancellation.ThrowIfCancellationRequested();
            if (result == null) throw new ArchiveException("압축 엔진이 비정상 종료되었습니다. 메모리 한도와 압축파일을 확인하세요. " + await stderr);
            if (result.Status == 1) throw new OperationCanceledException(cancellation);
            if (result.Status != 0) throw new ArchiveException(result.Error ?? "압축 작업에 실패했습니다.", result.Status);
            if (process.ExitCode != 0) throw new ArchiveException("압축 엔진이 오류 코드로 종료되었습니다.");
            return entries;
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
        }
    }
}

internal static class WorkerJob
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorking, MaxWorking;
        public uint ProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    public static SafeFileHandle Create(Process process, ulong memory)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new ExtendedLimit { Basic = new() { Flags = 0x2000 | 0x100 },
            ProcessMemory = (UIntPtr)checked(memory + 256UL * 1024 * 1024) };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            try { process.Kill(true); } catch { }
            throw new ArchiveException($"압축 작업의 자원 한도를 설정하지 못했습니다. Windows 오류 {error}");
        }
        return job;
    }
}

internal static class MemoryBudget
{
    [StructLayout(LayoutKind.Sequential)] private struct Status
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref Status status);
    public static ulong Available
    {
        get { var status = new Status { Length = (uint)Marshal.SizeOf<Status>() }; return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical : 1024UL * 1024 * 1024; }
    }
    public static ulong Resolve(ulong requested)
    {
        var available = Available;
        var result = requested == 0 ? available / 2 : Math.Min(requested, available * 3 / 4);
        if (result < 64UL * 1024 * 1024) throw new ArchiveException("압축 작업에 사용할 메모리가 부족합니다.");
        return result;
    }
}

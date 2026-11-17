using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using SevenZipper.Core;

namespace SevenZipper.App;

public partial class App : Application { }

public static class Program
{
    private static readonly HashSet<string> Operations = ["activate", "open", "compress-7z", "compress-zip", "compress-options", "extract-here", "extract-smart"];
    private const int RequestLimit = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--worker") return WorkerHost.RunAsync().GetAwaiter().GetResult();
        try
        {
            var request = ReadRequest(args);
            var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var pipeName = "7Zipper-" + identity;
            using var mutex = new Mutex(true, "Local\\" + pipeName, out bool primary);
            if (!primary) { SendRequest(pipeName, request); return 0; }
            using var stop = new CancellationTokenSource();
            var app = new App { ShutdownMode = ShutdownMode.OnLastWindowClose };
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) =>
            {
                MessageBox.Show(e.Exception.Message, "7Zipper", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            var window = new MainWindow();
            app.MainWindow = window;
            _ = ListenAsync(pipeName, app, stop.Token);
            window.Loaded += (_, _) => Dispatch(app, request);
            int exit = app.Run(window);
            stop.Cancel();
            mutex.ReleaseMutex();
            return exit;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "7Zipper 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static ShellRequest ReadRequest(string[] args)
    {
        if (args.Length == 0) return new("activate", []);
        if (args.Length == 2 && args[0] == "--shell-request")
        {
            if (!ulong.TryParse(args[1], out _)) throw new ArchiveException("탐색기 요청 핸들이 올바르지 않습니다.");
            using var pipe = new AnonymousPipeClientStream(PipeDirection.In, args[1]);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = pipe.Read(buffer)) > 0)
            {
                if (memory.Length + read > RequestLimit) throw new ArchiveException("탐색기 선택 항목이 너무 많습니다.");
                memory.Write(buffer, 0, read);
            }
            return Validate(JsonSerializer.Deserialize<ShellRequest>(memory.ToArray(), JsonOptions));
        }
        if (args.Any(x => x.StartsWith("--", StringComparison.Ordinal))) throw new ArchiveException("알 수 없는 실행 옵션입니다.");
        return Validate(new ShellRequest("open", args));
    }

    private static ShellRequest Validate(ShellRequest? request)
    {
        if (request == null || !Operations.Contains(request.Operation) || request.Paths == null || request.Paths.Length > 100000)
            throw new ArchiveException("탐색기 요청이 올바르지 않습니다.");
        return request with { Paths = request.Paths.Select(path => Path.GetFullPath(path)).ToArray() };
    }

    private static void SendRequest(string name, ShellRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(request));
    }

    private static async Task ListenAsync(string name, App app, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                using var memory = new MemoryStream();
                var buffer = new byte[8192];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                int read;
                while ((read = await pipe.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (memory.Length + read > RequestLimit) throw new ArchiveException("요청 크기 한도를 초과했습니다.");
                    memory.Write(buffer, 0, read);
                }
                var request = Validate(JsonSerializer.Deserialize<ShellRequest>(memory.ToArray(), JsonOptions));
                await app.Dispatcher.InvokeAsync(() => Dispatch(app, request));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested)
                    await app.Dispatcher.InvokeAsync(() => MessageBox.Show(error.Message, "7Zipper", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
    }

    private static void Dispatch(App app, ShellRequest request)
    {
        var window = app.Windows.OfType<MainWindow>().FirstOrDefault();
        if (window == null) { window = new MainWindow(); window.Show(); }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (request.Operation != "activate") window.HandleRequest(request);
    }
}

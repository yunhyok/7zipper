using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using SevenZipper.App;
using SevenZipper.Core;
using AppProgram = SevenZipper.App.Program;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            CheckRequestRouting();
            CheckInstanceNames();
            CheckArchiveContentsCaseInsensitive();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckRequestRouting()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        ProgressDisplayChecks.Run();
        CompressionLayoutChecks.Run();
        var first = new MainWindow();
        var second = new MainWindow();
        var queued = new ShellRequest("compress-7z", [Path.GetFullPath("queued.txt")]);
        var incoming = new ShellRequest("compress-7z", [Path.GetFullPath("incoming.txt")]);
        var dispatch = AppMethod("Dispatch");
        dispatch.Invoke(null, [app, queued]);
        Check(Requests(first).SequenceEqual([queued]) && Requests(second).Count == 0,
            "normal requests reuse the first window");

        var cleanup = new TaskCompletionSource();
        WindowField("_dragCleanup").SetValue(first, cleanup.Task);
        first.Close();
        Check((bool)WindowField("_closeRequested").GetValue(first)!, "close waits for drag cleanup");
        dispatch.Invoke(null, [app, incoming]);
        Check(Requests(first).SequenceEqual([queued]) && Requests(second).SequenceEqual([incoming]),
            "new shell request reaches an available window while the first window closes");
        Check(app.Windows.Count == 2, "an available window is reused");

        cleanup.SetResult();
        first.Close();
        second.Close();
        app.Shutdown();
        Console.WriteLine("Shell request routing passed.");
    }

    private static void CheckInstanceNames()
    {
        var format = AppMethod("InstanceName");
        string Name(string sid, uint session, bool elevated) => (string)format.Invoke(null, [sid, session, elevated])!;
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var account = identity.User!.Value;
        var session = (uint)process.SessionId;
        var standard = Name(account, session, false);
        var elevated = Name(account, session, true);
        var actual = (string)AppMethod("GetInstanceName").Invoke(null, [identity])!;
        Check(standard != elevated && actual != Name(account, session + 1, actual == elevated)
            && (actual == standard || actual == elevated), "IPC name uses the current token's SID, session and elevation");
        Console.WriteLine("IPC names distinguish session and elevation. Current token query passed.");
    }

    private static void CheckArchiveContentsCaseInsensitive()
    {
        var contentsType = typeof(App).Assembly.GetType("SevenZipper.App.ArchiveContents")!;
        ArchiveEntry[] entries =
        [
            new(0, "Folder", 0, 0, true, false, "", null),
            new(1, "folder/readme.txt", 1024, 512, false, false, "Deflate", null),
            new(2, "FOLDER/Sub/data.bin", 2048, 1024, false, false, "Deflate", null)
        ];
        var contents = Activator.CreateInstance(contentsType, [entries])!;
        var browse = contentsType.GetMethod("Browse")!;
        var rootRows = (Array)browse.Invoke(contents, ["", ""])!;
        Check(rootRows.Length == 1, "case-differing archive folders merge into one root folder");
        var folderRows = (Array)browse.Invoke(contents, ["Folder", ""])!;
        Check(folderRows.Length == 2, "browsing case-differing folder lists all children");
        var selection = (uint[])contentsType.GetMethod("Selection")!.Invoke(contents, [rootRows])!;
        Check(selection.SequenceEqual([0u, 1u, 2u]), "selecting merged folder includes all nested entries regardless of case");
        var selectedSize = (ulong)contentsType.GetMethod("SelectionSize")!.Invoke(contents, [rootRows])!;
        Check(selectedSize == 3072UL, "selection size sums nested files accurately");
        Console.WriteLine("Case-insensitive archive folder browsing and selection passed.");
    }

    private static MethodInfo AppMethod(string name) => typeof(AppProgram).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static FieldInfo WindowField(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static Queue<ShellRequest> Requests(MainWindow window) => (Queue<ShellRequest>)WindowField("_requests").GetValue(window)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

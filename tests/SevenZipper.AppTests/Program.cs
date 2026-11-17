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

    private static MethodInfo AppMethod(string name) => typeof(AppProgram).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static FieldInfo WindowField(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static Queue<ShellRequest> Requests(MainWindow window) => (Queue<ShellRequest>)WindowField("_requests").GetValue(window)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SevenZipper.App;
using SevenZipper.Core;

internal static class Program
{
    private const string NestedFile = "자료📂/깊은 폴더/표.csv", Empty = "빈 폴더";
    private const string CopyComplete = "끌어서 복사 완료", CopyEnded = "탐색기 복사 작업 종료", Canceled = "끌어서 복사 취소됨";
    private static readonly string[] Modes = ["normal-copy", "swapped-copy", "swapped-cancel"];
    private static readonly byte[] NestedBytes = "항목,값\n테스트,437\n"u8.ToArray();
    private static readonly byte[] RootBytes = Enumerable.Range(0, 8193).Select(i => (byte)(i % 251)).ToArray();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--worker"]) return WorkerHost.RunAsync().GetAwaiter().GetResult();
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args is ["--interactive", var artifacts]) RunCases(artifacts, Modes);
            else if (args is ["--interactive", var caseArtifacts, var singleMode] && Modes.Contains(singleMode))
                RunCases(caseArtifacts, [singleMode]);
            else if (args is ["--case", var target, var handle, var mode] && Modes.Contains(mode))
                return RunApp(target, (nint)long.Parse(handle), mode);
            else throw new ArgumentException("Usage: --interactive <existing-artifacts-directory> [normal-copy|swapped-copy|swapped-cancel] (uses the real mouse and Explorer; leave the desktop idle)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RunCases(string artifacts, string[] modes)
    {
        Assert(Directory.Exists(artifacts), "artifacts directory exists");
        Assert(Desktop.GetCursorPos(out var cursor), "interactive desktop is available");
        Assert(CleanInput(), "no mouse buttons, modifiers or Escape already held");
        var original = Desktop.Swapped;
        var work = Directory.CreateDirectory(Path.Combine(artifacts, "desktop-drag-" + Guid.NewGuid().ToString("N"))).FullName;
        Console.WriteLine($"Evidence folder: {work}; original SM_SWAPBUTTON={(original ? 1 : 0)}");
        try
        {
            foreach (var mode in modes)
            {
                Assert(CleanInput(), "inputs released before next case");
                var swapped = mode != "normal-copy";
                var target = Directory.CreateDirectory(Path.Combine(work, mode)).FullName;
                var existing = ExplorerWindows().Keys.ToHashSet();
                using var explorer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    $"/n,\"{target}\""));
                nint FindOwnedWindow() => ExplorerWindows().SingleOrDefault(window => !existing.Contains(window.Key) &&
                    string.Equals(window.Value, target, StringComparison.OrdinalIgnoreCase)).Key;
                var hwnd = (nint)0;
                try
                {
                    Wait(() => (hwnd = FindOwnedWindow()) != 0, "new Explorer window for " + target);
                    var start = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    };
                    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                    foreach (var arg in new[] { "--case", target, hwnd.ToString(), mode }) start.ArgumentList.Add(arg);
                    try
                    {
                        Desktop.SwapMouseButton(swapped);
                        Assert(Desktop.Swapped == swapped, $"{mode}: SM_SWAPBUTTON={(swapped ? 1 : 0)}");
                        using var child = Process.Start(start)!;
                        child.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                        child.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
                        child.BeginOutputReadLine();
                        child.BeginErrorReadLine();
                        if (!child.WaitForExit(60_000))
                        {
                            child.Kill();
                            child.WaitForExit(2_000);
                            throw new TimeoutException($"{mode} timed out; stopped the test app");
                        }
                        child.WaitForExit(); // Drain the redirected case log.
                        Assert(child.ExitCode == 0, $"{mode} passed");
                    }
                    finally
                    {
                        try
                        {
                            if (Desktop.Down(Desktop.Escape)) Desktop.Esc(false);
                            if (Desktop.Down(Desktop.LeftButton)) Desktop.Mouse(false, false);
                            if (Desktop.Down(Desktop.RightButton)) Desktop.Mouse(true, false);
                        }
                        finally
                        {
                            Desktop.SwapMouseButton(original);
                            Desktop.Move(new Point(cursor.X, cursor.Y));
                        }
                    }
                }
                finally
                {
                    if (hwnd != 0 && FindOwnedWindow() == hwnd) Desktop.PostMessage(hwnd, 0x0010, 0, 0); // WM_CLOSE
                }
            }
        }
        finally
        {
            Assert(Desktop.Swapped == original, $"original SM_SWAPBUTTON={(original ? 1 : 0)} restored");
            Assert(Desktop.GetCursorPos(out var current) && current.X == cursor.X && current.Y == cursor.Y, "cursor restored");
        }
    }

    private static Dictionary<nint, string> ExplorerWindows()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var paths = new Dictionary<nint, string>();
        foreach (var window in shell.Windows())
        {
            try
            {
                if (!Path.GetFileName((string)window.FullName).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Uri.TryCreate((string)window.LocationURL, UriKind.Absolute, out var url) && url.IsFile)
                    paths[(nint)(int)window.HWND] = url.LocalPath;
                else paths[(nint)(int)window.HWND] = "";
            }
            catch (System.Runtime.InteropServices.COMException) { } // A shell window may close during enumeration.
        }
        return paths;
    }

    private static int RunApp(string target, nint explorer, string mode)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            try { await DragAsync(target, explorer, mode); app.Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task DragAsync(string target, nint explorer, string mode)
    {
        var swapped = mode != "normal-copy";
        var cancel = mode == "swapped-cancel";
        Assert(Desktop.Swapped == swapped, "test app sees requested mouse configuration");
        var rootFile = $"보고서 📦 {Guid.NewGuid():N}.txt";
        var archive = target + ".zip";
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry(NestedFile).Open()) stream.Write(NestedBytes);
            using (var stream = zip.CreateEntry(rootFile).Open()) stream.Write(RootBytes);
            zip.CreateEntry(Empty + "/");
        }

        var work = SystemParameters.WorkArea;
        var main = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true, ShowInTaskbar = false };
        main.Left = work.Left + 8;
        main.Top = work.Top + 8;
        main.Width = main.MinWidth;
        main.Height = Math.Max(main.MinHeight, work.Height - 24);
        main.Show();
        var source = new WindowInteropHelper(main).Handle;
        var grid = (DataGrid)main.FindName("EntryGrid");
        var extract = (Button)main.FindName("ExtractButton");
        var taskTitle = (TextBlock)main.FindName("TaskTitle");
        string status = taskTitle.Text;
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(taskTitle,
            (_, _) => { Volatile.Write(ref status, taskTitle.Text); Console.WriteLine($"    App status: {taskTitle.Text}"); });
        string logicalPress = "";
        grid.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            logicalPress = $"{e.ChangedButton}, Left={e.LeftButton}, Right={e.RightButton}";
            Console.WriteLine($"    WPF PreviewMouseDown: {logicalPress}");
        }), true);

        main.HandleRequest(new ShellRequest("open", [archive]));
        for (var timer = Stopwatch.StartNew(); !extract.IsEnabled && timer.ElapsedMilliseconds < 8000;) await Task.Delay(50);
        Assert(extract.IsEnabled && grid.Items.Count == 3, $"three root rows loaded through native engine (status: {status})");
        grid.SelectAll();
        grid.ScrollIntoView(grid.Items[0]);
        main.UpdateLayout();
        Assert(grid.SelectedItems.Count == 3, "implicit folder, explicit empty folder and file selected");
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        var from = row.PointToScreen(new Point(48, row.ActualHeight / 2));
        var moved = row.PointToScreen(new Point(48 + Math.Max(SystemParameters.MinimumHorizontalDragDistance * 2, 24), row.ActualHeight / 2));
        var edge = main.PointToScreen(new Point(main.ActualWidth, 0)).X;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(main);
        var right = (int)(work.Right * dpi.DpiScaleX);
        var top = (int)(work.Top * dpi.DpiScaleY);
        var width = (int)(work.Width * dpi.DpiScaleX * 0.6);
        var height = (int)(work.Height * dpi.DpiScaleY * 0.85);
        Desktop.ShowWindow(explorer, 9); // SW_RESTORE
        Assert(Desktop.SetWindowPos(explorer, -1, right - width, top + 10, width, height, 0x0010), "positioned Explorer (SWP_NOACTIVATE)");
        Assert(Desktop.SetWindowPos(source, -1, 0, 0, 0, 0, 0x0013), "raised 7Zipper above Explorer");
        main.Activate();
        var view = AutomationElement.FromHandle(explorer).FindFirst(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ItemsView"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView")));
        var bounds = view?.Current.BoundingRectangle ?? Rect.Empty;
        var bandLeft = Math.Max(bounds.Left, edge + 12);
        Assert(!bounds.IsEmpty && bounds.Right - bandLeft >= 20, $"Explorer ItemsView has a visible drop area (bounds={bounds}, app right={edge})");
        var to = new Point((bandLeft + bounds.Right) / 2, bounds.Top + bounds.Height / 2);
        Console.WriteLine($"    DPI={dpi.PixelsPerInchX}; source={from}; threshold={moved}; Explorer ItemsView={bounds}; drop={to}");
        Assert(Desktop.At(from) == source && Desktop.At(moved) == source && Desktop.At(to) == explorer, "source and target are unobscured");

        await Task.Run(() =>
        {
            string? stage = null;
            var physical = swapped ? Desktop.RightButton : Desktop.LeftButton;
            var opposite = swapped ? Desktop.LeftButton : Desktop.RightButton;
            Assert(CleanInput(), "no competing input before gesture");
            Desktop.Move(from);
            Thread.Sleep(100);
            Assert(Desktop.At(from) == source, "pointer is on the test app before pressing");
            Desktop.Mouse(swapped, true);
            try
            {
                Wait(() => Desktop.Down(physical) && !Desktop.Down(opposite), "intended physical primary held (GetAsyncKeyState)", 1500);
                Console.WriteLine($"    Held: SM_SWAPBUTTON={(Desktop.Swapped ? 1 : 0)}, physical left={Desktop.Down(Desktop.LeftButton)}, physical right={Desktop.Down(Desktop.RightButton)}");
                Thread.Sleep(110);
                Desktop.Move(moved);
                Wait(() => Desktop.sz_drag_active() != 0, "native OLE drag started");
                stage = Directory.EnumerateDirectories(AppPaths.DragRoot).Single(path => File.Exists(Path.Combine(path, rootFile)));
                VerifyTree(stage, rootFile);
                Assert(Desktop.Down(physical) && !Desktop.Down(opposite), "physical primary remained held through staging");
                Assert(Desktop.At(to) == explorer, "drop point still belongs to destination Explorer");
                Desktop.Move(to);
                Thread.Sleep(450);
                Assert(Desktop.sz_drag_active() != 0 && Desktop.Down(physical), "drag remained active over Explorer until intended action");
                if (cancel)
                {
                    Desktop.Esc(true);
                    try { Wait(() => Volatile.Read(ref status) == Canceled, "Escape canceled the native drag before button release"); }
                    finally { Desktop.Esc(false); }
                    Assert(Desktop.Down(physical), "physical primary was still held when Escape canceled");
                }
            }
            finally { Desktop.Mouse(swapped, false); }

            if (cancel)
            {
                Wait(() => Desktop.sz_drag_active() == 0 && !Directory.Exists(stage), "canceled OLE operation and staging directory cleaned up");
                Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "canceled Explorer destination stayed empty");
            }
            else
            {
                Wait(() => HasFile(target, rootFile, RootBytes.Length) && HasFile(target, NestedFile, NestedBytes.Length) &&
                    Directory.Exists(Path.Combine(target, Empty)), "Explorer wrote the full files and empty folder");
                VerifyTree(target, rootFile);
                Console.WriteLine($"    After copy: active={Desktop.sz_drag_active()}, staging directory exists={Directory.Exists(stage)}, app status={Volatile.Read(ref status)}");
                if (!SpinWait.SpinUntil(() => Desktop.sz_drag_active() == 0, 2000))
                {
                    // Explorer can keep the data object until its window closes.
                    Assert(ExplorerWindows().TryGetValue(explorer, out var shown) && string.Equals(shown, target, StringComparison.OrdinalIgnoreCase),
                        "closing only the Explorer window still showing the test destination");
                    Assert(Desktop.PostMessage(explorer, 0x0010, 0, 0), "asked the test Explorer window to close");
                }
                Wait(() => Volatile.Read(ref status) is CopyComplete or CopyEnded, "app reported Explorer copy complete", 20_000);
                Wait(() => Desktop.sz_drag_active() == 0 && !Directory.Exists(stage), "copied OLE operation and staging directory cleaned up", 20_000);
            }
        });
        Assert(logicalPress == "Left, Left=Pressed, Right=Released", $"logical primary WPF event: {logicalPress}");
        Assert(CleanInput(), "injected buttons and Escape released");
        main.Close();
    }

    private static bool HasFile(string folder, string name, int length)
    {
        var file = new FileInfo(Path.Combine(folder, name));
        return file.Exists && file.Length == length;
    }

    private static void VerifyTree(string path, string rootFile)
    {
        Assert(Directory.EnumerateFileSystemEntries(path).Count() == 3, "exactly three root items in " + path);
        Assert(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() == 2, "exactly two files in " + path);
        Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(path, Empty)).Any(), "Unicode empty folder preserved");
        foreach (var (name, bytes) in new[] { (NestedFile, NestedBytes), (rootFile, RootBytes) })
        {
            using var file = File.OpenRead(Path.Combine(path, name));
            var hash = SHA256.HashData(file);
            Assert(hash.SequenceEqual(SHA256.HashData(bytes)), $"{name}: SHA-256 {Convert.ToHexString(hash)}");
        }
    }

    private static bool CleanInput() => !new[] { Desktop.LeftButton, Desktop.RightButton, Desktop.Escape, 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(Desktop.Down);

    private static void Wait(Func<bool> ready, string label, int timeoutMs = 8000)
    {
        for (var timer = Stopwatch.StartNew(); !ready(); Thread.Sleep(30))
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(label);
        Console.WriteLine($"PASS {label}");
    }

    private static void Assert(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        Console.WriteLine($"PASS {label}");
    }
}

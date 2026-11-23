using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SevenZipper.App;
using SevenZipper.Core;

internal static class ProgressDisplayChecks
{
    public static void Run()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow();
        try
        {
            IProgress<ArchiveProgress>? reports = null;
            var finish = new TaskCompletionSource<string?>();
            var task = Start(window, (progress, _) => { reports = progress; return finish.Task; });
            reports!.Report(new("create", 100, 100, "완료된 폴더/파일.txt", "a", 25, 1, 4));
            Pump();
            Check(Bar(window, "JobProgress").Value == 25 && Bar(window, "CurrentJobProgress").Value == 100,
                "overall progress stays at 25% when one of four archive jobs finishes");
            Check(Text(window, "ProgressCountText").Contains("1/4") && Text(window, "OverallProgressText").Contains("25%"),
                "overall percentage and completed archive count are visible");
            reports.Report(new("파일 확인", 0, 0, "다음 폴더", "b", 25, 1, 4));
            Pump();
            Check(!Bar(window, "JobProgress").IsIndeterminate && Bar(window, "JobProgress").Value == 25
                && Bar(window, "CurrentJobProgress").IsIndeterminate, "preparing another folder preserves overall progress");
            reports.Report(new("create", 50, 100, "한글 폴더/현재 파일.txt", "b", 37.5, 1, 4));
            Pump();
            Check(Bar(window, "JobProgress").Value == 37.5 && Bar(window, "CurrentJobProgress").Value == 50,
                "current archive progress is separate from overall progress");
            Render(window);
            finish.SetResult("4개 압축 파일");
            Pump();
            Check(task.IsCompletedSuccessfully && task.Result && Bar(window, "JobProgress").Value == 100,
                "successful work completes overall progress");
            Check(Text(window, "ProgressCountText").Contains("4/4"),
                "completed batch synchronizes final archive count even without a final async progress tick");
            Check(((FrameworkElement)window.FindName("CurrentProgressPanel")).Visibility == Visibility.Collapsed,
                "current progress row clears after completion");

            var canceled = new TaskCompletionSource<string?>();
            task = Start(window, (progress, token) =>
            {
                reports = progress;
                token.Register(() => canceled.TrySetCanceled(token));
                return canceled.Task;
            });
            reports!.Report(new("create", 30, 100, "여러 파일을 담은 압축 파일", "single"));
            Pump();
            Check(Bar(window, "JobProgress").Value == 30 && Text(window, "ProgressCountText") == ""
                && ((FrameworkElement)window.FindName("CurrentProgressPanel")).Visibility == Visibility.Collapsed,
                "single archive uses aggregate byte progress without stale batch counts");
            var source = (CancellationTokenSource)typeof(MainWindow).GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            source.Cancel();
            Pump();
            Check(task.IsCompletedSuccessfully && !task.Result && Text(window, "OverallProgressText").Contains("취소됨"),
                "cancel does not display successful completion");
            reports.Report(new("create", 100, 100, null, "late", 100, 4, 4));
            Pump();
            Check(Text(window, "OverallProgressText").Contains("취소됨"), "late progress cannot overwrite cancellation");
            Console.WriteLine("Overall/current progress display, reset and cancellation passed.");
        }
        finally
        {
            window.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Task<bool> Start(MainWindow window, Func<IProgress<ArchiveProgress>, CancellationToken, Task<string?>> work) =>
        (Task<bool>)typeof(MainWindow).GetMethod("RunJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["압축", work, false])!;
    private static ProgressBar Bar(MainWindow window, string name) => (ProgressBar)window.FindName(name);
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Render(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        foreach (var (width, height) in new[] { (520, 400), (1060, 690) })
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var current = (FrameworkElement)window.FindName("CurrentJobProgress");
            var bottom = current.TranslatePoint(new Point(0, current.ActualHeight), content).Y;
            Check(bottom <= height && current.ActualWidth > 100, "both progress rows fit the window");
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/progress-layout"));
            Directory.CreateDirectory(folder);
            using var output = File.Create(Path.Combine(folder, $"progress-{width}x{height}.png"));
            png.Save(output);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

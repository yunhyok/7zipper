using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SevenZipper.App;

internal static class CompressionLayoutChecks
{
    public static void Run()
    {
        // Small logical viewports also cover the reduced space available at high display scaling.
        foreach (var size in new[] { new Size(400, 270), new Size(512, 344), new Size(586, 730) })
        {
            var dialog = (Window)Activator.CreateInstance(typeof(App).Assembly.GetType("SevenZipper.App.CompressionDialog")!,
                [Array.Empty<string>()])!;
            try
            {
                var content = (FrameworkElement)dialog.Content;
                void Layout()
                {
                    content.Measure(size);
                    content.Arrange(new Rect(size));
                    content.UpdateLayout();
                }

                Layout();
                var scroll = Descendants(content).OfType<ScrollViewer>().First();
                var performance = Descendants(content).OfType<Expander>().Single();
                var volume = (ComboBox)dialog.FindName("VolumeBox");
                volume.SelectedIndex = volume.Items.Count - 1;
                performance.IsExpanded = true;
                Layout();
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Layout();

                var memory = (TextBox)dialog.FindName("MemoryBox");
                var top = memory.TransformToAncestor(scroll).Transform(new Point()).Y;
                Check(top >= 0 && top + memory.ActualHeight <= scroll.ViewportHeight + 1,
                    $"expanding performance reveals its fields at {size}");

                scroll.ScrollToEnd();
                Layout();
                top = memory.TransformToAncestor(scroll).Transform(new Point()).Y;
                Check(scroll.ViewportHeight - top - memory.ActualHeight >= 24,
                    $"performance fields retain bottom scroll space at {size}");

                var label = Descendants(performance).OfType<TextBlock>()
                    .Single(text => text.Text == "메모리 한도 (MiB, 0 = 자동)");
                Check(label.TextWrapping == TextWrapping.Wrap && label.ActualWidth <= memory.ActualWidth + 1,
                    $"memory label fits the narrow performance column at {size}");
            }
            finally { dialog.Close(); }
        }
        Console.WriteLine("Compression options scroll layout passed at small and standard logical sizes.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

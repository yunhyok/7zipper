using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SevenZipper.Core;

namespace SevenZipper.App;

public partial class MainWindow : Window
{
    private readonly ArchiveService _service = new();
    private readonly Queue<ShellRequest> _requests = new();
    private readonly SemaphoreSlim _promptGate = new(1);
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _job;
    private string? _archive;
    private string? _password;
    private ArchiveContents? _contents;
    private string _folder = "";
    private string? _lastOutputPath;
    private CollisionPolicy? _jobCollisionOverride;
    private bool _dialogOpen;
    private bool _processingRequests;
    private bool _closeRequested;
    private bool _waitingToClose;

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 20));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 20));
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshContents();
        };
        _service.ResolveCollisionAsync = path => PromptAsync(() =>
        {
            if (_jobCollisionOverride is { } remembered) return remembered;
            var policy = TaskDialog.AskCollision(this, path, out var applyToAll);
            if (applyToAll) _jobCollisionOverride = policy;
            return policy;
        });
        _service.ResolvePasswordAsync = (path, retry) => PromptAsync(() => TaskDialog.AskPassword(this, path, retry));
        SizeChanged += (_, _) =>
        {
            LayoutRoot.Margin = new Thickness(ActualHeight < 470 ? 8 : 14);
            LogList.MaxHeight = Math.Clamp((ActualHeight - 345) / 2, 28, 92);
        };
        UpdateControls();
    }

    internal bool IsClosing => _closeRequested;

    public void HandleRequest(ShellRequest request)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandleRequest(request));
            return;
        }
        _requests.Enqueue(request);
        ScheduleRequests();
    }

    private void ScheduleRequests()
    {
        if (_requests.Count > 0 && !_processingRequests && !_closeRequested)
            Dispatcher.BeginInvoke(new Action(ProcessRequests), DispatcherPriority.Background);
    }

    private async void ProcessRequests()
    {
        if (_job != null || _dialogOpen || _nativeDrag || _processingRequests || _closeRequested) return;
        _processingRequests = true;
        try
        {
            while (!_closeRequested && _requests.TryDequeue(out var request))
            {
                if (request.Paths.Length == 0) continue;
                switch (request.Operation)
                {
                    case "open":
                        await OpenArchiveAsync(request.Paths[0]);
                        foreach (var path in request.Paths.Skip(1))
                        {
                            if (_closeRequested) break;
                            var window = new MainWindow();
                            window.Show();
                            window.HandleRequest(new ShellRequest("open", [path]));
                        }
                        break;
                    case "compress-options":
                        await ShowCompressionAsync(request.Paths);
                        break;
                    case "compress-7z":
                    case "compress-zip":
                        await CreateAsync(request.Paths, new CompressionOptions
                        {
                            Format = request.Operation == "compress-zip" ? ArchiveFormat.Zip : ArchiveFormat.SevenZip
                        });
                        break;
                    case "extract-here":
                    case "extract-smart":
                        await ExtractBatchAsync(request.Paths, new ExtractionOptions
                        {
                            Mode = request.Operation == "extract-here" ? ExtractionMode.Here : ExtractionMode.Smart
                        });
                        break;
                    case "convert-7z":
                        await ConvertZipTo7zAsync(request.Paths, new ConversionOptions { Level = 9 });
                        break;
                }
            }
        }
        catch (Exception error)
        {
            if (!_closeRequested) TaskDialog.ShowError(this, "작업을 시작할 수 없습니다", error.Message);
        }
        finally { _processingRequests = false; }
    }

    private async void OnOpen(object sender, RoutedEventArgs e) => await ShowOpenAsync();

    private async Task ShowOpenAsync()
    {
        if (!OpenButton.IsEnabled) return;
        var picker = new OpenFileDialog { Title = "압축 파일 열기", Filter = ArchivePaths.OpenFilter };
        if (DuringDialog(() => picker.ShowDialog(this)) == true) await OpenArchiveAsync(picker.FileName);
    }

    private Task<bool> OpenArchiveAsync(string path) => RunJobAsync("압축 파일 읽기", async (_, token) =>
    {
        SetStatus("압축 파일 읽기", path);
        var archive = ArchiveService.NormalizeArchivePath(path);
        string? password = null;
        var entries = await WithPasswordAsync(archive, null, async candidate =>
        {
            var result = await _service.ListAsync(archive, candidate, token);
            password = candidate;
            return result;
        }, token);
        var contents = await Task.Run(() => new ArchiveContents(entries), token);
        _archive = archive;
        _password = password;
        _contents = contents;
        var archiveFileName = Path.GetFileName(archive);
        Title = $"{archiveFileName} - 7Zipper";
        ArchiveName.Text = archiveFileName;
        ArchiveName.ToolTip = archive;
        Navigate("");
        return $"{archiveFileName} · {entries.Count:N0}개 항목";
    });

    private async void OnCreate(object sender, RoutedEventArgs e) => await ShowCompressionAsync([]);

    private async Task ShowCompressionAsync(IReadOnlyList<string> paths)
    {
        if (!CreateButton.IsEnabled) return;
        var dialog = new CompressionDialog(paths) { Owner = this };
        if (DuringDialog(dialog.ShowDialog) == true && dialog.Options is { } options)
            await CreateAsync(dialog.Inputs, options);
    }

    private Task<bool> CreateAsync(IReadOnlyList<string> paths, CompressionOptions options) =>
        RunJobAsync("압축", async (progress, token) =>
        {
            if (options.OutputPath.Length == 0)
                options.OutputPath = ArchivePaths.SuggestOutput(paths, options.Format == ArchiveFormat.SevenZip ? ".7z" : ".zip");
            var outputs = await _service.CreateAsync(paths, options, progress, token);
            return DescribeOutputs(outputs);
        });

    private async void OnExtract(object sender, RoutedEventArgs e) => await ShowExtractionAsync(sender == ExtractSelectionMenu);

    private async Task ShowExtractionAsync(bool selectionOnly)
    {
        if (!ExtractButton.IsEnabled || _archive == null || _contents == null) return;
        var archive = _archive;
        var selection = _contents.Selection(SelectedRows());
        var dialog = new ExtractionDialog(archive, _password, selection, selectionOnly, _contents.TotalCount) { Owner = this };
        if (DuringDialog(dialog.ShowDialog) != true || dialog.Options is not { } options) return;
        await RunJobAsync("압축 풀기", async (progress, token) =>
        {
            var outputs = await WithPasswordAsync(archive, options.Password, async candidate =>
            {
                options.Password = candidate;
                var result = await _service.ExtractAsync(archive, options, progress, token);
                _password = candidate;
                return result;
            }, token);
            return DescribeOutputs(outputs);
        });
    }

    private Task<bool> ExtractBatchAsync(IReadOnlyList<string> paths, ExtractionOptions options) =>
        RunJobAsync("압축 풀기", async (progress, token) =>
        {
            var outputs = paths.Count > 1
                ? await _service.ExtractManyAsync(paths, options, progress, token)
                : await WithPasswordAsync(paths[0], options.Password, async candidate =>
                {
                    options.Password = candidate;
                    return await _service.ExtractAsync(paths[0], options, progress, token);
                }, token);
            return DescribeOutputs(outputs);
        });

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> initial = _archive != null && ArchiveService.IsZipArchivePath(_archive) ? [_archive] : [];
        await ShowConvertAsync(initial, initial.Count > 0 ? _password : null);
    }

    private async Task ShowConvertAsync(IReadOnlyList<string> paths, string? sourcePassword = null)
    {
        if (!ConvertButton.IsEnabled) return;
        var dialog = new ConvertDialog(paths, sourcePassword) { Owner = this };
        if (DuringDialog(dialog.ShowDialog) == true && dialog.Options is { } options)
            await ConvertZipTo7zAsync(dialog.Inputs, options);
    }

    private Task<bool> ConvertZipTo7zAsync(IReadOnlyList<string> paths, ConversionOptions options) =>
        RunJobAsync("7z 변환", async (progress, token) =>
        {
            var outputs = await _service.ConvertZipTo7zAsync(paths, options, progress, token);
            return DescribeOutputs(outputs);
        });

    private async void OnTest(object sender, RoutedEventArgs e) => await TestArchiveAsync();

    private async Task TestArchiveAsync()
    {
        if (!TestButton.IsEnabled || _archive == null) return;
        var archive = _archive;
        await RunJobAsync("무결성 검사", async (progress, token) =>
        {
            await WithPasswordAsync(archive, _password, async candidate =>
            {
                await _service.TestAsync(archive, candidate, progress, token);
                _password = candidate;
                return true;
            }, token);
            return "압축 파일의 모든 항목을 검사했습니다.";
        });
    }

    private async Task<T> WithPasswordAsync<T>(string archive, string? password, Func<string?, Task<T>> work,
        CancellationToken token, Action? beforePrompt = null)
    {
        while (true)
        {
            try { return await work(password); }
            catch (ArchiveException error) when (error.IsPasswordError)
            {
                token.ThrowIfCancellationRequested();
                beforePrompt?.Invoke();
                password = TaskDialog.AskPassword(this, archive, password != null) ?? throw new OperationCanceledException(token);
            }
        }
    }

    private async Task<T> PromptAsync<T>(Func<T> prompt)
    {
        await _promptGate.WaitAsync();
        try
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                _job?.Token.ThrowIfCancellationRequested();
                return prompt();
            });
        }
        finally { _promptGate.Release(); }
    }

    private T DuringDialog<T>(Func<T> show)
    {
        _dialogOpen = true;
        try { return show(); }
        finally
        {
            _dialogOpen = false;
            ScheduleRequests();
        }
    }

    private async Task<bool> RunJobAsync(string title,
        Func<IProgress<ArchiveProgress>, CancellationToken, Task<string?>> work, bool forDrag = false)
    {
        if (_job != null || _nativeDrag || _closeRequested) return false;
        if (!forDrag) DiscardPreparedDrag();
        using var cancellation = new CancellationTokenSource();
        _job = cancellation;
        _jobCollisionOverride = null;
        _lastOutputPath = null;
        OpenResultButton.Visibility = Visibility.Collapsed;
        JobProgress.Value = 0;
        JobProgress.IsIndeterminate = true;
        OverallProgressText.Text = "전체 진행률 · 준비 중";
        ProgressCountText.Text = "";
        CurrentProgressPanel.Visibility = Visibility.Collapsed;
        SetStatus($"{title} 중");
        AddLog($"{title} 시작");
        UpdateControls();
        var elapsed = Stopwatch.StartNew();
        var completedByJob = new Dictionary<string, ulong>();
        var lastTotalJobs = 0;
        var progress = new Progress<ArchiveProgress>(report =>
        {
            if (_job != cancellation || cancellation.IsCancellationRequested) return;
            double? currentPercent = report.TotalBytes > 0
                ? Math.Clamp(100d * report.CompletedBytes / report.TotalBytes, 0, 100) : null;
            var overallPercent = report.OverallPercent ?? currentPercent;
            JobProgress.IsIndeterminate = overallPercent == null;
            if (overallPercent is { } value) JobProgress.Value = Math.Clamp(value, 0, 100);
            OverallProgressText.Text = overallPercent == null ? "전체 진행률 · 준비 중"
                : $"전체 진행률 · {Math.Floor(JobProgress.Value):0}%";
            lastTotalJobs = report.TotalJobs;
            bool batch = report.TotalJobs > 1;
            ProgressCountText.Text = batch ? $"압축 파일 수 기준 · {report.CompletedJobs}/{report.TotalJobs}개 완료" : "";
            CurrentProgressPanel.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
            CurrentJobProgress.IsIndeterminate = currentPercent == null;
            CurrentJobProgress.Value = currentPercent ?? 0;
            CurrentProgressText.Text = currentPercent == null ? "현재 압축 파일 · 준비 중"
                : $"현재 압축 파일 · {Math.Floor(currentPercent.Value):0}%";
            var stage = report.Stage.ToLowerInvariant() switch
            {
                "create" or "compress" => "압축",
                "extract" => "압축 풀기",
                "test" => "무결성 검사",
                "list" or "open" => "파일 읽기",
                _ => report.Stage
            };
            if (report.JobId != null)
                completedByJob[report.JobId] = Math.Max(completedByJob.GetValueOrDefault(report.JobId), report.CompletedBytes);
            var rate = elapsed.Elapsed.TotalSeconds > 0.2 && completedByJob.Count > 0
                ? $" · {completedByJob.Values.Sum(value => (double)value) / elapsed.Elapsed.TotalSeconds / 1048576:0.0} MiB/s" : "";
            SetStatus($"{title} 중{rate}", string.IsNullOrWhiteSpace(report.CurrentFile)
                ? stage : $"{stage} · {report.CurrentFile}");
        });
        try
        {
            var detail = await work(progress, cancellation.Token);
            JobProgress.Value = 100;
            OverallProgressText.Text = "전체 진행률 · 100%";
            if (lastTotalJobs > 1) ProgressCountText.Text = $"압축 파일 수 기준 · {lastTotalJobs}/{lastTotalJobs}개 완료";
            if (_lastOutputPath != null && (File.Exists(_lastOutputPath) || Directory.Exists(_lastOutputPath)))
                OpenResultButton.Visibility = Visibility.Visible;
            SetStatus($"{title} 완료", detail);
            AddLog($"{title} 완료{(detail == null ? "" : $" · {detail}")}");
            return true;
        }
        catch (OperationCanceledException)
        {
            JobProgress.Value = 0;
            OverallProgressText.Text = "전체 진행률 · 취소됨";
            SetStatus($"{title} 취소됨");
            AddLog($"{title} 취소됨");
        }
        catch (Exception error)
        {
            ReleaseGesture();
            JobProgress.Value = 0;
            OverallProgressText.Text = "전체 진행률 · 실패";
            SetStatus($"{title} 실패", error.Message);
            AddLog($"{title} 실패 · {error.Message}");
            if (!_closeRequested) TaskDialog.ShowError(this, $"{title} 실패", error.Message);
        }
        finally
        {
            _job = null;
            _jobCollisionOverride = null;
            JobProgress.IsIndeterminate = false;
            CurrentProgressPanel.Visibility = Visibility.Collapsed;
            CurrentJobProgress.IsIndeterminate = false;
            UpdateControls();
            ScheduleRequests();
        }
        return false;
    }

    private void SetStatus(string title, string? detail = null)
    {
        if (TaskTitle.Text != title) TaskTitle.Text = title;
        TaskDetail.Text = detail ?? "";
        TaskDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string DescribeOutputs(IReadOnlyList<string> outputs)
    {
        foreach (var output in outputs.Take(8)) AddLog(output);
        if (outputs.Count > 8) AddLog($"외 {outputs.Count - 8:N0}개 항목");
        _lastOutputPath = outputs.Count switch
        {
            0 => null,
            1 => outputs[0],
            _ => CommonOutputDirectory(outputs)
        };
        return outputs.Count switch
        {
            0 => "저장된 항목이 없습니다.",
            1 => outputs[0],
            _ => $"{outputs.Count:N0}개 항목 저장"
        };
    }

    private static string? CommonOutputDirectory(IReadOnlyList<string> outputs)
    {
        var directory = Path.GetDirectoryName(outputs[0]);
        foreach (var output in outputs.Skip(1))
        {
            var current = Path.GetDirectoryName(output);
            while (!string.IsNullOrEmpty(directory) && current != null
                && !current.Equals(directory, StringComparison.OrdinalIgnoreCase)
                && !current.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(directory);
            }
        }
        return directory;
    }

    private void OnOpenResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(_lastOutputPath)) start.Arguments = $"/select,\"{_lastOutputPath}\"";
            else if (Directory.Exists(_lastOutputPath)) start.ArgumentList.Add(_lastOutputPath);
            else return;
            Process.Start(start);
        }
        catch (Exception error)
        {
            TaskDialog.ShowError(this, "탐색기를 열 수 없습니다", error.Message);
        }
    }

    private void AddLog(string message)
    {
        if (LogList.Items.Count >= 120) LogList.Items.RemoveAt(0);
        var entry = $"{DateTime.Now:HH:mm:ss}  {message.Replace('\r', ' ').Replace('\n', ' ')}";
        LogList.Items.Add(entry);
        if (LogToggle.IsChecked == true) LogList.ScrollIntoView(entry);
    }

    private void OnLogOpened(object sender, RoutedEventArgs e)
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelCurrentWork();

    private void CancelCurrentWork()
    {
        if (_job is { IsCancellationRequested: false })
        {
            _job.Cancel();
            SetStatus("작업 취소 중", "임시 파일과 작업 상태를 정리하고 있습니다.");
        }
        else if (_preparedDrag != null)
        {
            DiscardPreparedDrag();
            SetStatus("끌어서 복사 취소됨");
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        var idle = _job == null && !_nativeDrag && !_closeRequested;
        CreateButton.IsEnabled = OpenButton.IsEnabled = ConvertButton.IsEnabled = idle;
        ExtractButton.IsEnabled = TestButton.IsEnabled = Navigation.IsEnabled = idle && _archive != null;
        CancelButton.IsEnabled = _job is { IsCancellationRequested: false } || _preparedDrag != null;
        RootButton.IsEnabled = UpButton.IsEnabled = _folder.Length != 0 || SearchBox.Text.Length != 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer == null) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _searchTimer.Stop();
        RefreshContents();
        SearchBox.Focus();
    }

    private void RefreshContents()
    {
        DiscardPreparedDrag();
        var search = SearchBox.Text.Trim().Replace('\\', '/');
        var rows = _contents?.Browse(_folder, search) ?? [];
        EntryGrid.Tag = search.Length > 0;
        NameColumn.Header = search.Length == 0 ? "이름" : "경로";
        NameColumn.SortMemberPath = search.Length == 0 ? nameof(ArchiveRow.Name) : nameof(ArchiveRow.Path);
        foreach (var column in EntryGrid.Columns) column.SortDirection = null;
        EntryGrid.ItemsSource = rows;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _archive == null ? "압축 파일을 열어 보세요" : search.Length == 0 ? "이 폴더는 비어 있습니다" : "검색 결과가 없습니다";
        EmptyHint.Text = _archive == null ? "압축 파일을 끌어 놓거나 열기를 선택하세요."
            : search.Length == 0 ? "" : "다른 이름이나 경로로 검색해 보세요.";
        EmptyHint.Visibility = EmptyHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FolderBox.Text = _folder.Length == 0 ? "/" : "/" + _folder;
        UpdateCount();
        UpdateControls();
    }

    private void Navigate(string folder)
    {
        _folder = folder;
        SearchBox.Clear();
        _searchTimer.Stop();
        RefreshContents();
    }

    private void OnRoot(object sender, RoutedEventArgs e) => Navigate("");

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (!UpButton.IsEnabled) return;
        Navigate(SearchBox.Text.Length == 0 ? ArchiveContents.Parent(_folder) : _folder);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preparedDrag != null && !SelectedRows().SequenceEqual(_preparedDrag.Rows))
            DiscardPreparedDrag();
        UpdateCount();
        UpdateControls();
    }

    private void UpdateCount()
    {
        var count = EntryGrid.Items.Count;
        var selected = EntryGrid.SelectedItems.Count;
        var isSearch = SearchBox.Text.Trim().Length > 0;
        var scope = isSearch ? "검색 결과 " : "";
        if (_contents == null)
        {
            CountText.Text = $"{count:N0}개 항목";
            return;
        }
        if (selected > 0)
        {
            var selectedBytes = _contents.SelectionSize(SelectedRows());
            CountText.Text = $"{scope}{count:N0}개 항목 · {selected:N0}개 선택 ({ArchiveRow.FormatBytes(selectedBytes)})";
            return;
        }
        if (!isSearch && _folder.Length == 0 && _contents.FileCount > 0)
        {
            var sizeSummary = ArchiveRow.FormatBytes(_contents.TotalSize);
            if (_contents.TotalSize > 0 && _contents.TotalPackedSize > 0)
            {
                var ratio = Math.Clamp(100d * _contents.TotalPackedSize / _contents.TotalSize, 0, 999);
                CountText.Text = $"{count:N0}개 항목 · 전체 {_contents.FileCount:N0}개 파일 ({sizeSummary}, 압축 {ArchiveRow.FormatBytes(_contents.TotalPackedSize)} · {ratio:0}%)";
            }
            else
            {
                CountText.Text = $"{count:N0}개 항목 · 전체 {_contents.FileCount:N0}개 파일 ({sizeSummary})";
            }
            return;
        }
        CountText.Text = $"{scope}{count:N0}개 항목";
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e.OriginalSource)?.Item is ArchiveRow { IsDirectory: true } row && _job == null && !_nativeDrag)
            Navigate(row.Path);
    }

    private void OnRowContextClick(object sender, MouseButtonEventArgs e)
    {
        if (_job == null && !_nativeDrag && RowFrom(e.OriginalSource) is { IsSelected: false } row)
            EntryGrid.SelectedItem = row.Item;
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _job != null || _nativeDrag ||
            EntryGrid.SelectedItem is not ArchiveRow { IsDirectory: true } row) return;
        Navigate(row.Path);
        e.Handled = true;
    }

    private void OnEntryMenuOpened(object sender, RoutedEventArgs e)
    {
        OpenFolderMenu.IsEnabled = ExtractButton.IsEnabled &&
            EntryGrid.SelectedItems.Count == 1 && EntryGrid.SelectedItem is ArchiveRow { IsDirectory: true };
        ExtractSelectionMenu.IsEnabled = ExtractButton.IsEnabled && EntryGrid.SelectedItems.Count > 0;
        CopyPathMenu.IsEnabled = EntryGrid.SelectedItems.Count > 0;
        SelectAllMenu.IsEnabled = EntryGrid.Items.Count > 0;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (OpenFolderMenu.IsEnabled && EntryGrid.SelectedItem is ArchiveRow row) Navigate(row.Path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(row => row.Path))); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.Items.Count > 0) EntryGrid.SelectAll();
    }

    private async void OnShortcut(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.F when SearchBox.IsEnabled:
                    e.Handled = true;
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    break;
                case Key.O when OpenButton.IsEnabled:
                    e.Handled = true;
                    await ShowOpenAsync();
                    break;
                case Key.N when CreateButton.IsEnabled:
                    e.Handled = true;
                    await ShowCompressionAsync([]);
                    break;
                case Key.E when ExtractButton.IsEnabled:
                    e.Handled = true;
                    await ShowExtractionAsync(false);
                    break;
                case Key.T when TestButton.IsEnabled:
                    e.Handled = true;
                    await TestArchiveAsync();
                    break;
                case Key.D7 or Key.NumPad7 when ConvertButton.IsEnabled:
                    e.Handled = true;
                    IReadOnlyList<string> initial = _archive != null && ArchiveService.IsZipArchivePath(_archive) ? [_archive] : [];
                    await ShowConvertAsync(initial, initial.Count > 0 ? _password : null);
                    break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System && e.SystemKey == Key.Up && UpButton.IsEnabled)
        {
            OnUp(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && OpenButton.IsEnabled && _archive != null)
        {
            e.Handled = true;
            await OpenArchiveAsync(_archive);
        }
        else if (e.Key == Key.Back && !SearchBox.IsKeyboardFocusWithin && UpButton.IsEnabled)
        {
            OnUp(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        {
            OnClearSearch(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && CancelButton.IsEnabled)
        {
            CancelCurrentWork();
            e.Handled = true;
        }
    }

    private void OnFilesDragEnter(object sender, DragEventArgs e) => UpdateDragOverlay(e);

    private void OnFilesDragOver(object sender, DragEventArgs e) => UpdateDragOverlay(e);

    private void OnFilesDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void UpdateDragOverlay(DragEventArgs e)
    {
        var canDrop = CreateButton.IsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = canDrop ? e.AllowedEffects & DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!canDrop)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            if (files.Length == 1 && File.Exists(files[0]) && ArchiveService.IsArchivePath(files[0]))
            {
                DropOverlayTitle.Text = "압축 파일 열기";
                DropOverlayHint.Text = Path.GetFileName(files[0]);
            }
            else if (files.Length > 1 && files.All(path => File.Exists(path) && ArchiveService.IsArchivePath(path)))
            {
                DropOverlayTitle.Text = $"{files.Length:N0}개 압축 파일 풀기";
                DropOverlayHint.Text = "여기에 놓으면 압축 풀기 옵션을 엽니다.";
            }
            else
            {
                DropOverlayTitle.Text = "새 압축 만들기";
                DropOverlayHint.Text = files.Length == 1
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(files[0]))
                    : $"{files.Length:N0}개 항목을 새 압축으로 묶습니다.";
            }
        }
        DropOverlay.Visibility = Visibility.Visible;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!CreateButton.IsEnabled || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (files.Length == 1 && File.Exists(files[0]) && ArchiveService.IsArchivePath(files[0]))
            await OpenArchiveAsync(files[0]);
        else if (files.Length > 1 && files.All(path => File.Exists(path) && ArchiveService.IsArchivePath(path)))
        {
            var dialog = new ExtractionDialog(files) { Owner = this };
            if (DuringDialog(dialog.ShowDialog) == true && dialog.Options is { } options)
                await ExtractBatchAsync(files, options);
        }
        else
            await ShowCompressionAsync(files);
    }

    private ArchiveRow[] SelectedRows() => EntryGrid.SelectedItems.Cast<ArchiveRow>().ToArray();

    private DataGridRow? RowFrom(object source) => source is DependencyObject element
        ? ItemsControl.ContainerFromElement(EntryGrid, element) as DataGridRow : null;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        DiscardPreparedDrag();
        if (_job == null && !_nativeDrag && _dragCleanup.IsCompleted && !ShellDragIsActive())
        {
            _searchTimer.Stop();
            _password = null;
            return;
        }
        e.Cancel = true;
        _closeRequested = true;
        CancelCurrentWork();
        if (_waitingToClose) return;
        _waitingToClose = true;
        _ = WaitToCloseAsync();
    }

    private async Task WaitToCloseAsync()
    {
        do { await Task.Delay(250); }
        while (_job != null || _nativeDrag || !_dragCleanup.IsCompleted || ShellDragIsActive());
        Close();
    }
}

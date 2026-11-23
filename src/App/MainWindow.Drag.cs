using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SevenZipper.Core;

namespace SevenZipper.App;

public partial class MainWindow
{
    private sealed record PreparedDrag(DragPreparation Staging, string[] Paths, ArchiveRow[] Rows);

    private bool _nativeDrag;
    private PreparedDrag? _preparedDrag;
    private Task _dragCleanup = Task.CompletedTask;
    private Point? _pressedAt;
    private int _gesture;
    private ArchiveRow? _deferredClick;

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = null;
        _deferredClick = null;
        _gesture++;
        if (_job != null || _nativeDrag || e.ClickCount != 1 || RowFrom(e.OriginalSource) is not { Item: ArchiveRow row } container)
            return;
        _pressedAt = e.GetPosition(EntryGrid);
        if (container.IsSelected && EntryGrid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // A plain mouse click collapses the selection only if it ends without starting a drag.
            _deferredClick = row;
            EntryGrid.Focus();
            Mouse.Capture(EntryGrid, CaptureMode.SubTree);
            e.Handled = true;
        }
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = null;
        if (Mouse.Captured == EntryGrid) Mouse.Capture(null);
        if (_deferredClick is not { } row) return;
        _deferredClick = null;
        if (RowFrom(e.OriginalSource)?.Item is ArchiveRow item && item == row)
        {
            EntryGrid.SelectedItem = row;
            EntryGrid.CurrentItem = row;
        }
        e.Handled = true;
    }

    private async void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || _archive == null || _contents == null || _job != null || _nativeDrag) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedAt = null;
            _deferredClick = null;
            return;
        }
        var position = e.GetPosition(EntryGrid);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var rows = SelectedRows();
        if (rows.Length == 0) return;
        e.Handled = true;
        _deferredClick = null;
        if (_preparedDrag is { } ready && rows.SequenceEqual(ready.Rows))
        {
            BeginNativeDrag(ready);
            return;
        }
        DiscardPreparedDrag();
        var gesture = _gesture;
        var archive = _archive;
        var contents = _contents;
        DragPreparation? staging = null;
        string[]? paths = null;
        if (ShellDragIsActive())
        {
            ReleaseGesture();
            SetStatus("탐색기로 복사 중", "현재 복사가 끝난 뒤 다시 끌어 주세요.");
            return;
        }
        Mouse.Capture(EntryGrid, CaptureMode.SubTree);
        var prepared = await RunJobAsync("끌어서 복사 준비", async (progress, token) =>
        {
            var indices = await Task.Run(() => contents.Selection(rows), token);
            staging = await WithPasswordAsync(archive, _password, async candidate =>
            {
                var result = await _service.PrepareDragAsync(archive, indices, candidate, progress, token);
                _password = candidate;
                return result;
            }, token, ReleaseGesture);
            token.ThrowIfCancellationRequested();
            paths = await Task.Run(() => GetDropPaths(staging, rows), token);
            return null;
        }, forDrag: true);
        if (!prepared || _closeRequested || staging == null || paths == null || !rows.SequenceEqual(SelectedRows()))
        {
            ReleaseGesture();
            if (staging != null) DisposeStaging(staging);
            if (prepared && !_closeRequested)
                SetStatus("끌어서 복사 준비 취소됨", "현재 선택 항목을 다시 끌어 주세요.");
            return;
        }
        var drag = _preparedDrag = new PreparedDrag(staging, paths, rows);
        if (_gesture == gesture && _pressedAt.HasValue && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            BeginNativeDrag(drag);
            return;
        }
        ReleaseGesture();
        SetStatus("끌어서 복사 준비됨", "선택 항목을 다시 끌어서 탐색기에 놓으세요.");
        UpdateControls();
    }

    private static string[] GetDropPaths(DragPreparation staging, ArchiveRow[] rows)
    {
        // Archives may store the files in a folder without including an entry for the folder itself.
        var prefix = Path.TrimEndingDirectorySeparator(staging.Directory) + Path.DirectorySeparatorChar;
        var implicitFolders = rows.Where(row => row.IsDirectory && row.Entry == null).Select(row =>
        {
            var path = Path.GetFullPath(Path.Combine(staging.Directory, row.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
                throw new ArchiveException("선택한 폴더를 복사용 임시 폴더에서 찾을 수 없습니다.");
            return path;
        });
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in staging.Paths.Concat(implicitFolders).OrderBy(path => path.Length))
        {
            var parent = Path.GetDirectoryName(path);
            while (parent != null && parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !selected.Contains(parent))
                parent = Path.GetDirectoryName(parent);
            if (parent == null || !selected.Contains(parent)) selected.Add(path);
        }
        return selected.ToArray();
    }

    private void ReleaseGesture()
    {
        _pressedAt = null;
        _deferredClick = null;
        if (Mouse.Captured == EntryGrid) Mouse.Capture(null);
    }

    private void DiscardPreparedDrag()
    {
        if (_preparedDrag is not { } drag) return;
        _preparedDrag = null;
        DisposeStaging(drag.Staging);
    }

    private void DisposeStaging(DragPreparation staging)
    {
        var cleanup = Task.Run(() =>
        {
            try { staging.Dispose(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArchiveException) { }
        });
        _dragCleanup = _dragCleanup.IsCompleted ? cleanup : Task.WhenAll(_dragCleanup, cleanup);
    }

    private void BeginNativeDrag(PreparedDrag drag)
    {
        _preparedDrag = null;
        ReleaseGesture();
        _nativeDrag = true;
        var handedOff = false;
        SetStatus("탐색기에 놓을 위치 선택");
        UpdateControls();
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var result = sz_drag_files(handle, drag.Paths, drag.Paths.Length, drag.Staging.Directory);
            handedOff = true;
            Marshal.ThrowExceptionForHR(result);
            if (result == 0x00040101) SetStatus("끌어서 복사 취소됨");
            else
            {
                SetStatus(ShellDragIsActive() ? "탐색기로 복사 중" : "끌어서 복사 완료");
                AddLog($"{drag.Paths.Length:N0}개 항목을 탐색기에 전달");
                if (ShellDragIsActive()) _ = ObserveCopyAsync();
            }
        }
        catch (Exception error)
        {
            SetStatus("끌어서 복사 실패", error.Message);
            AddLog($"끌어서 복사 실패 · {error.Message}");
            if (!_closeRequested) TaskDialog.ShowError(this, "끌어서 복사 실패", error.Message);
        }
        finally
        {
            if (!handedOff) DisposeStaging(drag.Staging);
            _nativeDrag = false;
            UpdateControls();
            ScheduleRequests();
        }
    }

    private async Task ObserveCopyAsync()
    {
        while (ShellDragIsActive()) await Task.Delay(250);
        if (TaskTitle.Text == "탐색기로 복사 중") SetStatus("탐색기 복사 작업 종료");
    }

    private static bool ShellDragIsActive()
    {
        try { return sz_drag_active() != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [DllImport("SevenZipper.Shell.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int sz_drag_files(nint ownerHwnd,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths,
        int count, string stagingDirectory);

    [DllImport("SevenZipper.Shell.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int sz_drag_active();
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SevenZipper.Core;

namespace SevenZipper.App;

internal partial class ConvertDialog : Window
{
    private sealed record ZipItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    private const ulong MiB = 1024 * 1024;
    private readonly ObservableCollection<ZipItem> _inputs = [];
    private readonly string? _sourcePassword;
    private bool _syncingPassword;

    public ConvertDialog(IEnumerable<string> inputs, string? sourcePassword = null)
    {
        InitializeComponent();
        _sourcePassword = sourcePassword;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        InputList.ItemsSource = _inputs;
        LevelBox.ItemsSource = new Choice<int>[]
        {
            new("저장만 (무압축)", 0),
            new("빠르게", 3),
            new("보통", 5),
            new("최대 (권장)", 9)
        };
        LevelBox.SelectedIndex = 3; // 기본값: 최대 압축률 (Level 9)
        VolumeBox.ItemsSource = new Choice<ulong?>[]
        {
            new("분할 안 함", 0), new("100 MiB", 100 * MiB), new("650 MiB", 650 * MiB),
            new("700 MiB", 700 * MiB), new("1 GiB", 1024 * MiB), new("2 GiB", 2048 * MiB),
            new("4 GiB", 4096 * MiB), new("직접 입력", null)
        };
        VolumeBox.SelectedIndex = 0;
        ThreadsBox.Text = Environment.ProcessorCount.ToString();
        AddInputs(inputs);
    }

    public IReadOnlyList<string> Inputs => _inputs.Select(item => item.Path).ToArray();
    public ConversionOptions? Options { get; private set; }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "변환할 ZIP 파일 선택", Filter = ArchivePaths.ZipFilter, Multiselect = true };
        if (picker.ShowDialog(this) == true) AddInputs(picker.FileNames);
    }

    private void AddInputs(IEnumerable<string> inputs)
    {
        var known = _inputs.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = Path.GetFullPath(raw);
            if (!ArchiveService.IsZipArchivePath(normalized) || Directory.Exists(normalized))
            {
                skipped++;
                continue;
            }
            if (known.Add(normalized)) _inputs.Add(new ZipItem(normalized));
        }
        if (skipped > 0)
        {
            ErrorText.Text = "ZIP 파일(.zip, .z01 등)만 변환 목록에 추가할 수 있습니다.";
            ErrorText.Visibility = Visibility.Visible;
        }
        else if (_inputs.Count > 0)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        if (string.IsNullOrWhiteSpace(OutputBox.Text) && _inputs.Count > 0)
            OutputBox.Text = Path.GetDirectoryName(_inputs[0].Path) ?? "";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        SummaryText.Text = _inputs.Count switch
        {
            0 => "여러 ZIP 파일을 선택하면 각각 별도의 7z 파일로 변환합니다. (기본: 최대 압축)",
            1 => "선택한 ZIP 파일을 7z 파일로 변환합니다. (기본: 최대 압축)",
            _ => $"선택한 {_inputs.Count:N0}개 ZIP 파일을 각각 7z 파일로 변환합니다. (기본: 최대 압축)"
        };
    }

    private void OnInputDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.AllowedEffects & DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            AddInputs(files);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && RemoveButton.IsEnabled)
        {
            OnRemove(sender, e);
            e.Handled = true;
        }
    }

    private void OnInputSelection(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = InputList.SelectedItems.Count > 0;

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var item in InputList.SelectedItems.Cast<ZipItem>().ToArray()) _inputs.Remove(item);
        UpdateSummary();
    }

    private void OnSameFolderChanged(object sender, RoutedEventArgs e)
    {
        if (OutputDirectoryPanel == null) return;
        OutputDirectoryPanel.Visibility = SameFolderBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "변환된 7z 파일을 저장할 폴더",
            InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : null
        };
        if (picker.ShowDialog(this) == true) OutputBox.Text = picker.FolderName;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        UpdatePasswordState();
    }

    private void OnPasswordTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        UpdatePasswordState();
    }

    private void OnShowPasswordChanged(object sender, RoutedEventArgs e)
    {
        _syncingPassword = true;
        try
        {
            if (ShowPasswordBox.IsChecked == true)
            {
                PasswordText.Text = PasswordInput.Password;
                PasswordInput.Visibility = Visibility.Collapsed;
                PasswordText.Visibility = Visibility.Visible;
                PasswordText.CaretIndex = PasswordText.Text.Length;
                PasswordText.Focus();
            }
            else
            {
                PasswordInput.Password = PasswordText.Text;
                PasswordText.Clear();
                PasswordText.Visibility = Visibility.Collapsed;
                PasswordInput.Visibility = Visibility.Visible;
                PasswordInput.Focus();
            }
        }
        finally { _syncingPassword = false; }
        UpdatePasswordState();
    }

    private bool HasPassword()
    {
        if (ShowPasswordBox.IsChecked == true) return PasswordText.Text.Length > 0;
        using var securePassword = PasswordInput.SecurePassword;
        return securePassword.Length > 0;
    }

    private string ReadPassword() => ShowPasswordBox.IsChecked == true ? PasswordText.Text : PasswordInput.Password;

    private void ClearPasswords()
    {
        _syncingPassword = true;
        try { PasswordInput.Clear(); PasswordText.Clear(); }
        finally { _syncingPassword = false; }
    }

    private void OnDialogClosed(object? sender, EventArgs e) => ClearPasswords();

    private void UpdatePasswordState()
    {
        EncryptHeadersBox.IsEnabled = HasPassword();
        if (!EncryptHeadersBox.IsEnabled) EncryptHeadersBox.IsChecked = false;
    }

    private void OnVolumeChanged(object sender, SelectionChangedEventArgs e) =>
        CustomVolume.Visibility = VolumeBox.SelectedItem is Choice<ulong?> { Value: null } ? Visibility.Visible : Visibility.Collapsed;

    private void OnPerformanceExpanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => ((FrameworkElement)sender).BringIntoView(), DispatcherPriority.Loaded);

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_inputs.Count == 0) throw new ArgumentException("변환할 ZIP 파일을 추가하세요.");
            var paths = Inputs;
            if (paths.Any(path => !File.Exists(path)))
                throw new ArgumentException("목록에 존재하지 않는 ZIP 파일이 있습니다.");
            if (paths.Any(path => !ArchiveService.IsZipArchivePath(path)))
                throw new ArgumentException("ZIP 파일만 7z로 변환할 수 있습니다.");

            string? outputDirectory = null;
            if (SameFolderBox.IsChecked != true)
            {
                outputDirectory = ArchivePaths.AbsolutePath(OutputBox.Text, "저장 위치");
                if (File.Exists(outputDirectory))
                    throw new ArgumentException("저장 위치에 같은 이름의 파일이 있습니다. 폴더 경로를 입력하세요.");
            }
            if (!int.TryParse(ThreadsBox.Text, out var threads) || threads < 1 || threads > Environment.ProcessorCount)
                throw new ArgumentException($"CPU 스레드 수는 1에서 {Environment.ProcessorCount} 사이로 입력하세요.");
            if (!ulong.TryParse(MemoryBox.Text, out var memory) || memory > ulong.MaxValue / MiB)
                throw new ArgumentException("메모리 한도를 MiB 단위의 정수로 입력하세요.");
            if (memory != 0 && memory < 64)
                throw new ArgumentException("메모리 한도는 0(자동) 또는 64 MiB 이상으로 입력하세요.");

            var password = ReadPassword();
            Options = new ConversionOptions
            {
                Level = ((Choice<int>)LevelBox.SelectedItem).Value,
                Threads = threads,
                MemoryLimitBytes = memory * MiB,
                VolumeBytes = ReadVolume(),
                OutputDirectory = outputDirectory,
                SourcePassword = _sourcePassword,
                OutputPassword = password.Length == 0 ? null : password,
                EncryptHeaders = EncryptHeadersBox.IsChecked == true,
                DeleteSourceAfterSuccess = DeleteSourceBox.IsChecked == true
            };
            ClearPasswords();
            DialogResult = true;
        }
        catch (ArgumentException error)
        {
            ErrorText.Text = error.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private ulong ReadVolume()
    {
        if (VolumeBox.SelectedItem is Choice<ulong?> { Value: ulong preset }) return preset;
        var unit = 1UL << (10 * (VolumeUnit.SelectedIndex + 1));
        if (!ulong.TryParse(VolumeAmount.Text, out var amount) || amount == 0 || amount > ulong.MaxValue / unit)
            throw new ArgumentException("분할 크기를 0보다 큰 정수로 입력하세요.");
        var volume = amount * unit;
        if (volume < 64 * 1024) throw new ArgumentException("분할 크기는 64 KiB 이상이어야 합니다.");
        return volume;
    }
}

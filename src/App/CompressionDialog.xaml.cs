using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SevenZipper.Core;

namespace SevenZipper.App;

internal partial class CompressionDialog : Window
{
    private sealed record SourceItem(string Path)
    {
        public string Name
        {
            get
            {
                var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
                return string.IsNullOrEmpty(name) ? Path : name;
            }
        }
    }

    private const ulong MiB = 1024 * 1024;
    private readonly ObservableCollection<SourceItem> _inputs = [];
    private string _suggestedOutput = "";
    private bool _initialized;
    private bool _syncingPassword;

    public CompressionDialog(IEnumerable<string> inputs)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        InputList.ItemsSource = _inputs;
        FormatBox.ItemsSource = new[] { "7z", "ZIP", "ZIP · Zstandard" };
        FormatBox.SelectedIndex = 0;
        UpdateLevelChoices(defaultNormal: true);
        VolumeBox.ItemsSource = new Choice<ulong?>[]
        {
            new("분할 안 함", 0), new("100 MiB", 100 * MiB), new("650 MiB", 650 * MiB),
            new("700 MiB", 700 * MiB), new("1 GiB", 1024 * MiB), new("2 GiB", 2048 * MiB),
            new("4 GiB", 4096 * MiB), new("직접 입력", null)
        };
        VolumeBox.SelectedIndex = 0;
        ThreadsBox.Text = Environment.ProcessorCount.ToString();
        _initialized = true;
        AddInputs(inputs);
    }

    public IReadOnlyList<string> Inputs => _inputs.Select(item => item.Path).ToArray();
    public CompressionOptions? Options { get; private set; }

    private ArchiveFormat Format => FormatBox.SelectedIndex switch
    {
        1 => ArchiveFormat.Zip,
        2 => ArchiveFormat.ZipZstandard,
        _ => ArchiveFormat.SevenZip
    };

    private string Extension => Format == ArchiveFormat.SevenZip ? ".7z" : ".zip";

    private void UpdateLevelChoices(bool defaultNormal = false)
    {
        var previousLabel = (LevelBox.SelectedItem as Choice<int>)?.Label;
        Choice<int>[] choices = Format == ArchiveFormat.ZipZstandard
            ? [new("빠르게", 1), new("보통", 3), new("최대", 9)]
            : [new("저장만 (무압축)", 0), new("빠르게", 3), new("보통", 5), new("최대", 9)];
        LevelBox.ItemsSource = choices;
        var index = defaultNormal ? Array.FindIndex(choices, item => item.Label == "보통")
            : Array.FindIndex(choices, item => item.Label == previousLabel);
        LevelBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "압축할 파일 선택", Multiselect = true };
        if (picker.ShowDialog(this) == true) AddInputs(picker.FileNames);
    }

    private void OnAddFolders(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "압축할 폴더 선택", Multiselect = true };
        if (picker.ShowDialog(this) == true) AddInputs(picker.FolderNames);
    }

    private void AddInputs(IEnumerable<string> inputs)
    {
        var known = _inputs.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (known.Add(normalized)) _inputs.Add(new SourceItem(normalized));
        }
        UpdateSuggestedOutput();
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
        foreach (var item in InputList.SelectedItems.Cast<SourceItem>().ToArray()) _inputs.Remove(item);
        UpdateSuggestedOutput();
    }

    private void UpdateSuggestedOutput(bool replace = false)
    {
        var suggestion = _inputs.Count == 0 ? "" : ArchivePaths.SuggestOutput(
            Inputs, Extension, PerFolderBox.IsChecked == true);
        if (replace || OutputBox.Text.Length == 0 || OutputBox.Text == _suggestedOutput)
            OutputBox.Text = suggestion;
        _suggestedOutput = suggestion;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateLevelChoices();
        UpdatePasswordState();
        UpdateSuggestedOutput();
        if (PerFolderBox.IsChecked != true && OutputBox.Text.Length > 0)
            OutputBox.Text = WithArchiveExtension(OutputBox.Text);
    }

    private void OnPerFolderChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        OutputLabel.Content = PerFolderBox.IsChecked == true ? "저장할 폴더 경로" : "저장할 압축 파일 경로";
        UpdateSuggestedOutput(replace: true);
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
        EncryptHeadersBox.IsEnabled = Format == ArchiveFormat.SevenZip && HasPassword();
        if (!EncryptHeadersBox.IsEnabled) EncryptHeadersBox.IsChecked = false;
    }

    private void OnVolumeChanged(object sender, SelectionChangedEventArgs e) =>
        CustomVolume.Visibility = VolumeBox.SelectedItem is Choice<ulong?> { Value: null } ? Visibility.Visible : Visibility.Collapsed;

    private void OnPerformanceExpanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => ((FrameworkElement)sender).BringIntoView(), DispatcherPriority.Loaded);

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        if (PerFolderBox.IsChecked == true)
        {
            var folder = new OpenFolderDialog { Title = "압축 파일을 저장할 폴더", InitialDirectory = ExistingDirectory(OutputBox.Text) };
            if (folder.ShowDialog(this) == true) OutputBox.Text = folder.FolderName;
            return;
        }
        var picker = new SaveFileDialog
        {
            Title = "압축 파일 저장",
            InitialDirectory = ExistingDirectory(OutputBox.Text),
            FileName = Path.GetFileName(OutputBox.Text),
            DefaultExt = Extension,
            Filter = Format == ArchiveFormat.SevenZip ? "7z 압축 파일|*.7z" : "ZIP 압축 파일|*.zip",
            OverwritePrompt = false,
            AddExtension = true
        };
        if (picker.ShowDialog(this) == true) OutputBox.Text = picker.FileName;
    }

    private static string? ExistingDirectory(string text)
    {
        var directory = Directory.Exists(text) ? text : Path.GetDirectoryName(text);
        return Directory.Exists(directory) ? directory : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_inputs.Count == 0) throw new ArgumentException("압축할 파일 또는 폴더를 추가하세요.");
            var paths = Inputs;
            if (paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                throw new ArgumentException("목록에 존재하지 않는 파일이나 폴더가 있습니다.");
            if (paths.Any(path => string.IsNullOrEmpty(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))))
                throw new ArgumentException("드라이브 전체 대신 하위 폴더를 선택하세요.");

            var perFolder = PerFolderBox.IsChecked == true;
            if (perFolder && paths.Any(path => !Directory.Exists(path)))
                throw new ArgumentException("폴더별 압축을 사용하려면 목록에 폴더만 남겨 주세요.");

            var output = ArchivePaths.AbsolutePath(OutputBox.Text, "저장 위치");
            if (perFolder)
            {
                if (File.Exists(output))
                    throw new ArgumentException("저장 위치에 같은 이름의 파일이 있습니다. 폴더 경로를 입력하세요.");
                if (paths.Any(path => output.Equals(path, StringComparison.OrdinalIgnoreCase)
                    || output.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("폴더별 압축의 저장 위치는 원본 폴더 바깥으로 지정하세요.");
            }
            else
            {
                output = WithArchiveExtension(output);
                if (paths.Any(path => string.Equals(path, output, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("원본 파일을 저장 위치로 지정할 수 없습니다.");
                if (ArchiveService.OutputExists(output))
                    throw new ArgumentException("같은 이름의 압축 파일, 분할 볼륨 또는 폴더가 있습니다. 다른 저장 경로를 입력하세요.");
            }
            if (!int.TryParse(ThreadsBox.Text, out var threads) || threads < 1 || threads > Environment.ProcessorCount)
                throw new ArgumentException($"CPU 스레드 수는 1에서 {Environment.ProcessorCount} 사이로 입력하세요.");
            if (!ulong.TryParse(MemoryBox.Text, out var memory) || memory > ulong.MaxValue / MiB)
                throw new ArgumentException("메모리 한도를 MiB 단위의 정수로 입력하세요.");
            if (memory != 0 && memory < 64)
                throw new ArgumentException("메모리 한도는 0(자동) 또는 64 MiB 이상으로 입력하세요.");

            var level = ((Choice<int>)LevelBox.SelectedItem).Value;
            var password = ReadPassword();
            Options = new CompressionOptions
            {
                Format = Format, Level = level, Threads = threads, MemoryLimitBytes = memory * MiB,
                VolumeBytes = ReadVolume(), Password = password.Length == 0 ? null : password,
                EncryptHeaders = EncryptHeadersBox.IsChecked == true, PerFolder = perFolder, OutputPath = output
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

    private string WithArchiveExtension(string value)
    {
        var extension = Path.GetExtension(value);
        if (extension.Equals(Extension, StringComparison.OrdinalIgnoreCase)) return value;
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(value, Extension)
            : value + Extension;
    }
}

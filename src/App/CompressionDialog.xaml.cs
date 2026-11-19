using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SevenZipper.Core;

namespace SevenZipper.App;

internal partial class CompressionDialog : Window
{
    private sealed record SourceItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
    }

    private const ulong MiB = 1024 * 1024;
    private readonly ObservableCollection<SourceItem> _inputs = [];
    private string _suggestedOutput = "";
    private bool _initialized;

    public CompressionDialog(IEnumerable<string> inputs)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        InputList.ItemsSource = _inputs;
        FormatBox.ItemsSource = new[] { "7z", "ZIP", "ZIP · Zstandard" };
        FormatBox.SelectedIndex = 0;
        LevelBox.ItemsSource = new[] { "빠르게", "보통", "최대" };
        LevelBox.SelectedIndex = 1;
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
        foreach (var item in inputs)
            if (known.Add(item)) _inputs.Add(new SourceItem(item));
        UpdateSuggestedOutput();
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

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdatePasswordState();

    private void UpdatePasswordState()
    {
        using var securePassword = PasswordInput.SecurePassword;
        EncryptHeadersBox.IsEnabled = Format == ArchiveFormat.SevenZip && securePassword.Length > 0;
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

            var perFolder = PerFolderBox.IsChecked == true;
            if (perFolder && paths.Any(path => !Directory.Exists(path)))
                throw new ArgumentException("폴더별 압축을 사용하려면 목록에 폴더만 남겨 주세요.");

            var output = ArchivePaths.AbsolutePath(OutputBox.Text, "저장 위치");
            if (!perFolder) output = WithArchiveExtension(output);
            if (!perFolder && File.Exists(output))
                throw new ArgumentException("같은 이름의 압축 파일이 있습니다. 다른 저장 경로를 입력하세요.");
            if (!int.TryParse(ThreadsBox.Text, out var threads) || threads < 1 || threads > Environment.ProcessorCount)
                throw new ArgumentException($"CPU 스레드 수는 1에서 {Environment.ProcessorCount} 사이로 입력하세요.");
            if (!ulong.TryParse(MemoryBox.Text, out var memory) || memory > ulong.MaxValue / MiB)
                throw new ArgumentException("메모리 한도를 MiB 단위의 정수로 입력하세요.");

            var level = Format == ArchiveFormat.ZipZstandard ? new[] { 1, 3, 9 } : new[] { 3, 5, 9 };
            var password = PasswordInput.Password;
            Options = new CompressionOptions
            {
                Format = Format, Level = level[LevelBox.SelectedIndex], Threads = threads, MemoryLimitBytes = memory * MiB,
                VolumeBytes = ReadVolume(), Password = password.Length == 0 ? null : password,
                EncryptHeaders = EncryptHeadersBox.IsChecked == true, PerFolder = perFolder, OutputPath = output
            };
            PasswordInput.Clear();
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

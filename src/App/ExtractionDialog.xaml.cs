using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SevenZipper.Core;

namespace SevenZipper.App;

internal partial class ExtractionDialog : Window
{
    private bool _batch;

    public ExtractionDialog(string archive, string? password, uint[] selection, bool selectionOnly, int allCount)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        ArchiveLabel.Text = Path.GetFileName(archive);
        ArchiveLabel.ToolTip = archive;
        DestinationBox.Text = Path.GetDirectoryName(archive);
        PasswordInput.Password = password ?? "";
        ModeBox.ItemsSource = new Choice<ExtractionMode>[]
        {
            new("스마트", ExtractionMode.Smart), new("여기에", ExtractionMode.Here),
            new("압축 파일 이름으로 폴더 만들기", ExtractionMode.Folder)
        };
        ModeBox.SelectedIndex = 0;
        var scopes = new List<Choice<uint[]?>> { new(allCount < 0 ? "전체" : $"전체 ({allCount:N0}개)", null) };
        if (selection.Length > 0) scopes.Add(new($"선택 항목 ({selection.Length:N0}개)", selection));
        SelectionBox.ItemsSource = scopes;
        SelectionBox.SelectedIndex = selectionOnly && selection.Length > 0 ? 1 : 0;
        SelectionBox.IsEnabled = scopes.Count > 1;
        CollisionBox.ItemsSource = new Choice<CollisionPolicy>[]
        {
            new("물어보기", CollisionPolicy.Ask), new("건너뛰기", CollisionPolicy.Skip),
            new("이름 바꾸기", CollisionPolicy.Rename), new("덮어쓰기", CollisionPolicy.Overwrite)
        };
        CollisionBox.SelectedIndex = 0;
    }

    public ExtractionDialog(IReadOnlyList<string> archives) : this(archives[0], null, [], false, -1)
    {
        _batch = true;
        ArchiveLabel.Text = $"{archives.Count:N0}개 압축 파일";
        ArchiveLabel.ToolTip = string.Join("\n", archives.Take(10));
        DestinationBox.Clear();
        BatchDestinationHint.Visibility = Visibility.Visible;
    }

    public ExtractionOptions? Options { get; private set; }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedItem is not Choice<ExtractionMode> mode) return;
        ModeHint.Text = mode.Value switch
        {
            ExtractionMode.Here => "지정한 위치에 압축 파일의 내용을 풉니다.",
            ExtractionMode.Folder => "지정한 위치 아래에 압축 파일 이름으로 폴더를 만듭니다.",
            _ => "파일이나 최상위 폴더가 하나면 그대로 풀고, 여러 최상위 항목은 새 폴더에 모읍니다."
        };
    }

    private void OnBrowseDestination(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "압축을 풀 위치",
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : null
        };
        if (picker.ShowDialog(this) == true) DestinationBox.Text = picker.FolderName;
    }

    private void OnShowPasswordChanged(object sender, RoutedEventArgs e)
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

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        PasswordInput.Clear();
        PasswordText.Clear();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        try
        {
            var destination = _batch && string.IsNullOrWhiteSpace(DestinationBox.Text)
                ? "" : ArchivePaths.AbsolutePath(DestinationBox.Text, "압축을 풀 위치");
            if (destination.Length > 0 && File.Exists(destination))
                throw new ArgumentException("같은 이름의 파일이 있습니다. 폴더 경로를 입력하세요.");
            var password = ShowPasswordBox.IsChecked == true ? PasswordText.Text : PasswordInput.Password;
            Options = new ExtractionOptions
            {
                DestinationDirectory = destination,
                Mode = ((Choice<ExtractionMode>)ModeBox.SelectedItem).Value,
                Password = password.Length == 0 ? null : password,
                SelectedIndices = ((Choice<uint[]?>)SelectionBox.SelectedItem).Value,
                Collision = ((Choice<CollisionPolicy>)CollisionBox.SelectedItem).Value
            };
            PasswordInput.Clear();
            PasswordText.Clear();
            DialogResult = true;
        }
        catch (ArgumentException error)
        {
            ErrorText.Text = error.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}

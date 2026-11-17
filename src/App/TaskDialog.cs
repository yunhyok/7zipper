using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SevenZipper.Core;

namespace SevenZipper.App;

internal sealed class TaskDialog : Window
{
    private readonly StackPanel _body = new();
    private readonly WrapPanel _buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 13, 0, 0) };

    private TaskDialog(Window owner, string title, string message)
    {
        Owner = owner;
        Title = title;
        Style = (Style)FindResource("DialogWindow");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 475;
        MinWidth = 355;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var layout = new DockPanel { Margin = new(18) };
        var heading = new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle"), Margin = new(0, 0, 0, 9) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(_buttons);
        _body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = layout;
    }

    public static void ShowError(Window owner, string title, string message)
    {
        var dialog = new TaskDialog(owner, title, message);
        dialog.AddButton("확인", () => dialog.DialogResult = true, isDefault: true, isCancel: true);
        dialog.ShowDialog();
    }

    public static string? AskPassword(Window owner, string archive, bool retry)
    {
        var reason = retry ? "암호를 확인하고 다시 입력하세요." : "이 압축 파일을 읽으려면 암호가 필요합니다.";
        var dialog = new TaskDialog(owner, "압축 파일 암호", $"{archive}\n\n{reason}");
        var input = new PasswordBox { Style = (Style)dialog.FindResource("PasswordField"), Margin = new(0, 10, 0, 0) };
        AutomationProperties.SetName(input, "압축 파일 암호");
        dialog._body.Children.Add(input);
        dialog.AddButton("취소", null, isCancel: true);
        var confirm = dialog.AddButton("확인", () => dialog.DialogResult = true, isDefault: true);
        confirm.IsEnabled = false;
        input.PasswordChanged += (_, _) =>
        {
            using var securePassword = input.SecurePassword;
            confirm.IsEnabled = securePassword.Length > 0;
        };
        dialog.Loaded += (_, _) => input.Focus();
        var password = dialog.ShowDialog() == true ? input.Password : null;
        input.Clear();
        return password;
    }

    public static CollisionPolicy AskCollision(Window owner, string path)
    {
        var dialog = new TaskDialog(owner, "같은 이름의 항목이 있습니다", path);
        CollisionPolicy result = CollisionPolicy.Skip;
        dialog.AddButton("취소", null, isCancel: true);
        dialog.AddButton("건너뛰기", () => Select(CollisionPolicy.Skip), isDefault: true);
        dialog.AddButton("이름 바꾸기", () => Select(CollisionPolicy.Rename));
        dialog.AddButton("덮어쓰기", () => Select(CollisionPolicy.Overwrite));
        return dialog.ShowDialog() == true ? result : throw new OperationCanceledException();

        void Select(CollisionPolicy policy)
        {
            result = policy;
            dialog.DialogResult = true;
        }
    }

    private Button AddButton(string text, Action? action, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new(5, 3, 0, 0),
            Style = (Style)FindResource(isDefault ? "PrimaryAction" : "Action")
        };
        if (action != null) button.Click += (_, _) => action();
        _buttons.Children.Add(button);
        return button;
    }
}

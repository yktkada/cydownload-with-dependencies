using System.Windows;
using CyDownload.App.Localization;

namespace CyDownload.App.Views;

/// <summary>
/// 新建 / 重命名用户分组（照 IdeviceGui `Views/GroupNameWindow` 的用途；文案走资源）。
/// </summary>
public partial class GroupNameWindow : Window
{
    internal GroupNameWindow(string title, string prompt, string initialName = "")
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>用户输入的分组名（确认后有效）。</summary>
    public string ResultName { get; private set; } = "";

    /// <summary>给重复/空名报错（由调用方判断，窗口只负责显示）。</summary>
    public void ShowValidation(string message)
        => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation(Loc.Get("Group.NameEmpty"));
            return;
        }

        ResultName = name;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

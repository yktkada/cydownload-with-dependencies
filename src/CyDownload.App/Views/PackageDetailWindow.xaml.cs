using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyDownload.App.Localization;
using CyDownload.App.ViewModels;

namespace CyDownload.App.Views;

/// <summary>
/// 包详情窗口：只读展示索引里的字段。
/// 字段按 Debian control 的常用顺序排；空值显示 `—`，不留空行。
/// </summary>
public partial class PackageDetailWindow : Window
{
    internal PackageDetailWindow(PackageItemViewModel package)
    {
        ArgumentNullException.ThrowIfNull(package);

        InitializeComponent();

        var row = package.Row;
        AddField("Detail.PackageId", row.PackageId);
        AddField("Detail.DisplayName", row.Name);
        AddField("Column.Version", row.Version);
        AddField("Column.Architecture", row.Architecture);
        AddField("Column.Section", row.Section);
        AddField("Column.Size", package.SizeText);
        AddField("Column.Repo", row.Repo);
        AddField("Detail.Maintainer", row.Maintainer);
        AddField("Detail.Depends", row.Depends);
        AddField("Detail.PreDepends", row.PreDepends);
        AddField("Detail.Provides", row.Provides);
        AddField("Detail.Filename", row.Filename);
        AddField("Detail.Md5", row.Md5);
        AddField("Detail.Description", row.Description);
    }

    private void AddField(string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var row = FieldsGrid.RowDefinitions.Count;
        FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Loc.Get(labelKey),
            Margin = new Thickness(0, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);

        var content = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 4, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);

        FieldsGrid.Children.Add(label);
        FieldsGrid.Children.Add(content);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}



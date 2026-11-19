using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CyDownload.App.Infrastructure;
using CyDownload.App.Localization;

namespace CyDownload.App.Views;

/// <summary>
/// 设置窗口：语言、目标设备、依赖基线、网络超时、目录。
/// <para>
/// 语言是**立即生效**的（运行时切换）；其余值点「保存」才写回。
/// 存档目录只读展示——改它会换库，属 F8 的迁移功能，这里不给改。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;

    public SettingsWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _original = settings;
        InitializeComponent();
        Result = settings;

        BuildLanguageCombo();
        BuildPresetCombo();
        ProductTypeBox.Text = settings.Target.ProductType;
        IosVersionBox.Text = settings.Target.IosVersion;
        BuildVersionBox.Text = settings.Target.BuildVersion ?? "";
        CfVersionBox.Text = settings.Target.CfVersion ?? "";
        KernelVersionBox.Text = settings.Target.KernelVersion ?? "";
        BasePresetRadio.IsChecked = settings.UsePresetBase;
        ShowDisabledSourcesBox.IsChecked = settings.ShowDisabledSources;
        MergeSameGroupsBox.IsChecked = settings.MergeSameGroups;
        MaxParallelBox.Text = settings.MaxParallelDownloads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxPerRepoBox.Text = settings.MaxPerRepoConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BaseNoneRadio.IsChecked = !settings.UsePresetBase;
        NoProgressBox.Text = settings.HttpNoProgressSeconds.ToString(CultureInfo.InvariantCulture);
        TotalTimeoutBox.Text = settings.HttpTotalMinutes.ToString(CultureInfo.InvariantCulture);
        ArchiveRootBox.Text = AppPaths.ArchiveDirectory;
        ProgramDirBox.Text = AppPaths.ProgramDirectory;
    }

    /// <summary>点「保存」后的新设置（对话框返回 true 时有效）。</summary>
    public AppSettings Result { get; private set; }

    /// <summary>内置机型下拉：最后一项是「自定义」；选中内置项时把实测参数填进下面的字段。</summary>
    private void BuildPresetCombo()
    {
        PresetCombo.Items.Clear();

        foreach (var preset in CyDownload.Core.Repos.BuiltInTargets.All())
        {
            PresetCombo.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
        }

        PresetCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings.PresetCustom"), Tag = null });

        var current = CyDownload.Core.Repos.BuiltInTargets.Match(_original.TargetProfile);
        if (current is not null)
        {
            foreach (ComboBoxItem item in PresetCombo.Items)
            {
                if (item.Tag is CyDownload.Core.Resolve.TargetProfile tag &&
                    string.Equals(tag.ProductType, current.ProductType, StringComparison.Ordinal) &&
                    string.Equals(tag.IosVersion, current.IosVersion, StringComparison.Ordinal))
                {
                    PresetCombo.SelectedItem = item;
                    return;
                }
            }
        }

        PresetCombo.SelectedIndex = PresetCombo.Items.Count - 1;      // 自定义
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ComboBoxItem { Tag: CyDownload.Core.Resolve.TargetProfile preset }) return;

        ProductTypeBox.Text = preset.ProductType;
        IosVersionBox.Text = preset.IosVersion;
        BuildVersionBox.Text = preset.BuildVersion ?? "";
        CfVersionBox.Text = preset.CfVersion ?? "";
        KernelVersionBox.Text = preset.KernelVersion ?? "";
    }

    private void BuildLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "system", Tag = "system" });
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = LanguageRules.DisplayName(LanguageRules.Chinese), Tag = LanguageRules.Chinese,
        });
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = LanguageRules.DisplayName(LanguageRules.English), Tag = LanguageRules.English,
        });

        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (string.Equals((string?)item.Tag, _original.Language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageCombo.SelectedItem = item;
                return;
            }
        }

        LanguageCombo.SelectedIndex = 0;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var target = _original.Target with
        {
            ProductType = ProductTypeBox.Text.Trim(),
            IosVersion = IosVersionBox.Text.Trim(),
            BuildVersion = NullIfEmpty(BuildVersionBox.Text),
            CfVersion = NullIfEmpty(CfVersionBox.Text),
            KernelVersion = NullIfEmpty(KernelVersionBox.Text),
        };

        if (string.IsNullOrWhiteSpace(target.ProductType) || string.IsNullOrWhiteSpace(target.IosVersion))
        {
            MessageBox.Show(this, Loc.Get("Err.RequiredTarget"), Loc.Get("Settings.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

        Result = _original with
        {
            Language = language,
            Target = target,
            BaseMode = BasePresetRadio.IsChecked == true ? "preset" : "none",
            ShowDisabledSources = ShowDisabledSourcesBox.IsChecked == true,
            MergeSameGroups = MergeSameGroupsBox.IsChecked == true,
            MaxParallelDownloads = ClampParallel(MaxParallelBox.Text, settings: 4, maximum: 8),
            MaxPerRepoConcurrency = ClampParallel(MaxPerRepoBox.Text, settings: 2, maximum: 8),
            HttpNoProgressSeconds = Math.Clamp(ParseInt(NoProgressBox.Text, _original.HttpNoProgressSeconds), 5, 600),
            HttpTotalMinutes = Math.Clamp(ParseInt(TotalTimeoutBox.Text, _original.HttpTotalMinutes), 1, 120),
        };

        // 语言立即生效（不用等保存落盘）
        Loc.SetLanguage(language);

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 语言在保存前就生效了（立即切换），取消时要回滚到打开窗口时的语言
        Loc.SetLanguage(_original.Language);
        DialogResult = false;
    }

    private static string? NullIfEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>并发数：1–8，超出就夹紧（1 = 串行）。</summary>
    private static int ClampParallel(string text, int settings, int maximum)
        => int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, maximum)
            : settings;

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}





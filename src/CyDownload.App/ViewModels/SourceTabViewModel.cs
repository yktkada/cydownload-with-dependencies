using CyDownload.App.Localization;
using CyDownload.App.Mvvm;
using CyDownload.Core.Repos;

namespace CyDownload.App.ViewModels;

/// <summary>顶部的一个源页签（每源一个，）。</summary>
internal sealed class SourceTabViewModel : ObservableObject
{
    private int _packageCount;
    private string? _lastFetch;
    private string _lastResult = "";

    public SourceTabViewModel(RepoDescriptor repo, string? lastFetch, int packageCount)
    {
        Repo = repo;
        _lastFetch = lastFetch;
        _packageCount = packageCount;
    }

    /// <summary>「全部」合并页签：把所有源的包合成一个列表，本身不是一个源。</summary>
    private SourceTabViewModel()
    {
        Repo = new RepoDescriptor { Name = "", BaseUrl = "", Layout = RepoLayout.Flat };
    }

    public static SourceTabViewModel CreateMerged(int sourceCount, int packageCount) => new()
    {
        IsMerged = true,
        MergedSourceCount = sourceCount,
        PackageCount = packageCount,
    };

    public RepoDescriptor Repo { get; }

    /// <summary>是不是「全部源」合并页签（不能编辑/删除/单独抓取）。</summary>
    public bool IsMerged { get; private init; }

    public int MergedSourceCount { get; private init; }

    public string Name => Repo.Name;

    public string BaseUrl => Repo.BaseUrl;

    /// <summary>页签标题：源名 + 包数（包数变了要更新，所以走通知）。</summary>
    public string Header
    {
        get
        {
            if (IsMerged) return $"{Loc.Get("Source.Merged")} ({_packageCount})";

            var baseText = _packageCount > 0 ? $"{Name} ({_packageCount})" : Name;
            return Repo.Enabled ? baseText : baseText + Loc.Get("Source.DisabledSuffix");
        }
    }

    public int PackageCount
    {
        get => _packageCount;
        set
        {
            if (SetProperty(ref _packageCount, value)) OnPropertyChanged(nameof(Header));
        }
    }

    public string? LastFetch
    {
        get => _lastFetch;
        set
        {
            if (SetProperty(ref _lastFetch, value)) OnPropertyChanged(nameof(LastFetchText));
        }
    }

    /// <summary>「上次抓取 …」/「从未抓取」。</summary>
    public string LastFetchText => IsMerged
        ? Loc.Format("Source.MergedInfo", MergedSourceCount, PackageCount)
        : string.IsNullOrWhiteSpace(_lastFetch) ? Loc.Get("Source.Never") : Loc.Format("Source.LastFetch", _lastFetch);

    /// <summary>最近一次抓取结果（成功/失败的摘要），显示在页签下方。</summary>
    public string LastResult
    {
        get => _lastResult;
        set => SetProperty(ref _lastResult, value);
    }

    /// <summary>语言切换后重算文案。</summary>
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(LastFetchText));

    public override string ToString() => Name;
}






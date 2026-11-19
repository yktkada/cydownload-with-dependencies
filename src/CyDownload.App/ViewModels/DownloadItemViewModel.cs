using CyDownload.App.Localization;
using CyDownload.App.Mvvm;

namespace CyDownload.App.ViewModels;

/// <summary>右栏「下载队列」里的一项（一次导出一个包一行）。</summary>
internal sealed class DownloadItemViewModel : ObservableObject
{
    private string _status;

    public DownloadItemViewModel(string fileName, string packageId, string version, long bytes)
    {
        FileName = fileName;
        PackageId = packageId;
        Version = version;
        Bytes = bytes;
        _status = Loc.Get("Download.Waiting");
    }

    public string FileName { get; }

    public string PackageId { get; }

    /// <summary>版本（并行下载时用来精确定位队列行）。</summary>
    public string Version { get; }

    public long Bytes { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(IsDone));
        }
    }

    /// <summary>已结束（完成/缓存/跳过/失败）——用于界面上灰掉或打勾。</summary>
    public bool IsDone => !string.Equals(_status, Loc.Get("Download.Waiting"), StringComparison.Ordinal)
                          && !string.Equals(_status, Loc.Get("Download.Running"), StringComparison.Ordinal);

    public string SizeText => PackageItemViewModel.FormatBytes(Bytes);

    public override string ToString() => $"{FileName} {Status}";
}


// carried from IdeviceGui ViewModels/ObservableObject.cs (main)
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CyDownload.App.Mvvm;

/// <summary>MVVM 基类：属性变更通知 + 只在值真的变了才发通知。</summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

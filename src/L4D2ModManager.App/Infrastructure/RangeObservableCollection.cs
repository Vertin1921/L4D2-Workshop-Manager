using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace L4D2ModManager.App.Infrastructure;

/// <summary>
/// 支持批量替换的 ObservableCollection。
///
/// 为什么需要它？
///   普通写法 Items.Clear() 之后再逐条 Add()，会为每个元素各发一次集合变更通知，
///   WPF 列表要为每条通知重建/回收容器 —— 100 个 Mod 就是 100 次通知，
///   排序或切换筛选时会明显掉帧。这里用 <see cref="ReplaceAll"/> 只发一次 Reset 通知。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    /// <summary>一次性替换全部内容（只触发一次通知）。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in list)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications) return;
        base.OnPropertyChanged(e);
    }
}

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using L4D2ModManager.App.ViewModels;

namespace L4D2ModManager.App.Views;

public partial class ModsView : UserControl
{
    private INotifyCollectionChanged? _observedCollection;

    public ModsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachCollection();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachCollection();

        if (e.NewValue is ModsViewModel viewModel)
        {
            _observedCollection = viewModel.Items;
            _observedCollection.CollectionChanged += OnItemsChanged;
        }
    }

    private void DetachCollection()
    {
        if (_observedCollection == null) return;
        _observedCollection.CollectionChanged -= OnItemsChanged;
        _observedCollection = null;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 列表被整体替换（切换排序 / 筛选 / 重新扫描）时播放一次级联入场动画。
        // 只在"整体替换"时播放，滚动复用容器不会触发，因此不影响滚动帧率。
        if (e.Action != NotifyCollectionChangedAction.Reset) return;

        Dispatcher.BeginInvoke(new Action(RunCascadeAnimation), DispatcherPriority.Loaded);
    }

    /// <summary>macOS 风格的级联入场：只对当前已实现的容器施加入场动画（最多 14 个）。</summary>
    private void RunCascadeAnimation()
    {
        try
        {
            var count = Math.Min(ModList.Items.Count, 14);

            for (int i = 0; i < count; i++)
            {
                if (ModList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;

                var delay = TimeSpan.FromMilliseconds(i * 24);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                if (container.RenderTransform is not TranslateTransform translate)
                {
                    translate = new TranslateTransform();
                    container.RenderTransform = translate;
                }

                container.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                    });

                translate.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(420))
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                    });
            }
        }
        catch
        {
            // 动画失败不影响功能
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using L4D2ModManager.App.ViewModels;

namespace L4D2ModManager.App.Views;

/// <summary>L4D2 语音管理器页面：支持把语音 Mod（文件夹 / .vpk）拖进来自动识别角色。</summary>
public partial class VoiceView : UserControl
{
    public VoiceView()
    {
        InitializeComponent();
    }

    private VoiceViewModel? ViewModel => DataContext as VoiceViewModel;

    private void View_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void View_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        e.Handled = true;

        // 拖到某个角色卡片上 = 手动指定该角色；拖到空白处 = 自动识别
        var card = FindCard(e.OriginalSource as DependencyObject);
        await ViewModel.AcceptDropAsync(paths[0], card);
    }

    private static VoiceCharacterCard? FindCard(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && element.DataContext is VoiceCharacterCard card) return card;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }
    /// <summary>
    /// 把鼠标滚轮转交给外层的整页滚动区：
    /// 鼠标停在备份列表等内部可滚动控件上时，滚轮会被它们吃掉，导致"划不动、要移出这块才能划"。
    /// </summary>
    private void InnerScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var scroller = FindAncestorScrollViewer(sender as DependencyObject);
        if (scroller == null) return;

        e.Handled = true;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
    }

    private static System.Windows.Controls.ScrollViewer? FindAncestorScrollViewer(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.ScrollViewer viewer) return viewer;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
using System.Windows;
using System.Windows.Controls;
using L4D2ModManager.App.ViewModels;

namespace L4D2ModManager.App.Views;

/// <summary>人物语音替换页：支持拖入文件夹/文件。</summary>
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

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        ViewModel.SetSource(paths[0]);
        e.Handled = true;
    }
}

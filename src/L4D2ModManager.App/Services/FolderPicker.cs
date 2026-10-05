using System.Windows;
using Microsoft.Win32;

namespace L4D2ModManager.App.Services;

/// <summary>
/// 文件夹选择对话框。
/// WPF 没有内置的目录选择器，这里使用 OpenFileDialog 的“选择文件夹”技巧，
/// 保持与系统一致的外观（Vista+ 风格）。
/// </summary>
public static class FolderPicker
{
    public static string? Pick(Window? owner, string title = "选择文件夹", string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择此文件夹",
            Filter = "文件夹|*.folder",
            InitialDirectory = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                ? initialDirectory
                : null,
        };

        var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != true) return null;

        var path = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;

        // 用户直接输入了路径
        return Directory.Exists(dialog.FileName) ? dialog.FileName : null;
    }
}

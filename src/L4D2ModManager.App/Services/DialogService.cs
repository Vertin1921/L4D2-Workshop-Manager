using System.Windows;
using L4D2ModManager.Core.Models;
using L4D2ModManager.App.Dialogs;

namespace L4D2ModManager.App.Services;

/// <summary>统一的对话框入口（便于以后替换为自绘 Toast / 无模态提示）。</summary>
public sealed class DialogService
{
    private readonly Func<Window?> _ownerProvider;

    public DialogService(Func<Window?> ownerProvider) => _ownerProvider = ownerProvider;

    private Window? Owner => _ownerProvider();

    public void Info(string message, string title = "提示", string? details = null) =>
        DialogWindow.ShowMessage(Owner, title, message, details);

    public void Error(string message, string title = "出错了", string? details = null) =>
        DialogWindow.ShowMessage(Owner, title, message, details, "关闭");

    public bool Confirm(string message, string title = "确认操作", string? details = null,
        string okText = "确定") =>
        DialogWindow.Confirm(Owner, title, message, details, okText);

    public string? Prompt(string message, string title = "输入", string initialValue = "", string? label = null) =>
        DialogWindow.Prompt(Owner, title, message, initialValue, label);

    public string? Choose(string message, string title, IEnumerable<string> choices) =>
        DialogWindow.Choose(Owner, title, message, choices);

    /// <summary>删除 Mod 前的二次确认，列出将删除的内容。</summary>
    public bool ConfirmDelete(ModItem item)
    {
        var details =
            $"名称：{item.DisplayName}\r\n" +
            $"文件：{item.FileName}\r\n" +
            $"路径：{item.FilePath}\r\n" +
            $"大小：{item.SizeText}\r\n" +
            $"状态：{item.StateText}\r\n" +
            $"分类：{item.CategoryText}";

        var message =
            "确定要删除这个 Mod 吗？\r\n\r\n" +
            "将同时删除：\r\n" +
            "  · VPK 文件（含 .disabled 副本）\r\n" +
            "  · 缩略图缓存\r\n" +
            "  · 数据库记录\r\n\r\n" +
            "此操作不可撤销。";

        return DialogWindow.Confirm(Owner, "删除 Mod", message, details, "删除", "取消");
    }
}

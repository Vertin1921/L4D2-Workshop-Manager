using System.Windows;

namespace L4D2ModManager.App.Services;

/// <summary>
/// 剪贴板工具：Windows 的剪贴板是全局独占资源，别的程序（输入法、剪贴板管理器、
/// 网盘/即时通讯的监控程序）短暂占用时会直接抛 CLIPBRD_E_CANT_OPEN（0x800401D0）。
/// 这里带重试地写入，避免"复制命令失败"。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>把文本写入剪贴板（带重试），成功返回 true。</summary>
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // WPF 自带的 SetDataObject(data, copy, retryTimes, retryDelay) 就是为这种情况准备的
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                Core.Services.Log.Warn($"写入剪贴板失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(120);
            }

            // 关键：写入抛异常 ≠ 没写进去。WPF 在把数据交给剪贴板后仍可能抛
            // CLIPBRD_E_CANT_OPEN；只要内容已经在剪贴板里，就应当按"成功"处理，
            // 否则会出现"Ctrl+V 能粘贴，界面却说复制失败"的假报错。
            if (IsAlreadyOnClipboard(text)) return true;
        }

        return false;
    }


    /// <summary>内容是否已经在剪贴板里（用于把"抛异常但其实写成功了"识别为成功）。</summary>
    private static bool IsAlreadyOnClipboard(string text)
    {
        try
        {
            return Clipboard.ContainsText() && Clipboard.GetText() == text;
        }
        catch
        {
            return false;
        }
    }
    /// <summary>写入剪贴板；失败时返回 false（调用方给出友好提示，而不是把异常抛给用户）。</summary>
    public static bool TrySetText(string text, out string? error)
    {
        error = null;

        if (SetText(text)) return true;

        error = "剪贴板被其它程序占用（Windows 剪贴板是全局独占的）。\r\n" +
                "请稍后再试一次，或直接手动输入命令。";
        Core.Services.Log.Warn("剪贴板写入最终失败：" + text);
        return false;
    }
}

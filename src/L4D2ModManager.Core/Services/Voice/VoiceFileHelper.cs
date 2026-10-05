namespace L4D2ModManager.Core.Services.Voice;

/// <summary>语音文件操作的小工具。</summary>
internal static class VoiceFileHelper
{
    /// <summary>
    /// 先删除原文件再粘贴。
    /// 用户要求：语音安装应当是"卸载原文件再粘贴"，而不是覆盖写入
    /// （覆盖会保留旧文件里不该留下的内容；此前创建的 ZIP 备份仍可用于一键还原）。
    /// </summary>
    internal static void DeleteThenCopy(string source, string destination)
    {
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        if (File.Exists(destination)) File.Delete(destination);
        File.Copy(source, destination);
    }
}

using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>批量状态修改结果。</summary>
public sealed class StateChangeResult
{
    public int Changed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();

    public string Summary => Failed == 0
        ? $"已处理 {Changed} 个 Mod" + (Skipped > 0 ? $"（{Skipped} 个无需改动）" : string.Empty)
        : $"成功 {Changed} 个，失败 {Failed} 个";
}

/// <summary>
/// Mod 状态服务：通过重命名后缀实现启用 / 禁用，绝不删除或修改 VPK 内容。
///   启用：xxx.vpk       禁用：xxx.vpk.disabled
/// </summary>
public sealed class ModStateService
{
    /// <summary>状态变化通知：(item, oldState, newState)。</summary>
    public event Action<ModItem, ModState, ModState>? StateChanged;

    /// <summary>修改单个 Mod 状态。</summary>
    public bool TrySetState(ModItem item, ModState target, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(item);

        if (item.State == target)
            return true;

        var source = item.ActualPath;
        var destination = target == ModState.Enabled ? item.FilePath : item.FilePath + ModItem.DisabledSuffix;

        if (!File.Exists(source))
        {
            error = $"文件不存在：{source}";
            return false;
        }

        if (File.Exists(destination) && !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            error = $"目标文件已存在，为避免覆盖已取消操作：{Path.GetFileName(destination)}";
            return false;
        }

        try
        {
            File.Move(source, destination);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"切换状态失败: {source} -> {destination}", ex);
            return false;
        }

        var old = item.State;
        item.State = target;
        item.IndexStamp = null; // 文件名变化后重新计算指纹
        var info = new FileInfo(destination);
        if (info.Exists)
        {
            item.SizeBytes = info.Length;
            item.ModifiedUtc = info.LastWriteTimeUtc;
        }

        Log.Info($"{(target == ModState.Enabled ? "启用" : "禁用")} Mod：{Path.GetFileName(item.FilePath)}");
        StateChanged?.Invoke(item, old, target);
        return true;
    }

    /// <summary>批量修改状态。</summary>
    public StateChangeResult Apply(IEnumerable<ModItem> items, ModState target)
    {
        var result = new StateChangeResult();
        foreach (var item in items)
        {
            if (item.State == target)
            {
                result.Skipped++;
                continue;
            }

            if (TrySetState(item, target, out var error))
                result.Changed++;
            else
            {
                result.Failed++;
                if (!string.IsNullOrWhiteSpace(error)) result.Errors.Add($"{Path.GetFileName(item.FilePath)}: {error}");
            }
        }
        return result;
    }

    /// <summary>切换（启用 ↔ 禁用）。</summary>
    public bool Toggle(ModItem item, out string? error)
    {
        var target = item.State == ModState.Enabled ? ModState.Disabled : ModState.Enabled;
        return TrySetState(item, target, out error);
    }
}

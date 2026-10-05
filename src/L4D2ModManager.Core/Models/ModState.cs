namespace L4D2ModManager.Core.Models;

/// <summary>Mod 启用状态。采用后缀管理：启用 = xxx.vpk，禁用 = xxx.vpk.disabled。</summary>
public enum ModState
{
    Disabled,
    Enabled,
}

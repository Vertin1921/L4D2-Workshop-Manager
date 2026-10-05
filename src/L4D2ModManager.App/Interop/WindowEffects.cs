using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace L4D2ModManager.App.Interop;

/// <summary>
/// 窗口视觉效果（毛玻璃、圆角、深色标题栏）。
/// 使用 DWM / user32 的原生接口，不使用 AllowsTransparency，
/// 因此对 WebView2 完全兼容（分层窗口会导致 WebView2 无法渲染）。
/// </summary>
internal static class WindowEffects
{
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentEnableBlurBehind = 3;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>窗口句柄是否已创建。</summary>
    public static bool IsReady(Window window) =>
        new WindowInteropHelper(window).Handle != IntPtr.Zero;

    /// <summary>
    /// 开启毛玻璃背景。tint 为叠加色，opacity 0-255。
    /// 失败时静默返回 false（界面仍有纯色背景，不影响使用）。
    /// </summary>
    public static bool EnableAcrylic(Window window, Color tint, byte opacity = 0xCC)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            // GradientColor 为 ABGR
            int gradient = (opacity << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0,
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size,
                };

                if (SetWindowCompositionAttribute(handle, ref data) != 0)
                    return true;

                // 退回到普通模糊（Windows 10 早期版本）
                policy.AccentState = AccentEnableBlurBehind;
                Marshal.StructureToPtr(policy, pointer, false);
                data.Data = pointer;
                data.SizeOfData = size;
                return SetWindowCompositionAttribute(handle, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭毛玻璃 / 模糊（恢复为普通窗口，滚动与动画更流畅）。</summary>
    public static bool DisableBlur(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            var policy = new AccentPolicy
            {
                AccentState = 0, // ACCENT_DISABLED
                AccentFlags = 0,
                GradientColor = 0,
                AnimationId = 0,
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(handle, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows 11 圆角窗口。</summary>
    public static bool EnableRoundedCorners(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            int preference = DwmwcpRound;
            return DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>深色标题栏 / 边框。</summary>
    public static bool EnableDarkTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            int enabled = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) == 0)
                return true;

            return DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一次性应用全部效果，返回毛玻璃是否生效。</summary>
    public static bool Apply(Window window, Color tint, byte opacity = 0xCC)
    {
        EnableDarkTitleBar(window);
        EnableRoundedCorners(window);
        return EnableAcrylic(window, tint, opacity);
    }
}

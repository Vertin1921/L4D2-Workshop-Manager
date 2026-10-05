using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.Infrastructure;

/// <summary>macOS 风格交通灯按钮的符号（✕ / — / ⤢）附加属性。</summary>
public static class TrafficLight
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(TrafficLight), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);
}

/// <summary>bool → Visibility，ConverterParameter="Invert" 时取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>取反布尔值。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

/// <summary>对象为 null 时折叠（ConverterParameter="Invert" 反转）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value != null && !(value is string s && string.IsNullOrWhiteSpace(s));
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>启用状态 → 颜色（绿色 / 灰色）。</summary>
public sealed class EnabledStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var enabled = value is bool b && b;
        var key = enabled ? "Brush.Success" : "Brush.TextMuted";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>冲突严重程度 → 颜色（高=红 / 中=橙 / 低=灰）。</summary>
public sealed class ConflictSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ConflictSeverity severity
            ? severity switch
            {
                ConflictSeverity.High => "Brush.Danger",
                ConflictSeverity.Medium => "Brush.Warning",
                _ => "Brush.TextMuted",
            }
            : "Brush.TextMuted";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>分类 → 颜色（用于左侧色条）。</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ModCategory category
            ? category switch
            {
                ModCategory.Weapon => "Brush.Category.Weapon",
                ModCategory.Character => "Brush.Category.Character",
                ModCategory.Audio => "Brush.Category.Audio",
                ModCategory.Map => "Brush.Category.Map",
                ModCategory.Script => "Brush.Category.Script",
                ModCategory.Ui => "Brush.Category.Ui",
                _ => "Brush.Category.Other",
            }
            : "Brush.Category.Other";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.DimGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>下载状态 → 颜色。</summary>
public sealed class DownloadStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is DownloadStatus status
            ? status switch
            {
                DownloadStatus.Completed => "Brush.Success",
                DownloadStatus.Failed => "Brush.Danger",
                DownloadStatus.Canceled => "Brush.TextMuted",
                DownloadStatus.Paused => "Brush.Warning",
                _ => "Brush.Accent",
            }
            : "Brush.Accent";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>数值 → 是否大于 0（用于“有内容/空列表”提示切换）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return (count > 0) != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

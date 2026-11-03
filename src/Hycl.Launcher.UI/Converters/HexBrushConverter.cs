using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Hycl.Launcher.UI.Converters;

/// <summary>把十六进制颜色字符串转换为画刷（用于主题色色板）。</summary>
public class HexBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex ? Brush.Parse(hex) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

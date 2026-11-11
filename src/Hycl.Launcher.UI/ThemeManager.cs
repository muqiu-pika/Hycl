using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Hycl.Launcher.UI;

/// <summary>
/// 主题应用器：按配置把 Colors.Light.axaml / Colors.Dark.axaml 整体替换进应用资源，
/// DynamicResource 引用的画刷随即刷新（对应 PCL-CE 个性化-浅色/深色的实时换肤）。
/// 支持主题色覆盖（参考 OpenPCL 的个性化主题色）：在替换后的字典上重写主色系画刷，
/// 由主色派生出深/浅变体与各级软底色，浅色/深色主题都通过带透明度的主色适配背景。
/// </summary>
public static class ThemeManager
{
    /// <summary>主题变更时触发（值为 "light"/"dark"）。</summary>
    public static event EventHandler<string>? ThemeApplied;

    /// <summary>当前生效的主题色（空 = 跟随默认配色）；换主题时随字典重置后重新应用。</summary>
    private static string? _accentHex;

    /// <summary>按配置值（"light"/"dark" + 可选 #RRGGBB 主题色）应用主题。</summary>
    public static void Apply(string? theme, string? accentHex)
    {
        if (accentHex is not null)
        {
            // 显式传参（含空串 = 撤销主题色）总是生效；不传参沿用当前主题色
            _accentHex = string.IsNullOrWhiteSpace(accentHex) ? "" : accentHex;
        }

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var isDark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        var variant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        application.RequestedThemeVariant = variant;

        // 替换合并字典中的 Colors 条目（以特征键识别），触发 DynamicResource 全量刷新。
        // 用 XAML 编译器生成的类型化字典实例（ColorsLight/ColorsDark）替代运行时 URI 加载，
        // 后者带 RequiresUnreferencedCode 注解，裁剪发布下过不了 0 警告门槛。
        var merged = application.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is not ResourceDictionary dictionary
                || !dictionary.ContainsKey("TextBrush"))
            {
                continue;
            }

            // 每次都换全新字典实例：撤销主题色时无需记住旧画刷，直接回到默认配色
            ResourceDictionary replacement = isDark ? new Styles.ColorsDark() : new Styles.ColorsLight();
            ApplyAccent(replacement, _accentHex);
            merged[i] = replacement;
            break;
        }

        ThemeApplied?.Invoke(null, isDark ? "dark" : "light");
    }

    /// <summary>按当前主题重刷主题色（主题色变化、不换主题时调用）。</summary>
    public static void ApplyAccent(string? accentHex) => Apply(null, accentHex);

    private static void ApplyAccent(ResourceDictionary dictionary, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !TryParseHex(hex, out var color))
        {
            return;
        }

        var deep = Darken(color, 0.30);
        var light = Lighten(color, 0.38);
        dictionary["PrimaryBrush"] = new SolidColorBrush(color);
        dictionary["PrimaryDeepBrush"] = new SolidColorBrush(deep);
        dictionary["PrimaryLightBrush"] = new SolidColorBrush(light);
        // 各级软底色：主色带透明度，浅色/深色主题下都能自然融入表面底色
        dictionary["Primary5Brush"] = new SolidColorBrush(WithAlpha(color, 0x9E));
        dictionary["Primary6Brush"] = new SolidColorBrush(WithAlpha(color, 0x73));
        dictionary["Primary7Brush"] = new SolidColorBrush(WithAlpha(color, 0x4D));
        dictionary["Primary8Brush"] = new SolidColorBrush(WithAlpha(color, 0x1E));
        dictionary["PrimaryGradientBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(deep, 1)
            }
        };
    }

    /// <summary>按比例向黑色靠拢（factor = 降低亮度的比例）。</summary>
    private static Color Darken(Color color, double factor)
        => Color.FromRgb(
            (byte)Math.Round(color.R * (1 - factor)),
            (byte)Math.Round(color.G * (1 - factor)),
            (byte)Math.Round(color.B * (1 - factor)));

    /// <summary>按比例向白色靠拢。</summary>
    private static Color Lighten(Color color, double factor)
        => Color.FromRgb(
            (byte)Math.Round(color.R + (255 - color.R) * factor),
            (byte)Math.Round(color.G + (255 - color.G) * factor),
            (byte)Math.Round(color.B + (255 - color.B) * factor));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>解析 #RRGGBB / #AARRGGBB 主题色（TryParse 需要 CultureInfo 重载在部分目标框架不可用，手写兜底）。</summary>
    private static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        var value = hex.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length is not (6 or 8) || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        byte r, g, b, a = 0xFF;
        if (value.Length == 8)
        {
            a = Convert.ToByte(value[..2], 16);
            value = value[2..];
        }

        r = Convert.ToByte(value[..2], 16);
        g = Convert.ToByte(value[2..4], 16);
        b = Convert.ToByte(value[4..6], 16);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}

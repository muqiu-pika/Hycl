using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Hycl.Launcher.UI;

/// <summary>
/// 主题应用器：按配置把 Colors.Light.axaml / Colors.Dark.axaml 整体替换进应用资源，
/// DynamicResource 引用的画刷随即刷新（对应 PCL-CE 个性化-浅色/深色的实时换肤）。
/// </summary>
public static class ThemeManager
{
    /// <summary>主题变更时触发（值为 "light"/"dark"）。</summary>
    public static event EventHandler<string>? ThemeApplied;

    /// <summary>按配置值（"light"/"dark"）应用主题。</summary>
    public static void Apply(string? theme)
    {
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
            if (merged[i] is not ResourceDictionary dictionary || !dictionary.ContainsKey("TextBrush"))
            {
                continue;
            }

            merged[i] = isDark ? new Styles.ColorsDark() : new Styles.ColorsLight();
            break;
        }

        ThemeApplied?.Invoke(null, isDark ? "dark" : "light");
    }
}

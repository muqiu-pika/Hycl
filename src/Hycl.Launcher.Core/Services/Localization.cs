using System.Globalization;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 界面语言（参考 PCL-CE 的语言选择）：键值表按语言切换，未命中键回退简体中文，
/// 简体中文表缺失的键回退键名本身（开发期即中文键）。当前覆盖主界面外壳
/// （标签页 / 窗口标题 / 常用按钮），页面深处的长文案随版本逐步接入。
/// </summary>
public static class Localization
{
    /// <summary>语言代码常量（供 Config 归一化引用，避免字符串散落）。</summary>
    public static class LocalizationDefaults
    {
        public const string Chinese = "zh-Hans";
        public const string English = "en-US";
    }

    public const string Chinese = "zh-Hans";

    public const string English = "en-US";

    // 静态字段按声明顺序初始化：表字段必须先于 _table（_table 的初始化器引用了中文表）
    private static readonly IReadOnlyDictionary<string, string> ChineseTableField =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EnglishTableField =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 标签页 / 窗口外壳
            ["启动"] = "Play",
            ["下载"] = "Download",
            ["版本"] = "Instances",
            ["设置"] = "Settings",
            ["工具"] = "Tools",
            ["Hyplay Craft Launcher"] = "Hyplay Craft Launcher",
            // 常用按钮
            ["添加账号"] = "Add account",
            ["创建新档案"] = "New profile",
            ["检查更新"] = "Check for updates",
            ["下载更新"] = "Download update",
            ["安装更新并重启"] = "Install and restart",
            ["打开数据目录"] = "Open data folder",
            ["打开日志目录"] = "Open log folder",
            ["保存实例设置"] = "Save instance settings",
            ["恢复全局设置"] = "Reset to global",
            ["从文件安装"] = "Install from file",
            ["下载新资源"] = "Download content",
            ["打开文件夹"] = "Open folder",
            ["删除"] = "Delete",
            ["禁用"] = "Disable",
            ["启用"] = "Enable",
            ["取消"] = "Cancel",
            ["关闭"] = "Close",
            ["保存"] = "Save",
            // 设置分区标题
            ["个性化"] = "Personalization",
            ["语言"] = "Language",
            ["关于"] = "About",
            ["跟随全局"] = "Follow global",
            ["跟随实例"] = "Per instance",
            ["简体中文"] = "简体中文",
            ["English"] = "English"
        };

    private static volatile IReadOnlyDictionary<string, string> _table = ChineseTableField;

    public static string CurrentLanguage { get; private set; } = Chinese;

    /// <summary>语言切换事件（UI 层订阅后刷新已渲染文本）。</summary>
    public static event EventHandler<string>? LanguageChanged;

    /// <summary>支持的语言（键 + 显示名）。</summary>
    public static IReadOnlyList<(string Code, string Label)> SupportedLanguages { get; } =
    [
        (Chinese, "简体中文"),
        (English, "English")
    ];

    /// <summary>取当前语言的文案；zh-Hans 直接回键名本身（键即中文），en 查英文表后回退中文。</summary>
    public static string T(string key)
    {
        if (CurrentLanguage == Chinese)
        {
            return key;
        }

        return _table.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>切换语言；非法值忽略。</summary>
    public static void SetLanguage(string language)
    {
        if (language != Chinese && language != English)
        {
            return;
        }

        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        _table = language == English ? EnglishTableField : ChineseTableField;
        LanguageChanged?.Invoke(null, language);
    }
}

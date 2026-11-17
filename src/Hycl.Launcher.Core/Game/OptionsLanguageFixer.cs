using System.Globalization;
using System.Text;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// options.txt 语言自动修正（每次启动前执行，规则移植自 PCL-CE 的启动前语言处理，用中文注释重写）：
/// - 1.0 及更早（发布时间 ≤ 2011-11-18）：尚无多语言概念，lang 固定为 none；
/// - 1.1 ~ 1.10（发布时间 2012-01-12 ~ 2016-06-08）：区域码必须大写（zh_CN）——
///   旧版游戏 zh_cn 会直接 NPE 崩溃或语言设置显示错误；
/// - 其余版本：统一小写（zh_cn）。
/// lang 缺失或实例还没有任何存档时，采用启动器语言兜底（中文 Windows 用户默认 zh_cn）。
/// 目标语言为中文/日文/韩文等非拉丁语言时，自动开启 forceUnicodeFont，避免旧版本把字符渲染成方框。
/// 独立于 <see cref="GameOptionsService"/>：那是「仅首次启动」的一次性写入（有 applied 标记），
/// 而语言格式依赖具体版本且可能被游戏改写，必须在每次启动前重新校验。
/// </summary>
public static class OptionsLanguageFixer
{
    /// <summary>
    /// 修正 options.txt 中的 lang / forceUnicodeFont 两个键，其余行与行序原样保留。
    /// 文件不存在或无需修改时不写入。失败只记日志，不阻断启动。
    /// </summary>
    /// <param name="optionsFilePath">options.txt 路径（版本隔离时为实例目录，否则为游戏根目录）。</param>
    /// <param name="releaseTime">版本发布时间（version.json 的 releaseTime）；无法获取时传 null，按新版格式处理。</param>
    /// <param name="defaultLanguage">lang 缺失/未配置时写入的语言（应传启动器当前语言，中文 Windows 用户为 zh_cn）。</param>
    /// <param name="logger">可选日志。</param>
    /// <returns>是否发生了修改并写回文件。</returns>
    public static bool Apply(
        string optionsFilePath,
        DateTime? releaseTime,
        string defaultLanguage = "zh_cn",
        LauncherLogger? logger = null)
    {
        try
        {
            if (!File.Exists(optionsFilePath))
            {
                logger?.Info("未找到 options.txt，跳过语言修正");
                return false;
            }

            // 只读出需要的两个键，其余行不动
            var lines = File.ReadAllLines(optionsFilePath).ToList();
            var langIndex = IndexOfKey(lines, "lang");
            var fontIndex = IndexOfKey(lines, "forceUnicodeFont");
            var currentLang = langIndex >= 0 ? GetLineValue(lines[langIndex]) : null;

            // 实例还没有存档时视为「未配置」：别的启动器/整合包留下的语言不算玩家的选择，
            // 直接采用启动器语言（与 PCL-CE 一致）
            var isUnconfigured = string.IsNullOrWhiteSpace(currentLang)
                                 || string.Equals(currentLang, "none", StringComparison.OrdinalIgnoreCase);
            var optionsDirectory = Path.GetDirectoryName(Path.GetFullPath(optionsFilePath))!;
            var hasExistingSaves = Directory.Exists(Path.Combine(optionsDirectory, "saves"));
            var shouldUseDefault = isUnconfigured || !hasExistingSaves;

            var requiredLang = ResolveLanguage(
                shouldUseDefault ? defaultLanguage : currentLang, releaseTime);

            var changed = false;
            if (!string.Equals(currentLang, requiredLang, StringComparison.Ordinal))
            {
                if (langIndex >= 0)
                {
                    lines[langIndex] = $"lang:{requiredLang}";
                }
                else
                {
                    // 老版本可能根本没有 lang 行，补到文件末尾
                    lines.Add($"lang:{requiredLang}");
                }

                changed = true;
                logger?.Info($"已将游戏语言从 {currentLang ?? "（未设置）"} 修正为 {requiredLang}");
            }

            // 中文/日文/韩文等非拉丁语言需要强制 Unicode 字体，否则旧版本会把字符渲染成方框
            var needsUnicodeFont = IsNonLatinLanguage(requiredLang);
            var hasUnicodeFont = fontIndex >= 0
                                 && string.Equals(GetLineValue(lines[fontIndex]), "true", StringComparison.OrdinalIgnoreCase);
            if (needsUnicodeFont && !hasUnicodeFont)
            {
                if (fontIndex >= 0)
                {
                    lines[fontIndex] = "forceUnicodeFont:true";
                }
                else
                {
                    lines.Add("forceUnicodeFont:true");
                }

                changed = true;
                logger?.Info("已开启 forceUnicodeFont，确保中文等非拉丁语言正常显示");
            }

            if (!changed)
            {
                logger?.Info($"游戏语言 {currentLang ?? "（未设置）"} 无需修正");
                return false;
            }

            // 原子写，避免游戏读到半截文件
            var temp = optionsFilePath + ".hycl-tmp";
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Move(temp, optionsFilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"修正 options.txt 语言失败（不阻断启动）：{ex.Message}");
            return false;
        }
    }

    /// <summary>解析 version.json 的 releaseTime（如 2016-06-08T09:06:42+00:00）；无法解析时返回 null。</summary>
    public static DateTime? ParseReleaseTime(string? releaseTime)
    {
        if (string.IsNullOrWhiteSpace(releaseTime))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.Parse(releaseTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                .UtcDateTime;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>按版本发布时间确定应有的语言码：1.0 及更早为 none，其余按新旧格式归一化大小写。</summary>
    private static string ResolveLanguage(string? language, DateTime? releaseTime)
    {
        if (IsVersionUnder1Dot1(releaseTime))
        {
            return "none";
        }

        return NormalizeLanguageCode(language, UseLegacyRegionCase(releaseTime));
    }

    /// <summary>
    /// 归一化语言码格式：连字符换下划线，语言部分小写，区域部分按版本选择大小写；
    /// 无区域段的（如 ja、ko 本身就是全小写）整体转小写。
    /// </summary>
    private static string NormalizeLanguageCode(string? languageCode, bool legacyRegionCase)
    {
        var normalized = string.IsNullOrWhiteSpace(languageCode)
            ? "none"
            : languageCode.Replace('-', '_').Trim();
        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        var segments = normalized.Split('_', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return normalized.ToLowerInvariant();
        }

        var region = legacyRegionCase
            ? segments[1].ToUpperInvariant()
            : segments[1].ToLowerInvariant();
        return $"{segments[0].ToLowerInvariant()}_{region}";
    }

    /// <summary>
    /// 1.0 及更早（发布时间 2011-11-18 及之前，且晚于 2000-01-01 以排除脏数据）没有语言概念。
    /// </summary>
    private static bool IsVersionUnder1Dot1(DateTime? releaseTime)
    {
        return releaseTime is { } time
               && time > new DateTime(2000, 1, 1)
               && time <= new DateTime(2011, 11, 18);
    }

    /// <summary>
    /// 1.1 ~ 1.10（发布时间 2012-01-12 ~ 2016-06-08）区域码必须大写（zh_CN）：
    /// 1.1 ~ 1.5 用 zh_cn 会 NPE 崩溃；1.11 起才改用小写。
    /// </summary>
    private static bool UseLegacyRegionCase(DateTime? releaseTime)
    {
        return releaseTime is { } time
               && time >= new DateTime(2012, 1, 12)
               && time <= new DateTime(2016, 6, 8);
    }

    /// <summary>语言主码属于中文/日文/韩文（非拉丁文字）时需要强制 Unicode 字体。</summary>
    private static bool IsNonLatinLanguage(string languageCode)
    {
        var language = languageCode.Split('_', 2)[0].ToLowerInvariant();
        return language is "zh" or "ja" or "ko";
    }

    private static int IndexOfKey(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0 && lines[i][..colon].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetLineValue(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? "" : line[(colon + 1)..].Trim();
    }
}

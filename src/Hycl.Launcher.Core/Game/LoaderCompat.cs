using Hycl.Launcher.Core.Game.Loaders;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 加载器组合兼容性规则（移植自 PCL-CE PageDownloadInstall 的 Load*GetError 检查）：
/// 主加载器单选；OptiFine/LiteLoader 按矩阵与主加载器共存。
/// </summary>
public static class LoaderCompat
{
    /// <summary>比较两个版本号；无法解析时按字符串比较。返回 -1/0/1。</summary>
    public static int CompareVersion(string? a, string? b)
    {
        var versionA = TryParse(a);
        var versionB = TryParse(b);
        if (versionA is null || versionB is null)
        {
            return string.CompareOrdinal(a ?? "", b ?? "");
        }

        return versionA.CompareTo(versionB);
    }

    public static bool IsVersionAtLeast(string version, string baseline)
        => CompareVersion(version.Split('-')[0].Split('+')[0], baseline) >= 0;

    public static bool IsVersionAtMost(string version, string baseline)
        => CompareVersion(version.Split('-')[0].Split('+')[0], baseline) <= 0;

    private static Version? TryParse(string? text)
        => Version.TryParse((text ?? "").Split('-')[0].Split('+')[0], out var value) ? value : null;

    /// <summary>
    /// 主加载器是否与 OptiFine 兼容（对应 PCL-CE LoadOptiFineGetError 前置检查）。
    /// mainKind 为 null 表示原版。
    /// </summary>
    public static bool IsOptiFineCompatibleWith(string? mainKind, string minecraftVersion)
    {
        return mainKind?.ToLowerInvariant() switch
        {
            null or "" or "vanilla" => true,
            // NeoForge / LabyMod / Cleanroom 与 OptiFine 完全互斥
            "neoforge" or "labymod" or "cleanroom" => false,
            // Forge 1.13 ~ 1.14.3 与 OptiFine 全部不兼容
            "forge" => !(IsVersionAtLeast(minecraftVersion, "1.13") && IsVersionAtMost(minecraftVersion, "1.14.3")),
            // Fabric / Quilt / LegacyFabric 需要 OptiFabric 桥（尚未实现），一律判不兼容，
            // 与 LoaderInstallService 的执行口径保持一致，避免 UI 可选但安装必失败
            "fabric" or "quilt" or "legacyfabric" => false,
            _ => true
        };
    }

    /// <summary>
    /// 主加载器是否与 LiteLoader 兼容（对应 PCL-CE LoadLiteLoaderGetError）：
    /// 仅 NeoForge / LegacyFabric / LabyMod / Cleanroom 互斥。
    /// </summary>
    public static bool IsLiteLoaderCompatibleWith(string? mainKind)
    {
        return mainKind?.ToLowerInvariant() switch
        {
            "neoforge" or "legacyfabric" or "labymod" or "cleanroom" => false,
            _ => true
        };
    }

    /// <summary>
    /// OptiFine 是否适配某个 Forge 版本（移植自 PCL-CE IsOptiFineSuitForForge，
    /// 依据 OptiFine 的 RequiredForgeVersion 精确匹配）。
    /// </summary>
    public static bool IsOptiFineSuitableForForge(OptiFineEntry optiFine, ForgeEntry forge)
    {
        if (!string.Equals(optiFine.Inherit, forge.Inherit, StringComparison.OrdinalIgnoreCase))
        {
            return false; // 不是同一个大版本
        }

        if (optiFine.RequiredForgeVersion is null)
        {
            return false; // 不兼容 Forge
        }

        if (string.IsNullOrWhiteSpace(optiFine.RequiredForgeVersion))
        {
            return true; // 对 Forge 版本无要求
        }

        if (optiFine.RequiredForgeVersion.Contains('.', StringComparison.Ordinal))
        {
            // 形如 28.1.56：要求 Forge 版本完全一致
            return CompareVersion(forge.NumericVersion.ToString(), optiFine.RequiredForgeVersion) == 0;
        }

        // 形如 1161：只匹配版本号最后一段
        return forge.NumericVersion.Revision >= 0
               && Math.Abs(forge.NumericVersion.Revision - TryParseRevision(optiFine.RequiredForgeVersion)) < 0.5;
    }

    private static double TryParseRevision(string text)
        => double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : -1;
}

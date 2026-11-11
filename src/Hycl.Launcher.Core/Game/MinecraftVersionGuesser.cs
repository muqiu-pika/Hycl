using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Game.Comp;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 把实例的版本标识（可能带加载器名，如 <c>1.20.1-fabric</c>、<c>fabric-loader-0.15.11-1.20.1</c>）
/// 规整成纯 Minecraft 版本号，供社区资源的“游戏版本”筛选与搜索参数使用。
/// 加载器信息不进入版本号：筛选页另有“加载器”下拉负责按加载器过滤。
/// </summary>
public static class MinecraftVersionGuesser
{
    private static readonly Regex NumericToken = new(@"\d+(\.\d+)+", RegexOptions.Compiled);

    public static string? Normalize(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        var trimmed = versionId.Trim();

        // 本身就是纯版本号（含 26.3-1 这类合法的带尾缀版本，已在正式版清单里时直接认可）
        if (CompLocalization.IsReleaseGameVersion(trimmed)
            || CompLocalization.ReleaseGameVersions.Any(v => v.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return trimmed;
        }

        var tokens = NumericToken.Matches(trimmed)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        // 数字开头（Hycl 命名 1.20.1-fabric / 26.3-fabric / 1.20.1-OptiFine_HD_U_I6）：MC 版本在最前；
        // 加载器前缀（官方安装器 fabric-loader-0.15.11-1.20.1 / quilt-loader-0.26.4-1.20.1）：MC 版本在最后。
        return char.IsDigit(trimmed[0]) ? tokens[0] : tokens[^1];
    }
}

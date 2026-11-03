using System.Globalization;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>Forge 系加载器种类。</summary>
public enum ForgelikeKind
{
    Forge,
    NeoForge,
    Cleanroom
}

/// <summary>Forge 系（Forge / NeoForge / Cleanroom）条目的公共部分，移植自 PCL-CE DlForgelikeEntry。</summary>
public abstract class ForgelikeEntry(
    string inherit,
    string versionName,
    ForgelikeKind kind,
    Version version)
{
    /// <summary>对应的 Minecraft 版本，如 1.12.2。</summary>
    public string Inherit { get; } = inherit;

    /// <summary>可对玩家显示的非格式化版本名。</summary>
    public string VersionName { get; } = versionName;

    public ForgelikeKind Kind { get; } = kind;

    /// <summary>标准化版本号，仅用于比较与排序。</summary>
    public Version NumericVersion { get; } = version;

    /// <summary>
    /// Forge：MC 版本是否小于 1.13（走 installer 解包）；NeoForge：是否为 1.20.1 旧包名；Cleanroom：固定 false。
    /// </summary>
    public bool IsLegacy => Kind != ForgelikeKind.Cleanroom && NumericVersion.Major < 20;

    public int CompareTo(ForgelikeEntry? other)
        => other is null ? 1 : NumericVersion.CompareTo(other.NumericVersion);
}

/// <summary>Forge 版本条目（移植自 PCL-CE DlForgeVersionEntry，含分支特判）。</summary>
public sealed class ForgeEntry : ForgelikeEntry
{
    private static readonly string[] ForcedBranchVersions = ["11.15.1.2318", "11.15.1.1902", "11.15.1.1890"];

    public ForgeEntry(string version, string? branch, string inherit)
        : base(inherit, version, ForgelikeKind.Forge, ParseVersion(version))
    {
        // PCL-CE 分支特判：1.8.9 的三个特殊 build 强制走 1.8.9 分支；
        // 1.7.10 的第 4 段 ≥1300 属于 1.7.10 分支
        if (ForcedBranchVersions.Contains(version))
        {
            branch = "1.8.9";
        }

        if (branch is null
            && inherit == "1.7.10"
            && version.Split('.') is { Length: 4 } segments
            && double.TryParse(segments[3], CultureInfo.InvariantCulture, out var build)
            && build >= 1300d)
        {
            branch = "1.7.10";
        }

        Branch = branch;
        FileVersion = version + (branch is null ? "" : $"-{branch}");
    }

    private static Version ParseVersion(string version)
        => new(version);

    /// <summary>用于下载的文件版本名（可能含分支）。</summary>
    public string FileVersion { get; }

    public string? Branch { get; }

    /// <summary>安装类型：installer / client / universal。</summary>
    public string Category { get; init; } = "";

    /// <summary>文件 MD5 或 SHA1。</summary>
    public string Hash { get; init; } = "";

    public bool IsRecommended { get; init; }

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>文件扩展名（installer 为 jar，其余 zip）。</summary>
    public string FileExtension => Category == "installer" ? "jar" : "zip";

    /// <summary>安装器下载地址（镜像在前，官方在后；由 DownloadClient 按序回退）。</summary>
    public IReadOnlyList<string> GetInstallerUrls()
        =>
        [
            $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{Inherit}-{FileVersion}/forge-{Inherit}-{FileVersion}-{Category}.{FileExtension}",
            $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/{Inherit}-{FileVersion}/forge-{Inherit}-{FileVersion}-{Category}.{FileExtension}"
        ];
}

/// <summary>NeoForge 版本条目（移植自 PCL-CE DlNeoForgeListEntry，含 1.20.1 特判）。</summary>
public sealed class NeoForgeEntry : ForgelikeEntry
{
    public NeoForgeEntry(string apiName)
        : base(ResolveInherit(apiName), ResolveVersionName(apiName), ForgelikeKind.NeoForge, ParseVersion(apiName))
    {
        ApiName = apiName;
        IsBeta = apiName.Contains("beta", StringComparison.Ordinal)
                 || apiName.Contains("alpha", StringComparison.Ordinal);
        LegacyPackage = apiName.Contains("1.20.1", StringComparison.Ordinal);
    }

    private static string ResolveVersionName(string apiName)
        => apiName.Contains("1.20.1", StringComparison.Ordinal)
            ? apiName.Replace("1.20.1-", "")
            : apiName;

    private static string ResolveInherit(string apiName)
    {
        if (apiName.Contains("1.20.1", StringComparison.Ordinal))
        {
            // 1.20.1 的 NeoForge 仍走旧 forge 包名（47.1.x）
            return "1.20.1";
        }

        if (apiName.StartsWith("0.", StringComparison.Ordinal))
        {
            return apiName.Split('-')[0].Split('.')[1];
        }

        var numeric = ParseVersion(apiName);
        var inherit = numeric.Major >= 24
            ? $"{numeric.Major}.{numeric.Minor}{(numeric.Build > 0 ? $".{numeric.Build}" : "")}"
            : $"1.{numeric.Major}{(numeric.Minor > 0 ? $".{numeric.Minor}" : "")}";
        if (apiName.Contains('+', StringComparison.Ordinal))
        {
            inherit += $"-{apiName.Split('+', 2)[1]}";
        }

        return inherit;
    }

    private static Version ParseVersion(string apiName)
    {
        var numeric = apiName.Split('-')[0].Split('+')[0];
        if (apiName.Contains("1.20.1-", StringComparison.Ordinal))
        {
            numeric = "19." + apiName.Replace("1.20.1-", "").Split('-')[0].Split('+')[0];
        }
        else if (numeric.StartsWith("0.", StringComparison.Ordinal))
        {
            var segments = numeric.Split('.');
            return new Version(0, 0, int.Parse(segments[^1], CultureInfo.InvariantCulture));
        }

        return new Version(numeric);
    }

    /// <summary>API 使用的原始版本字符串，如 20.4.30-beta / 1.20.1-47.1.99。</summary>
    public string ApiName { get; }

    public bool IsBeta { get; }

    /// <summary>是否为 1.20.1 的旧 forge 包名版本。</summary>
    public bool LegacyPackage { get; }

    /// <summary>文件在官方 maven 的基础地址（不含后缀）。</summary>
    public string UrlBase
    {
        get
        {
            var packageName = LegacyPackage ? "forge" : "neoforge";
            return $"https://maven.neoforged.net/releases/net/neoforged/{packageName}/{ApiName}/{packageName}-{ApiName}";
        }
    }

    /// <summary>安装器下载地址（官方 maven 在前，BMCLAPI 镜像在后）。</summary>
    public IReadOnlyList<string> GetInstallerUrls()
        =>
        [
            UrlBase + "-installer.jar",
            UrlBase + "-installer.jar"
                .Replace("https://maven.neoforged.net/releases", "https://bmclapi2.bangbang93.com/maven")
        ];
}

/// <summary>Cleanroom 版本条目（移植自 PCL-CE DlCleanroomListEntry）。</summary>
public sealed class CleanroomEntry : ForgelikeEntry
{
    public CleanroomEntry(string apiName)
        : base("1.12.2", apiName, ForgelikeKind.Cleanroom, new Version(apiName.Split('-')[0].Split('+')[0]))
    {
        ApiName = apiName;
        IsBeta = apiName.Contains("alpha", StringComparison.Ordinal);
    }

    /// <summary>API 使用的原始版本字符串，如 0.2.4-alpha。</summary>
    public string ApiName { get; }

    public bool IsBeta { get; }

    /// <summary>文件在 GitHub Release 的基础地址（不含后缀）。</summary>
    public string UrlBase
        => $"https://github.com/CleanroomMC/Cleanroom/releases/download/{ApiName}/cleanroom-{ApiName}";
}

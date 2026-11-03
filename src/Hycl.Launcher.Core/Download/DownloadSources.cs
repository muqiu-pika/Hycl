using Hycl.Launcher.Core.Config;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 下载源与镜像地址映射（对应 PCL-CE 的下载源设置：官方 / BMCLAPI）。
/// </summary>
public static class DownloadSources
{
    public const string OfficialVersionManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public const string BmclapiVersionManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";

    public const string OfficialLibraries = "https://libraries.minecraft.net";

    public const string OfficialAssets = "https://resources.download.minecraft.net";

    public static string GetVersionManifestUrl(DownloadSourceKind source)
        => source == DownloadSourceKind.Official ? OfficialVersionManifest : BmclapiVersionManifest;

    public static string GetVersionJsonUrl(DownloadSourceKind source, string versionId, string officialUrl)
        => source == DownloadSourceKind.Official
            ? officialUrl
            : $"{BmclapiBase}/version/{Uri.EscapeDataString(versionId)}/json";

    public static string GetClientJarUrl(DownloadSourceKind source, string versionId, string officialUrl)
        => source == DownloadSourceKind.Official
            ? officialUrl
            : $"{BmclapiBase}/version/{Uri.EscapeDataString(versionId)}/client";

    public static string GetAssetIndexUrl(DownloadSourceKind source, string assetIndexId, string officialUrl)
        => source == DownloadSourceKind.Official
            ? officialUrl
            : $"{BmclapiBase}/assets/indexes/{Uri.EscapeDataString(assetIndexId)}.json";

    /// <summary>把 Mojang 官方资源地址改写到镜像地址；非官方地址（如 Fabric Maven）保持原样。</summary>
    public static string RewriteResourceUrl(DownloadSourceKind source, string url)
    {
        if (source == DownloadSourceKind.Official || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith(OfficialLibraries, StringComparison.OrdinalIgnoreCase))
        {
            return BmclapiBase + "/maven" + url[OfficialLibraries.Length..];
        }

        if (url.StartsWith(OfficialAssets, StringComparison.OrdinalIgnoreCase))
        {
            return BmclapiBase + "/assets" + url[OfficialAssets.Length..];
        }

        return url;
    }
}

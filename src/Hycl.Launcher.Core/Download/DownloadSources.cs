namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 下载源与镜像地址映射：逐条对应 PCL-CE GameDownload 的 DlSource* 重写表。
/// 镜像站为 BMCLAPI；Mod 社区镜像为 mcimirror。
/// </summary>
public static class DownloadSources
{
    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";

    /// <summary>官方版本清单（v2 带各版本 releaseTime，用于分组排序）。</summary>
    public const string OfficialVersionManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public const string BmclapiVersionManifest = BmclapiBase + "/mc/game/version_manifest_v2.json";

    public const string OfficialLibraries = "https://libraries.minecraft.net";

    public const string OfficialAssets = "https://resources.download.minecraft.net";

    private const string UnlistedOfficial = "https://zkitefly.github.io/unlisted-versions-of-minecraft";

    private const string UnlistedMirror = "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto";

    /// <summary>涉及加载器官方仓库的库文件，BMCLAPI 是唯一可用镜像（对应 PCL-CE SourceArray）。</summary>
    private static readonly string[] LoaderSourceArray = ["minecraftforge", "fabricmc", "neoforged"];

    /// <summary>按官方/镜像优先级排序 URL（对应 PCL-CE DlSourceOrder / DlVersionListOrder；Union 顺带去重）。</summary>
    public static IReadOnlyList<string> OrderUrls(
        IEnumerable<string> officialUrls,
        IEnumerable<string> mirrorUrls,
        bool preferMojang)
        => preferMojang
            ? officialUrls.Union(mirrorUrls).ToList()
            : mirrorUrls.Union(officialUrls).ToList();

    /// <summary>版本清单地址列表：版本列表源三档决定官方与 BMCLAPI 的先后。</summary>
    public static IReadOnlyList<string> GetVersionManifestUrls(int versionListSource, bool mojangFast)
    {
        var preferMojang = versionListSource == (int)DownloadSourceMode.OfficialOnly
            || (versionListSource == (int)DownloadSourceMode.OfficialAuto && mojangFast);
        return preferMojang
            ? [OfficialVersionManifest, BmclapiVersionManifest]
            : [BmclapiVersionManifest, OfficialVersionManifest];
    }

    /// <summary>Launcher/Meta 文件（版本 JSON、客户端 jar、资源索引等）的源顺序。</summary>
    public static IReadOnlyList<string> GetLauncherOrMetaUrls(string original, DownloadPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(original);
        return OrderUrls(
            [original],
            [
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase)
                    .Replace("https://piston-meta.mojang.com", BmclapiBase)
                    .Replace("https://launcher.mojang.com", BmclapiBase)
                    .Replace("https://launchermeta.mojang.com", BmclapiBase)
                    .Replace(UnlistedOfficial, UnlistedMirror)
            ],
            policy.PreferMojangForFiles);
    }

    /// <summary>
    /// 依赖库文件的源顺序。
    /// 自定义 maven 仓库域名（maven.minecraftforge.net / maven.fabricmc.net / maven.neoforged.net 等）
    /// 对应 PCL-CE Library\Net.cs _GetLibraryUrls：原始 URL + BMCLAPI maven 变体；
    /// 官方 libraries/piston 域名对应 PCL-CE GameDownload.DlSourceLibraryGet 重写表（加载器库仅用镜像）。
    /// </summary>
    public static IReadOnlyList<string> GetLibraryUrls(string original, DownloadPolicy policy)
    {
        if (original.Contains("maven", StringComparison.Ordinal))
        {
            var urls = new List<string>();
            var bmclapiUrl = original
                .Replace(original[..original.IndexOf("maven", StringComparison.Ordinal)], BmclapiBase + "/")
                .Replace("maven.fabricmc.net", "maven")
                .Replace("maven.minecraftforge.net", "maven")
                .Replace("maven.neoforged.net/releases", "maven");
            if (policy.PreferMojangForFiles)
            {
                urls.Add(original);
                urls.Add(bmclapiUrl);
            }
            else
            {
                urls.Add(bmclapiUrl);
                urls.Add(original);
            }

            if (original.Contains("launcher.mojang.com/v1/objects", StringComparison.Ordinal)
                || original.Contains("client.txt", StringComparison.Ordinal)
                || original.Contains(".tsrg", StringComparison.Ordinal))
            {
                urls.AddRange(GetLauncherOrMetaUrls(original, policy));
            }

            return urls.Distinct(StringComparer.Ordinal).ToList();
        }

        // Forge/Fabric/NeoForge 相关库只有 BMCLAPI 有镜像，官方源不参与（对应 PCL-CE 特判分支）
        if (LoaderSourceArray.Any(original.Contains))
        {
            return
            [
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase + "/maven")
                    .Replace("https://piston-meta.mojang.com", BmclapiBase + "/maven")
                    .Replace(OfficialLibraries, BmclapiBase + "/maven")
                    .Replace(UnlistedOfficial, UnlistedMirror),
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase + "/libraries")
                    .Replace("https://piston-meta.mojang.com", BmclapiBase + "/libraries")
                    .Replace(OfficialLibraries, BmclapiBase + "/libraries")
                    .Replace(UnlistedOfficial, UnlistedMirror)
            ];
        }

        return OrderUrls(
            [original],
            [
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase + "/maven")
                    .Replace("https://piston-meta.mojang.com", BmclapiBase + "/maven")
                    .Replace(OfficialLibraries, BmclapiBase + "/maven")
                    .Replace(UnlistedOfficial, UnlistedMirror),
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase + "/libraries")
                    .Replace("https://piston-meta.mojang.com", BmclapiBase + "/libraries")
                    .Replace(OfficialLibraries, BmclapiBase + "/libraries")
                    .Replace(UnlistedOfficial, UnlistedMirror),
                original
            ],
            policy.PreferMojangForFiles);
    }

    /// <summary>资源（assets）文件的源顺序（对应 PCL-CE DlSourceAssetsGet）。</summary>
    public static IReadOnlyList<string> GetAssetUrls(string original, DownloadPolicy policy)
    {
        original = original.Replace(
            "http://resources.download.minecraft.net",
            "https://resources.download.minecraft.net");
        return OrderUrls(
            [original],
            [
                original
                    .Replace("https://piston-data.mojang.com", BmclapiBase + "/assets")
                    .Replace("https://piston-meta.mojang.com", BmclapiBase + "/assets")
                    .Replace(OfficialAssets, BmclapiBase + "/assets")
            ],
            policy.PreferMojangForFiles);
    }

    /// <summary>Mod API（Modrinth / CurseForge）镜像地址（对应 PCL-CE DlSourceModGet）。</summary>
    public static string GetModApiMirror(string original)
        => original
            .Replace("https://api.modrinth.com", "https://mod.mcimirror.top/modrinth")
            .Replace("https://api.curseforge.com", "https://mod.mcimirror.top/curseforge");

    /// <summary>Mod 文件镜像地址列表（对应 PCL-CE DlSourceModDownloadGet；compSourceSolution：0 镜像 / 1 官方+镜像 / 2 仅官方）。</summary>
    public static IReadOnlyList<string> GetModDownloadUrls(string original, int compSourceSolution)
    {
        var mirror = original
            .Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top")
            .Replace("https://edge.forgecdn.net", "https://mod.mcimirror.top");
        List<string> result = compSourceSolution switch
        {
            0 => [mirror, mirror],
            1 => [original, mirror],
            2 => [original, original],
            _ => [original]
        };
        result.Add(original);
        return result.Distinct().ToList();
    }
}

namespace Hycl.Launcher.PluginContract;

/// <summary>Hycl-online 的托管站点约定（契约层：启动器与插件共享同一份配置）。</summary>
public static class HyclOnlineSites
{
    /// <summary>
    /// EdgeOne Pages 托管站点地址（国内加速主源），例如 "https://hycl-online.edgeone.app"。
    /// 空 = 尚未部署 EdgeOne，跳过该源（GitHub 链接由启动器镜像池自动展开，GitHub 原站托底）。
    /// </summary>
    public const string EdgeOneSiteUrl = "";

    /// <summary>EdgeOne 托管清单地址；未部署返回空字符串。</summary>
    public static string EdgeOneManifestUrl => EdgeOneSiteUrl.Length == 0 ? "" : EdgeOneSiteUrl.TrimEnd('/') + "/online.json";

    /// <summary>
    /// EdgeOne 托管的工具包文件地址（deploy 工作流的目录约定：tools/terracotta/&lt;版本&gt;/&lt;文件名&gt;）；
    /// 未部署返回 null。
    /// </summary>
    public static string? EdgeOneToolFileUrl(string toolVersion, string fileName)
        => EdgeOneSiteUrl.Length == 0 ? null : $"{EdgeOneSiteUrl.TrimEnd('/')}/tools/terracotta/{toolVersion}/{fileName}";
}

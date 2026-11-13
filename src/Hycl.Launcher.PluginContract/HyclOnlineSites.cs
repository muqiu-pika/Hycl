namespace Hycl.Launcher.PluginContract;

/// <summary>Hycl-online 的托管站点约定（契约层：启动器与插件共享同一份配置）。</summary>
public static class HyclOnlineSites
{
    /// <summary>
    /// EdgeOne Pages 托管站点地址（已绑定的自定义域名，作为清单与二进制的主源），
    /// 例如 "https://hyclonline.mcapp.eu.org"。
    /// 必须是自定义域名，不能填 *.edgeone.app 预览域名（带鉴权 / 大陆网络返回 401）。
    /// 空 = 尚未部署 EdgeOne，跳过该源（GitHub 链接由启动器镜像池自动展开，GitHub 原站托底）。
    /// </summary>
    public const string EdgeOneSiteUrl = "https://hyclonline.mcapp.eu.org";

    /// <summary>EdgeOne 托管清单地址；未部署返回空字符串。</summary>
    public static string EdgeOneManifestUrl => EdgeOneSiteUrl.Length == 0 ? "" : EdgeOneSiteUrl.TrimEnd('/') + "/online.json";

    /// <summary>EdgeOne 托管的社区 EasyTier 公共节点清单（AES-256-GCM 密文 public-nodes.enc）。</summary>
    public static string EdgeOnePublicNodesUrl => EdgeOneSiteUrl.Length == 0 ? "" : EdgeOneSiteUrl.TrimEnd('/') + "/public-nodes.enc";

    /// <summary>
    /// EdgeOne 托管的工具包文件地址（deploy 工作流的目录约定：tools/terracotta/&lt;版本&gt;/&lt;文件名&gt;）；
    /// 未部署返回 null。
    /// 【兼容保留】已分发的插件二进制（≤1.3.1）的 BuildOfficialUrls 直接调用本方法，
    /// 删除会让旧插件 JIT 抛 MissingMethodException、陶瓦卡永远停在「正在检查」——
    /// 契约层的 public 成员一经发布即冻结，重构只能加不能删。
    /// </summary>
    [Obsolete("旧插件二进制兼容保留；新代码请使用插件内置官方镜像链。")]
    public static string? EdgeOneToolFileUrl(string toolVersion, string fileName)
        => EdgeOneSiteUrl.Length == 0 ? null : $"{EdgeOneSiteUrl.TrimEnd('/')}/tools/terracotta/{toolVersion}/{fileName}";
}

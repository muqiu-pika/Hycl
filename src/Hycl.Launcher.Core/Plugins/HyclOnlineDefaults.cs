using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>Hycl-online 清单的默认地址与插件 ABI 常量。</summary>
public static class HyclOnlineDefaults
{
    /// <summary>
    /// Hycl-online 仓库标识。
    /// 用户也可在设置页 → 插件工坊里自定义清单源地址，优先级更高。
    /// </summary>
    public const string OnlineRepository = "muqiu-pika/Hycl-online";

    /// <summary>启动器当前支持的插件契约版本（与 PluginContract 同步升级）。</summary>
    public const int CurrentAbi = 1;

    /// <summary>
    /// 默认清单源：EdgeOne 托管（若已部署，见 <see cref="HyclOnlineSites.EdgeOneSiteUrl"/>）
    /// → GitHub raw 直链（经 GitHubSources 镜像池展开，原站托底）。
    /// </summary>
    public static IReadOnlyList<string> DefaultManifestUrls
    {
        get
        {
            var edgeOne = HyclOnlineSites.EdgeOneManifestUrl;
            return edgeOne.Length == 0
                ? [$"https://raw.githubusercontent.com/{OnlineRepository}/main/online.json"]
                : [edgeOne, $"https://raw.githubusercontent.com/{OnlineRepository}/main/online.json"];
        }
    }

    /// <summary>当前平台在清单 assets 里的键（如 win-x64）；不支持的平台返回空。</summary>
    public static string ToolAssetKey => HyclOnlineTools.ToolAssetKey;
}

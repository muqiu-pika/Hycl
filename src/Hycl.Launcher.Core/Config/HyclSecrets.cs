namespace Hycl.Launcher.Core.Config;

/// <summary>
/// 需要用户自行填写的密钥与鉴权配置（框架预留，默认留空）。
/// 留空时相关功能自动降级或停用，不影响其余下载功能。
/// </summary>
public static class HyclSecrets
{
    /// <summary>
    /// CurseForge API Key（用于 OptiFabric 等仅上架 CurseForge 的资源检索）。
    /// 留空时：跳过 CurseForge 源，仅使用 Modrinth。
    /// 申请地址：https://console.curseforge.com/
    /// </summary>
    public const string CurseForgeApiKey = "";

    /// <summary>
    /// 自定义镜像站地址（可选；留空使用默认 BMCLAPI / mcimirror）。
    /// </summary>
    public const string CustomMirrorBase = "";

    /// <summary>CurseForge API Key 是否已配置。</summary>
    public static bool HasCurseForgeApiKey => !string.IsNullOrWhiteSpace(CurseForgeApiKey);
}

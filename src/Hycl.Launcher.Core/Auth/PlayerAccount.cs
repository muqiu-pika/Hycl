using System.Security.Cryptography;
using System.Text;

namespace Hycl.Launcher.Core.Auth;

/// <summary>
/// 启动器账号。Type 取值：offline（离线）/ msa（微软正版）/ authlib（皮肤站，预留）。
/// 正版与皮肤站账号携带令牌字段；离线账号仅 Name/Uuid。
/// </summary>
public sealed class PlayerAccount
{
    public string Name { get; set; } = "Player";

    public string Uuid { get; set; } = "";

    public string Type { get; set; } = "offline";

    /// <summary>进入游戏用的访问令牌（正版为 MC 服务令牌，约 24 小时有效）。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>微软 refresh_token，用于启动前静默续期。</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>皮肤站 clientToken（预留）。</summary>
    public string ClientToken { get; set; } = "";

    /// <summary>皮肤站登录名（预留）。</summary>
    public string LoginName { get; set; } = "";

    /// <summary>皮肤站 Yggdrasil 根地址（预留）。</summary>
    public string Server { get; set; } = "";

    /// <summary>皮肤站显示名（预留）。</summary>
    public string ServerName { get; set; } = "";

    /// <summary>正版 Xbox XUID（auth_xuid 占位符）。</summary>
    public string Xuid { get; set; } = "";

    /// <summary>玩家皮肤贴图 URL（正版/皮肤站登录后由档案接口取得）。</summary>
    public string SkinUrl { get; set; } = "";

    /// <summary>皮肤贴图本地缓存路径（下载后填充，供首页纸娃娃渲染）。</summary>
    public string SkinPath { get; set; } = "";

    /// <summary>披风贴图 URL（档案接口返回，随「刷新」更新）。</summary>
    public string CapeUrl { get; set; } = "";

    /// <summary>披风贴图本地缓存路径（下载完成后填充，供首页纸娃娃渲染）。</summary>
    public string CapePath { get; set; } = "";

    /// <summary>皮肤模型：slim（Alex）或空（Classic/Steve）。</summary>
    public string SkinModel { get; set; } = "";

    /// <summary>离线档案自选的内置皮肤键（steve / alex / zuri）；仅离线账号可更换。</summary>
    public string SkinKey { get; set; } = "";

    /// <summary>访问令牌过期时间；null 表示无需续期（离线）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsMicrosoft => Type == "msa";

    public bool IsAuthlib => Type == "authlib";

    /// <summary>令牌是否临近过期（启动前 10 分钟内到期视为过期）。</summary>
    public bool IsTokenNearExpiry => Type != "offline"
                                     && (string.IsNullOrWhiteSpace(AccessToken)
                                         || ExpiresAt is null
                                         || ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(10));

    public string AvatarText => Name.Length >= 2 ? Name[..2].ToUpperInvariant() : Name.ToUpperInvariant();
}

public static class OfflineUuid
{
    /// <summary>按 "OfflinePlayer:名称" 生成稳定的 UUID（与原版离线 UUID 规则一致）。</summary>
    public static string Create(string playerName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        // 设置 UUID v3 版本位与 RFC 4122 variant
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Guid.Parse(ToHex(bytes)).ToString();
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(32);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }
}

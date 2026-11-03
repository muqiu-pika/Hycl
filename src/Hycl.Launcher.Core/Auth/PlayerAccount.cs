using System.Security.Cryptography;
using System.Text;

namespace Hycl.Launcher.Core.Auth;

/// <summary>离线账号（对应 PCL-CE 的离线登录）。</summary>
public sealed class PlayerAccount
{
    public string Name { get; set; } = "Player";

    public string Uuid { get; set; } = "";

    public string Type { get; set; } = "offline";

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

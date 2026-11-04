using System.Security.Cryptography;
using System.Text;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Config;

/// <summary>
/// 需要随程序分发的密钥与鉴权配置。
/// <para>
/// CurseForge API Key 以“密文分片 + 分散掩码材料”的形式内置：源码中不存在明文，
/// 只有首次使用时才在内存中还原（不做磁盘缓存）。任何需要展示密钥的日志都必须走
/// <see cref="MaskApiKey"/> 掩码。
/// </para>
/// <para>
/// 可通过环境变量 <c>HYCL_CURSEFORGE_API_KEY</c> 或配置文件字段 <c>curseForgeApiKey</c>
/// 覆盖内置密钥，便于轮换密钥而无需重新编译；显式覆盖时优先使用覆盖值。
/// </para>
/// </summary>
public static class HyclSecrets
{
    /// <summary>显式覆盖 CurseForge API Key 的环境变量名。</summary>
    public const string OverrideEnvironmentVariable = "HYCL_CURSEFORGE_API_KEY";

    // ---------- 密文分片（Base64，按序拼接后为完整密文） ----------
    // 三片分别偏移存放，避免任何单一位置出现完整密文。
    private const string BlobHead = "2T7FE2cweVGBK6WDZbWxSR";
    private const string BlobBody = "Kr0osdow05Q2vtCoHLy1gUNQ";
    private const string BlobTail = "ClQpRqn79477/aDBdegw1935av/cE+j2Mw";

    // ---------- 掩码材料（两段异或后与运行期序号混合） ----------
    private static readonly byte[] MaskSeedA =
    [
        0x3B, 0x9E, 0x41, 0xD7, 0x25, 0x88, 0x6C, 0xF2,
        0x19, 0xAE, 0x57, 0xC4, 0x0D, 0x71, 0x93, 0xE8
    ];

    private static readonly byte[] MaskSeedB =
    [
        0xCA, 0x17, 0x73, 0x28, 0x9F, 0x5B, 0xE4, 0x36,
        0x82, 0x5D, 0xB1, 0x0A, 0xF7, 0x49, 0xC6, 0x2E
    ];

    private const byte MaskBlend = 0x6D;

    private const int MaskOffset = 11;

    /// <summary>明文 SHA-256 前 12 字节的 Base64（仅用于解密自检，不含明文信息）。</summary>
    private const string PlainDigest = "56ljrZsZN14FgZgl";

    private static string? _override;
    private static string? _builtIn;
    private static bool _builtInAttempted;
    private static LauncherLogger? _logger;

    /// <summary>
    /// 自定义镜像站地址（可选；留空使用默认 BMCLAPI / mcimirror）。
    /// </summary>
    public const string CustomMirrorBase = "";

    /// <summary>
    /// CurseForge API Key。优先使用环境变量 / 配置项覆盖值，否则返回内置密钥解密结果；
    /// 两者都不可用时返回空字符串（调用方应视为“无密钥”，走镜像）。
    /// </summary>
    public static string CurseForgeApiKey => _override ?? DecryptBuiltIn() ?? "";

    /// <summary>CurseForge API Key 是否可用。</summary>
    public static bool HasCurseForgeApiKey => !string.IsNullOrWhiteSpace(CurseForgeApiKey);

    /// <summary>
    /// 读取显式覆盖值并做一次解密自检；应在应用启动时调用一次。
    /// 解密失败只记录一条错误日志并降级为镜像，不影响启动。
    /// </summary>
    public static void Initialize(LauncherConfig config, LauncherLogger logger)
    {
        _logger = logger;

        var fromEnvironment = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        var fromConfig = config.CurseForgeApiKey;
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            _override = fromEnvironment.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            _override = fromConfig.Trim();
        }
        else
        {
            _override = null;
        }

        var key = CurseForgeApiKey;
        if (string.IsNullOrEmpty(key))
        {
            logger.Warn("CurseForge 官方源未启用：内置密钥解密失败或未配置，社区资源将只使用镜像源");
            return;
        }

        var source = _override is null
            ? "内置密文"
            : !string.IsNullOrWhiteSpace(fromEnvironment) ? "环境变量覆盖" : "配置项覆盖";
        logger.Info($"CurseForge 官方源已启用（密钥 {MaskApiKey(key)}，来源：{source}）");
    }

    /// <summary>日志用掩码：只保留前缀 <c>$2a$10$</c>，其余一律替换为 ***。</summary>
    public static string MaskApiKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "***";
        }

        // bcrypt 密钥固定以 $2a$10$ 开头，只暴露这一段（本身不含秘密信息）
        var prefixLength = key.StartsWith("$2a$", StringComparison.Ordinal) && key.Length > 7 ? 7 : 0;
        return key[..prefixLength] + "***";
    }

    /// <summary>解密内置密钥；失败返回 null 并记录一次错误日志，绝不返回半成品。</summary>
    private static string? DecryptBuiltIn()
    {
        if (_builtInAttempted)
        {
            return _builtIn;
        }

        _builtInAttempted = true;
        try
        {
            var encoded = BlobHead + BlobBody + BlobTail;
            var cipher = Convert.FromBase64String(encoded);
            var seed = new byte[MaskSeedA.Length];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = (byte)(MaskSeedA[i] ^ MaskSeedB[i] ^ MaskBlend);
            }

            var plain = new byte[cipher.Length];
            for (var i = 0; i < cipher.Length; i++)
            {
                var mixed = (seed[i % seed.Length] + i * 29 + MaskOffset) & 0xFF;
                var tweak = i % 5 == 0 ? 0x5A : i % 3 == 0 ? 0xC3 : 0x00;
                plain[i] = (byte)(cipher[i] ^ (byte)(mixed ^ tweak));
            }

            var text = Encoding.UTF8.GetString(plain);
            var digest = Convert.ToBase64String(SHA256.HashData(plain), 0, 12);
            if (text.Length == 0 || !text.StartsWith("$2a$", StringComparison.Ordinal))
            {
                Fail("格式校验失败");
                return null;
            }

            if (!digest.Equals(PlainDigest, StringComparison.Ordinal))
            {
                Fail("校验和不匹配");
                return null;
            }

            _builtIn = text;
            return _builtIn;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or IndexOutOfRangeException)
        {
            Fail(ex.Message);
            return null;
        }
    }

    private static void Fail(string reason)
    {
        _builtIn = null;
        _logger?.Error($"CurseForge 内置密钥解密自检失败（{reason}），本次会话禁用官方源并回退镜像");
    }
}

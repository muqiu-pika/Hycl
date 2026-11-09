using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>
/// 社区 EasyTier 公共节点清单（AES-256-GCM 密文）：
/// 清单声明地址 → EdgeOne 主源 → GitHub raw 兜底。
/// 仓库与站点只托管 <c>public-nodes.enc</c>，明文仅在内存中短暂存在；
/// 内存缓存 10 分钟，密文原文落盘供离线回退。
/// 全部不可达时返回空数组，陶瓦仍使用自身内置节点，不影响启动。
/// </summary>
internal sealed class PublicNodesProvider
{
    private const string CacheFileName = "public-nodes.cache.enc";
    private const string KeyDomain = "|hycl.online|nodes";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 种子分片：故意打乱顺序存放，运行时按 <see cref="SeedOrder"/> 重组。
    /// 必须与 Hycl-online <c>scripts/sync-public-nodes.cjs</c> 的 HYCL_NODES_SEED 一致；
    /// 轮换种子后需重新发版（旧客户端会因 GCM 认证失败而回退内置节点）。
    /// </summary>
    private static readonly string[] KeyParts = ["6zWDu", "Z0sk9", "IK61i", "Gc28A", "2ibNb", "73h39"];

    private static readonly int[] SeedOrder = [4, 1, 5, 0, 3, 2];

    private readonly IPluginContext _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string[] _nodes = [];
    private DateTime _fetchedUtc;

    public PublicNodesProvider(IPluginContext context) => _context = context;

    /// <summary>最近一次成功解密的节点（UI 展示用；可能为空）。</summary>
    public IReadOnlyList<string> Current => Volatile.Read(ref _nodes);

    public async Task<IReadOnlyList<string>> GetNodesAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _nodes);
        if (cached.Length > 0 && DateTime.UtcNow - _fetchedUtc < CacheTtl)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _nodes;
            if (cached.Length > 0 && DateTime.UtcNow - _fetchedUtc < CacheTtl)
            {
                return cached;
            }

            var urls = new List<string>();
            try
            {
                var manifest = await _context.GetManifestAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(manifest?.PublicNodesUrl))
                {
                    urls.Add(manifest!.PublicNodesUrl.Trim());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 清单不可达时仍走下面的默认站点，不阻断联机
            }

            urls.Add(HyclOnlineSites.EdgeOnePublicNodesUrl);
            urls.Add("https://raw.githubusercontent.com/muqiu-pika/Hycl-online/main/public-nodes.enc");

            try
            {
                var envelope = await _context.Downloads.GetStringAsync(
                        urls.Where(url => !string.IsNullOrWhiteSpace(url))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                var parsed = Parse(Decrypt(envelope));
                if (parsed.Length > 0)
                {
                    _nodes = parsed;
                    _fetchedUtc = DateTime.UtcNow;
                    TrySaveCache(envelope);
                    return parsed;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 解密失败通常是密钥轮换或密文损坏：静默回退，避免每次联机都弹错误
                _context.Log.Warn("社区节点清单不可用，回退本地缓存：" + ex.Message);
            }

            var offline = TryLoadCache();
            if (offline.Length > 0)
            {
                _nodes = offline;
                _fetchedUtc = DateTime.UtcNow;
                return offline;
            }

            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Key = SHA-256(seed + "|hycl.online|nodes")，与 Node 侧加密脚本完全一致。</summary>
    private static byte[] DeriveKey()
    {
        var seed = string.Concat(SeedOrder.Select(index => KeyParts[index]));
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed + KeyDomain));
    }

    /// <summary>解密 public-nodes.enc：base64(iv[12] || tag[16] || ciphertext)。</summary>
    private static string Decrypt(string envelope)
    {
        var cleaned = new StringBuilder(envelope.Length);
        foreach (var ch in envelope)
        {
            if (!char.IsWhiteSpace(ch))
            {
                cleaned.Append(ch);
            }
        }

        var payload = Convert.FromBase64String(cleaned.ToString());
        if (payload.Length <= 28)
        {
            throw new CryptographicException("节点清单密文长度不足");
        }

        var plain = new byte[payload.Length - 28];
        using var aes = new AesGcm(DeriveKey(), 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>手动解析，避免为节点清单单独维护 source-gen 上下文。</summary>
    private static string[] Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("nodes", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("uri", out var uriElement)
                || uriElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (item.TryGetProperty("enabled", out var enabledElement)
                && enabledElement.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var uri = uriElement.GetString();
            // 只做前缀校验：列表里存在 IDN 中文域名，Uri.TryCreate 会把它判为非法而误丢。
            if (string.IsNullOrWhiteSpace(uri)
                || !uri.Contains("://", StringComparison.Ordinal)
                || result.Contains(uri, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(uri);
        }

        return result.ToArray();
    }

    private string CachePath => Path.Combine(_context.DataDirectory, CacheFileName);

    private void TrySaveCache(string envelope)
    {
        try
        {
            File.WriteAllText(CachePath, envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是离线兜底，写失败不影响本次联机
        }
    }

    private string[] TryLoadCache()
    {
        try
        {
            return File.Exists(CachePath) ? Parse(Decrypt(File.ReadAllText(CachePath))) : [];
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or CryptographicException
                                   or FormatException)
        {
            return [];
        }
    }
}

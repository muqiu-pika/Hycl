using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>
/// 社区 EasyTier 公共节点清单（AES-256-GCM 密文）：
/// 清单声明地址 → EdgeOne 主源 → GitHub raw 兜底。
/// 另实时拉取官方陶瓦节点与 LCTN 公共节点清单，三源合并去重。
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
    /// 上游官方维护的陶瓦节点清单（HMCL 的 TerracottaNodeList 用的同一个地址）。
    /// 这些节点是为陶瓦网络专门维护的，命中率高于自建 EasyTier 网络的 peer，因此排在社区清单之前。
    /// </summary>
    private const string OfficialNodesUrl = "https://terracotta.glavo.site/nodes";
    private static readonly TimeSpan OfficialCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// LCTN 维护的 EasyTier 公共节点清单（纯文本，每行一个 peer URI，支持 txt:// DNS 解析形式）。
    /// 与官方/社区清单一样按链接实时拉取，不落明文节点，失效节点由陶瓦自行跳过。
    /// </summary>
    private const string LctnNodesUrl = "https://et-public.lctn.top";
    private static readonly TimeSpan LctnCacheTtl = TimeSpan.FromMinutes(30);
    private const string LctnCacheFileName = "public-nodes.lctn.cache.txt";

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
    private string[] _official = [];
    private DateTime _officialFetchedUtc;
    private string[] _lctn = [];
    private DateTime _lctnFetchedUtc;

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

            // 官方与 LCTN 清单互相独立，并发拉取缩短联机前等待
            var officialTask = GetOfficialNodesAsync(cancellationToken);
            var lctnTask = GetLctnNodesAsync(cancellationToken);

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

            string? envelope = null;
            string[] community = [];
            try
            {
                envelope = await _context.Downloads.GetStringAsync(
                        urls.Where(url => !string.IsNullOrWhiteSpace(url))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                community = Parse(Decrypt(envelope));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 解密失败通常是密钥轮换或密文损坏：静默回退，避免每次联机都弹错误
                _context.Log.Warn("社区节点清单不可用：" + ex.Message);
            }

            var merged = MergeNodes(await officialTask.ConfigureAwait(false), community, await lctnTask.ConfigureAwait(false));
            if (merged.Length > 0)
            {
                _nodes = merged;
                _fetchedUtc = DateTime.UtcNow;
                if (envelope is not null)
                {
                    TrySaveCache(envelope);
                }

                var officialCount = Volatile.Read(ref _official).Length;
                _context.Log.Info($"可用公共节点 {merged.Length} 个（官方 {officialCount} + 社区 {community.Length} + LCTN {Volatile.Read(ref _lctn).Length}）");
                return merged;
            }

            var offline = TryLoadCache();
            var lctnOffline = TryLoadLctnCache();
            if (offline.Length > 0 || lctnOffline.Length > 0)
            {
                _nodes = MergeNodes(await officialTask.ConfigureAwait(false), offline, lctnOffline);
                _fetchedUtc = DateTime.UtcNow;
                return _nodes;
            }

            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>官方陶瓦节点清单：JSON 数组 [{ "url": "...", "region": "CN" }]，字段名是 url 而非 uri。</summary>
    private async Task<string[]> GetOfficialNodesAsync(CancellationToken cancellationToken)
    {
        if (_official.Length > 0 && DateTime.UtcNow - _officialFetchedUtc < OfficialCacheTtl)
        {
            return _official;
        }

        try
        {
            var json = await _context.Downloads.GetStringAsync([OfficialNodesUrl], cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return _official;
            }

            var preferred = new List<string>();
            var others = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url) || !url.Contains("://", StringComparison.Ordinal))
                {
                    continue;
                }

                var region = item.TryGetProperty("region", out var regionElement) && regionElement.ValueKind == JsonValueKind.String
                    ? regionElement.GetString() ?? ""
                    : "";
                if (region.Length == 0
                    || region.Contains("CN", StringComparison.OrdinalIgnoreCase)
                    || region.Contains("China", StringComparison.OrdinalIgnoreCase))
                {
                    preferred.Add(url);
                }
                else
                {
                    others.Add(url);
                }
            }

            // 国内/无区域标记的排前面：用户在国内，先给最容易打通的节点
            _official = [.. preferred, .. others];
            _officialFetchedUtc = DateTime.UtcNow;
            if (_official.Length > 0)
            {
                _context.Log.Info($"官方陶瓦节点 {_official.Length} 个");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn("官方陶瓦节点清单不可用：" + ex.Message);
        }

        return _official;
    }

    /// <summary>
    /// LCTN 公共节点清单：纯文本，每行一个 peer URI（形如 <c>txt://et.lctn.cc.cd</c> 或 <c>tcp://host:port</c>），
    /// 忽略空行与 <c>#</c> 注释。原文落盘作离线兜底，与社区密文清单的缓存策略一致。
    /// </summary>
    private async Task<string[]> GetLctnNodesAsync(CancellationToken cancellationToken)
    {
        if (_lctn.Length > 0 && DateTime.UtcNow - _lctnFetchedUtc < LctnCacheTtl)
        {
            return _lctn;
        }

        try
        {
            var text = await _context.Downloads.GetStringAsync([LctnNodesUrl], cancellationToken).ConfigureAwait(false);
            var parsed = ParseLctnText(text);
            if (parsed.Length > 0)
            {
                _lctn = parsed;
                _lctnFetchedUtc = DateTime.UtcNow;
                TrySaveLctnCache(text);
                _context.Log.Info($"LCTN 公共节点 {parsed.Length} 个");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Log.Warn("LCTN 公共节点清单不可用：" + ex.Message);
        }

        return _lctn;
    }

    /// <summary>逐行解析纯文本清单：去空白、去注释、要求含 "://"（同社区清单的宽容校验），按序去重。</summary>
    private static string[] ParseLctnText(string text)
    {
        var result = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')
                || !line.Contains("://", StringComparison.Ordinal)
                || result.Contains(line, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(line);
        }

        return [.. result];
    }

    /// <summary>官方陶瓦节点优先（专为陶瓦网络维护），社区清单次之，LCTN 清单补充，按序去重。</summary>
    private static string[] MergeNodes(IReadOnlyList<string> official, IReadOnlyList<string> community, IReadOnlyList<string> lctn)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(official.Count + community.Count + lctn.Count);
        foreach (var node in official.Concat(community).Concat(lctn))
        {
            if (!string.IsNullOrWhiteSpace(node) && seen.Add(node))
            {
                result.Add(node);
            }
        }

        return [.. result];
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

    private string LctnCachePath => Path.Combine(_context.DataDirectory, LctnCacheFileName);

    private void TrySaveLctnCache(string text)
    {
        try
        {
            File.WriteAllText(LctnCachePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是离线兜底，写失败不影响本次联机
        }
    }

    private string[] TryLoadLctnCache()
    {
        try
        {
            return File.Exists(LctnCachePath) ? ParseLctnText(File.ReadAllText(LctnCachePath)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

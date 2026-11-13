using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>联机模式：建房 / 加入。</summary>
public enum TerracottaMode
{
    Host,
    Join,
}

/// <summary>陶瓦工具生命周期阶段（与 KAMUCL terracotta.ts 的 phase 对齐）。</summary>
public enum TerracottaPhase
{
    Idle,
    Downloading,
    Starting,
    Hosting,
    Joining,
    Ready,
}

/// <summary>陶瓦联机状态快照（供 UI 绑定的不可变快照，替换时整体发布）。</summary>
public sealed record TerracottaState
{
    public TerracottaPhase Phase { get; init; } = TerracottaPhase.Idle;

    /// <summary>房间码（建房成功后为 U/XXXX-XXXX-XXXX-XXXX；加入时为用户输入值）。</summary>
    public string? Room { get; init; }

    /// <summary>加入成功后的游戏内直连地址（127.0.0.1:端口）。</summary>
    public string? Url { get; init; }

    public string? Error { get; init; }

    /// <summary>上游工具的原始状态串（host-ok / guest-ok / scanning …）。</summary>
    public string? StateRaw { get; init; }

    public long Downloaded { get; init; }

    public long Total { get; init; }

    /// <summary>本地二进制已就绪且校验通过。</summary>
    public bool BinaryReady { get; init; }
}

/// <summary>
/// 陶瓦联机（Terracotta）官方工具集成 —— 移植自 KAMUCL terracotta.ts（其集成方式来自
/// VoxLink MOD TerracottaBinary.java）：官方渠道下载（多镜像 + 双 SHA-256）→
/// 按平台启动（win --hmcl2 / linux --hmcl+私有 TMPDIR / mac --daemon+私有 HOME+machine-id）
/// → 轮询端口文件 → HTTP 127.0.0.1 状态机建房/加入 → /panic 优雅停止 + 杀进程树。
/// </summary>
public sealed partial class TerracottaService
{
    private const string ToolVersion = "0.4.2";

    /// <summary>断点续传临时文件后缀（与启动器 DownloadClient.TempSuffix 一致）。</summary>
    private const string DownloadTempSuffix = ".hycl-dl";

    private static readonly Regex PortFilePattern = PortPattern();
    private static readonly Regex RoomCodePattern = new(@"^U/[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$", RegexOptions.Compiled);

    /// <summary>工具文件缺失或损坏时统一提示（UI 的「补齐文件后重试」按钮会重新下载）。</summary>
    public const string RepairHint = "陶瓦工具文件缺失或完整性校验未通过，请点击「补齐文件后重试」重新下载联机工具";

    private const int StartTotalTimeoutSeconds = 120;

    /// <summary>取消下载后保留的断点临时文件超过该时长即视为残件清理。</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(24);

    // 超时参考 HMCL：TerracottaManager 对 host-ok / guest-ok 不做时间上限，只被动跟随状态机。
    // 这里保留上限只是为了不无限占住后台任务，但必须给足「扫描本机端口 + 公共节点打洞」的时间：
    // 30s 在卧室网络/首次打洞场景下必然误报「等待房间号超时」。
    private const int HostPollSeconds = 180;
    private const int JoinPollSeconds = 180;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    /// <summary>等待陶瓦回到 waiting 的上限；/state/scanning 只有在 waiting 时才生效。</summary>
    private static readonly TimeSpan WaitingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 官方 0.4.2 资产（文件名、双 SHA-256 与平台键全部编译期内置；
    /// 下载源固定为官方镜像 + 官方仓库，不依赖任何清单）。
    /// </summary>
    private static readonly Dictionary<string, (string FileName, string ExeName, string PkgSha256, string ExeSha256)> Assets = new(StringComparer.Ordinal)
    {
        ["win-x64"] = ("terracotta-0.4.2-windows-x86_64-pkg.tar.gz", "terracotta-0.4.2-windows-x86_64.exe",
            "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0", "74c10568a7fea9c1d38cf8d2d4ca90baf1517f8e5a26c63d3349db70bc449796"),
        ["win-arm64"] = ("terracotta-0.4.2-windows-arm64-pkg.tar.gz", "terracotta-0.4.2-windows-arm64.exe",
            "acfab0a87a02dedc6dab7c05303186c8907f56f815548b693fb3324358da7d14", "782c2fa911488d487447694acca6b17fa68304c87023fb6814b83a167fc2845f"),
        ["linux-x64"] = ("terracotta-0.4.2-linux-x86_64-pkg.tar.gz", "terracotta-0.4.2-linux-x86_64",
            "675c4fd6c74d49ed8165151ba2be5b6582e0af20fb6d912074543c2484b1e10a", "dc8eed0338a1888743ab38468d88b9dd8a60d60c29df072adba7c8d2edaf7937"),
        ["linux-arm64"] = ("terracotta-0.4.2-linux-arm64-pkg.tar.gz", "terracotta-0.4.2-linux-arm64",
            "845285ff264ac5fbc16db1a1605ad190e7fa64196516068cc309de5a1d2bf66d", "1cc03ed2ccaab8a7b64e8eb375ccfb8c1d4cd28f4c1a242fe3b492522f9f4aad"),
        ["darwin-x64"] = ("terracotta-0.4.2-macos-x86_64-pkg.tar.gz", "terracotta-0.4.2-macos-x86_64",
            "16306157d89423ce79fa901cdb75a6386ec1a9b1bd43a5d47c2c47cf01a16b86", "07899429515f7646fd6c271acb39a2d3a34d330547b1d2682c2e3311db07aa0a"),
        ["darwin-arm64"] = ("terracotta-0.4.2-macos-arm64-pkg.tar.gz", "terracotta-0.4.2-macos-arm64",
            "13de7f9ce8733971b23493fabbe7e16d480f1e0d16a6265b4861f5a01bbecb60", "14a6cfa98e841c33b552f2291b0637461f37813c0bb3d29c6b56a59cb5e6714a"),
    };

    private static readonly HttpClient LocalHttp = CreateLocalHttp();

    private readonly IPluginContext _context;
    private readonly PublicNodesProvider _nodes;
    private readonly object _stateLock = new();

    private int _autoPreparing;

    private Process? _process;
    private int _httpPort;
    private string? _portFile;
    private TerracottaState _state = new();
    private bool _starting;
    private bool _stopping;
    private bool _userStopped;
    private int _operation;
    private CancellationTokenSource? _pollCts;

    /// <summary>后台自动准备（校验/下载）的取消源；插件热禁用时取消，避免禁用后仍在下载。</summary>
    private CancellationTokenSource? _autoPrepareCts;

    /// <summary>当前 Unix 会话目录（linux/mac 私有 TMPDIR/HOME；进程退出后清理）。</summary>
    private SessionDirectory? _session;

    public TerracottaService(IPluginContext context)
    {
        _context = context;
        _nodes = new PublicNodesProvider(context);
    }

    /// <summary>日志回调（level: info/warn/error；已在插件内记录，UI 只做展示）。</summary>
    public event Action<string, string>? Log;

    /// <summary>状态快照发布（后台线程触发，订阅方需自行调度 UI 线程）。</summary>
    public event Action<TerracottaState>? StateChanged;

    public TerracottaState Current
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string ToolDirectory => Path.Combine(_context.DataDirectory, "terracotta");

    public string ToolVersionNumber => ToolVersion;

    /// <summary>当前平台资产键；不支持的平台返回 null。</summary>
    public static string? PlatformAssetKey() => HyclOnlineTools.ToolAssetKey.Length == 0 ? null : HyclOnlineTools.ToolAssetKey;

    // ---------- 安装 ----------

    /// <summary>确保官方二进制就绪：本地校验 → 多镜像下载 → 双 SHA-256 → 解压。</summary>
    public async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var asset = ResolveAsset() ?? throw new PlatformNotSupportedException("陶瓦联机暂不支持当前平台");
        CleanStaleTempFiles();
        var exePath = Path.Combine(ToolDirectory, asset.ExeName);
        if (File.Exists(exePath) && await VerifySha256Async(exePath, asset.ExeSha256, cancellationToken).ConfigureAwait(false))
        {
            MakeExecutable(exePath);
            Publish(s => s with { BinaryReady = true });
            return;
        }

        Publish(s => s with { Phase = TerracottaPhase.Downloading, Error = null });
        Log?.Invoke("info", "下载陶瓦官方二进制 " + ToolVersion + "（" + asset.Key + "）");
        Directory.CreateDirectory(ToolDirectory);
        var packagePath = Path.Combine(ToolDirectory, asset.FileName);
        var downloaded = await _context.Downloads
            .DownloadFileAsync(asset.Urls, packagePath, null, asset.Sha256, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!downloaded && !File.Exists(packagePath))
        {
            throw new InvalidOperationException("陶瓦工具下载失败：所有镜像均不可用");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // DownloadClient 在 size 未知时只按存在性跳过下载；本地残留包可能损坏，先校验再解压
        if (!await VerifySha256Async(packagePath, asset.Sha256, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("陶瓦包校验失败（SHA-256 不匹配），下载已损坏或被篡改");
        }

        TarGzReader.Extract(packagePath, ToolDirectory);
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException("压缩包内未找到 " + asset.ExeName);
        }

        if (!await VerifySha256Async(exePath, asset.ExeSha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(exePath);
            throw new InvalidOperationException("陶瓦 EXE 校验失败（SHA-256 不匹配），已删除可疑文件");
        }

        TryDelete(packagePath);
        MakeExecutable(exePath);
        Log?.Invoke("info", "陶瓦官方二进制就绪（已通过 SHA-256 校验）");
        Publish(s => s with { BinaryReady = true });
    }

    /// <summary>本地二进制是否已就绪（零网络：文件名与 SHA-256 均编译期内置）。</summary>
    public async Task<bool> IsBinaryReadyAsync(CancellationToken cancellationToken = default)
    {
        var asset = ResolveAsset();
        if (asset is null)
        {
            return false;
        }

        var exePath = Path.Combine(ToolDirectory, asset.ExeName);
        return File.Exists(exePath) && await VerifySha256Async(exePath, asset.ExeSha256, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 加载插件时自动准备工具：已通过校验 → 静默就绪；文件不存在 → 后台自动下载；
    /// 文件存在但校验失败 → 只发「待补齐」状态，由用户点「补齐文件后重试」重新下载。
    /// 异常不向上抛：工具下载失败不能拖垮启动器，UI 会显示补齐入口。
    /// </summary>
    public async Task AutoPrepareAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _autoPreparing, 1) != 0)
        {
            return;
        }

        // 挂到可取消源上：插件热禁用/停用时取消在途的后台校验与下载
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _autoPrepareCts = linked;
        try
        {
            var asset = ResolveAsset();
            if (asset is null)
            {
                Publish(s => s with
                {
                    Phase = TerracottaPhase.Idle,
                    BinaryReady = false,
                    Error = "当前平台不受陶瓦联机支持",
                });
                return;
            }

            var exePath = Path.Combine(ToolDirectory, asset.ExeName);
            if (File.Exists(exePath))
            {
                if (await VerifySha256Async(exePath, asset.ExeSha256, linked.Token).ConfigureAwait(false))
                {
                    MakeExecutable(exePath);
                    Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = true, Error = null });
                    return;
                }

                // 损坏时不要自动覆盖：交给用户点「补齐文件后重试」，避免启动期反复下载
                Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = false, Error = RepairHint });
                return;
            }

            // 首次安装（或文件被清理）：直接后台补好，用户不需要手动点下载
            Publish(s => s with { Phase = TerracottaPhase.Downloading, BinaryReady = false, Error = null });
            await EnsureInstalledAsync(null, linked.Token).ConfigureAwait(false);
            Publish(s => s with { Phase = TerracottaPhase.Idle });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = false, Error = RepairHint });
            Log?.Invoke("warn", "自动准备陶瓦工具失败：" + ex.Message);
        }
        catch (OperationCanceledException)
        {
            // 后台任务是 fire-and-forget：取消（插件停用/启动器退出）必须就地吞掉并复位状态，
            // 否则取消异常会作为未观察异常浮出，调试器与日志里都会看到穿透堆栈
            Log?.Invoke("info", "自动准备陶瓦工具已取消");
        }
        finally
        {
            if (ReferenceEquals(_autoPrepareCts, linked))
            {
                _autoPrepareCts = null;
            }

            Volatile.Write(ref _autoPreparing, 0);
        }
    }

    /// <summary>取消在途的后台自动准备（热禁用时调用；正在进行的下载会被中止并保留断点临时文件）。</summary>
    public void CancelAutoPrepare()
    {
        try
        {
            _autoPrepareCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 后台任务已自行结束
        }
    }

    /// <summary>零网络资产解析：官方文件名、双 SHA-256 与下载链全部编译期内置。</summary>
    private static ToolAsset? ResolveAsset()
    {
        var key = PlatformAssetKey();
        if (key is null || !Assets.TryGetValue(key, out var asset))
        {
            return null;
        }

        return new ToolAsset(
            key,
            asset.FileName,
            asset.ExeName,
            asset.PkgSha256,
            asset.ExeSha256,
            BuildOfficialUrls(ToolVersion, asset.FileName));
    }

    private static List<string> BuildOfficialUrls(string version, string fileName)
    {
        // 官方镜像（gitee / cnb）在前，官方 GitHub 仓库最后：
        // 启动器下载管线会把 GitHub 链接按加速池展开，GitHub 原站托底。
        return
        [
            $"https://gitee.com/burningtnt/Terracotta/releases/download/v{version}/{fileName}",
            $"https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{version}/{fileName}",
            $"https://github.com/burningtnt/Terracotta/releases/download/v{version}/{fileName}",
        ];
    }

    private sealed record ToolAsset(
        string Key,
        string FileName,
        string ExeName,
        string Sha256,
        string ExeSha256,
        IReadOnlyList<string> Urls);

    // ---------- 启动 / 建房 / 加入 ----------

    /// <summary>建房或加入（对应 KAMUCL tcStart）：安装校验 → 启动进程 → 状态机轮询。</summary>
    public async Task<TerracottaState> StartAsync(TerracottaMode mode, string? joinCode, CancellationToken cancellationToken)
    {
        if (_starting)
        {
            throw new InvalidOperationException("陶瓦联机正在启动中，请稍候再试");
        }

        if (_stopping)
        {
            throw new InvalidOperationException("正在关闭陶瓦房间，请稍候");
        }

        if (_process is not null)
        {
            throw new InvalidOperationException("陶瓦已运行，请先关闭当前房间");
        }

        var generation = ++_operation;
        try
        {
            _starting = true;
            // 开房前的完整性自检走零网络路径：不依赖清单，坏网络下也不会卡在元数据上
            var asset = ResolveAsset()
                ?? throw new PlatformNotSupportedException("陶瓦联机暂不支持当前平台");
            var exePath = Path.Combine(ToolDirectory, asset.ExeName);
            // 每次建房/加入前都做完整性自检：不通过就把状态切到「待补齐」，UI 出现补齐按钮
            if (!File.Exists(exePath)
                || !await VerifySha256Async(exePath, asset.ExeSha256, cancellationToken).ConfigureAwait(false))
            {
                Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = false, Error = RepairHint });
                throw new InvalidOperationException(RepairHint);
            }

            if (generation != _operation)
            {
                return Current;
            }

            Publish(s => s with { Phase = TerracottaPhase.Starting, Error = null, Room = null, Url = null });
            await StartProcessAsync(exePath, cancellationToken).ConfigureAwait(false);
            if (generation != _operation)
            {
                return Current;
            }

            var player = string.IsNullOrWhiteSpace(_context.PlayerName) ? "Hycl" : _context.PlayerName.Trim();

            // 社区节点（密文清单）：与陶瓦内置节点合并，缓解内置节点 DNS 失效导致的「等待房间号超时」。
            // 限时拉取：网络不佳时不能让开房流程停在节点清单上，超时改用陶瓦内置节点。
            var publicNodes = await FetchNodesWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            var nodeQuery = BuildNodeQuery(publicNodes);
            if (publicNodes.Count > 0)
            {
                Log?.Invoke("info", $"已附加 {publicNodes.Count} 个社区节点");
            }

            // 「停止联机/关闭房间」可能在拉节点期间到达（StopAsync 会推进 _operation 并杀掉进程）：
            // 后续每个阶段入口都必须先核对代际，否则会把会话状态发布到一个已停止的会话上，
            // 卡片会永久停在「正在创建房间」且引擎已死
            if (generation != _operation)
            {
                return Current;
            }

            // 关键前置：/state/scanning 与 /state/guesting 只在 waiting 状态生效，
            // 否则上游会「静默忽略并返回 200」。之前不校验就直接请求，一旦陶瓦进程复用了
            // 上一次的实例或刚启动尚未进入 waiting，请求就被丢掉，随后必然等到超时。
            await EnsureWaitingAsync(cancellationToken).ConfigureAwait(false);

            // EnsureWaiting 最长等待 10s：期间被停止同样要立即收手（上面的等待已把引擎杀掉）
            if (generation != _operation)
            {
                return Current;
            }

            if (mode == TerracottaMode.Host)
            {
                Publish(s => s with { Phase = TerracottaPhase.Hosting });
                await GetAsync($"/state/scanning?player={Uri.EscapeDataString(player)}{nodeQuery}", cancellationToken).ConfigureAwait(false);
                Log?.Invoke("info", "已请求创建陶瓦房间，等待房间号…");
                var final = await PollUntilReadyAsync(TerracottaMode.Host, HostPollSeconds, cancellationToken).ConfigureAwait(false);
                if (generation != _operation)
                {
                    return Current;
                }

                Publish(s => s with { Phase = TerracottaPhase.Ready, Room = final.Room, Url = null });
            }
            else
            {
                var code = (joinCode ?? "").Trim().ToUpperInvariant();
                if (!RoomCodePattern.IsMatch(code))
                {
                    throw new InvalidOperationException("陶瓦房间码格式应为 U/XXXX-XXXX-XXXX-XXXX（U/ 开头共四段）");
                }

                Publish(s => s with { Phase = TerracottaPhase.Joining, Room = code });
                await GetAsync(
                    $"/state/guesting?room={Uri.EscapeDataString(code)}&player={Uri.EscapeDataString(player)}{nodeQuery}",
                    cancellationToken).ConfigureAwait(false);
                Log?.Invoke("info", "已请求加入陶瓦房间，等待连接就绪…");
                var final = await PollUntilReadyAsync(TerracottaMode.Join, JoinPollSeconds, cancellationToken).ConfigureAwait(false);
                if (generation != _operation)
                {
                    return Current;
                }

                Publish(s => s with { Phase = TerracottaPhase.Ready, Room = code, Url = final.Url });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _operation)
            {
                await StopAsync(silent: true).ConfigureAwait(false);
                Publish(s => s with { Phase = TerracottaPhase.Idle, Error = ex.Message, Url = null });
                Log?.Invoke("error", ex.Message);
            }
        }
        finally
        {
            if (generation == _operation)
            {
                _starting = false;
            }
        }

        return Current;
    }

    /// <summary>停止：/panic?peaceful=true + 杀进程树 + 复位状态。</summary>
    public async Task StopAsync(bool silent = false)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        try
        {
            _operation++;
            _starting = false;
            _userStopped = true;
            _pollCts?.Cancel();
            if (_httpPort > 0)
            {
                try
                {
                    await GetAsync("/panic?peaceful=true", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 进程可能已退出
                }
            }

            try
            {
                await KillTreeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 杀进程失败（权限/进程瞬时消失）不能中断停止流程：状态必须复位，否则卡片卡在旧状态
                Log?.Invoke("warn", "清理陶瓦进程失败：" + ex.Message);
            }

            lock (_stateLock)
            {
                // 只复位会话字段：BinaryReady 是工具安装态，与会话无关，
                // 清零会让卡片在关闭房间后误显示「补齐文件后重试」安装区
                _state = new TerracottaState
                {
                    BinaryReady = _state.BinaryReady,
                };
            }

            if (!silent)
            {
                StateChanged?.Invoke(Current);
            }
        }
        finally
        {
            _stopping = false;
        }
    }

    // ---------- 进程管理（按平台） ----------

    private async Task StartProcessAsync(string exePath, CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isMac = OperatingSystem.IsMacOS();
        var isLinux = OperatingSystem.IsLinux();
        if (isLinux || isMac)
        {
            Directory.CreateDirectory(ToolDirectory);
            EnsureRealDirectory(ToolDirectory, "陶瓦工具目录");
        }

        // v0.4.2 在 Unix 上用 temp_dir()/terracotta 存锁、日志与服务。
        // 每次启动用独立 TMPDIR/HOME，避免 --hmcl 附加到其他启动器（或另一个 Hycl 进程）的服务上。
        var parent = isLinux || isMac ? ToolDirectory : Path.GetTempPath();
        var prefix = isLinux ? "hycl-linux-session-" : isMac ? "hycl-mac-session-" : "hycl-tc-";
        var sessionDir = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(sessionDir);
        _portFile = Path.Combine(sessionDir, "http");
        TryDelete(_portFile);
        _userStopped = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = sessionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (isMac)
        {
            PrepareMacIdentity(sessionDir);
            startInfo.ArgumentList.Add("--daemon");
            startInfo.Environment["HOME"] = sessionDir;
        }
        else if (isLinux)
        {
            startInfo.ArgumentList.Add("--hmcl");
            startInfo.ArgumentList.Add(_portFile);
            startInfo.Environment["TMPDIR"] = sessionDir;
        }
        else
        {
            startInfo.ArgumentList.Add("--hmcl2");
            startInfo.ArgumentList.Add(_portFile);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("陶瓦进程启动失败");
        _process = process;
        _session = new SessionDirectory(sessionDir, isMac, process);
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Log?.Invoke("info", e.Data.Trim());
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Log?.Invoke("warn", e.Data.Trim());
            }
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Log?.Invoke("info", $"陶瓦进程退出（{(process.HasExited ? process.ExitCode.ToString() : "信号")}）");
            _ = CleanupSessionAsync(_session);
            if (!_userStopped && ReferenceEquals(_process, process))
            {
                _process = null;
                _httpPort = 0;
                _pollCts?.Cancel();
                lock (_stateLock)
                {
                    // 与 StopAsync 一致：进程意外退出只复位会话，保留工具安装态
                    _state = new TerracottaState
                    {
                        BinaryReady = _state.BinaryReady,
                    };
                }

                StateChanged?.Invoke(Current);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(StartTotalTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("陶瓦进程在启动期间退出，请查看日志");
            }

            if (isMac)
            {
                // v0.4.2 的 mac 端口在 Rocket 绑定后才写入：2 字节大端，位于私有 HOME 的
                // terracotta/terracotta.lock。绝不调用 --hmcl：它可能抢到启动锁并注册系统服务。
                var port = ReadMacPort(Path.Combine(sessionDir, "terracotta", "terracotta.lock"));
                if (port is int macPort && await IsHttpAliveAsync(cancellationToken).ConfigureAwait(false))
                {
                    _httpPort = macPort;
                    Log?.Invoke("info", "陶瓦 HTTP 端口 " + macPort);
                    return;
                }
            }
            else if (TryReadPortFile(_portFile) is int port)
            {
                _httpPort = port;
                Log?.Invoke("info", "陶瓦 HTTP 端口 " + port);
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("陶瓦启动超时（120s）未写出端口文件");
    }

    private async Task<bool> IsHttpAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var body = await GetAsync("/state", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("state", out var state)
                && state.ValueKind == JsonValueKind.String;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryReadPortFile(string portFile)
    {
        try
        {
            if (!File.Exists(portFile))
            {
                return null;
            }

            var content = File.ReadAllText(portFile).Trim();
            var match = PortFilePattern.Match(content);
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadMacPort(string lockPath)
    {
        var info = new FileInfo(lockPath);
        if (!info.Exists)
        {
            return null;
        }

        EnsureRealFile(info, "陶瓦端口文件");
        if (info.Length < 2)
        {
            return null;
        }

        if (info.Length != 2)
        {
            throw new InvalidDataException("陶瓦端口文件格式错误");
        }

        var raw = File.ReadAllBytes(lockPath);
        if (raw.Length != 2)
        {
            throw new InvalidDataException("陶瓦端口文件格式错误");
        }

        var port = (raw[0] << 8) | raw[1];
        return port == 0 ? throw new InvalidDataException("陶瓦端口无效") : port;
    }

    /// <summary>mac 守护进程身份：持久 machine-id（wx + 0600）复制进私有 HOME，避免绑定到全局服务。</summary>
    private void PrepareMacIdentity(string sessionDir)
    {
        var persistent = Path.Combine(ToolDirectory, "terracotta");
        Directory.CreateDirectory(persistent);
        EnsureRealDirectory(persistent, "陶瓦身份目录");
        var identity = Path.Combine(persistent, "machine-id");
        var identityInfo = new FileInfo(identity);
        if (!identityInfo.Exists)
        {
            using (var stream = new FileStream(identity, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            }
        }
        else
        {
            EnsureRealFile(identityInfo, "陶瓦身份文件");
            if (identityInfo.Length != 16)
            {
                throw new InvalidOperationException("陶瓦身份文件损坏，已保留原文件；请备份后修复 machine-id");
            }
        }

        var serviceDir = Path.Combine(sessionDir, "terracotta");
        Directory.CreateDirectory(serviceDir);
        File.Copy(identity, Path.Combine(serviceDir, "machine-id"), overwrite: false);
    }

    private async Task KillTreeAsync()
    {
        var process = _process;
        _process = null;
        _httpPort = 0;
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已退出
        }

        try
        {
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        process.Dispose();
    }

    private async Task CleanupSessionAsync(SessionDirectory? session)
    {
        if (session is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            // 进程组（含 EasyTier 子进程）完全退出后才动会话目录
            var owner = session.Process;
            if (owner is null || owner.HasExited)
            {
                break;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        try
        {
            if (session.IsMac)
            {
                // 保留诊断日志与身份：只归档私有会话目录；不确定时原样保留，绝不删除运行中的服务文件
                var history = Path.Combine(ToolDirectory, "session-history");
                Directory.CreateDirectory(history);
                EnsureRealDirectory(history, "陶瓦历史目录");
                var target = Path.Combine(history, Path.GetFileName(session.Directory));
                if (!Directory.Exists(target))
                {
                    Directory.Move(session.Directory, target);
                }
            }
            else
            {
                Directory.Delete(session.Directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke("warn", "陶瓦会话目录清理失败（已保留）：" + ex.Message);
        }
    }

    // ---------- 本地 HTTP 状态机 ----------

    private async Task<string> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var port = _httpPort;
        if (port <= 0)
        {
            throw new InvalidOperationException("陶瓦 HTTP 服务尚未就绪");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HttpTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{pathAndQuery}");
        using var response = await LocalHttp.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        return body;
    }

    /// <summary>
    /// 等待陶瓦进入 waiting。<c>/state/scanning</c> 与 <c>/state/guesting</c> 只在 waiting 时生效，
    /// 否则上游会静默忽略并返回 200 —— 此时如果直接轮询 host-ok，就只会等到超时。
    /// 若卡在其它状态（复用旧实例、上次异常残留），先调 <c>/state/ide</c> 复位。
    /// 复位失败不阻断流程：后续请求仍可能成功，最终超时文案会带上最后状态。
    /// </summary>
    private async Task EnsureWaitingAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + WaitingTimeout;
        var resetTried = false;
        var lastSeen = "";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                lastSeen = TerracottaStateJson.Parse(await GetAsync("/state", cancellationToken).ConfigureAwait(false)).State;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (lastSeen == "waiting")
            {
                return;
            }

            if (!resetTried)
            {
                resetTried = true;
                Log?.Invoke("info", $"陶瓦当前状态为 {lastSeen}，先复位为待机再发起请求");
                try
                {
                    await GetAsync("/state/ide", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
                {
                    Log?.Invoke("warn", "复位陶瓦状态失败：" + ex.Message);
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        Log?.Invoke("warn", $"陶瓦未在 {WaitingTimeout.TotalSeconds:0}s 内回到待机（当前 {lastSeen}），继续尝试发起请求");
    }

    private async Task<TerracottaStateJson> PollUntilReadyAsync(TerracottaMode mode, int timeoutSeconds, CancellationToken cancellationToken)
    {
        _pollCts?.Dispose();
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _pollCts.Token;
        var startedUtc = DateTime.UtcNow;
        var deadline = startedUtc + TimeSpan.FromSeconds(timeoutSeconds);
        var lastRaw = Current.StateRaw;
        var lastIndex = -1;
        var lastNotice = startedUtc;

        while (DateTime.UtcNow < deadline)
        {
            if (_process is null || _process.HasExited)
            {
                throw new InvalidOperationException("陶瓦进程已退出");
            }

            TerracottaStateJson state;
            try
            {
                state = TerracottaStateJson.Parse(await GetAsync("/state", token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                continue;
            }

            // 上游约定 index 单调递增：忽略回退/重复的快照，避免旧状态把界面拉回去
            if (state.Index >= 0 && state.Index < lastIndex)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                continue;
            }

            lastIndex = state.Index;

            if (state.State != lastRaw)
            {
                lastRaw = state.State;
                // host-starting 起上游就已给出房间码：先展示出来，用户不必干等到 host-ok 才能复制
                var room = mode == TerracottaMode.Host && !string.IsNullOrEmpty(state.Room)
                    ? state.Room
                    : Current.Room;
                Publish(s => s with { StateRaw = state.State, Room = room });
                Log?.Invoke("info", "陶瓦状态：" + state.State);
            }

            if (state.State is "exception" or "fatal" or "failed")
            {
                try
                {
                    await GetAsync("/state/ide", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 复位尽力而为
                }

                throw new InvalidOperationException(DescribeException(state.Type, mode == TerracottaMode.Host));
            }

            if (mode == TerracottaMode.Host && state.State == "host-ok" && !string.IsNullOrEmpty(state.Room))
            {
                return state;
            }

            if (mode == TerracottaMode.Join && state.State == "guest-ok" && !string.IsNullOrEmpty(state.Url))
            {
                return state;
            }

            // 长等待期间每 15s 打一条进度，便于用户反馈日志时定位卡在哪一步
            if (DateTime.UtcNow - lastNotice > TimeSpan.FromSeconds(15))
            {
                lastNotice = DateTime.UtcNow;
                Log?.Invoke("info", $"仍在等待（{state.State}），已等待 {(int)(DateTime.UtcNow - startedUtc).TotalSeconds}s");
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        throw new TimeoutException(mode == TerracottaMode.Host
            ? $"等待房间号超时（{timeoutSeconds}s，最后状态：{lastRaw ?? "未知"}）。"
              + "若一直停在 host-scanning，请先在游戏里「对局域网开放」世界；"
              + "若停在 host-starting，说明正在通过公共节点打洞，可稍等后重试或在插件设置中查看日志"
            : $"等待连接就绪超时（{timeoutSeconds}s，最后状态：{lastRaw ?? "未知"}）。"
              + "请确认房间码正确、房主的房间仍然开启，并检查网络后重试");
    }

    /// <summary>把上游 exception 的 type 翻成可操作提示（定义见 http-api.md）。</summary>
    private static string DescribeException(int? type, bool isHost) => type switch
    {
        0 => "无法连接房主：房间已关闭或网络不稳定，请确认房主房间仍在后重试",
        1 => "与房主的连接已中断，请重新加入",
        2 => "本机 EasyTier 崩溃了，请重试；多次失败可更换网络环境",
        3 => "房主端 EasyTier 崩溃，请关闭房间后重新创建",
        4 => "房主端 Minecraft 服务器失联（房主退出存档后房间会自动关闭）",
        5 => "陶瓦脚手架协议错误，请更新插件与联机工具后重试",
        _ => isHost ? "陶瓦进入异常状态，已复位，可重试" : "加入房间失败，已复位，可重试",
    };

    // ---------- 小工具 ----------

    /// <summary>限时拉取社区节点清单：超时返回空列表（陶瓦自动改用内置节点），不阻塞开房。</summary>
    private async Task<IReadOnlyList<string>> FetchNodesWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            return await _nodes.GetNodesAsync(limited.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log?.Invoke("warn", "社区节点清单拉取超时，本次联机改用陶瓦内置节点");
            return [];
        }
    }

    /// <summary>把节点列表拼成重复的 public_nodes 查询参数（Terracotta 会与内置节点合并）。</summary>
    private static string BuildNodeQuery(IReadOnlyList<string> nodes)
    {
        if (nodes.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        var added = 0;
        foreach (var node in nodes)
        {
            var encoded = "&public_nodes=" + Uri.EscapeDataString(node);
            // 本地 HTTP 服务的请求行有长度上限：按编码后总长 6000 字符截断。
            // 宁可少带几个节点，也不要因为请求行过长被 431/断开而让整次开房直接失败。
            if (added >= 48 || builder.Length + encoded.Length > 6000)
            {
                break;
            }

            builder.Append(encoded);
            added++;
        }

        return builder.ToString();
    }

    private void Publish(Func<TerracottaState, TerracottaState> mutate)
    {
        TerracottaState snapshot;
        lock (_stateLock)
        {
            _state = mutate(_state);
            snapshot = _state;
        }

        StateChanged?.Invoke(snapshot);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限已正确时忽略
        }
    }

    /// <summary>目录必须真实存在且不是符号链接（对齐 KAMUCL 的 lstat 检查）。</summary>
    private static void EnsureRealDirectory(string path, string label)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            throw new InvalidOperationException(label + "不存在");
        }

        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException(label + "不是独立目录（符号链接）");
        }
    }

    private static void EnsureRealFile(FileInfo info, string label)
    {
        if (!info.Exists)
        {
            throw new InvalidOperationException(label + "不存在");
        }

        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException(label + "不是普通文件（符号链接）");
        }
    }

    private static async Task<bool> VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant() == expected.ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>清理工具目录里过期的断点临时文件（取消下载会保留 .hycl-dl 供续传，过期才算残件）。</summary>
    private void CleanStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(ToolDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(ToolDirectory, "*" + DownloadTempSuffix))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTempAge)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static HttpClient CreateLocalHttp()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    [GeneratedRegex("\"port\"\\s*:\\s*(\\d+)")]
    private static partial Regex PortPattern();

    private sealed record SessionDirectory(string Directory, bool IsMac, Process? Process);

    private sealed record TerracottaStateJson(string State, string? Room, string? Url, int Index, int? Type)
    {
        public static TerracottaStateJson Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new TerracottaStateJson(
                root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("room", out var room) && room.ValueKind == JsonValueKind.String ? room.GetString() : null,
                root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
                root.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var indexValue)
                    ? indexValue
                    : -1,
                root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var typeValue)
                    ? typeValue
                    : null);
        }
    }
}

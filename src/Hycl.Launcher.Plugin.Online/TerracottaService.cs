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
    private static readonly Regex PortFilePattern = PortPattern();
    private static readonly Regex RoomCodePattern = new(@"^U/[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$", RegexOptions.Compiled);

    /// <summary>工具文件缺失或损坏时统一提示（UI 的「补齐文件后重试」按钮会重新下载）。</summary>
    public const string RepairHint = "陶瓦工具文件缺失或完整性校验未通过，请点击「补齐文件后重试」重新下载联机工具";

    private const int StartTotalTimeoutSeconds = 120;
    private const int HostPollSeconds = 30;
    private const int JoinPollSeconds = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    /// <summary>官方 0.4.2 资产（清单不可达时的兜底；键与 HyclOnlineDefaults.ToolAssetKey 一致）。</summary>
    private static readonly Dictionary<string, (string FileName, string ExeName, string PkgSha256, string ExeSha256)> FallbackAssets = new(StringComparer.Ordinal)
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
        var asset = await ResolveAssetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformNotSupportedException("陶瓦联机暂不支持当前平台");
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
            .DownloadFileAsync(asset.Urls, packagePath, asset.Size > 0 ? asset.Size : null, asset.Sha256, progress, cancellationToken)
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

    /// <summary>本地二进制是否已就绪（快速校验）。</summary>
    public async Task<bool> IsBinaryReadyAsync(CancellationToken cancellationToken = default)
    {
        var asset = await ResolveAssetAsync(cancellationToken).ConfigureAwait(false);
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

        try
        {
            var asset = await ResolveAssetAsync(cancellationToken).ConfigureAwait(false);
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
                if (await VerifySha256Async(exePath, asset.ExeSha256, cancellationToken).ConfigureAwait(false))
                {
                    MakeExecutable(exePath);
                    Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = true, Error = null });
                }
                else
                {
                    // 损坏时不要自动覆盖：交给用户点「补齐文件后重试」，避免启动期反复下载
                    Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = false, Error = RepairHint });
                }

                return;
            }

            // 首次安装（或文件被清理）：直接后台补好，用户不需要手动点下载
            Publish(s => s with { Phase = TerracottaPhase.Downloading, BinaryReady = false, Error = null });
            await EnsureInstalledAsync(null, cancellationToken).ConfigureAwait(false);
            Publish(s => s with { Phase = TerracottaPhase.Idle });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(s => s with { Phase = TerracottaPhase.Idle, BinaryReady = false, Error = RepairHint });
            Log?.Invoke("warn", "自动准备陶瓦工具失败：" + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _autoPreparing, 0);
        }
    }

    private async Task<ToolAsset?> ResolveAssetAsync(CancellationToken cancellationToken)
    {
        var key = PlatformAssetKey();
        if (key is null || !FallbackAssets.TryGetValue(key, out var fallback))
        {
            return null;
        }

        var manifest = await _context.GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var tool = manifest?.Tools.GetValueOrDefault("terracotta");
        var asset = tool?.Assets.GetValueOrDefault(key);
        var fileName = asset?.FileName ?? fallback.FileName;
        var exeName = asset?.ExeName ?? fallback.ExeName;
        var pkgSha = asset?.Sha256 ?? fallback.PkgSha256;
        var exeSha = asset?.ExeSha256 ?? fallback.ExeSha256;
        var size = asset?.Size ?? 0;
        IReadOnlyList<string> urls;
        if (asset is { Urls.Count: > 0 } && !string.IsNullOrWhiteSpace(asset.Sha256))
        {
            urls = asset.Urls;
        }
        else
        {
            // 清单不可达时的兜底链：EdgeOne 托管（若已部署）→ gitee / cnb 官方镜像 →
            // GitHub Release（启动器侧按 GitHubSources 镜像池自动展开，GitHub 原站托底）
            var version = string.IsNullOrWhiteSpace(tool?.Version) ? ToolVersion : tool!.Version;
            var fallbackUrls = new List<string>();
            var edgeOne = HyclOnlineSites.EdgeOneToolFileUrl(version, fileName);
            if (edgeOne is not null)
            {
                fallbackUrls.Add(edgeOne);
            }

            fallbackUrls.Add($"https://gitee.com/burningtnt/Terracotta/releases/download/v{version}/{fileName}");
            fallbackUrls.Add($"https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{version}/{fileName}");
            fallbackUrls.Add($"https://github.com/burningtnt/Terracotta/releases/download/v{version}/{fileName}");
            urls = fallbackUrls;
        }

        return new ToolAsset(key, fileName, exeName, pkgSha, exeSha, size, urls);
    }

    private sealed record ToolAsset(
        string Key,
        string FileName,
        string ExeName,
        string Sha256,
        string ExeSha256,
        long Size,
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
            var asset = await ResolveAssetAsync(cancellationToken).ConfigureAwait(false)
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

            // 社区节点（密文清单）：与陶瓦内置节点合并，缓解内置节点 DNS 失效导致的「等待房间号超时」
            var publicNodes = await _nodes.GetNodesAsync(cancellationToken).ConfigureAwait(false);
            var nodeQuery = BuildNodeQuery(publicNodes);
            if (publicNodes.Count > 0)
            {
                Log?.Invoke("info", $"已附加 {publicNodes.Count} 个社区节点");
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

            await KillTreeAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _state = new TerracottaState();
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
                    _state = new TerracottaState();
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

    private async Task<TerracottaStateJson> PollUntilReadyAsync(TerracottaMode mode, int timeoutSeconds, CancellationToken cancellationToken)
    {
        _pollCts?.Dispose();
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _pollCts.Token;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        var lastRaw = Current.StateRaw;
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

            if (state.State != lastRaw)
            {
                lastRaw = state.State;
                Publish(s => s with { StateRaw = state.State });
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

                throw new InvalidOperationException($"陶瓦进入异常状态（{state.State}），已复位，可重试");
            }

            if (mode == TerracottaMode.Host && state.State == "host-ok" && !string.IsNullOrEmpty(state.Room))
            {
                return state;
            }

            if (mode == TerracottaMode.Join && state.State == "guest-ok" && !string.IsNullOrEmpty(state.Url))
            {
                return state;
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        throw new TimeoutException(mode == TerracottaMode.Host ? "等待房间号超时（30s）" : "等待连接就绪超时");
    }

    // ---------- 小工具 ----------

    /// <summary>把节点列表拼成重复的 public_nodes 查询参数（Terracotta 会与内置节点合并）。</summary>
    private static string BuildNodeQuery(IReadOnlyList<string> nodes)
    {
        if (nodes.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        // 上限 48 个：避免本地 HTTP 请求行过长（当前清单 31 个，留有余量）
        foreach (var node in nodes.Take(48))
        {
            builder.Append("&public_nodes=").Append(Uri.EscapeDataString(node));
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

    private sealed record TerracottaStateJson(string State, string? Room, string? Url)
    {
        public static TerracottaStateJson Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new TerracottaStateJson(
                root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("room", out var room) && room.ValueKind == JsonValueKind.String ? room.GetString() : null,
                root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null);
        }
    }
}

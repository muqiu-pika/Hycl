using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Java;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

public sealed class LaunchOptions
{
    public required string InstanceId { get; init; }

    public required string GameDirectory { get; init; }

    /// <summary>版本隔离时的实例目录；空字符串表示使用 GameDirectory。</summary>
    public string InstanceDirectory { get; init; } = "";

    public string JavaPath { get; init; } = "";

    public int MemoryMegabytes { get; init; } = 2048;

    /// <summary>0 默认；1 全屏；2 与启动器相同；3 自定义；4 最大化。</summary>
    public int WindowType { get; init; }

    public int WindowWidth { get; init; } = 854;

    public int WindowHeight { get; init; } = 480;

    public int LauncherWindowWidth { get; init; } = 850;

    public int LauncherWindowHeight { get; init; } = 500;

    public string ExtraJvmArguments { get; init; } = "";

    public string ExtraGameArguments { get; init; } = "";

    public string PlayerName { get; init; } = "Player";

    public string PlayerUuid { get; init; } = "";

    /// <summary>进入游戏的访问令牌；离线账号为空（占位符回退 "0"）。</summary>
    public string AccessToken { get; init; } = "";

    /// <summary>user_type 占位符：msa（正版）/ Legacy（皮肤站）/ legacy（离线）。</summary>
    public string UserType { get; init; } = "legacy";

    /// <summary>clientid 占位符（正版登录填启动器自己的 ClientId）。</summary>
    public string AuthClientId { get; init; } = "";

    /// <summary>auth_xuid 占位符（正版登录的 Xbox XUID）。</summary>
    public string AuthXuid { get; init; } = "";

    /// <summary>皮肤站 Yggdrasil 根地址；非空时注入 authlib-injector javaagent。</summary>
    public string AuthlibServer { get; init; } = "";

    /// <summary>authlib-injector.jar 的完整路径（随 AuthlibServer 一起提供）。</summary>
    public string AuthlibJarPath { get; init; } = "";

    /// <summary>皮肤站根响应的 Base64（-Dauthlibinjector.yggdrasil.prefetched）。</summary>
    public string AuthlibPrefetched { get; init; } = "";

    /// <summary>游戏窗口标题；空 = 不修改（Windows 下经 Win32 注入）。</summary>
    public string WindowTitle { get; init; } = "";

    /// <summary>自定义信息（version_type 参数，显示在游戏左下角与 F3）。</summary>
    public string TypeInfo { get; init; } = "";

    /// <summary>0 游戏启动后立即关闭 / 4 最小化 / 5 保持不变。</summary>
    public int LauncherVisibility { get; init; } = 5;

    /// <summary>0 高 / 1 中 / 2 低 / 3 极高 / 4 实时（ProcessPriorityClass 语义见 ApplyPriority）。</summary>
    public int ProcessPriority { get; init; } = 1;

    /// <summary>0 IPv4 优先 / 1 Java 默认 / 2 IPv6 优先。</summary>
    public int PreferredIpStack { get; init; } = 0;

    /// <summary>初始内存（-Xms）MB；0 = 自动。</summary>
    public int InitialMemoryMegabytes { get; init; }

    /// <summary>锁定内存分配：-Xms = -Xmx。</summary>
    public bool LockMemory { get; init; }

    /// <summary>启动前执行命令；空 = 不执行。</summary>
    public string PreLaunchCommand { get; init; } = "";

    /// <summary>等待启动前命令执行完成后再继续启动。</summary>
    public bool PreLaunchCommandWait { get; init; } = true;

    /// <summary>自动进入的服务器；空 = 不自动进服。对应 PCL-CE VersionServerEnter。</summary>
    public string ServerToEnter { get; init; } = "";
}

public sealed class GameSession : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    internal GameSession(Process process, GameLogSignal logSignal)
    {
        _process = process;
        LogSignal = logSignal;
    }

    internal Process Process => _process;

    internal GameLogSignal LogSignal { get; }

    /// <summary>启动成功后探测到的游戏主窗口句柄（非 Windows 或未探测到时为 0）。</summary>
    internal nint WindowHandle { get; set; }

    public int Id => _process.Id;

    /// <summary>进程退出状态；即使 Process 已被释放也返回安全结果，避免 UI 线程异常。</summary>
    public bool HasExited
    {
        get
        {
            if (_disposed)
            {
                return true;
            }

            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            try
            {
                return _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
        catch (Win32Exception)
        {
            // 权限不足等，忽略
        }
    }

    /// <summary>
    /// 优雅关闭：先向游戏窗口发送正常关闭请求（Windows 为 WM_CLOSE），
    /// 超过宽限期仍未退出再强制结束进程；每一步都写入日志。
    /// </summary>
    public async Task StopGracefullyAsync(Action<string>? log = null, TimeSpan? gracePeriod = null)
    {
        if (HasExited)
        {
            return;
        }

        var grace = gracePeriod ?? TimeSpan.FromSeconds(8);
        var requested = false;
        var handle = WindowHandle;
        if (handle == 0 && PlatformInfo.IsWindows)
        {
            handle = GameWindowProbe.Probe(_process.Id).Handle;
        }

        if (PlatformInfo.IsWindows && handle != 0)
        {
            // 新版 Minecraft 使用 SDL 窗口，直接向探测到的窗口发送 WM_CLOSE
            requested = GameWindowProbe.RequestClose(handle);
            if (requested)
            {
                log?.Invoke("已向游戏窗口发送关闭请求（WM_CLOSE），等待进程退出…");
            }
        }
        else
        {
            try
            {
                requested = _process.CloseMainWindow();
                if (requested)
                {
                    log?.Invoke("已向游戏窗口发送正常关闭请求，等待进程退出…");
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出
            }
            catch (Win32Exception ex)
            {
                log?.Invoke($"发送正常关闭请求失败：{ex.Message}");
            }
        }

        if (requested)
        {
            try
            {
                await _process.WaitForExitAsync().WaitAsync(grace).ConfigureAwait(false);
                log?.Invoke("游戏已正常退出。");
                return;
            }
            catch (TimeoutException)
            {
                log?.Invoke($"游戏在 {grace.TotalSeconds:0} 秒内未正常退出，准备强制结束…");
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        else
        {
            log?.Invoke($"{PlatformInfo.OperatingSystemName} 未能发送窗口关闭请求，准备强制结束…");
        }

        try
        {
            if (!HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                log?.Invoke("已强制结束游戏进程。");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            log?.Invoke($"强制结束游戏进程失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}

/// <summary>
/// 组装并启动 Minecraft 进程；对应 PCL-CE 的启动逻辑，跨平台实现。
/// </summary>
public sealed class LaunchService
{
    public const string LauncherVersion = "1.0.0";

    private static readonly Regex PlaceholderRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private readonly VersionResolver _resolver;
    private readonly JavaDetector _javaDetector;
    private readonly LauncherLogger _logger;

    private IReadOnlyList<JavaInstallation>? _cachedJava;
    private DateTimeOffset _javaCacheTime = DateTimeOffset.MinValue;

    public LaunchService(VersionResolver resolver, JavaDetector javaDetector, LauncherLogger logger)
    {
        _resolver = resolver;
        _javaDetector = javaDetector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<JavaInstallation>> GetJavaInstallationsAsync(
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _cachedJava is not null && DateTimeOffset.Now - _javaCacheTime < TimeSpan.FromMinutes(10))
        {
            return _cachedJava;
        }

        _cachedJava = await _javaDetector.DetectAsync(null, cancellationToken).ConfigureAwait(false);
        _javaCacheTime = DateTimeOffset.Now;
        return _cachedJava;
    }

    /// <summary>
    /// 启动前检查（对应 PCL-CE 启动前的检查阶段）：版本文件 / 资源与依赖库 / Java 与内存 / 账号 / 游戏目录。
    /// 返回所有检查项；不通过时由启动过程页展示，禁止继续进入「启动进程」。
    /// </summary>
    public Task<LaunchPreflightResult> RunPreflightAsync(
        LaunchOptions options,
        PlayerAccount? account,
        Action<int>? onStep = null,
        CancellationToken cancellationToken = default) =>
        LaunchPreflight.RunAsync(options, account, _resolver, _javaDetector, onStep, cancellationToken);

    public async Task<GameSession> LaunchAsync(
        LaunchOptions options,
        Action<string>? onOutput = null,
        Action<string>? onStage = null,
        CancellationToken cancellationToken = default)
    {
        var gameDirectory = Path.GetFullPath(options.GameDirectory);
        onStage?.Invoke("正在检查版本文件…");
        var resolved = await _resolver.ResolveAsync(gameDirectory, options.InstanceId, cancellationToken).ConfigureAwait(false)
                       ?? throw new LaunchException($"版本不完整或不存在：{options.InstanceId}");

        if (!File.Exists(resolved.ClientJarPath))
        {
            throw new LaunchException($"缺少客户端核心文件：{resolved.ClientJarPath}{Environment.NewLine}请在版本管理中重新安装该版本。");
        }

        var environment = RuleEnvironment.Current(options.WindowType == 3);
        var assetsDirectory = Path.Combine(gameDirectory, "assets");
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        Directory.CreateDirectory(resolved.NativesDirectory);
        EnsureNatives(resolved, librariesDirectory);

        var libraries = LibraryResolver.CollectLibraries(resolved.Info, environment, DownloadPolicy.Default);
        var classPath = BuildClassPath(libraries, librariesDirectory, resolved.ClientJarPath);

        onStage?.Invoke("正在检测 Java 运行环境…");
        var java = await ResolveJavaAsync(options, resolved, cancellationToken).ConfigureAwait(false);
        _logger.Info($"启动 {options.InstanceId}：Java {java.VersionText}（{java.ExecutablePath}）");

        onStage?.Invoke("正在准备启动参数…");
        var placeholders = BuildPlaceholders(options, resolved, gameDirectory, assetsDirectory, librariesDirectory, classPath);

        // 启动前执行命令（对应 PCL-CE LaunchAdvanceRun）
        if (!string.IsNullOrWhiteSpace(options.PreLaunchCommand))
        {
            await RunPreLaunchCommandAsync(options, placeholders, gameDirectory, cancellationToken).ConfigureAwait(false);
        }

        var jvmArguments = BuildJvmArguments(options, resolved.Info, environment, placeholders, resolved.NativesDirectory, classPath);
        var gameArguments = BuildGameArguments(options, resolved.Info, environment, placeholders);

        var mainClass = resolved.Info.MainClass ?? "net.minecraft.client.main.Main";
        var startInfo = new ProcessStartInfo(java.ExecutablePath)
        {
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in jvmArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(mainClass);
        foreach (var argument in gameArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.Info($"命令：{java.ExecutablePath} {string.Join(' ', jvmArguments)} {mainClass}");

        onStage?.Invoke("正在启动游戏…");
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new LaunchException("无法创建游戏进程。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new LaunchException($"启动 Java 进程失败：{ex.Message}", ex);
        }

        var logSignal = new GameLogSignal();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                logSignal.Feed(args.Data);
                onOutput?.Invoke(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                logSignal.Feed(args.Data);
                onOutput?.Invoke(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        ApplyProcessPriority(process, options.ProcessPriority);
        _logger.Info($"游戏进程已启动，PID={process.Id}");

        if (PlatformInfo.IsWindows && !string.IsNullOrWhiteSpace(options.WindowTitle))
        {
            var windowTitle = ApplyTitlePlaceholders(options, resolved.Info);
            _ = InjectWindowTitleAsync(process, windowTitle, placeholders);
        }

        return new GameSession(process, logSignal);
    }

    /// <summary>
    /// 等待游戏窗口真正出现（启动成功的真实判定）；超时或进程提前退出时返回失败。
    /// Windows 使用窗口枚举，macOS / Linux 降级为日志特征行 + 超时兜底。
    /// </summary>
    public static Task<GameStartResult> WaitForGameWindowAsync(
        GameSession session,
        Action<string>? onStage = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return GameStartMonitor.WaitAsync(
            session.Process,
            session.LogSignal,
            onStage,
            handle => session.WindowHandle = handle,
            timeout ?? GameStartMonitor.DefaultTimeout,
            cancellationToken);
    }

    private async Task<JavaInstallation> ResolveJavaAsync(
        LaunchOptions options,
        ResolvedVersion resolved,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.JavaPath))
        {
            var specified = await _javaDetector.InspectAsync(options.JavaPath, cancellationToken).ConfigureAwait(false);
            return specified ?? throw new LaunchException($"指定的 Java 无法使用：{options.JavaPath}");
        }

        var required = resolved.Info.JavaVersion?.MajorVersion ?? JavaDetector.RequiredMajorVersion(resolved.RootId);
        var installations = await GetJavaInstallationsAsync(false, cancellationToken).ConfigureAwait(false);
        var best = JavaDetector.FindBest(installations, required);
        if (best is null)
        {
            throw new LaunchException(
                $"未检测到可用的 Java（该版本建议 Java {required} 或更高）。{Environment.NewLine}" +
                "请安装 Java 后重试，或在设置中手动指定 Java 路径。");
        }

        if (best.MajorVersion < required)
        {
            _logger.Warn($"Java {best.MajorVersion} 低于建议版本 {required}，可能导致启动失败。");
        }

        return best;
    }

    private static string BuildClassPath(
        IReadOnlyList<LibraryFile> libraries,
        string librariesDirectory,
        string clientJarPath)
    {
        var entries = new List<string>();
        var missing = new List<string>();
        foreach (var library in libraries)
        {
            var path = Path.Combine(librariesDirectory, library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                entries.Add(path);
            }
            else
            {
                missing.Add(library.RelativePath);
            }
        }

        if (missing.Count > 0)
        {
            var preview = string.Join(Environment.NewLine, missing.Take(5));
            throw new LaunchException(
                $"缺少 {missing.Count} 个依赖库文件，无法启动。请在版本管理中重新安装该版本。{Environment.NewLine}{preview}");
        }

        entries.Add(clientJarPath);
        return string.Join(PlatformInfo.ClassPathSeparator, entries);
    }

    private static Dictionary<string, string> BuildPlaceholders(
        LaunchOptions options,
        ResolvedVersion resolved,
        string gameDirectory,
        string assetsDirectory,
        string librariesDirectory,
        string classPath)
    {
        var info = resolved.Info;
        var uuid = string.IsNullOrWhiteSpace(options.PlayerUuid)
            ? OfflineUuid.Create(options.PlayerName)
            : options.PlayerUuid;
        var assetIndex = info.AssetIndex?.Id ?? info.Assets ?? "legacy";
        var virtualAssets = Path.Combine(assetsDirectory, "virtual", assetIndex);
        var width = options.WindowType == 3
            ? options.WindowWidth
            : options.WindowType == 2 ? options.LauncherWindowWidth : 854;
        var height = options.WindowType == 3
            ? options.WindowHeight
            : options.WindowType == 2 ? options.LauncherWindowHeight : 480;
        // 自定义信息覆盖 version_type（显示在游戏主界面左下角与 F3，对应 PCL-CE LaunchArgumentInfo）
        var versionType = string.IsNullOrWhiteSpace(options.TypeInfo) ? info.Type ?? "release" : options.TypeInfo;
        // 版本隔离：game_directory 指向实例目录，让 saves/mods 等落在实例内
        var instanceDirectory = string.IsNullOrWhiteSpace(options.InstanceDirectory)
            ? gameDirectory
            : Path.GetFullPath(options.InstanceDirectory);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = options.PlayerName,
            ["version_name"] = options.InstanceId,
            ["game_directory"] = instanceDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = assetIndex,
            ["game_assets"] = virtualAssets,
            ["auth_uuid"] = uuid.Replace("-", "", StringComparison.Ordinal),
            ["auth_access_token"] = string.IsNullOrWhiteSpace(options.AccessToken) ? "0" : options.AccessToken,
            ["user_type"] = string.IsNullOrWhiteSpace(options.UserType) ? "legacy" : options.UserType,
            ["version_type"] = versionType,
            ["natives_directory"] = resolved.NativesDirectory,
            ["launcher_name"] = "Hycl",
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = classPath,
            ["classpath_separator"] = PlatformInfo.ClassPathSeparator,
            ["library_directory"] = librariesDirectory,
            ["resolution_width"] = width.ToString(),
            ["resolution_height"] = height.ToString(),
            ["user_properties"] = "{}",
            ["clientid"] = options.AuthClientId,
            ["auth_xuid"] = options.AuthXuid
        };
    }

    private static List<string> BuildJvmArguments(
        LaunchOptions options,
        VersionInfo info,
        RuleEnvironment environment,
        IReadOnlyDictionary<string, string> placeholders,
        string nativesDirectory,
        string classPath)
    {
        var memory = Math.Max(512, options.MemoryMegabytes);
        var arguments = new List<string>
        {
            $"-Xmx{memory}M",
            "-Dfile.encoding=UTF-8",
            "-Djava.library.path=" + nativesDirectory,
            "-Dminecraft.launcher.brand=Hycl",
            $"-Dminecraft.launcher.version={LauncherVersion}"
        };

        // 皮肤站账号：注入 authlib-injector javaagent（对应 PCL-CE _AddAuthlibInjectorJvmArgs，插在最前）
        if (!string.IsNullOrWhiteSpace(options.AuthlibServer) && !string.IsNullOrWhiteSpace(options.AuthlibJarPath))
        {
            arguments.Insert(
                0,
                $"-javaagent:\"{options.AuthlibJarPath}\"={options.AuthlibServer}");
            arguments.Insert(1, "-Dauthlibinjector.side=client");
            if (!string.IsNullOrWhiteSpace(options.AuthlibPrefetched))
            {
                arguments.Insert(2, $"-Dauthlibinjector.yggdrasil.prefetched={options.AuthlibPrefetched}");
            }

            // 兼容自签名证书的皮肤站（对齐 PCL-CE 的 WINDOWS-ROOT 处理）
            if (OperatingSystem.IsWindows())
            {
                arguments.Insert(3, "-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT");
            }
        }

        // 初始内存（-Xms）：锁定内存 > 手动初始内存 > 自动（对应 PCL-CE 内存策略）
        if (options.LockMemory)
        {
            arguments.Add($"-Xms{memory}M");
        }
        else if (options.InitialMemoryMegabytes > 0)
        {
            arguments.Add($"-Xms{Math.Clamp(options.InitialMemoryMegabytes, 256, memory)}M");
        }
        else
        {
            arguments.Add($"-Xms{Math.Clamp(memory / 4, 256, 1024)}M");
        }

        // IP 协议偏好（对应 PCL-CE LaunchPreferredIpStack）
        switch (options.PreferredIpStack)
        {
            case 0:
                arguments.Add("-Djava.net.preferIPv4Stack=true");
                break;
            case 2:
                arguments.Add("-Djava.net.preferIPv6Stack=true");
                break;
        }

        if (info.Logging?.Client?.File is not null)
        {
            var logFile = info.Logging.Client.File;
            var path = Path.Combine(
                placeholders["game_directory"],
                "assets",
                "log_configs",
                (logFile.Path ?? logFile.Id ?? "client") + ".xml");
            if (File.Exists(path) && info.Logging.Client.Argument is not null)
            {
                var loggingPlaceholders = placeholders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                loggingPlaceholders["path"] = path;
                arguments.Add(Substitute(info.Logging.Client.Argument, loggingPlaceholders));
            }
        }

        arguments.AddRange(ArgumentTokenizer.Split(options.ExtraJvmArguments));

        var hasClassPath = false;
        foreach (var entry in info.Arguments?.Jvm ?? Enumerable.Empty<ArgumentEntry>())
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, environment))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                if (value.Contains("${classpath}", StringComparison.Ordinal))
                {
                    hasClassPath = true;
                }

                arguments.Add(Substitute(value, placeholders));
            }
        }

        if (!hasClassPath)
        {
            arguments.Add("-cp");
            arguments.Add(classPath);
        }

        return arguments;
    }

    private static List<string> BuildGameArguments(
        LaunchOptions options,
        VersionInfo info,
        RuleEnvironment environment,
        IReadOnlyDictionary<string, string> placeholders)
    {
        var arguments = new List<string>();
        if (info.Arguments is not null && info.Arguments.Game.Count > 0)
        {
            foreach (var entry in info.Arguments.Game)
            {
                if (!RuleEvaluator.IsAllowed(entry.Rules, environment))
                {
                    continue;
                }

                foreach (var value in entry.Values)
                {
                    arguments.Add(Substitute(value, placeholders));
                }
            }
        }
        else
        {
            foreach (var token in ArgumentTokenizer.Split(info.MinecraftArguments))
            {
                arguments.Add(Substitute(token, placeholders));
            }
        }

        if (options.WindowType is 2 or 3 &&
            !arguments.Contains("--width", StringComparer.Ordinal))
        {
            arguments.Add("--width");
            arguments.Add(placeholders["resolution_width"]);
            arguments.Add("--height");
            arguments.Add(placeholders["resolution_height"]);
        }

        arguments.AddRange(ArgumentTokenizer.Split(options.ExtraGameArguments));

        // 自动进服（对应 PCL-CE _AppendWorldAndServerArguments）：1.20.3+ 用 quickPlay，旧版本用 --server/--port
        if (!string.IsNullOrWhiteSpace(options.ServerToEnter))
        {
            var server = options.ServerToEnter.Trim();
            if (DateTimeOffset.TryParse(info.ReleaseTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var releaseTime)
                && releaseTime > new DateTimeOffset(2023, 4, 4, 0, 0, 0, TimeSpan.Zero))
            {
                arguments.Add("--quickPlayMultiplayer");
                arguments.Add(server);
            }
            else
            {
                var parts = server.Split(':');
                arguments.Add("--server");
                arguments.Add(parts[0]);
                arguments.Add("--port");
                arguments.Add(parts.Length > 1 ? parts[1] : "25565");
            }
        }

        return arguments;
    }

    private static string Substitute(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        return PlaceholderRegex.Replace(value, match =>
        {
            var key = match.Groups[1].Value;
            return placeholders.TryGetValue(key, out var replacement) ? replacement : match.Value;
        });
    }

    /// <summary>启动前执行命令（对应 PCL-CE LaunchAdvanceRun；占位符与游戏参数一致）。</summary>
    private async Task RunPreLaunchCommandAsync(
        LaunchOptions options,
        IReadOnlyDictionary<string, string> placeholders,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        var command = options.PreLaunchCommand.Trim();
        var arguments = ArgumentTokenizer.Split(Substitute(command, placeholders)).ToList();
        if (arguments.Count == 0)
        {
            return;
        }

        var info = new ProcessStartInfo(arguments[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = gameDirectory
        };
        foreach (var argument in arguments.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        _logger.Info($"执行启动前命令：{command}");
        using var process = Process.Start(info);
        if (process is null)
        {
            _logger.Warn("启动前命令未能创建进程，继续启动流程");
            return;
        }

        if (!options.PreLaunchCommandWait)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info($"启动前命令已退出（code={process.ExitCode}）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"等待启动前命令失败（继续启动）：{ex.Message}");
        }
    }

    /// <summary>应用游戏进程优先级（对应 PCL-CE LaunchArgumentPriority）。</summary>
    private static void ApplyProcessPriority(Process process, int priority)
    {
        try
        {
            process.PriorityClass = priority switch
            {
                0 => ProcessPriorityClass.AboveNormal,
                2 => ProcessPriorityClass.BelowNormal,
                3 => ProcessPriorityClass.High,
                4 => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 优先级设置失败不影响启动
        }
    }

    /// <summary>展开 PCL-CE 支持的大括号标题标记（{name}/{user}/{login}/{version}/{date}/{time}）。</summary>
    private static string ApplyTitlePlaceholders(LaunchOptions options, VersionInfo info)
    {
        var login = options.UserType switch
        {
            "msa" => "正版",
            "authlib" => "第三方",
            _ => "离线"
        };
        var now = DateTime.Now;
        return options.WindowTitle
            .Replace("{name}", options.InstanceId, StringComparison.Ordinal)
            .Replace("{user}", options.PlayerName, StringComparison.Ordinal)
            .Replace("{login}", login, StringComparison.Ordinal)
            .Replace("{version}", info.Id, StringComparison.Ordinal)
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time}", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// 游戏窗口标题注入（对应 PCL-CE LaunchArgumentTitle）：
    /// 游戏窗口创建后轮询主窗口句柄并用 SetWindowText 覆写（仅 Windows）。
    /// </summary>
    private async Task InjectWindowTitleAsync(
        Process process,
        string titleTemplate,
        IReadOnlyDictionary<string, string> placeholders)
    {
        var title = Substitute(titleTemplate, placeholders);
        var logged = false;
        // 与 PCL-CE Watcher 一致：游戏会自行重置标题，因此需要在进程存活期间反复设置。
        while (true)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return;
                }

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    handle = GameWindowProbe.Probe(process.Id).Handle;
                }

                if (handle != IntPtr.Zero && Win32WindowTitle.SetWindowText(handle, title))
                {
                    if (!logged)
                    {
                        _logger.Info($"已设置游戏窗口标题：{title}");
                        logged = true;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static class Win32WindowTitle
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetWindowText(IntPtr hWnd, string text);
    }

    private void EnsureNatives(ResolvedVersion resolved, string librariesDirectory)
    {
        if (Directory.Exists(resolved.NativesDirectory) &&
            Directory.EnumerateFileSystemEntries(resolved.NativesDirectory).Any())
        {
            return;
        }

        var environment = RuleEnvironment.Current(false);
        foreach (var library in resolved.Info.Libraries)
        {
            if (library.Natives is null || !RuleEvaluator.IsAllowed(library.Rules, environment))
            {
                continue;
            }

            var nativeFile = LibraryResolver.ResolveNative(library, environment, DownloadPolicy.Default);
            if (nativeFile is null)
            {
                continue;
            }

            var archivePath = Path.Combine(librariesDirectory, nativeFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(archivePath))
            {
                continue;
            }

            ExtractArchive(archivePath, resolved.NativesDirectory, library.Extract?.Exclude);
        }
    }

    private void ExtractArchive(string archivePath, string destinationDirectory, IReadOnlyList<string>? excludes)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludes is not null &&
                    excludes.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationDirectory);
                entry.ExtractToFile(destination, true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.Warn($"解压 natives 失败：{archivePath} · {ex.Message}");
        }
    }
}

public sealed class LaunchException : Exception
{
    public LaunchException(string message)
        : base(message)
    {
    }

    public LaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

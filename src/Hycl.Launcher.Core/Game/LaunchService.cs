using System.ComponentModel;
using System.Diagnostics;
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
}

public sealed class GameSession : IDisposable
{
    private readonly Process _process;

    internal GameSession(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

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

    public void Dispose() => _process.Dispose();
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

    public async Task<GameSession> LaunchAsync(
        LaunchOptions options,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var gameDirectory = Path.GetFullPath(options.GameDirectory);
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

        var java = await ResolveJavaAsync(options, resolved, cancellationToken).ConfigureAwait(false);
        _logger.Info($"启动 {options.InstanceId}：Java {java.VersionText}（{java.ExecutablePath}）");

        var placeholders = BuildPlaceholders(options, resolved, gameDirectory, assetsDirectory, librariesDirectory, classPath);
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

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new LaunchException("无法创建游戏进程。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new LaunchException($"启动 Java 进程失败：{ex.Message}", ex);
        }

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                onOutput?.Invoke(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                onOutput?.Invoke(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _logger.Info($"游戏进程已启动，PID={process.Id}");

        return new GameSession(process);
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

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = options.PlayerName,
            ["version_name"] = options.InstanceId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = assetIndex,
            ["game_assets"] = virtualAssets,
            ["auth_uuid"] = uuid.Replace("-", "", StringComparison.Ordinal),
            ["auth_access_token"] = "0",
            ["user_type"] = "legacy",
            ["version_type"] = info.Type ?? "release",
            ["natives_directory"] = resolved.NativesDirectory,
            ["launcher_name"] = "Hycl",
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = classPath,
            ["classpath_separator"] = PlatformInfo.ClassPathSeparator,
            ["library_directory"] = librariesDirectory,
            ["resolution_width"] = width.ToString(),
            ["resolution_height"] = height.ToString(),
            ["user_properties"] = "{}",
            ["clientid"] = "",
            ["auth_xuid"] = ""
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
            $"-Xms{Math.Clamp(memory / 4, 256, 1024)}M",
            "-Dfile.encoding=UTF-8",
            "-Djava.library.path=" + nativesDirectory,
            "-Dminecraft.launcher.brand=Hycl",
            $"-Dminecraft.launcher.version={LauncherVersion}"
        };

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

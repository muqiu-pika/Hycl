using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;

namespace Hycl.Launcher.Core.Services;

/// <summary>归一后的更新信息：version.json 清单或 GitHub Releases API 解析出的统一形态。</summary>
public sealed record LauncherUpdateInfo(
    string Version,
    string? Notes,
    string? ReleaseDate,
    IReadOnlyList<string> DownloadUrls,
    long Size,
    string? Sha256,
    string AssetFileName);

/// <summary>
/// 启动器自更新（对应 PCL-CE 的 SystemUpdate 链路）：
/// 检测：version.json 清单（EdgeOne 自家域名 → jsDelivr CDN → GitHub raw，均按候选池自动回退）
///       与 GitHub Releases API 双形态，谁先返回用谁；
/// 下载：对每个候选地址自动展开直连 + 4 个 GitHub 加速代理（mirror.ghproxy / gh-proxy / ghfast / ghproxy.net），
///       走 DownloadClient 的重试 / 断点续传 / 大文件分段，SHA-256 校验；
/// 安装：写替换脚本（等待当前进程退出 → 覆盖可执行文件 → 重启）后由调用方退出应用。
/// </summary>
public sealed class LauncherUpdateService
{
    /// <summary>清单与 Releases 检测源（加速源优先）。</summary>
    private static readonly string[] ManifestUrls =
    [
        "https://hyclonline.mcapp.eu.org/version.json",
        "https://cdn.jsdelivr.net/gh/muqiu-pika/Hycl@main/version.json",
        "https://raw.githubusercontent.com/muqiu-pika/Hycl/main/version.json",
        "https://mirror.ghproxy.com/https://raw.githubusercontent.com/muqiu-pika/Hycl/main/version.json",
        "https://api.github.com/repos/muqiu-pika/Hycl/releases/latest",
        "https://mirror.ghproxy.com/https://api.github.com/repos/muqiu-pika/Hycl/releases/latest",
    ];

    private const string RepositoryDownloadBase = "https://github.com/muqiu-pika/Hycl/releases/latest/download/";

    private readonly DownloadClient _download;

    private readonly LauncherLogger _logger;

    private readonly LauncherPaths _paths;

    public LauncherUpdateService(DownloadClient download, LauncherLogger logger, LauncherPaths paths)
    {
        _download = download;
        _logger = logger;
        _paths = paths;
    }

    /// <summary>当前运行时的 RID 形态键（win-x64 / linux-arm64 / osx-arm64…），与产物命名一致。</summary>
    private static string Rid
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64"
            };
            if (OperatingSystem.IsWindows())
            {
                return $"win-{arch}";
            }

            if (OperatingSystem.IsMacOS())
            {
                return $"osx-{arch}";
            }

            return $"linux-{arch}";
        }
    }

    /// <summary>
    /// 检查更新：候选源顺序请求（加速源优先，DownloadClient 自动换源重试）。
    /// 已是最新版本返回 null。
    /// </summary>
    public async Task<LauncherUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var json = await _download.GetStringAsync(ManifestUrls, cancellationToken).ConfigureAwait(false);
        var info = Parse(json);
        if (info is null)
        {
            _logger.Info("[更新] 已是最新版本：" + LaunchService.LauncherVersion);
        }
        else
        {
            _logger.Info($"[更新] 发现新版本 {info.Version}（当前 {LaunchService.LauncherVersion}），候选源 {info.DownloadUrls.Count} 个");
        }

        return info;
    }

    /// <summary>解析 version.json 清单与 GitHub Releases API 两种形态。</summary>
    private LauncherUpdateInfo? Parse(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            throw new JsonException("更新源返回了无法解析的数据");
        }

        if (node["version"] is { } versionNode)
        {
            return ParseVersionManifest(versionNode.ToString(), node);
        }

        if (node["tag_name"] is { } tagNode)
        {
            return ParseReleaseApi(tagNode.ToString(), node);
        }

        throw new JsonException("更新源返回的数据缺少 version / tag_name 字段");
    }

    private LauncherUpdateInfo? ParseVersionManifest(string version, JsonNode root)
    {
        if (IsCurrent(version))
        {
            return null;
        }

        var asset = root["assets"]?[Rid] ?? throw new JsonException($"更新清单缺少当前平台（{Rid}）的资产");
        var urls = new List<string>();
        if (asset["url"] is JsonArray array)
        {
            urls.AddRange(array.Select(item => item?.ToString() ?? "").Where(url => url.Length > 0));
        }
        else if (asset["url"] is { } single)
        {
            urls.Add(single.ToString());
        }

        if (urls.Count == 0)
        {
            throw new JsonException("更新清单资产缺少下载地址");
        }

        return new LauncherUpdateInfo(
            Normalize(version),
            root["notes"]?.ToString(),
            root["date"]?.ToString(),
            urls,
            asset["size"]?.GetValue<long>() ?? 0,
            asset["sha256"]?.ToString(),
            Path.GetFileName(urls[0]));
    }

    private LauncherUpdateInfo? ParseReleaseApi(string tag, JsonNode root)
    {
        if (IsCurrent(tag))
        {
            return null;
        }

        var prefix = $"Hycl-{Rid}";
        string? directUrl = null;
        long size = 0;
        if (root["assets"] is JsonArray assets)
        {
            foreach (var asset in assets)
            {
                if (asset is null)
                {
                    continue;
                }

                var name = asset["name"]?.ToString() ?? "";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && asset["browser_download_url"] is { } assetUrl)
                {
                    directUrl = assetUrl.ToString();
                    size = asset["size"]?.GetValue<long>() ?? 0;
                    break;
                }
            }
        }

        directUrl ??= RepositoryDownloadBase + $"{prefix}.exe";
        return new LauncherUpdateInfo(
            Normalize(tag),
            root["body"]?.ToString(),
            root["published_at"]?.ToString(),
            BuildAcceleratedUrls(directUrl),
            size,
            null,
            $"{prefix}{(OperatingSystem.IsWindows() ? ".exe" : "")}");
    }

    /// <summary>版本号比较（去掉 v 前缀后与当前版本一致视为最新）。</summary>
    private static bool IsCurrent(string version)
        => Normalize(version).Equals(LaunchService.LauncherVersion, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version) => version.Trim().TrimStart('v', 'V').Trim();

    /// <summary>
    /// 为 GitHub 资产地址展开加速源池：4 个常用加速代理在前、直连兜底，
    /// DownloadFileAsync 会按顺序自动换源 + 断点续传 + 大文件分段。
    /// </summary>
    private static IReadOnlyList<string> BuildAcceleratedUrls(string url)
        =>
        [
            "https://mirror.ghproxy.com/" + url,
            "https://gh-proxy.com/" + url,
            "https://ghfast.top/" + url,
            "https://ghproxy.net/" + url,
            url
        ];

    /// <summary>下载更新包到缓存目录（SHA-256 校验，多源自动切换），返回本地文件路径。</summary>
    public async Task<string> DownloadUpdateAsync(
        LauncherUpdateInfo info,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_paths.CacheDirectory, "update");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, info.AssetFileName);
        _logger.Info($"[更新] 开始下载 {info.Version} → {destination}");
        await _download
            .DownloadFileAsync(info.DownloadUrls, destination, info.Size > 0 ? info.Size : null, info.Sha256, progress, cancellationToken)
            .ConfigureAwait(false);
        return destination;
    }

    /// <summary>
    /// 安装更新：写「等待当前进程退出 → 覆盖 → 重启」的替换脚本并启动；
    /// 调用方在返回后应尽快退出启动器（脚本会等进程完全退出再覆盖）。
    /// </summary>
    public void InstallUpdate(string downloadedFile)
    {
        var currentExe = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName
                         ?? throw new InvalidOperationException("无法定位当前启动器可执行文件");
        var pid = Environment.ProcessId;
        downloadedFile = Path.GetFullPath(downloadedFile);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"hycl-update-{Guid.NewGuid():N}.cmd");
            var content = $"""
                @echo off
                :wait
                tasklist /fi "PID eq {pid}" | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                move /y "{downloadedFile}" "{currentExe}" >nul
                start "" "{currentExe}"
                del "%~f0"
                """;
            File.WriteAllText(scriptPath, content);
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        else
        {
            var scriptPath = Path.Combine(_paths.CacheDirectory, "hycl-update.sh");
            var content = $"""
                #!/bin/sh
                while kill -0 {pid} 2>/dev/null; do sleep 1; done
                mv -f "{downloadedFile}" "{currentExe}"
                chmod +x "{currentExe}"
                (nohup "{currentExe}" >/dev/null 2>&1 &)
                rm -f -- "$0"
                """;
            File.WriteAllText(scriptPath, content);
            try
            {
                File.SetUnixFileMode(scriptPath,
                    File.GetUnixFileMode(scriptPath)
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute);
            }
            catch (PlatformNotSupportedException)
            {
                // 旧平台无 SetUnixFileMode，交给 sh 显式解释执行
            }

            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        _logger.Info($"[更新] 替换脚本已启动：{downloadedFile} → {currentExe}");
    }
}

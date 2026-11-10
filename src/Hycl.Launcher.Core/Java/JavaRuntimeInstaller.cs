using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Java;

/// <summary>自动下载安装 Java 的结果：三态区分（成功 / 下载失败 / 清单里没有可用运行时）。</summary>
public sealed record JavaRuntimeInstallOutcome(string? Directory, string? Error)
{
    public bool Success => Directory is not null;
}

/// <summary>
/// 缺少所需版本的 Java 时自动下载安装，数据源与 PCL-CE、HMCL 一致：
/// Mojang piston-meta 的 java-runtime 清单（BMCLAPI 镜像回退），逐文件按 SHA-1 校验下载到暂存目录，
/// 完成后整体移动到 HYCL\java\&lt;组件&gt;-&lt;版本&gt;，供 JavaDetector 全局扫描发现。
/// 取消或失败都会清理暂存目录，保证再次启动可以重新下载。
/// </summary>
public sealed class JavaRuntimeInstaller
{
    /// <summary>官方索引（PCL-CE Download.cs 与 HMCL MojangJavaDownloadTask 使用同一地址）。</summary>
    private const string IndexUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private const string MirrorHost = "bmclapi2.bangbang93.com";

    private const string OfficialHost = "piston-meta.mojang.com";

    /// <summary>版本名取开头数字作为主版本（8u51-cacert… → 8，25.0.1 → 25）。</summary>
    private static readonly Regex LeadingNumber = new(@"^\d+", RegexOptions.Compiled);

    private readonly DownloadClient _download;

    private readonly LauncherLogger _logger;

    private readonly string _rootDirectory;

    private readonly string _stagingRootDirectory;

    public JavaRuntimeInstaller(DownloadClient download, LauncherLogger logger, LauncherPaths paths)
    {
        _download = download;
        _logger = logger;
        _rootDirectory = Path.Combine(paths.DataDirectory, "java");
        // 暂存目录放在 java 之外：java\ 会被 JavaDetector 全局扫描，半成品不该出现在候选里
        _stagingRootDirectory = Path.Combine(paths.DataDirectory, "java-staging");
    }

    /// <summary>托管 Java 的根目录（HYCL\java），会作为全局候选被 JavaDetector 扫描。</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>
    /// 确保 requiredMajor 及以上的 Java 可用：已安装直接返回目录，否则下载安装。
    /// 目录与错误都为空表示清单里没有满足要求的运行时（不视为失败）。
    /// </summary>
    public async Task<JavaRuntimeInstallOutcome> EnsureAsync(
        int requiredMajor,
        Action<string>? onStage,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlatformKey(out var platformKey))
        {
            _logger.Info("[Java] 当前平台没有 Mojang 运行时清单，无法自动下载");
            return new JavaRuntimeInstallOutcome(null, null);
        }

        _logger.Info($"[Java] 缺少 Java {requiredMajor}+，开始自动下载安装");
        onStage?.Invoke("正在获取 Java 运行时清单…");
        string name;
        string version;
        int major;
        string manifestUrl;
        try
        {
            var indexJson = await _download
                .GetStringAsync(new[] { IndexUrl, ToMirror(IndexUrl) }, cancellationToken)
                .ConfigureAwait(false);
            using var index = JsonDocument.Parse(indexJson);
            if (!index.RootElement.TryGetProperty(platformKey, out var products))
            {
                _logger.Warn($"[Java] 运行时清单缺少平台条目：{platformKey}");
                return new JavaRuntimeInstallOutcome(null, null);
            }

            var selected = SelectProduct(products, requiredMajor);
            if (selected is null)
            {
                _logger.Info($"[Java] Mojang 未提供 Java {requiredMajor} 或更高的运行时");
                return new JavaRuntimeInstallOutcome(null, null);
            }

            (name, version, major, manifestUrl) = selected.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("[Java] 获取运行时清单失败：" + ex.Message);
            return new JavaRuntimeInstallOutcome(null, $"获取运行时清单失败：{ex.Message}");
        }

        var folderName = SanitizeFolderName($"{name}-{version}");
        var finalDirectory = Path.Combine(_rootDirectory, folderName);
        var executableName = PlatformInfo.IsWindows ? "java.exe" : "java";
        if (File.Exists(Path.Combine(finalDirectory, "bin", executableName)))
        {
            // 之前已装好：交给调用方重新扫描识别，不重复下载
            return new JavaRuntimeInstallOutcome(finalDirectory, null);
        }

        var stagingDirectory = Path.Combine(_stagingRootDirectory, folderName + ".tmp");
        try
        {
            var manifestJson = await _download
                .GetStringAsync(new[] { manifestUrl, ToMirror(manifestUrl) }, cancellationToken)
                .ConfigureAwait(false);
            var files = ParseFiles(manifestJson);
            if (files.Count == 0)
            {
                _logger.Warn("[Java] 运行时清单没有可下载的文件");
                return new JavaRuntimeInstallOutcome(null, "运行时清单为空");
            }

            TryDeleteDirectory(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var done = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    done++;

                    // 阶段文案带进度，启动过程页据此展示「x/N」，避免长时间只有转圈
                    if (done == 1 || done % 5 == 0 || done == files.Count)
                    {
                        onStage?.Invoke($"正在下载 Java 运行环境（Java {major}）… {done}/{files.Count}");
                    }

                    var relative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative) ||
                        relative.Split(Path.DirectorySeparatorChar).Contains(".."))
                    {
                        throw new DownloadException($"运行时清单包含非法路径：{file.RelativePath}");
                    }

                    var destination = Path.Combine(stagingDirectory, relative);
                    await _download
                        .DownloadFileAsync(file.Url, destination, file.Size, file.Sha1, null, cancellationToken)
                        .ConfigureAwait(false);
                    if (!File.Exists(destination))
                    {
                        throw new DownloadException($"Java 运行时文件下载失败：{file.RelativePath}");
                    }

                    if (file.Executable && !OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(
                            destination,
                            File.GetUnixFileMode(destination)
                            | UnixFileMode.UserExecute
                            | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherExecute);
                    }
                }

                if (!File.Exists(Path.Combine(stagingDirectory, "bin", executableName)))
                {
                    throw new DownloadException("下载的 Java 运行时缺少 bin/" + executableName);
                }

                onStage?.Invoke($"正在安装 Java 运行环境（Java {major}）…");
                Directory.CreateDirectory(_rootDirectory);
                if (Directory.Exists(finalDirectory))
                {
                    // 旧版本号的半成品/被判定损坏的安装：整体重装
                    Directory.Delete(finalDirectory, true);
                }

                Directory.Move(stagingDirectory, finalDirectory);
            }
            catch
            {
                // 取消与失败都清理暂存目录：不残留半成品，下次启动重新下载
                TryDeleteDirectory(stagingDirectory);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("[Java] 下载 Java 已取消，暂存文件已清理");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("[Java] 自动下载 Java 失败：" + ex.Message);
            return new JavaRuntimeInstallOutcome(null, $"自动下载失败：{ex.Message}");
        }

        _logger.Info($"[Java] 已自动安装 {name} {version} → {finalDirectory}");
        return new JavaRuntimeInstallOutcome(finalDirectory, null);
    }

    /// <summary>清单里挑满足主版本要求的运行时：主版本升序优先，同版本避开 snapshot 组件。</summary>
    private static (string Name, string Version, int Major, string ManifestUrl)? SelectProduct(
        JsonElement products,
        int requiredMajor)
    {
        (string Name, string Version, int Major, string ManifestUrl)? best = null;
        var bestIsSnapshot = false;
        foreach (var product in products.EnumerateObject())
        {
            // all.json 里每个产品是长度 1 的数组（gamecore 等平台是空数组），先取出条目本体
            var entry = Unwrap(product.Value);
            if (product.Name.Contains("minecraft-java-exe", StringComparison.OrdinalIgnoreCase) ||
                entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetProperty("name", out var versionNameElement) ||
                !entry.TryGetProperty("manifest", out var manifestElement) ||
                !manifestElement.TryGetProperty("url", out var manifestUrlElement))
            {
                continue;
            }

            var versionName = versionNameElement.GetString() ?? "";
            var match = LeadingNumber.Match(versionName);
            if (!match.Success || !int.TryParse(match.Value, out var major) || major < requiredMajor)
            {
                continue;
            }

            var manifestUrl = manifestUrlElement.GetString();
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                continue;
            }

            var isSnapshot = product.Name.Contains("snapshot", StringComparison.OrdinalIgnoreCase);
            var candidate = (product.Name, versionName, major, manifestUrl);
            if (best is null ||
                major < best.Value.Major ||
                (major == best.Value.Major && bestIsSnapshot && !isSnapshot))
            {
                best = candidate;
                bestIsSnapshot = isSnapshot;
            }
        }

        return best;
    }

    private sealed record RuntimeFile(string RelativePath, string Sha1, long Size, string Url, bool Executable);

    /// <summary>解析组件清单：只取 type=file 且带 raw 下载的条目（与 PCL-CE CreateJavaDownloadFiles 一致）。</summary>
    private static List<RuntimeFile> ParseFiles(string manifestJson)
    {
        var files = new List<RuntimeFile>();
        using var manifest = JsonDocument.Parse(manifestJson);
        if (!manifest.RootElement.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Object)
        {
            return files;
        }

        foreach (var file in filesElement.EnumerateObject())
        {
            var entry = Unwrap(file.Value);
            if (entry.ValueKind != JsonValueKind.Object ||
                (entry.TryGetProperty("type", out var typeElement) &&
                 !string.Equals(typeElement.GetString(), "file", StringComparison.Ordinal)) ||
                !entry.TryGetProperty("downloads", out var downloadsElement) ||
                !downloadsElement.TryGetProperty("raw", out var rawElement) ||
                !rawElement.TryGetProperty("url", out var urlElement) ||
                !rawElement.TryGetProperty("sha1", out var sha1Element) ||
                !rawElement.TryGetProperty("size", out var sizeElement))
            {
                continue;
            }

            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var executable = entry.TryGetProperty("executable", out var executableElement) &&
                             executableElement.ValueKind == JsonValueKind.True;
            files.Add(new RuntimeFile(
                file.Name,
                sha1Element.GetString() ?? "",
                sizeElement.GetInt64(),
                url,
                executable));
        }

        return files;
    }

    /// <summary>清单条目既可能是对象，也可能是长度 1 的数组（all.json 的产品就是数组），统一取条目本体。</summary>
    private static JsonElement Unwrap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.GetArrayLength() > 0 ? element[0] : default;
        }

        return element;
    }

    /// <summary>清单地址在官方与 BMCLAPI 镜像之间做主机替换（路径完全一致，PCL-CE 同款做法）。</summary>
    private static string ToMirror(string url) => url.Replace(OfficialHost, MirrorHost, StringComparison.Ordinal);

    private static bool TryGetPlatformKey(out string platformKey)
    {
        platformKey = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsWindows() => "windows-x64",
            Architecture.X86 when OperatingSystem.IsWindows() => "windows-x86",
            Architecture.Arm64 when OperatingSystem.IsWindows() => "windows-arm64",
            Architecture.X64 when OperatingSystem.IsMacOS() => "mac-os",
            Architecture.Arm64 when OperatingSystem.IsMacOS() => "mac-os-arm64",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux",
            _ => ""
        };
        return platformKey.Length > 0;
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录被占用时留到下次启动清理，不阻断当前流程
        }
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Java;

/// <summary>一个可用的 Java 运行时。</summary>
public sealed class JavaInstallation
{
    public JavaInstallation(
        string executablePath,
        int majorVersion,
        string versionText,
        string vendor,
        string architecture,
        bool is64Bit)
    {
        ExecutablePath = executablePath;
        MajorVersion = majorVersion;
        VersionText = versionText;
        Vendor = vendor;
        Architecture = architecture;
        Is64Bit = is64Bit;
    }

    public string ExecutablePath { get; }

    public int MajorVersion { get; }

    public string VersionText { get; }

    public string Vendor { get; }

    public string Architecture { get; }

    public bool Is64Bit { get; }

    public string HomePath
    {
        get
        {
            var binDirectory = Path.GetDirectoryName(ExecutablePath);
            return binDirectory is null ? ExecutablePath : Path.GetDirectoryName(binDirectory) ?? binDirectory;
        }
    }

    public string DisplayName => $"Java {MajorVersion} ({Architecture}) · {Vendor}";

    public string Detail => $"{VersionText} · {ExecutablePath}";
}

/// <summary>
/// 跨平台 Java 探测：JAVA_HOME、PATH、常见安装目录与 Minecraft 自带运行时。
/// 不使用 Windows 注册表，保证 macOS/Linux 可运行。
/// </summary>
public sealed class JavaDetector
{
    private static readonly Regex VersionPropertyRegex = new(@"java\.version\s*=\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex VersionQuotedRegex = new("version \"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ArchPropertyRegex = new(@"os\.arch\s*=\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex VendorPropertyRegex = new(@"java\.vendor\s*=\s*(.+)", RegexOptions.Compiled);

    private readonly LauncherLogger _logger;

    public JavaDetector(LauncherLogger logger)
    {
        _logger = logger;
    }

    /// <summary>探测本机 Java；显式路径优先，去重后按主版本降序。</summary>
    public async Task<IReadOnlyList<JavaInstallation>> DetectAsync(
        string? explicitPath = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? extraRoots = null)
    {
        var candidates = CollectCandidates(explicitPath, extraRoots);
        return await InspectCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只扫描指定根目录（如当前游戏目录的 runtime/），供启动时增量补充。</summary>
    public async Task<IReadOnlyList<JavaInstallation>> DetectInRootsAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
            {
                ScanDirectory(root, 4, path => AddResolved(path, candidates));
            }
        }

        return await InspectCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JavaInstallation>> InspectCandidatesAsync(
        HashSet<string> candidates,
        CancellationToken cancellationToken)
    {
        var found = new ConcurrentBag<JavaInstallation>();
        using var gate = new SemaphoreSlim(4);

        var tasks = candidates.Select(async path =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var installation = await InspectAsync(path, cancellationToken).ConfigureAwait(false);
                if (installation is not null)
                {
                    found.Add(installation);
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return found
            .GroupBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(x => x.MajorVersion)
            .ThenBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>检查单个 Java 可执行文件。</summary>
    public async Task<JavaInstallation?> InspectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveExecutable(executablePath);
        if (fullPath is null)
        {
            return null;
        }

        var result = await RunAsync(fullPath, new[] { "-XshowSettings:properties", "-version" }, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var text = result.Value.Output;
        var versionText = MatchValue(VersionPropertyRegex, text) ?? MatchValue(VersionQuotedRegex, text);
        if (versionText is null)
        {
            return null;
        }

        var major = ParseMajorVersion(versionText);
        if (major <= 0)
        {
            return null;
        }

        var arch = MatchValue(ArchPropertyRegex, text) ?? "unknown";
        var vendor = MatchValue(VendorPropertyRegex, text)?.Trim() ?? "Unknown";
        var is64Bit = arch.Contains("64", StringComparison.OrdinalIgnoreCase);
        var normalizedArch = is64Bit ? "64-bit" : "32-bit";

        return new JavaInstallation(fullPath, major, versionText, vendor, normalizedArch, is64Bit);
    }

    /// <summary>根据 Minecraft 版本推断最低 Java 主版本。</summary>
    public static int RequiredMajorVersion(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.');
        if (parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var minor))
        {
            var patch = parts.Length >= 3 ? int.Parse(new string(parts[2].TakeWhile(char.IsDigit).ToArray())) : 0;
            if (minor >= 21 || (minor == 20 && patch >= 5))
            {
                return 21;
            }

            if (minor >= 18)
            {
                return 17;
            }

            if (minor == 17)
            {
                return 16;
            }

            return 8;
        }

        // 快照或无法识别的版本，按现代版本处理
        return 17;
    }

    /// <summary>在候选列表中挑选最合适的 Java。</summary>
    public static JavaInstallation? FindBest(IEnumerable<JavaInstallation> installations, int requiredMajor)
    {
        var list = installations.ToList();
        var adequate = list.Where(x => x.MajorVersion >= requiredMajor)
            .OrderBy(x => x.MajorVersion)
            .FirstOrDefault();
        return adequate ?? list.OrderByDescending(x => x.MajorVersion).FirstOrDefault();
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int ParseMajorVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith("1.", StringComparison.Ordinal))
        {
            var rest = normalized[2..];
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var legacy) ? legacy : 0;
        }

        var leading = new string(normalized.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(leading, out var major) ? major : 0;
    }

    private static string? ResolveExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Directory.Exists(expanded))
        {
            expanded = Path.Combine(expanded, "bin", PlatformInfo.JavaExecutableName);
        }

        if (!File.Exists(expanded))
        {
            return null;
        }

        var fileName = Path.GetFileName(expanded);
        return fileName.Equals("java", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(expanded)
            : null;
    }

    private HashSet<string> CollectCandidates(string? explicitPath, IEnumerable<string>? extraRoots = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddExecutable(string? path) => AddResolved(path, candidates);

        AddExecutable(explicitPath);
        AddExecutable(Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome
            ? Path.Combine(javaHome, "bin", PlatformInfo.JavaExecutableName)
            : null);

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            AddExecutable(Path.Combine(directory.Trim('"'), PlatformInfo.JavaExecutableName));
        }

        foreach (var root in GetSearchRoots().Concat(extraRoots ?? Enumerable.Empty<string>()))
        {
            if (Directory.Exists(root))
            {
                ScanDirectory(root, 3, AddExecutable);
            }
        }

        _logger.Info($"Java 候选路径：{candidates.Count} 个");
        return candidates;
    }

    private static void AddResolved(string? path, HashSet<string> candidates)
    {
        var resolved = ResolveExecutable(path ?? "");
        if (resolved is not null)
        {
            candidates.Add(resolved);
        }
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        // 启动器自动下载安装的 Java（HYCL\java，JavaRuntimeInstaller 的落点）
        var launcherJavaRoot = Path.Combine(PlatformInfo.LauncherDataDirectory, "java");
        if (Directory.Exists(launcherJavaRoot))
        {
            yield return launcherJavaRoot;
        }

        var home = PlatformInfo.GetUserHome();
        if (PlatformInfo.IsWindows)
        {
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (string.IsNullOrWhiteSpace(programFiles))
                {
                    continue;
                }

                yield return Path.Combine(programFiles, "Java");
                yield return Path.Combine(programFiles, "Eclipse Adoptium");
                yield return Path.Combine(programFiles, "Microsoft");
                yield return Path.Combine(programFiles, "Amazon Corretto");
                yield return Path.Combine(programFiles, "Zulu");
                yield return Path.Combine(programFiles, "BellSoft");
                yield return Path.Combine(programFiles, "Semeru");
                yield return Path.Combine(programFiles, "JetBrains");
                yield return Path.Combine(programFiles, "GraalVM");
            }

            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium");
            yield return Path.Combine(home, ".jdks");
            yield return Path.Combine(home, ".gradle", "jdks");
            yield return Path.Combine(home, "scoop", "apps");
            yield return Path.Combine(home, ".minecraft", "runtime");
            yield return Path.Combine(home, ".lunarclient", "jre");

            // 其他启动器托管的运行时（目录不存在时自动跳过）
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // 微软商店版 Minecraft 的运行时
            yield return Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");
            yield return Path.Combine(localAppData, "PrismLauncher", "java");
            yield return Path.Combine(roamingAppData, "PrismLauncher", "java");
            yield return Path.Combine(localAppData, "com.modrinth.theseus", "cache", "java");
            yield return Path.Combine(roamingAppData, "com.modrinth.theseus", "cache", "java");
            yield return Path.Combine(home, "curseforge", "minecraft", "Install", "runtime");
            yield break;
        }

        if (PlatformInfo.IsMacOS)
        {
            yield return "/Library/Java/JavaVirtualMachines";
            yield return "/System/Library/Java/JavaVirtualMachines";
            yield return Path.Combine(home, "Library", "Java", "JavaVirtualMachines");
            yield return "/opt/homebrew/opt";
            yield return "/usr/local/opt";
            yield return Path.Combine(home, ".sdkman", "candidates", "java");
            yield return Path.Combine(home, ".jdks");
            yield return Path.Combine(home, ".minecraft", "runtime");
            yield return Path.Combine(home, "Library", "Application Support", "minecraft", "runtime");
            yield return Path.Combine(home, "Library", "Application Support", "PrismLauncher", "java");
            yield break;
        }

        yield return "/usr/lib/jvm";
        yield return "/usr/java";
        yield return "/usr/lib64/jvm";
        yield return Path.Combine(home, ".sdkman", "candidates", "java");
        yield return Path.Combine(home, ".jdks");
        yield return Path.Combine(home, ".minecraft", "runtime");
        yield return Path.Combine(home, ".lunarclient", "jre");
        yield return Path.Combine(home, ".var", "app", "com.mojang.Minecraft", "data", "minecraft", "runtime");
        yield return "/opt";
    }

    private static void ScanDirectory(string root, int depth, Action<string> addExecutable)
    {
        try
        {
            // 先查当前层的 bin 再决定是否继续下钻：深度耗尽也不漏掉叶层的可执行文件
            addExecutable(Path.Combine(root, "bin", PlatformInfo.JavaExecutableName));
            addExecutable(Path.Combine(root, "Contents", "Home", "bin", PlatformInfo.JavaExecutableName));

            if (depth <= 0)
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(child);
                // /opt 等大目录仅扫描名字像 JDK 的子目录
                if (root is "/opt" or "/usr/local/opt" or "/opt/homebrew/opt"
                    && !name.Contains("jdk", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("java", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("temurin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ScanDirectory(child, depth - 1, addExecutable);
            }
        }
        catch (IOException)
        {
            // 无权限/目录变化时跳过
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略不可读目录
        }
    }

    private async Task<(int ExitCode, string Output)?> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            _logger.Warn($"无法读取 Java 信息：{executablePath}（{ex.Message}）");
            return null;
        }
    }
}

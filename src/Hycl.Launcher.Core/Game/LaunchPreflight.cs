using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Java;

namespace Hycl.Launcher.Core.Game;

/// <summary>单项启动前检查的结论。</summary>
public enum LaunchCheckStatus
{
    Passed,
    Failed
}

/// <summary>
/// 一项启动前检查：标题用于列表展示，详情说明实际发现，建议给出修正动作。
/// 纯数据，不依赖 UI，便于在 Core 内单测与复用。
/// </summary>
public sealed record LaunchCheckItem(
    string Key,
    string Title,
    string Detail,
    string Suggestion,
    LaunchCheckStatus Status)
{
    public bool IsFailed => Status == LaunchCheckStatus.Failed;
}

/// <summary>
/// 启动前检查结果。检查项全部执行完再返回（不在第一处失败就短路），
/// 以便启动过程页能一次列出所有需要修正的问题。
/// </summary>
public sealed record LaunchPreflightResult(
    IReadOnlyList<LaunchCheckItem> Items,
    string? JavaSummary = null,
    int? RequiredJavaMajor = null)
{
    public bool Passed => Items.All(x => !x.IsFailed);

    public IReadOnlyList<LaunchCheckItem> FailedItems =>
        Items.Where(x => x.IsFailed).ToArray();
}

/// <summary>
/// 对应 PCL-CE 启动前的「检查」阶段：版本文件 / 资源与依赖库 / Java 与内存 / 账号 / 游戏目录可写。
/// 任何一项不通过都不进入「启动进程」，由启动过程页展示原因并允许返回修正。
/// </summary>
public static class LaunchPreflight
{
    private const int MinMemoryMegabytes = 512;

    /// <summary>检查顺序与启动过程页的前三步一致（1 版本文件，2 资源与依赖库，3 Java 与内存）。</summary>
    public static async Task<LaunchPreflightResult> RunAsync(
        LaunchOptions options,
        PlayerAccount? account,
        VersionResolver resolver,
        JavaDetector javaDetector,
        Action<int>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<LaunchCheckItem>();
        var gameDirectory = Path.GetFullPath(options.GameDirectory);

        onStep?.Invoke(1);
        items.Add(CheckGameDirectory(gameDirectory));
        items.Add(CheckAccount(account));

        var resolved = await TryResolveAsync(resolver, gameDirectory, options.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        items.Add(CheckVersionFiles(options.InstanceId, resolved));

        // 版本文件缺失时后续检查没有意义（无法确定 assets 索引与所需 Java）
        if (resolved is null)
        {
            return new LaunchPreflightResult(items);
        }

        onStep?.Invoke(2);
        items.Add(CheckResources(resolved, gameDirectory, options.WindowType));

        onStep?.Invoke(3);
        var requiredJava = resolved.Info.JavaVersion?.MajorVersion
                           ?? JavaDetector.RequiredMajorVersion(resolved.RootId);
        var java = await ResolveJavaAsync(options, javaDetector, requiredJava, cancellationToken)
            .ConfigureAwait(false);
        items.Add(CheckJava(options.JavaPath, java, requiredJava));
        items.Add(CheckMemory(options));
        return new LaunchPreflightResult(items, DescribeJava(java), requiredJava);
    }

    private static async Task<ResolvedVersion?> TryResolveAsync(
        VersionResolver resolver,
        string gameDirectory,
        string instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resolver.ResolveAsync(gameDirectory, instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // 版本 json 损坏按「版本文件不完整」处理，不让异常冒泡到启动流程
            return null;
        }
    }

    private static LaunchCheckItem CheckVersionFiles(string instanceId, ResolvedVersion? resolved)
    {
        if (resolved is null)
        {
            return Failed(
                "version",
                "版本文件完整性",
                $"版本「{instanceId}」的 json 缺失或已损坏。",
                "在「下载」页重新安装该版本；若是自己放入的版本，请确认 versions 目录下 json 与 jar 齐全。");
        }

        if (!File.Exists(resolved.ClientJarPath))
        {
            return Failed(
                "version",
                "版本文件完整性",
                $"缺少客户端核心文件：{resolved.ClientJarPath}",
                "在「下载」页对该版本执行补全/重新安装。");
        }

        return Passed("version", "版本文件完整性", $"已找到 {resolved.InstanceId} 的版本 json 与客户端 jar。");
    }

    private static LaunchCheckItem CheckResources(ResolvedVersion resolved, string gameDirectory, int windowType)
    {
        var info = resolved.Info;
        var assetsIndexId = info.AssetIndex?.Id ?? info.Assets;
        if (!string.IsNullOrWhiteSpace(assetsIndexId)
            && !File.Exists(Path.Combine(gameDirectory, "assets", "indexes", assetsIndexId + ".json")))
        {
            return Failed(
                "assets",
                "资源文件索引",
                $"缺少资源索引 assets/indexes/{assetsIndexId}.json。",
                "在「下载」页对该版本执行「补全资源」；离线导入的版本需要补齐 assets。");
        }

        // 依赖库逐个确认存在：缺库在启动时会直接抛异常，这里提前拦下
        var environment = RuleEnvironment.Current(windowType == 3);
        IReadOnlyList<LibraryFile> libraries;
        try
        {
            libraries = LibraryResolver.CollectLibraries(info, environment, DownloadPolicy.Default);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Failed(
                "libraries",
                "依赖库完整性",
                $"解析依赖库列表失败：{ex.Message}",
                "在「下载」页重新安装该版本。");
        }

        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var missing = libraries
            .Where(x => !File.Exists(Path.Combine(
                librariesDirectory,
                x.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .Select(x => x.RelativePath)
            .ToArray();
        if (missing.Length > 0)
        {
            return Failed(
                "libraries",
                "依赖库完整性",
                $"缺少 {missing.Length} 个依赖库，例如 {missing[0]}。",
                "在「下载」页对该版本执行补全/重新安装，安装完成后再启动。");
        }

        return Passed(
            "libraries",
            "依赖库完整性",
            $"资源索引与 {libraries.Count} 个依赖库均已就位。");
    }

    private static async Task<JavaInstallation?> ResolveJavaAsync(
        LaunchOptions options,
        JavaDetector javaDetector,
        int requiredMajor,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.JavaPath))
        {
            return await javaDetector.InspectAsync(options.JavaPath, cancellationToken).ConfigureAwait(false);
        }

        var installations = await javaDetector.DetectAsync(null, cancellationToken).ConfigureAwait(false);
        return JavaDetector.FindBest(installations, requiredMajor);
    }

    private static LaunchCheckItem CheckJava(string? configuredPath, JavaInstallation? java, int requiredMajor)
    {
        if (java is null)
        {
            return string.IsNullOrWhiteSpace(configuredPath)
                ? Failed(
                    "java",
                    "Java 运行环境",
                    $"未检测到可用的 Java（该版本需要 Java {requiredMajor} 或更高）。",
                    "安装对应版本的 Java（1.20.5 及以上建议 21，1.17~1.20.4 建议 17），"
                    + "或在「设置 → Java 设置」手动指定 javaw.exe。")
                : Failed(
                    "java",
                    "Java 运行环境",
                    $"指定的 Java 无法使用：{configuredPath}",
                    "在「设置 → Java 设置」重新检测，或关闭「自动选择 Java」后指定有效的 javaw.exe。");
        }

        if (java.MajorVersion < requiredMajor)
        {
            return Failed(
                "java",
                "Java 运行环境",
                $"当前 Java {java.MajorVersion} 低于该版本要求的 Java {requiredMajor}。",
                $"改装 Java {requiredMajor}（或更高版本），并在「设置 → Java 设置」指定它。");
        }

        return Passed("java", "Java 运行环境", DescribeJava(java)!);
    }

    private static LaunchCheckItem CheckMemory(LaunchOptions options)
    {
        var memory = options.MemoryMegabytes;
        if (memory <= 0)
        {
            return Failed(
                "memory",
                "内存分配",
                "未设置游戏内存（-Xmx）。",
                "在「设置 → 游戏管理」设置最大内存（建议 2048~4096 MB）。");
        }

        if (memory < MinMemoryMegabytes)
        {
            return Failed(
                "memory",
                "内存分配",
                $"最大内存 {memory} MB 过小，游戏无法加载。",
                $"把最大内存提高到至少 {MinMemoryMegabytes} MB（建议 2048 MB 以上）。");
        }

        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L));
        if (totalMb > 0 && memory > totalMb)
        {
            return Failed(
                "memory",
                "内存分配",
                $"最大内存 {memory} MB 超过本机可用物理内存（约 {totalMb} MB）。",
                "下调最大内存，或关闭占用内存的程序后重试。");
        }

        return Passed("memory", "内存分配", $"最大内存 {memory} MB · 初始内存 {options.InitialMemoryMegabytes} MB。");
    }

    private static LaunchCheckItem CheckAccount(PlayerAccount? account)
    {
        if (account is null)
        {
            return Failed(
                "account",
                "登录账号",
                "未选择账号。",
                "在启动页「添加账号」选择一个正版 / 皮肤站 / 离线账号后再启动。");
        }

        if (account.Type == "offline")
        {
            return System.Text.RegularExpressions.Regex.IsMatch(account.Name, "^[A-Za-z0-9_]{3,16}$")
                ? Passed("account", "登录账号", $"离线登录 / {account.Name}")
                : Failed(
                    "account",
                    "登录账号",
                    $"离线玩家名「{account.Name}」不合法。",
                    "离线名只能包含字母、数字与下划线，长度 3~16；请在档案中重命名后再启动。");
        }

        if (account.IsAuthlib && (string.IsNullOrWhiteSpace(account.Server)
                                    || string.IsNullOrWhiteSpace(account.Name)))
        {
            return Failed(
                "account",
                "登录账号",
                "皮肤站账号信息不完整（缺少站点地址或角色名）。",
                "重新登录皮肤站账号后再启动。");
        }

        if (account.IsTokenNearExpiry)
        {
            // 令牌过期不算失败：启动流程会先静默续期，续期失败再中止
            return Passed("account", "登录账号", $"{TypeLabel(account)} / {account.Name}（令牌即将到期，启动前自动续期）");
        }

        return Passed("account", "登录账号", $"{TypeLabel(account)} / {account.Name}");
    }

    private static string TypeLabel(PlayerAccount account) => account.Type switch
    {
        "msa" => "正版登录",
        "authlib" => "皮肤站登录",
        _ => "离线登录"
    };

    private static LaunchCheckItem CheckGameDirectory(string gameDirectory)
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);
            var probe = Path.Combine(gameDirectory, ".hycl-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Passed("directory", "游戏目录", $"可写入：{gameDirectory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(
                "directory",
                "游戏目录",
                $"游戏目录不可写：{gameDirectory}（{ex.Message}）",
                "把游戏目录改到有写入权限的位置（如用户目录下的 .minecraft），或修复该目录的权限。");
        }
    }

    private static string? DescribeJava(JavaInstallation? java) =>
        java is null ? null : $"Java {java.MajorVersion} · {java.ExecutablePath}";

    private static LaunchCheckItem Passed(string key, string title, string detail) =>
        new(key, title, detail, "", LaunchCheckStatus.Passed);

    private static LaunchCheckItem Failed(string key, string title, string detail, string suggestion) =>
        new(key, title, detail, suggestion, LaunchCheckStatus.Failed);
}

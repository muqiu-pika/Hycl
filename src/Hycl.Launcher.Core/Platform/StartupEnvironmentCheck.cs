using System.Runtime.InteropServices;

namespace Hycl.Launcher.Core.Platform;

/// <summary>启动前环境问题的类别。</summary>
public enum StartupEnvironmentIssueKind
{
    /// <summary>缺少或版本过低的 .NET 运行时（阻断启动）。</summary>
    DotNetRuntime,

    /// <summary>Windows 版本低于建议下限。</summary>
    WindowsVersion,

    /// <summary>系统不是 64 位。</summary>
    SystemArchitecture,

    /// <summary>启动器位于临时目录或聊天软件目录。</summary>
    UnsafeLocation
}

/// <summary>单条环境问题（<see cref="Detail"/> 为可用于提示的原始信息，文案由 UI 负责）。</summary>
public readonly record struct StartupEnvironmentIssue(StartupEnvironmentIssueKind Kind, string Detail);

/// <summary>环境检查结果。</summary>
public sealed class StartupEnvironmentReport
{
    public StartupEnvironmentReport(IReadOnlyList<StartupEnvironmentIssue> issues)
    {
        Issues = issues;
    }

    public IReadOnlyList<StartupEnvironmentIssue> Issues { get; }

    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// 启动前运行环境检查，对齐 PCL-CE 的启动环境提示：Windows 版本、32 位系统、
/// 临时目录与聊天软件目录都会给出提示；.NET 运行时缺失/过低属于阻断项。
/// 逻辑停留在 Core，提示文案与交互由 UI 负责。
/// </summary>
public static class StartupEnvironmentCheck
{
    /// <summary>与 PCL-CE 相同的 .NET 下载页。</summary>
    public const string DotNetDownloadUrl = "https://get.dot.net/10";

    /// <summary>本项目（net10.0）要求的最低 .NET 运行时主版本。</summary>
    public const int RequiredDotNetMajorVersion = 10;

    /// <summary>与 PCL-CE 一致的 Windows 版本下限（Windows 10 1809）。</summary>
    public const int MinimumWindowsBuild = 17763;

    public static StartupEnvironmentReport Evaluate(string? executablePath = null)
    {
        var issues = new List<StartupEnvironmentIssue>();

        // 自包含发布使用的是随包携带的运行时；框架依赖运行时若版本过低则无法保证界面正常加载。
        if (Environment.Version.Major < RequiredDotNetMajorVersion)
        {
            issues.Add(new StartupEnvironmentIssue(
                StartupEnvironmentIssueKind.DotNetRuntime,
                $"{RuntimeInformation.FrameworkDescription}（需要 .NET {RequiredDotNetMajorVersion} 或更高版本）"));
        }

        if (!PlatformInfo.IsWindows)
        {
            return new StartupEnvironmentReport(issues);
        }

        if (RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            issues.Add(new StartupEnvironmentIssue(
                StartupEnvironmentIssueKind.SystemArchitecture,
                RuntimeInformation.OSArchitecture.ToString()));
        }

        var build = Environment.OSVersion.Version.Build;
        if (build > 0 && build < MinimumWindowsBuild)
        {
            issues.Add(new StartupEnvironmentIssue(
                StartupEnvironmentIssueKind.WindowsVersion,
                $"Windows 10.0.{build}"));
        }

        var path = executablePath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (IsUnsafeLocation(path))
        {
            issues.Add(new StartupEnvironmentIssue(StartupEnvironmentIssueKind.UnsafeLocation, path));
        }

        return new StartupEnvironmentReport(issues);
    }

    /// <summary>阻断性问题：运行环境不满足时直接退出，避免继续运行后抛出原始异常。</summary>
    public static bool IsBlocking(StartupEnvironmentIssueKind kind) =>
        kind == StartupEnvironmentIssueKind.DotNetRuntime;

    /// <summary>PCL-CE 判定为容易丢文件/被杀软拦截的位置：系统临时目录、微信与 QQ 接收目录。</summary>
    private static bool IsUnsafeLocation(string path)
    {
        var full = path;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 路径异常时按原样继续判断
        }

        var temp = Path.GetTempPath();
        if (!string.IsNullOrEmpty(temp) &&
            full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return full.Contains("wechat_files", StringComparison.OrdinalIgnoreCase)
               || full.Contains("WeChat Files", StringComparison.OrdinalIgnoreCase)
               || full.Contains("Tencent Files", StringComparison.OrdinalIgnoreCase);
    }
}

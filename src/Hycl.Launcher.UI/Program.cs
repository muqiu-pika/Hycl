using System.Diagnostics;
using Avalonia;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.UI;

internal static class Program
{
    private const string DialogTitle = "Hyplay Craft Launcher";

    [STAThread]
    public static void Main(string[] args)
    {
        PerfTrace.Mark("process_main");

        // 单实例：已有 Hycl 在运行时不再开第二个窗口，改为唤起现有窗口并退出
        if (!SingleInstance.TryAcquire())
        {
            return;
        }

        if (!EnsureRuntimeEnvironment())
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    /// <summary>
    /// 启动前检查必需运行环境（对齐 PCL-CE 的启动环境提示）。
    /// 运行时缺失/过低属于阻断项，给出明确提示与 .NET 下载入口；其余问题只提示不阻断。
    /// </summary>
    private static bool EnsureRuntimeEnvironment()
    {
        StartupEnvironmentReport report;
        try
        {
            report = StartupEnvironmentCheck.Evaluate();
        }
        catch (Exception)
        {
            // 环境探测自身失败时不影响启动
            return true;
        }

        if (!report.HasIssues)
        {
            return true;
        }

        StartupEnvironmentIssue? blocking = null;
        foreach (var issue in report.Issues)
        {
            if (StartupEnvironmentCheck.IsBlocking(issue.Kind))
            {
                blocking = issue;
                break;
            }
        }

        if (blocking is { } runtimeIssue)
        {
            var text =
                $"{Describe(runtimeIssue)}\n\n" +
                $"启动器需要 .NET {StartupEnvironmentCheck.RequiredDotNetMajorVersion} 或更高版本的运行环境才能启动。\n" +
                $"下载地址：{StartupEnvironmentCheck.DotNetDownloadUrl}\n\n" +
                "是否现在打开下载页面？";
            if (ConfirmOpenDotNetDownload(text))
            {
                OpenDotNetDownload();
            }

            return false;
        }

        var list = string.Join("\n", report.Issues.Select(issue => $"- {Describe(issue)}"));
        NativeMessageBox.ShowInfo(
            DialogTitle,
            $"检测到运行环境提示：\n\n{list}\n\n继续使用可能出现异常，建议按提示调整运行环境后重启启动器。");
        return true;
    }

    /// <summary>启动阶段无法恢复的异常：记录日志并给出可读提示，避免闪退或只显示原始堆栈。</summary>
    private static void ReportStartupFailure(Exception ex)
    {
        var logPath = TryLogStartupFailure(ex);
        var summary = $"{ex.GetType().Name}: {ex.Message}";

        // 缺失的是启动器自身组件时，与本机已安装的 .NET 版本无关（自包含发布更是自带运行时），
        // 不能引导用户去安装 .NET —— 这正是“明明装了 .NET 却提示缺 .NET”的来源。
        if (IsLauncherComponentMissing(ex))
        {
            NativeMessageBox.ShowInfo(
                DialogTitle,
                $"启动器启动组件缺失，无法启动。\n\n{summary}\n\n" +
                "这不是 .NET 运行环境问题：请重新获取完整的启动器文件，或检查是否被杀毒软件拦截。\n" +
                $"详细日志：{logPath}",
                isError: true);
            return;
        }

        var runtimeIssue = LooksLikeRuntimeLoadFailure(ex);

        if (runtimeIssue)
        {
            var text =
                "启动器启动失败：缺少或损坏的 .NET 运行环境。\n\n" +
                $"{summary}\n\n" +
                $"详细日志：{logPath}\n" +
                $"下载地址：{StartupEnvironmentCheck.DotNetDownloadUrl}\n\n" +
                "是否现在打开下载页面？";
            if (ConfirmOpenDotNetDownload(text))
            {
                OpenDotNetDownload();
            }

            return;
        }

        NativeMessageBox.ShowInfo(
            DialogTitle,
            $"启动器启动失败。\n\n{summary}\n\n详细日志：{logPath}",
            isError: true);
    }

    private static string Describe(StartupEnvironmentIssue issue) => issue.Kind switch
    {
        StartupEnvironmentIssueKind.DotNetRuntime => $"缺少或版本过低的 .NET 运行时：{issue.Detail}",
        StartupEnvironmentIssueKind.WindowsVersion => $"Windows 版本过低：{issue.Detail}（建议 Windows 10 1809 或更高版本）",
        StartupEnvironmentIssueKind.SystemArchitecture => $"系统不是 64 位：{issue.Detail}",
        StartupEnvironmentIssueKind.UnsafeLocation => $"启动器位于临时目录或聊天软件目录，容易被清理或拦截：{issue.Detail}",
        _ => issue.Detail
    };

    private static string TryLogStartupFailure(Exception ex)
    {
        try
        {
            var logger = new LauncherLogger(LauncherPaths.CreateDefault().LogDirectory);
            logger.Error("启动阶段发生未处理异常", ex);
            return logger.LogFilePath;
        }
        catch (Exception)
        {
            return "（日志目录不可写，无法记录日志）";
        }
    }

    /// <summary>判断异常是否指向运行环境（程序集/原生库加载失败），此类问题给出 .NET 下载入口。</summary>
    private static bool LooksLikeRuntimeLoadFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or FileLoadException or BadImageFormatException or DllNotFoundException)
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("程序集", StringComparison.Ordinal) ||
                message.Contains("assembly", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("coreclr", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("hostfxr", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 加载失败的文件属于启动器自身组件（Avalonia / Hycl / SkiaSharp 等）而非 .NET 框架程序集时返回 true：
    /// 这类缺失说明发布包不完整或文件被拦截，与本机已安装的 .NET 版本无关，不应引导用户去安装 .NET。
    /// </summary>
    private static bool IsLauncherComponentMissing(Exception ex)
    {
        string[] components = ["Avalonia", "Hycl", "SkiaSharp", "HarfBuzzSharp"];

        for (var current = ex; current is not null; current = current.InnerException)
        {
            string? missing = current switch
            {
                FileNotFoundException notFound => notFound.FileName,
                FileLoadException loadFailed => loadFailed.FileName,
                BadImageFormatException badImage => badImage.FileName,
                DllNotFoundException notFound => notFound.Message,
                _ => null
            };

            if (missing is not null &&
                components.Any(component => missing.Contains(component, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 非 Windows 没有原生弹窗（ShowYesNo 只会写 stderr 并返回 false），
    /// GUI 启动时看不到控制台，因此直接打开下载页而不是静默退出。
    /// </summary>
    private static bool ConfirmOpenDotNetDownload(string text)
    {
        if (PlatformInfo.IsWindows)
        {
            return NativeMessageBox.ShowYesNo(DialogTitle, text, isError: true);
        }

        Console.Error.WriteLine(text);
        return true;
    }

    private static void OpenDotNetDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo(StartupEnvironmentCheck.DotNetDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开浏览器时，提示框里已经给出了可手动复制的下载地址
        }
    }
}

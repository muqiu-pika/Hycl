using System.Text.RegularExpressions;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 启动失败的结构化结论：标题 / 原因 / 建议操作 / 退出码。
/// 由 UI 层直接展示，不依赖任何 UI 类型，便于单测。
/// </summary>
public sealed record LaunchFailureInfo(
    string Title,
    string Reason,
    string Suggestion,
    int? ExitCode,
    bool CrashedInGame = false,
    string? CrashReportPath = null);

/// <summary>
/// 启动失败判定：只看进程退出码、日志特征行与启动前抛出的异常文本，
/// 规则按“越具体越优先”的顺序匹配；全部不命中时返回带退出码的兜底结论。
/// 判定逻辑与 UI 解耦，不读写任何界面状态。
/// </summary>
public static class LaunchFailureAnalyzer
{
    /// <summary>分析一次启动失败。</summary>
    /// <param name="exitCode">游戏进程退出码；未创建进程（启动前失败）时为 null。</param>
    /// <param name="logLines">本次启动捕获到的原始日志行（按时间顺序）。</param>
    /// <param name="message">启动前异常/等待窗口失败的原因文本，可为空。</param>
    public static LaunchFailureInfo Analyze(
        int? exitCode,
        IReadOnlyList<string> logLines,
        string? message = null)
    {
        var text = BuildText(logLines, message);
        var lower = text.ToLowerInvariant();

        // 1. 安装不完整：缺客户端 jar / 版本 json 缺失或损坏（多数在启动前就被拦下）
        if (ContainsAny(lower, "缺少客户端 jar", "缺少客户端核心文件", "缺少 jar", "客户端 jar 缺失",
                "版本不完整或不存在", "版本文件损坏",
                "版本 json 缺失", "版本文件缺失", "couldn't find version json",
                "could not find version json", "no version json")
            || (ContainsAny(lower, "versions") && ContainsAny(lower, "no such file", "not found", "找不到"))
               && ContainsAny(lower, ".jar", "版本"))
        {
            return new LaunchFailureInfo(
                "安装不完整",
                "客户端 jar 或版本文件缺失/损坏，无法组装启动参数。",
                "请在「下载」页重新安装该版本，安装完成后再试。",
                exitCode);
        }

        // 2. Java 路径无效：设置里指定的 Java 不存在 / 无法执行
        if (ContainsAny(lower, "指定的 java 无法使用", "指定的java无法使用", "java 路径无效",
                "未找到可用的 java", "java not found", "javaw 不存在", "java 可执行文件不存在"))
        {
            return new LaunchFailureInfo(
                "Java 路径无效",
                "设置中手动指定的 Java 不存在或无法运行。",
                "在「设置 → Java 设置」重新检测 Java，或指定有效的 javaw.exe 路径；"
                + "也可以开启「自动选择 Java」交给启动器挑选。",
                exitCode);
        }

        // 3. Java 版本不匹配：UnsupportedClassVersionError / 明确提示 Java 版本过低或过高
        if (ContainsAny(lower, "unsupportedclassversionerror", "class file version",
                "requires java", "java version", "需要 java",
                "java 版本过低", "java 版本过高", "java版本过低", "java版本过高",
                "无法找到合适的 java", "未检测到可用的 java", "未找到可用的 java", "java 不兼容"))
        {
            var required = DetectRequiredJava(text);
            return new LaunchFailureInfo(
                "Java 版本不匹配",
                required is null
                    ? "当前 Java 版本与游戏版本要求不一致，游戏无法加载。"
                    : $"游戏版本需要 Java {required}，当前配置的 Java 不满足要求。",
                "在「设置 → Java 设置」重新检测 Java；1.20.5 及以上建议 Java 21，"
                + "1.17~1.20.4 建议 Java 17。也可手动指定对应版本的 javaw.exe。",
                exitCode);
        }

        // 3. 内存不足 / 分配过大
        if (ContainsAny(lower, "outofmemoryerror", "java heap space", "could not reserve enough space",
                "insufficient memory", "failed to create the java virtual machine",
                "unable to allocate", "内存不足", "内存分配失败"))
        {
            return new LaunchFailureInfo(
                "内存分配失败",
                "JVM 无法申请到所需内存：可能是分配值超过了系统可用内存，或系统本身内存不足。",
                "在「设置 → 游戏管理」下调最大内存（如 2048~4096 MB），关闭占用内存的程序后重试；"
                + "32 位 Java 单进程最多约 1.5 GB。",
                exitCode);
        }

        // 4. 账号 / 正版验证失败
        if (ContainsAny(lower, "failed to login", "invalid credentials", "authenticationexception",
                "invalid token", "failed to verify username", "authentication failed",
                "正版验证失败", "账号无效", "登录失败"))
        {
            return new LaunchFailureInfo(
                "账号验证失败",
                "游戏拒绝当前账号：令牌无效或已过期，正版验证未通过。",
                "在启动页重新登录正版账号（或检查皮肤站地址与账号），确认后再启动。",
                exitCode);
        }

        // 5. 模组缺少依赖
        if (ContainsAny(lower, "missing or unsupported mandatory dependencies", "missing dependency",
                "dependencies are missing", "mod resolution", "modresolutionexception",
                "缺少依赖", "missing mod", "requires mod"))
        {
            return new LaunchFailureInfo(
                "模组缺少依赖",
                "有模组声明的前置未安装或版本不满足，加载器拒绝启动。",
                "按日志里列出的模组 ID 补齐前置；或在「下载」页用前置自动安装功能重新安装该模组。",
                exitCode);
        }

        // 6. 模组冲突
        if (ContainsAny(lower, "duplicate mods", "duplicate mod", "conflicting mod", "mod conflict",
                "模组冲突", "重复安装"))
        {
            return new LaunchFailureInfo(
                "模组冲突",
                "存在重复或互斥的模组，加载器无法合并。",
                "按日志列出的模组移除其中一方，或改用整合包作者推荐的版本组合。",
                exitCode);
        }

        // 7. Forge / NeoForge 加载器自身加载失败
        if (ContainsAny(lower, "fml", "forge", "neoforge", "modlauncher", "mixin")
            && ContainsAny(lower, "exception", "failed", "crash", "error", "失败"))
        {
            return new LaunchFailureInfo(
                "加载器启动失败",
                "Forge / NeoForge 在初始化阶段抛出异常（混入（Mixin）或加载器自身失败）。",
                "确认该版本使用匹配的加载器（Forge / NeoForge 不要混用），"
                + "并检查是否安装了不兼容的优化/修复类模组。",
                exitCode);
        }

        // 8. 端口占用
        if (ContainsAny(lower, "address already in use", "bind failed", "bindexception", "端口被占用"))
        {
            return new LaunchFailureInfo(
                "端口被占用",
                "游戏需要的本地端口已被其他进程占用（常见于重复启动或联机模组）。",
                "关闭可能占用端口的程序（如另一个游戏实例），或修改联机模组配置后重试。",
                exitCode);
        }

        // 9. 显卡 / OpenGL 初始化失败
        if (ContainsAny(lower, "opengl", "pixel format not accelerated", "failed to create window",
                "could not create gl context", "glfw", "wgldxcontext", "显卡驱动",
                "无法初始化图形"))
        {
            return new LaunchFailureInfo(
                "图形初始化失败",
                "OpenGL / 显卡驱动初始化失败，游戏无法创建渲染窗口。",
                "更新显卡驱动；笔记本双显卡用户请让 javaw.exe 使用独立显卡，"
                + "或关闭光影/渲染相关模组后重试。",
                exitCode);
        }

        // 10. 资源文件缺失
        if (ContainsAny(lower, "couldn't download asset", "could not download asset",
                "missing required resource", "unable to load asset", "file not found",
                "no such file or directory", "资源文件缺失", "找不到文件"))
        {
            return new LaunchFailureInfo(
                "资源文件缺失",
                "游戏运行所需的声音/语言/资源文件缺失，通常是下载中断或安装不完整。",
                "在「下载」页对该版本执行补全/重新安装（或检查资源索引），完成后重试。",
                exitCode);
        }

        // 兜底：不猜测原因，如实展示退出码与原始日志
        return new LaunchFailureInfo(
            exitCode is null ? "启动失败" : $"启动失败（退出码 {exitCode}）",
            "未能从日志中识别出具体原因。",
            "请展开下方原始日志查看最后几行；如需反馈，请复制日志一并提交。",
            exitCode);
    }

    private static string BuildText(IReadOnlyList<string> logLines, string? message)
    {
        // 只扫描尾部若干行：崩溃特征总在最后出现，也避免超大日志拖慢判定
        const int scanLines = 400;
        var start = Math.Max(0, logLines.Count - scanLines);
        var builder = new System.Text.StringBuilder();
        for (var i = start; i < logLines.Count; i++)
        {
            builder.AppendLine(logLines[i]);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AppendLine(message);
        }

        return builder.ToString();
    }

    private static bool ContainsAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从 “class file version 61.0” 或 “需要 Java 17” 推断所需 Java 主版本。</summary>
    private static int? DetectRequiredJava(string text)
    {
        var classFile = Regex.Match(text, @"class file version (\d+)", RegexOptions.IgnoreCase);
        if (classFile.Success && int.TryParse(classFile.Groups[1].Value, out var major))
        {
            // Java 1.0 = 45，此后每个大版本 +1
            return major - 44;
        }

        var javaVersion = Regex.Match(text, @"java\s*(?:版本)?\s*([0-9]{1,2})(?:\.[0-9]+)?",
            RegexOptions.IgnoreCase);
        if (javaVersion.Success && int.TryParse(javaVersion.Groups[1].Value, out var parsed)
            && parsed is >= 8 and <= 30)
        {
            return parsed;
        }

        return null;
    }
}

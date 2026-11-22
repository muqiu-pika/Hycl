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
/// 启动失败判定：只看进程退出码、日志特征行与启动前抛出的异常文本。
/// 判定流程：
/// 1. 退出类型前置分类（未创建进程 / 正常退出 / JVM 错误 / 强制结束 / 游戏崩溃 / 一般错误）；
/// 2. 崩溃报告发现：合并日志里记录的保存路径与实例目录 crash-reports 下最新一份；
/// 3. 按「越具体越优先」的规则表匹配（模组名/互斥组合等具体规则先于 OutOfMemory 等泛化规则）；
/// 4. 全部不命中时按退出类型给出兜底结论，并在原因中带上退出类型标签。
/// 判定逻辑与 UI 解耦，不读写任何界面状态。
/// </summary>
public static class LaunchFailureAnalyzer
{
    /// <summary>分析一次启动失败。</summary>
    /// <param name="exitCode">游戏进程退出码；未创建进程（启动前失败）时为 null。</param>
    /// <param name="logLines">本次启动捕获到的原始日志行（按时间顺序）。</param>
    /// <param name="message">启动前异常/等待窗口失败的原因文本，可为空。</param>
    /// <param name="instanceDirectory">实例（游戏）目录；提供且游戏异常退出时，
    /// 会扫描其下 crash-reports 目录里最新的 crash-*.txt 一并参与规则匹配。可为空。</param>
    public static LaunchFailureInfo Analyze(
        int? exitCode,
        IReadOnlyList<string> logLines,
        string? message = null,
        string? instanceDirectory = null)
    {
        var text = BuildText(logLines, message);
        var lower = text.ToLowerInvariant();

        // 1. 退出类型前置分类：先归类，再走规则
        var exitKind = ClassifyExit(exitCode, lower);
        if (exitKind == ExitKind.Normal)
        {
            return new LaunchFailureInfo(
                "游戏已正常退出",
                "游戏进程以退出码 0 正常结束，日志中未检测到崩溃特征。",
                "无需处理。若要再次游玩，直接重新启动即可。",
                exitCode);
        }

        // 2. 崩溃报告发现：stdout 记录的保存路径 + 实例目录 crash-reports 最新一份，
        //    与合并日志一起参与规则匹配（crash-report 里的 System Details / 堆栈是重要特征来源）
        var crashReportPath = FindCrashReportPath(exitCode, text, instanceDirectory);
        var crashReport = ReadCrashReport(crashReportPath);
        if (crashReport is not null)
        {
            text += "\n" + crashReport;
            lower += "\n" + crashReport.ToLowerInvariant();
        }

        // 3. 规则表按序匹配：首个命中即返回
        foreach (var rule in Rules)
        {
            if (!Matches(lower, rule))
            {
                continue;
            }

            var reason = rule.ReasonBuilder is { } builder ? builder(text) : rule.Reason;
            var info = new LaunchFailureInfo(
                rule.Title,
                reason,
                rule.Suggestion,
                exitCode,
                CrashedInGame: exitCode is not null,
                CrashReportPath: crashReportPath);
            return Tag(info, exitKind, exitCode);
        }

        // 4. 兜底：不猜测原因，按退出类型如实展示
        var fallback = exitKind == ExitKind.ForciblyKilled
            ? new LaunchFailureInfo(
                exitCode is { } killedCode ? $"游戏进程被强制结束（退出码 {killedCode}）" : "游戏进程被强制结束",
                "进程被外部强制终止（SIGKILL/SIGTERM）：常见于系统内存不足触发 OOM Killer、"
                + "杀毒软件查杀游戏进程，或被用户/管理工具手动结束。",
                "关闭占用内存的程序后重试；将游戏目录与启动器加入杀毒软件白名单；"
                + "如多次复现，可在「设置 → 游戏管理」下调最大内存并检查系统事件日志。",
                exitCode,
                CrashedInGame: true,
                CrashReportPath: crashReportPath)
            : new LaunchFailureInfo(
                exitCode is null ? "启动失败" : $"启动失败（退出码 {exitCode}）",
                "未能从日志中识别出具体原因。",
                "请展开下方原始日志查看最后几行；如需反馈，请复制日志一并提交。",
                exitCode,
                CrashedInGame: exitCode is not null,
                CrashReportPath: crashReportPath);
        return Tag(fallback, exitKind, exitCode);
    }

    // ==================== 退出类型 ====================

    /// <summary>退出类型：参照 HMCL ExitWaiter 的分类（NORMAL / JVM_ERROR / APPLICATION_ERROR / SIGKILL）扩展。</summary>
    private enum ExitKind
    {
        /// <summary>进程未创建：启动前检查或组装参数阶段就失败。</summary>
        PreLaunch,

        /// <summary>退出码 0：游戏正常退出。</summary>
        Normal,

        /// <summary>JVM 初始化失败：Java 虚拟机根本没起来。</summary>
        JvmError,

        /// <summary>进程被强制结束：退出码 137/143（SIGKILL/SIGTERM）。</summary>
        ForciblyKilled,

        /// <summary>游戏崩溃：日志里出现崩溃报告/Forge 错误屏特征。</summary>
        GameCrash,

        /// <summary>一般应用错误：非零退出且无更具体分类。</summary>
        AppError,
    }

    private static ExitKind ClassifyExit(int? exitCode, string lower)
    {
        if (exitCode is null)
        {
            return ExitKind.PreLaunch;
        }

        if (exitCode == 0)
        {
            return ExitKind.Normal;
        }

        // JVM 没起来优先于其它分类（参照 ExitWaiter：JVM_ERROR 先于 SIGKILL 判定）
        if (ContainsAny(lower,
                "could not create the java virtual machine",
                "error occurred during initialization of vm",
                "a fatal exception has occurred. program will exit"))
        {
            return ExitKind.JvmError;
        }

        if (exitCode is 137 or 143)
        {
            return ExitKind.ForciblyKilled;
        }

        if (ContainsAny(lower,
                "---- minecraft crash report ----",
                "crash report saved to",
                "this crash report has been saved to:",
                "could not save crash report",
                "an exception was thrown, the game will display an error screen and halt"))
        {
            return ExitKind.GameCrash;
        }

        return ExitKind.AppError;
    }

    /// <summary>在原因末尾附上退出类型标签，让结论可被程序与玩家同时识别。</summary>
    private static LaunchFailureInfo Tag(LaunchFailureInfo info, ExitKind kind, int? exitCode)
    {
        var label = kind switch
        {
            ExitKind.PreLaunch => "启动前失败（游戏进程未创建）",
            ExitKind.JvmError => "JVM 错误（Java 虚拟机初始化失败）",
            ExitKind.ForciblyKilled => exitCode is { } killedCode
                ? $"进程被强制结束（SIGKILL/SIGTERM，退出码 {killedCode}）"
                : "进程被强制结束（SIGKILL/SIGTERM）",
            ExitKind.GameCrash => "游戏崩溃（应用错误）",
            _ => exitCode is { } code ? $"一般应用错误（退出码 {code}）" : "一般应用错误",
        };
        return info with { Reason = info.Reason + $"\n退出类型：{label}" };
    }

    // ==================== 崩溃报告发现 ====================

    /// <summary>
    /// 游戏异常退出后定位崩溃报告：优先用 stdout 里游戏自己记录的保存路径，
    /// 其次扫描实例目录 crash-reports 下最新的 crash-*.txt（目录可能不存在，全部容错）。
    /// </summary>
    private static string? FindCrashReportPath(int? exitCode, string text, string? instanceDirectory)
    {
        if (exitCode is null or 0)
        {
            return null;
        }

        try
        {
            var match = CrashReportPathPattern.Match(text);
            if (match.Success)
            {
                var loggedPath = match.Groups[1].Value.Trim();
                if (File.Exists(loggedPath))
                {
                    return loggedPath;
                }
            }

            if (string.IsNullOrWhiteSpace(instanceDirectory))
            {
                return null;
            }

            var directory = Path.Combine(instanceDirectory, "crash-reports");
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(directory, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>读取崩溃报告内容参与规则匹配；超长截断，读不到时返回 null。</summary>
    private static string? ReadCrashReport(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[200_000];
            var read = reader.Read(buffer, 0, buffer.Length);
            return read <= 0 ? null : new string(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>游戏自身打印的崩溃报告保存位置（新旧版本两种措辞都覆盖）。</summary>
    private static readonly Regex CrashReportPathPattern = new(
        "(?:#@!@# Game crashed! Crash report saved to: #@!@#|This crash report has been saved to:)"
        + "[ \\t]*([^\\r\\n]+?\\.txt)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // ==================== 规则表 ====================

    /// <summary>一条失败判定规则：所有关键词组都命中（每组内任一命中）才算匹配。</summary>
    private sealed record FailureRule(
        string Title,
        string Reason,
        string Suggestion,
        string[][] MarkerGroups,
        Func<string, string>? ReasonBuilder = null);

    private static FailureRule R(string title, string reason, string suggestion, params string[][] groups) =>
        new(title, reason, suggestion, groups);

    /// <summary>
    /// 规则表：越具体的规则越靠前（模组名/互斥组合 → 错误类 → 泛化规则）。
    /// 关键词一律小写（匹配前已统一 ToLowerInvariant）。
    /// </summary>
    private static readonly FailureRule[] Rules =
    [
        // ---------- 安装 / 启动前 ----------
        // 1. 安装不完整：缺客户端 jar / 版本 json 缺失或损坏（多数在启动前就被拦下）
        R("安装不完整",
            "客户端 jar 或版本文件缺失/损坏，无法组装启动参数。",
            "请在「下载」页重新安装该版本，安装完成后再试。",
            ["缺少客户端 jar", "缺少客户端核心文件", "缺少 jar", "客户端 jar 缺失",
                "版本不完整或不存在", "版本文件损坏", "版本 json 缺失", "版本文件缺失",
                "couldn't find version json", "could not find version json", "no version json"]),
        // 1b. 安装不完整：versions 目录 + 找不到 + jar 的组合特征
        R("安装不完整",
            "客户端 jar 或版本文件缺失/损坏，无法组装启动参数。",
            "请在「下载」页重新安装该版本，安装完成后再试。",
            [["versions"], ["no such file", "not found", "找不到"], [".jar", "版本"]]),
        // 2. Java 路径无效：设置里指定的 Java 不存在 / 无法执行
        R("Java 路径无效",
            "设置中手动指定的 Java 不存在或无法运行。",
            "在「设置 → Java 设置」重新检测 Java，或指定有效的 javaw.exe 路径；"
            + "也可以开启「自动选择 Java」交给启动器挑选。",
            ["指定的 java 无法使用", "指定的java无法使用", "java 路径无效",
                "未找到可用的 java", "java not found", "javaw 不存在", "java 可执行文件不存在"]),

        // ---------- JVM / Java 环境（具体规则优先于泛化的「Java 版本不匹配」） ----------
        // 3. OpenJ9 虚拟机不兼容
        R("OpenJ9 虚拟机不兼容",
            "当前使用的是 OpenJ9 虚拟机，Minecraft 及其模组加载器依赖 HotSpot 的内部实现，无法在 OpenJ9 上运行。",
            "改用 HotSpot 系 Java（如 Temurin、Zulu、Oracle JDK），不要使用 OpenJ9/AdoptOpenJDK J9 版本。",
            ["open j9 is not supported", "openj9 is incompatible", ".j9vminternals."]),
        // 4. JVM 初始化失败（退出类型里的 JVM_ERROR 对应结论）
        R("Java 虚拟机启动失败",
            "Java 虚拟机在初始化阶段直接退出，游戏进程根本没有启动：通常是 JVM 参数非法（如 -Xmx 超过 32 位上限）或 Java 安装损坏。",
            "在「设置 → 游戏管理」下调最大内存并移除自定义 JVM 参数；必要时重装 Java 后重试。",
            ["could not create the java virtual machine",
                "error occurred during initialization of vm",
                "a fatal exception has occurred. program will exit"]),
        // 5. 32 位 Java 内存上限 / -Xmx 超限（比泛化 OOM 更具体）
        R("内存分配失败",
            "JVM 在启动阶段无法申请对象堆：常见于 32 位 Java 单进程约 1.5 GB 的上限，或 -Xmx 设置超出可表示范围/超过系统可用内存。",
            "在「设置 → 游戏管理」下调最大内存（如 2048~4096 MB），并确认使用 64 位 Java；32 位 Java 单进程最多约 1.5 GB。",
            ["could not reserve enough space", "invalid maximum heap size",
                "the specified size exceeds the maximum representable size"]),
        // 6. Java 版本过高：反射/内部 API/模块导出在新 JVM 上被限制
        R("Java 版本过高",
            "当前 Java 版本对游戏或模组来说过新：旧版本游戏与 Forge 依赖的内部 API、反射访问在较新 JVM 上被禁止。",
            "在「设置 → Java 设置」改用与游戏版本匹配的 Java：1.12.2 及更早用 Java 8，1.17~1.20.4 用 Java 17，1.20.5 及以上用 Java 21。",
            ["unable to make protected final java.lang.class java.lang.classloader.defineclass",
                "java.lang.nosuchfieldexception: ucp",
                "unsupported class file major version",
                "because module java.base does not export",
                "jdk.nashorn.api.scripting.nashornscriptenginefactory",
                "java.lang.invoke.lambdametafactory",
                "cannot read the array length because \"urls\" is null",
                "classcastexception: java.base/jdk",
                "classcastexception: class jdk."]),
        // 7. 旧版 Forge 与新 Java 不兼容（ManifestEntryVerifier 已在 JDK 17+ 移除）
        R("旧版 Forge 与新 Java 不兼容",
            "旧版 Forge（1.16.5 及更早）使用的 jar 签名校验 API 已在较新 JDK 中移除，加载器无法启动。",
            "为该版本改用 Java 8，或升级 Forge 至修复该问题的版本。",
            ["sun.security.util.manifestentryverifier"]),
        // 8. Java 版本过低：模组要求 Java 11+（class file version 55）
        R("Java 版本过低",
            "模组或加载器要求 Java 11 及以上，而当前配置的 Java 版本过低，无法加载新版类文件。",
            "在「设置 → Java 设置」改用 Java 17/21（对应游戏版本），或移除要求新版 Java 的模组。",
            ["no such method: sun.misc.unsafe.defineanonymousclass",
                "the requested compatibility level java_11 could not be set",
                "has been compiled by a more recent version of the java runtime (class file version 55.0)"]),

        // 8.1 主类无法加载：JVM 参数（--add-opens / -cp 等）被拆散，取值被当成了主类
        R("启动参数异常",
            "JVM 找不到游戏主类：通常是 --add-opens / -cp 这类「标志 + 独立取值」的 JVM 参数被拆散或丢失，"
            + "取值被当成了主类名（Forge/NeoForge 的启动参数最容易被影响）。",
            "移除自定义 JVM 参数后重试；若使用 Forge/NeoForge，重新安装该版本以重建启动参数。",
            ["找不到或无法加载主类", "could not find or load main class"]),

        // ---------- 内存 ----------
        // 9. 内存不足 / 分配过大（泛化 OOM）
        R("内存分配失败",
            "JVM 无法申请到所需内存：可能是分配值超过了系统可用内存，或系统本身内存不足。",
            "在「设置 → 游戏管理」下调最大内存（如 2048~4096 MB），关闭占用内存的程序后重试；"
            + "32 位 Java 单进程最多约 1.5 GB。",
            ["outofmemoryerror", "java heap space", "insufficient memory", "unable to allocate",
                "failed to create the java virtual machine",
                "the system is out of physical ram or swap space",
                "there is insufficient memory for the java runtime environment to continue",
                "out of memory error", "内存不足", "内存分配失败"]),
        // 10. 显存不足
        R("显存不足",
            "显卡显存（GPU 内存）耗尽：光影/高分辨率资源包或过大的渲染距离把显存用完了。",
            "关闭光影与高分辨率资源包、降低渲染距离；笔记本双显卡用户请让 javaw.exe 使用独立显卡。",
            ["gl_out_of_memory", "out of video memory", "dxgi_error_out_of_memory", "显存不足"]),

        // ---------- 账号 ----------
        // 11. 账号 / 正版验证失败
        R("账号验证失败",
            "游戏拒绝当前账号：令牌无效或已过期，正版验证未通过。",
            "在启动页重新登录正版账号（或检查皮肤站地址与账号），确认后再启动。",
            ["failed to login", "invalid credentials", "authenticationexception",
                "invalid token", "failed to verify username", "authentication failed",
                "正版验证失败", "账号无效", "登录失败"]),

        // ---------- 模组互斥 / 版本冲突（具体模组名优先） ----------
        // 12. OptiFine 与 Forge 不兼容（同一批渲染/网络类被双方修改，方法签名对不上）
        R("OptiFine 与 Forge 不兼容",
            "OptiFine 与当前 Forge 版本不兼容：二者同时修改同一批游戏类，导致方法签名对不上而崩溃。",
            "移除 OptiFine（新版 Forge 可用 Sodium/Rubidium 系模组替代优化），"
            + "或改用整合包作者推荐的 Forge × OptiFine 版本组合。",
            ["sun.misc.unsafe.defineanonymousclass(java.lang.class, byte[], java.lang.object[])",
                "net.optifine.reflect.reflector",
                "forgegui.rendersselecteditemname",
                "rendersystem.getbackenddescription",
                "net.minecraft.client.gui.font.ellipsize",
                "net.minecraft.server.level.distancemanager",
                "spritecontents.<init>",
                "bakedquad.<init>"]),
        // 13. OptiFine 与 NeoForge 互斥
        R("OptiFine 与 NeoForge 不兼容",
            "NeoForge 明确检测到 OptiFine 注入并拒绝启动，两者互不兼容。",
            "移除 OptiFine，改用 NeoForge 生态的渲染优化模组（如 Sodium/Embeddium）。",
            ["invalid services found optifine", "invalidlaunchersetupexception"]),
        // 14. OptiFine 重复安装（mods 目录手动放了一份 + 自动安装又装了一份）
        R("OptiFine 重复安装",
            "检测到两份 OptiFine：mods 目录里手动放置了一份，版本又自动安装了一份，模块系统无法合并。",
            "删除 mods 目录里的 OptiFine jar，只保留启动器自动安装的那一份。",
            ["module optifine reads another module named optifine"]),
        // 15. 光影模组与 OptiFine 冲突（OptiFine 内置光影支持）
        R("光影模组与 OptiFine 冲突",
            "OptiFine 检测到独立的 Shaders Mod：OptiFine 已内置光影支持，再装光影模组会直接冲突。",
            "移除独立的 Shaders/光影模组，光影直接交给 OptiFine 加载。",
            ["shaders mod detected. please remove it"]),
        // 16. OptiFine 导致世界无法加载
        R("OptiFine 导致世界无法加载",
            "OptiFine 与当前游戏版本不匹配，进入世界时在区块管理器内部崩溃。",
            "更换与游戏版本匹配的 OptiFine 版本，或移除 OptiFine 后重试。",
            ["chunkmanager$proxyticketmanager.shouldforceticks"]),
        // 17. Performant 与 OptiFine 冲突
        R("Performant 与 OptiFine 冲突",
            "Performant 优化模组的 Mixin 与 OptiFine 的修改冲突，注入校验失败。",
            "移除 Performant 或 OptiFine 其中一方。",
            ["performant.mixins.json"]),
        // 18. 暮色森林（TwilightForest）与 OptiFine 冲突
        R("暮色森林与 OptiFine 冲突",
            "TwilightForest 与 OptiFine 同时修改渲染逻辑，绘制时超出图像边界而崩溃。",
            "移除 OptiFine 或 TwilightForest 其中一方，或更换两者兼容的版本组合。",
            ["outside of image bounds"]),
        // 19. Jade 与 OptiFine 冲突（1.20+）
        R("Jade 与 OptiFine 冲突",
            "Jade（物品提示/WAILA 类模组）的 Mixin 与 OptiFine 的渲染修改冲突，注入目标已变更。",
            "移除 OptiFine 或 Jade 其中一方，或更换两者兼容的版本组合。",
            [["critical injection failure"], ["jade.mixins.json"]]),
        // 20. 优化类核心模组（coremod）与 OptiFine 冲突
        R("优化模组与 OptiFine 冲突",
            "某个优化类核心模组（coremod）对渲染管线的字节码修改与 OptiFine 冲突，转换失败。",
            "移除 OptiFine 或日志中指出的优化模组（coremod）其中一方。",
            ["meta-inf/asm/multipart.js"]),
        // 21. RTSS 与 Sodium 不兼容
        R("RTSS 与 Sodium 不兼容",
            "RivaTuner Statistics Server（RTSS）与 Sodium 渲染优化模组不兼容。",
            "关闭 RTSS（或其游戏内叠加层）后再启动，或移除 Sodium。",
            ["rivatuner statistics server (rtss) is not compatible with sodium"]),

        // ---------- 模组文件问题 ----------
        // 22. 模组 jar 被解压
        R("模组文件被解压",
            "mods 目录里存在被解压过的模组（文件夹形式或解压出的 jar），Forge/Fabric 拒绝加载。",
            "把解压出来的内容删除，直接将原始 .jar 文件放回 mods 目录。",
            ["appear to be extracted jar files", "extracted mod jars found"]),
        // 23. 模组文件名不合法（中文/特殊字符文件名被 JPMS 当成空模块名）
        R("模组文件名不合法",
            "某个模组 jar 的文件名（常见为纯中文或特殊字符）无法被 Forge 生成合法的模块名。",
            "把该模组文件重命名为英文、数字与短横线组合（如 spleef-1.0.jar）后再启动。",
            ["invalid module name: '' is not a java identifier"]),
        // 24. 模组过多超出 ID 上限
        R("模组数量超限",
            "安装的模组过多，注册表条目超出游戏 ID 上限（maximum id range exceeded）。",
            "精简模组数量，或使用支持扩展 ID 上限的修复方案后重试。",
            ["maximum id range exceeded"]),

        // ---------- 模组依赖 / 冲突（泛化） ----------
        // 25. 模组缺少依赖
        R("模组缺少依赖",
            "有模组声明的前置未安装或版本不满足，加载器拒绝启动。",
            "按日志里列出的模组 ID 补齐前置；或在「下载」页用前置自动安装功能重新安装该模组。",
            ["missing or unsupported mandatory dependencies", "missing dependency",
                "dependencies are missing", "mod resolution", "modresolutionexception",
                "could not find required mod", "缺少依赖", "missing mod", "requires mod"]),
        // 26. 模组冲突
        R("模组冲突",
            "存在重复或互斥的模组，加载器无法合并。",
            "按日志列出的模组移除其中一方，或改用整合包作者推荐的版本组合。",
            ["duplicate mods", "duplicate mod", "found duplicate mods", "found conflicting mods",
                "conflicting mod", "mod conflict", "模组冲突", "重复安装"]),

        // ---------- Fabric ----------
        // 27. Fabric 模组集不兼容
        R("Fabric 模组组合不兼容",
            "Fabric 加载器检测到模组组合不兼容或重复，拒绝继续加载。",
            "按日志列出的警告逐项处理：移除冲突/重复的模组或改为兼容版本。",
            ["incompatible mod set!", "incompatible mods found!", "warnings were found!"]),
        // 28. Fabric 模组入口点失败
        R("Fabric 模组加载失败",
            "某个模组在 Fabric 入口点初始化阶段抛出异常，游戏无法继续加载。",
            "按日志中 provided by 后的模组 ID 移除、更新或重装该模组。",
            ["could not execute entrypoint stage"]),
        // 29. Fabric 加载器版本过旧（0.12 之前）
        R("Fabric 加载器版本过旧",
            "当前 Fabric Loader 版本过旧，缺少新版模组依赖的 Mixin 支持（FabricMixinTransformerProxy 缺失）。",
            "在版本管理中升级 Fabric Loader 到最新版本。",
            ["org/spongepowered/asm/mixin/transformer/fabricmixintransformerproxy"]),

        // ---------- Mixin / 模组加载 ----------
        // 30. Mixin 注入失败
        R("Mixin 注入失败",
            "某个模组的 Mixin 混入失败：模组与游戏/加载器版本不匹配，注入目标的代码已变更。",
            "移除日志中指出的模组，或更换与当前游戏版本匹配的模组版本。",
            ["mixinapplyerror", "mixin prepare failed", "mixin apply failed",
                "mixin.injection.throwables.", "critical injection failure"]),
        // 31. 模组在加载阶段崩溃（Forge/Fabric 指名道姓）
        R("模组加载崩溃",
            "某个模组在初始化阶段抛出异常，加载器已指名该模组。",
            "按日志中括号内的模组 ID 移除、更新或重装该模组。",
            ["loaderexceptionmodcrash: caught exception from",
                "failed to create mod instance. modid:"]),
        // 32. 模组配置文件加载失败
        R("模组配置文件损坏",
            "某个模组的配置文件无法加载（内容损坏或格式不合法），模组初始化失败。",
            "删除实例 config 目录里日志指出的配置文件后重试（会重新生成默认配置）。",
            ["failed loading config file"]),
        // 33. NightConfig 解析失败
        R("配置文件解析失败",
            "NightConfig 在解析 TOML 配置时数据不足/损坏（已知问题，常由配置文件写入中断引起）。",
            "删除实例 config 目录中损坏的 .toml 配置后重试；必要时安装 Night Config Fixes 模组。",
            ["com.electronwill.nightconfig.core.io.parsingexception"]),

        // ---------- 错误类（泛化，具体签名类规则已在上文） ----------
        // 34. NoSuchMethodError
        R("模组方法缺失",
            "模组调用的方法不存在：通常是模组与游戏/前置版本不匹配，或模组 jar 损坏。",
            "更新或重装日志中涉及的模组与前置，确保全部与当前游戏版本匹配。",
            ["java.lang.nosuchmethoderror"]),
        // 35. NoClassDefFoundError
        R("类定义缺失",
            "运行时找不到某个类：模组/加载器文件不完整，或依赖的前置缺失。",
            "重新安装该版本补全文件；按日志中的类名定位缺失的模组或前置并补齐。",
            ["java.lang.noclassdeffounderror"]),
        // 36. IllegalAccessError
        R("类访问受限",
            "模组试图访问不允许访问的类成员：模组与游戏/前置版本不匹配（coremod 注入不完整）。",
            "更新或移除日志中涉及的模组；确认模组与游戏版本匹配。",
            ["java.lang.illegalaccesserror"]),
        // 37. jar 签名校验失败（手动改过原版/模组 jar）
        R("文件校验失败",
            "jar 文件被修改后与签名不符：游戏或模组 jar 被手动改动/损坏。",
            "恢复被修改的 jar（重新下载或重装该版本），不要直接改动原版与模组 jar。",
            ["sha1 digest error", "signer information does not match"]),
        // 38. 文件已存在冲突
        R("文件已存在冲突",
            "游戏要创建的文件/目录已经存在，内容冲突导致初始化失败。",
            "按日志中的路径删除或改名冲突文件（常见于重复解压的配置/模组文件）后重试。",
            ["java.nio.file.filealreadyexistsexception"]),
        // 39. 本地库加载失败（LWJGL/jinput 等）
        R("本地库加载失败",
            "JVM 无法加载本地库：LWJGL/jinput 等原生依赖缺失、被删改或架构（32/64 位）不匹配。",
            "在「下载」页对该版本执行补全/重新安装；确认使用 64 位 Java；"
            + "关闭可能锁定 DLL 的程序（杀毒/同步盘）后重试。",
            ["java.lang.unsatisfiedlinkerror", "org.lwjgl.lwjglexception"]),

        // ---------- Forge / NeoForge 加载器 ----------
        // 40. Forge 重复安装
        R("Forge 重复安装",
            "版本 JSON 中出现了重复的 Forge 启动参数，通常是 Forge 被安装了两次（安装不完整后重装导致）。",
            "删除该版本后重新安装一次 Forge，不要重复执行安装。",
            ["found multiple arguments for option"]),
        // 41. Forge 安装不完整
        R("Forge 安装不完整",
            "Forge 的客户端核心库缺失或安装损坏，加载器找不到自己的类/路径。",
            "在版本管理中删除该版本后重新安装 Forge/NeoForge。",
            ["cannot find launch target fmlclient",
                "invalid paths argument, contained no existing paths",
                "failed to find minecraft resource version",
                "could not find net/minecraft/client/minecraft.class in classloader"]),
        // 42. Forge 错误屏（模组运行时异常，Forge 已给出信息）
        R("模组运行时异常",
            "某个模组在初始化/运行中抛出异常，Forge 进入错误暂停屏；日志上方通常已注明具体模组与原因。",
            "按日志中最后出现的 mod 信息移除或更新对应模组。",
            ["an exception was thrown, the game will display an error screen and halt"]),

        // ---------- 图形 / 显卡（具体 GL 错误优先于泛化图形规则） ----------
        // 43. 显卡驱动不支持 OpenGL
        R("显卡驱动不支持 OpenGL",
            "当前显卡驱动不支持 OpenGL（常见于远程桌面、虚拟机或仅装了基本显示驱动）。",
            "安装显卡官方驱动并在本地显示器环境启动；远程/虚拟机场景请改用支持 OpenGL 的方案。",
            ["the driver does not appear to support opengl"]),
        // 44. OpenGL 1282 操作错误（资源包/光影/模组做了非法 GL 调用）
        R("OpenGL 操作错误",
            "游戏中出现非法 OpenGL 调用（1282 Invalid Operation）：常见于资源包、光影或渲染类模组操作不当。",
            "移除最近添加的资源包/光影/渲染类模组；也可以尝试降低分辨率后重试。",
            ["1282: invalid operation"]),
        // 45. 资源包分辨率过高
        R("资源包分辨率过高",
            "资源包/材质分辨率过高，超出显卡可处理范围。",
            "更换低分辨率资源包，或卸载当前资源包后重试。",
            ["maybe try a lower resolution resourcepack", "maybe try a lower resolution texturepack",
                "maybe try a lowerresolution"]),
        // 46. 显卡驱动崩溃（EXCEPTION_ACCESS_VIOLATION，hs_err 可见显卡模块）
        R("显卡驱动崩溃",
            "JVM 在显卡驱动代码中发生非法内存访问后崩溃（EXCEPTION_ACCESS_VIOLATION）。",
            "更新显卡驱动到官方最新版；笔记本双显卡用户让 javaw.exe 使用独立显卡；关闭光影后重试。",
            ["exception_access_violation"]),
        // 47. 字体 / 输入法崩溃
        R("字体/输入法崩溃",
            "游戏在加载或渲染字体时崩溃：常见于系统字体缺失、第三方输入法（IME）注入冲突或 forceUnicodeFont 异常。",
            "在版本设置中开启 forceUnicodeFont；关闭第三方输入法的注入/兼容模式；必要时重装该版本补全字体资源。",
            ["net.minecraft.client.gui.font", "could not load font", "输入法"]),

        // ---------- 加载器（泛化，保持原有兜底顺序） ----------
        // 48. Forge / NeoForge 加载器自身加载失败
        R("加载器启动失败",
            "Forge / NeoForge 在初始化阶段抛出异常（混入（Mixin）或加载器自身失败）。",
            "确认该版本使用匹配的加载器（Forge / NeoForge 不要混用），"
            + "并检查是否安装了不兼容的优化/修复类模组。",
            [["fml", "forge", "neoforge", "modlauncher", "mixin"],
                ["exception", "failed", "crash", "error", "失败"]]),

        // ---------- 图形（泛化，保持原有兜底顺序） ----------
        // 49. 显卡 / OpenGL 初始化失败
        R("图形初始化失败",
            "OpenGL / 显卡驱动初始化失败，游戏无法创建渲染窗口。",
            "更新显卡驱动；笔记本双显卡用户请让 javaw.exe 使用独立显卡，"
            + "或关闭光影/渲染相关模组后重试。",
            ["opengl", "pixel format not accelerated", "failed to create window",
                "could not create gl context", "glxbadfbconfig", "couldn't set pixel format",
                "glfw", "wgldxcontext", "显卡驱动", "无法初始化图形"]),

        // ---------- 系统 / IO ----------
        // 50. 端口占用
        R("端口被占用",
            "游戏需要的本地端口已被其他进程占用（常见于重复启动或联机模组）。",
            "关闭可能占用端口的程序（如另一个游戏实例），或修改联机模组配置后重试。",
            ["address already in use", "bind failed", "bindexception", "端口被占用"]),
        // 51. 文件被占用
        R("文件被占用",
            "游戏/启动器需要读写的文件正被其他进程锁定：常见于残留的 java 进程、同步盘或杀毒软件正在扫描。",
            "在任务管理器结束残留的 java/javaw 进程，关闭同步盘与杀毒软件对游戏目录的占用后重试。",
            ["being used by another process", "the process cannot access the file",
                "另一个程序正在使用此文件", "文件被占用"]),
        // 52. 磁盘空间不足
        R("磁盘空间不足",
            "游戏所在磁盘没有剩余空间，无法写入存档/资源/崩溃报告。",
            "清理游戏所在磁盘（至少保留数 GB 空闲），或把实例移动到空间充足的分区后重试。",
            ["no space left on device", "there is not enough space on the disk",
                "磁盘空间不足", "磁盘已满"]),
        // 53. 文件访问被拒绝（杀毒拦截 / 权限不足）
        R("文件访问被拒绝",
            "游戏目录内的文件被拒绝访问：常见于杀毒软件/Windows Defender 隔离或拦截了游戏文件，或目录权限不足。",
            "将游戏目录与启动器加入杀毒软件白名单并恢复被隔离的文件；必要时以管理员身份运行启动器。",
            ["access is denied", "accessdeniedexception", "拒绝访问"]),

        // ---------- 泛化兜底规则 ----------
        // 54. Java 版本不匹配（泛化：UnsupportedClassVersionError / 提示语；
        //     具体的 Java 过高/过低规则已在上文优先命中）
        new FailureRule(
            "Java 版本不匹配",
            "当前 Java 版本与游戏版本要求不一致，游戏无法加载。",
            "在「设置 → Java 设置」重新检测 Java；1.20.5 及以上建议 Java 21，"
            + "1.17~1.20.4 建议 Java 17。也可手动指定对应版本的 javaw.exe。",
            [["unsupportedclassversionerror", "class file version", "requires java", "java version",
                "需要 java", "java 版本过低", "java 版本过高", "java版本过低", "java版本过高",
                "无法找到合适的 java", "未检测到可用的 java", "未找到可用的 java", "java 不兼容"]])
        {
            ReasonBuilder = text => DetectRequiredJava(text) is { } required
                ? $"游戏版本需要 Java {required}，当前配置的 Java 不满足要求。"
                : "当前 Java 版本与游戏版本要求不一致，游戏无法加载。",
        },
        // 55. 资源文件缺失
        R("资源文件缺失",
            "游戏运行所需的声音/语言/资源文件缺失，通常是下载中断或安装不完整。",
            "在「下载」页对该版本执行补全/重新安装（或检查资源索引），完成后重试。",
            ["couldn't download asset", "could not download asset",
                "missing required resource", "unable to load asset", "file not found",
                "no such file or directory", "资源文件缺失", "找不到文件"]),
    ];

    private static bool Matches(string lower, FailureRule rule)
    {
        foreach (var group in rule.MarkerGroups)
        {
            if (!ContainsAny(lower, group))
            {
                return false;
            }
        }

        return true;
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

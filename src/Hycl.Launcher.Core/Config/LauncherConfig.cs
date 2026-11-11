using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Config;

/// <summary>
/// 启动器全局配置（对应 PCL-CE 的 PCL.ini 中启动相关项）。
/// </summary>
public sealed class LauncherConfig
{
    public string UserName { get; set; } = "Player";

    public string SelectedVersion { get; set; } = "";

    /// <summary>空字符串表示使用平台默认的 .minecraft 目录。</summary>
    public string GameDirectory { get; set; } = "";

    /// <summary>
    /// 版本管理页「文件夹列表」中已添加的游戏文件夹（绝对路径）。
    /// 当前正在使用的文件夹由 <see cref="GameDirectory"/> 决定；列表仅用于快速切换。
    /// </summary>
    public List<string> InstanceFolders { get; set; } = new();

    /// <summary>空字符串表示自动匹配 Java。</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>
    /// 可选的 CurseForge API Key 覆盖值（默认空 = 使用内置密钥）；
    /// 环境变量 <c>HYCL_CURSEFORGE_API_KEY</c> 优先级更高。仅保存在本机配置，不会提交到仓库。
    /// </summary>
    public string CurseForgeApiKey { get; set; } = "";

    /// <summary>0 表示自动（物理内存的一半，限制在 512–8192 MB）。</summary>
    public int MemoryMegabytes { get; set; } = 0;

    /// <summary>0 默认；1 全屏；2 与启动器相同；3 自定义；4 最大化。</summary>
    public int WindowType { get; set; }

    public int WindowWidth { get; set; } = 854;

    public int WindowHeight { get; set; } = 480;

    public string ExtraJvmArguments { get; set; } = "";

    public string ExtraGameArguments { get; set; } = "";

    /// <summary>游戏窗口标题；空 = 不修改。支持 {name}/{user}/{version} 占位符（对应 PCL-CE LaunchArgumentTitle）。</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>自定义信息，显示在游戏主界面左下角与 F3（对应 PCL-CE LaunchArgumentInfo）。</summary>
    public string TypeInfo { get; set; } = "HYCL";

    /// <summary>启动器可见性：0 游戏启动后立即关闭 / 4 启动游戏后最小化 / 5 保持不变（对应 PCL-CE LaunchArgumentVisible 的常用档）。</summary>
    public int LauncherVisibility { get; set; } = 5;

    /// <summary>游戏进程优先级：0 高 / 1 中 / 2 低 / 3 极高 / 4 实时（对应 PCL-CE LaunchArgumentPriority）。</summary>
    public int ProcessPriority { get; set; } = 1;

    /// <summary>IP 协议偏好：0 IPv4 优先 / 1 Java 默认 / 2 IPv6 优先（对应 PCL-CE LaunchPreferredIpStack）。</summary>
    public int PreferredIpStack { get; set; } = 0;

    /// <summary>初始内存（-Xms）；0 = 自动（对应 PCL-CE LaunchRamCustomInitial）。</summary>
    public int InitialMemoryMegabytes { get; set; } = 0;

    /// <summary>锁定内存分配：-Xms = -Xmx（对应 PCL-CE LaunchAdvanceLockMemory）。</summary>
    public bool LockMemory { get; set; }

    /// <summary>启动前执行命令；支持 {path}/{user}/{name}/{version} 占位符（对应 PCL-CE LaunchAdvanceRun）。</summary>
    public string PreLaunchCommand { get; set; } = "";

    /// <summary>等待启动前命令执行完成后再继续启动（对应 PCL-CE LaunchAdvanceRunWait）。</summary>
    public bool PreLaunchCommandWait { get; set; } = true;

    /// <summary>安装新实例后自动选定该实例（对应 PCL-CE ToolDownloadAutoSelectVersion）。</summary>
    public bool AutoSelectInstance { get; set; } = true;

    /// <summary>版本管理列表的手动排序（实例 ID 顺序；未列入的按默认规则排在后面）。</summary>
    public List<string> InstanceOrder { get; set; } = new();

    /// <summary>
    /// 每个实例的累计启动次数，键为 versions 下的实例目录名（不区分大小写）。
    /// 只在检测到游戏窗口后累加，并随配置持久化（重启启动器不清零）。
    /// </summary>
    public Dictionary<string, int> InstanceLaunchCounts { get; set; } = new();

    /// <summary>社区资源列表忽略 Quilt（对应 PCL-CE ToolDownloadIgnoreQuilt）。</summary>
    public bool CompIgnoreQuilt { get; set; } = true;

    /// <summary>快速下载行为：0 总是询问 / 1 安装到当前实例 / 2 询问实例 / 3 询问路径（对应 PCL-CE ToolDownloadQuickBehavior）。</summary>
    public int QuickDownloadBehavior { get; set; } = 0;

    /// <summary>启动器更新通道：0 正式版 / 1 测试版 / 2 开发版（对应 PCL-CE SystemUpdateChannel）。</summary>
    public int UpdateChannel { get; set; } = 0;

    /// <summary>版本列表源：0 镜像优先 / 1 官方优先（缓慢时自动切换）/ 2 仅官方（对应 PCL-CE ToolDownloadVersion）。</summary>
    public int VersionListSource { get; set; } = (int)Download.DownloadSourceMode.OfficialAuto;

    /// <summary>下载文件源：0 镜像优先 / 1 官方优先（缓慢时自动切换）/ 2 仅官方（对应 PCL-CE ToolDownloadSource）。</summary>
    public int FileSource { get; set; } = (int)Download.DownloadSourceMode.OfficialAuto;

    /// <summary>旧版配置的单一下载源字段（"Official"/"Bmclapi"），仅用于读取迁移，不再写出。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadSource { get; set; }

    public int DownloadThreads { get; set; } = 16;

    public int RetryCount { get; set; } = 3;

    /// <summary>社区资源源策略：0 镜像优先 / 1 官方优先（失败转镜像）/ 2 仅官方（对应 PCL-CE ToolDownloadMod）。</summary>
    public int CommunitySourceSolution { get; set; } = 1;

    /// <summary>下载模组时自动解析并安装前置（对应 PCL-CE ToolDownloadAutoInstallDependencies）。</summary>
    public bool CompAutoInstallDependencies { get; set; } = true;

    /// <summary>修复 1.16.5 的 authlib 2.1.28 兼容问题（对应 PCL-CE ToolFixAuthlib，默认开启）。</summary>
    public bool FixAuthlib { get; set; } = true;

    public bool CloseLauncherAfterLaunch { get; set; }

    public bool ShowGameOutput { get; set; } = true;

    public bool EnableAnimations { get; set; } = true;

    /// <summary>
    /// 插件清单（Hycl-online online.json）源地址；空 = 使用内置默认源
    /// （HyclOnlineDefaults.DefaultManifestUrls，raw 直链 + ghproxy 镜像）。
    /// </summary>
    public string OnlineManifestUrl { get; set; } = "";

    /// <summary>全局游戏内设置：实例首次启动且选择「跟随全局」时写入 options.txt。</summary>
    public GameSettingsConfig GameSettings { get; set; } = new();

    /// <summary>light / dark。</summary>
    public string Theme { get; set; } = "light";

    /// <summary>界面主题色（#RRGGBB；空 = 跟随默认配色，参考 OpenPCL 的主题色自定义）。</summary>
    public string AccentColor { get; set; } = "";

    /// <summary>
    /// 启动器背景图片路径（空 = 不启用）。选择文件时复制进数据目录 cache/backgrounds，
    /// 避免用户移动/删除原文件后背景失效。
    /// </summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>背景图片不透明度（0-100，默认 22；0 时背景不可见）。</summary>
    public int BackgroundOpacity { get; set; } = 22;

    /// <summary>启动时是否静默检查启动器更新（发现新版本仅提示，不自动下载）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// 实例级覆盖设置，键为 versions 下的实例目录名。
    /// 未设置或留空的字段回退到本类中的全局设置（实例覆盖 &gt; 全局）。
    /// </summary>
    public Dictionary<string, InstanceSettings> InstanceSettings { get; set; } = new();

    [JsonIgnore]
    public string EffectiveGameDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory)
            ? Core.Platform.PlatformInfo.DefaultMinecraftDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(GameDirectory));

    public static LauncherConfig CreateDefault()
    {
        var config = new LauncherConfig();
        config.Normalize();
        return config;
    }

    /// <summary>把用户输入/旧配置文件里的非法值收敛到可用范围。</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            UserName = "Player";
        }

        MemoryMegabytes = Math.Clamp(MemoryMegabytes, 0, 65536);
        InitialMemoryMegabytes = Math.Clamp(InitialMemoryMegabytes, 0, 65536);
        LauncherVisibility = LauncherVisibility is 0 or 4 or 5 ? LauncherVisibility : 5;
        ProcessPriority = Math.Clamp(ProcessPriority, 0, 4);
        PreferredIpStack = Math.Clamp(PreferredIpStack, 0, 2);
        QuickDownloadBehavior = Math.Clamp(QuickDownloadBehavior, 0, 3);
        UpdateChannel = Math.Clamp(UpdateChannel, 0, 2);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100);
        WindowType = Math.Clamp(WindowType, 0, 4);
        WindowWidth = Math.Clamp(WindowWidth, 320, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 240, 4320);
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 128);
        RetryCount = Math.Clamp(RetryCount, 0, 10);
        VersionListSource = Math.Clamp(VersionListSource, 0, 2);
        FileSource = Math.Clamp(FileSource, 0, 2);
        // 迁移旧版单一下载源字段：仅在用户尚未改动新字段时生效
        if (VersionListSource == (int)Download.DownloadSourceMode.OfficialAuto
            && FileSource == (int)Download.DownloadSourceMode.OfficialAuto)
        {
            if (DownloadSource == "Official")
            {
                VersionListSource = (int)Download.DownloadSourceMode.OfficialOnly;
                FileSource = (int)Download.DownloadSourceMode.OfficialOnly;
            }
            else if (DownloadSource == "Bmclapi")
            {
                VersionListSource = (int)Download.DownloadSourceMode.MirrorFirst;
                FileSource = (int)Download.DownloadSourceMode.MirrorFirst;
            }
        }

        DownloadSource = null;
        OnlineManifestUrl = OnlineManifestUrl?.Trim() ?? "";
        InstanceFolders = NormalizeInstanceFolders(InstanceFolders);
        if (Theme is not ("light" or "dark"))
        {
            Theme = "light";
        }

        // 实例键统一为不区分大小写，并收敛各覆盖值到可用范围
        if (InstanceSettings.Count > 0)
        {
            var normalized = new Dictionary<string, InstanceSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in InstanceSettings)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    continue;
                }

                pair.Value.Normalize();
                normalized[pair.Key] = pair.Value;
            }

            InstanceSettings = normalized;
        }

        if (InstanceLaunchCounts.Count > 0)
        {
            var normalizedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in InstanceLaunchCounts)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0)
                {
                    continue;
                }

                normalizedCounts[pair.Key] = pair.Value;
            }

            InstanceLaunchCounts = normalizedCounts;
        }
    }

    /// <summary>文件夹列表去空、去重（不区分大小写），保留原有顺序。</summary>
    private static List<string> NormalizeInstanceFolders(List<string>? folders)
    {
        var normalized = new List<string>();
        if (folders is null)
        {
            return normalized;
        }

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var trimmed = folder.Trim();
            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    /// <summary>读取实例覆盖设置；不存在时创建空设置（空值表示跟随全局）。</summary>
    public InstanceSettings GetOrCreateInstanceSettings(string instanceId)
    {
        if (InstanceSettings.TryGetValue(instanceId, out var settings) && settings is not null)
        {
            return settings;
        }

        settings = new InstanceSettings();
        InstanceSettings[instanceId] = settings;
        return settings;
    }

    /// <summary>读取实例覆盖设置；不存在时返回 null（不写入配置）。</summary>
    public InstanceSettings? FindInstanceSettings(string instanceId) =>
        InstanceSettings.TryGetValue(instanceId, out var settings) ? settings : null;

    /// <summary>该实例是否启用版本隔离（未设置时与 PCL-CE 一致，默认开启）。</summary>
    public bool IsInstanceIsolated(string instanceId) =>
        FindInstanceSettings(instanceId)?.VersionIsolation ?? true;

    /// <summary>删除实例覆盖设置（删除/重命名实例时调用）。</summary>
    public void RemoveInstanceSettings(string instanceId) => InstanceSettings.Remove(instanceId);

    /// <summary>读取某实例的累计启动次数（未记录时为 0）。</summary>
    public int GetLaunchCount(string instanceId) =>
        InstanceLaunchCounts.TryGetValue(instanceId, out var count) && count > 0 ? count : 0;

    /// <summary>启动成功（检测到游戏窗口）后累加该实例的启动次数。</summary>
    public int IncrementLaunchCount(string instanceId)
    {
        var count = GetLaunchCount(instanceId) + 1;
        InstanceLaunchCounts[instanceId] = count;
        return count;
    }

    /// <summary>删除实例时同步移除其启动次数，避免同名新实例继承旧计数。</summary>
    public void RemoveLaunchCount(string instanceId) => InstanceLaunchCounts.Remove(instanceId);

    /// <summary>实例重命名时迁移启动次数（保留累计值）。</summary>
    public void MoveLaunchCount(string fromId, string toId)
    {
        var count = GetLaunchCount(fromId);
        InstanceLaunchCounts.Remove(fromId);
        if (count > 0)
        {
            InstanceLaunchCounts[toId] = count;
        }
    }

    /// <summary>实际分配给游戏的堆内存（MB）。</summary>
    public int GetEffectiveMemoryMegabytes()
    {
        if (MemoryMegabytes > 0)
        {
            return MemoryMegabytes;
        }

        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L));
        if (totalMb <= 0)
        {
            return 2048;
        }

        var half = totalMb / 2;
        var rounded = half / 256 * 256;
        return Math.Clamp(rounded, 512, 8192);
    }
}

/// <summary>
/// 全局游戏内设置（对应 Minecraft「选项」里的视频 / 控制 / 音乐和声音 / 聊天 / 辅助功能 / 语言）。
/// 仅在实例首次启动且为「跟随全局」时写入一次，之后不再覆盖游戏内修改。
/// </summary>
public sealed class GameSettingsConfig
{
    /// <summary>启用：首次启动写入 options.txt；禁用：完全使用游戏默认，不写任何值。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>视距（2-32 区块）。</summary>
    public int RenderDistance { get; set; } = 8;

    /// <summary>模拟距离（5-32 区块；1.18 之前的版本会忽略该键）。</summary>
    public int SimulationDistance { get; set; } = 12;

    /// <summary>视野 FOV（30-110）。</summary>
    public int Fov { get; set; } = 70;

    /// <summary>界面尺寸（0 自动 / 1-4）。</summary>
    public int GuiScale { get; set; }

    /// <summary>最大帧率（30-260，260 表示不限）。</summary>
    public int MaxFps { get; set; } = 120;

    /// <summary>垂直同步。</summary>
    public bool VSync { get; set; } = true;

    /// <summary>亮度（0-1）。</summary>
    public double Gamma { get; set; } = 0.5;

    /// <summary>图形质量：fast / fancy / fabulous。</summary>
    public string Graphics { get; set; } = "fancy";

    /// <summary>粒子效果：all / decreased / minimal。</summary>
    public string Particles { get; set; } = "all";

    /// <summary>云。</summary>
    public bool Clouds { get; set; } = true;

    /// <summary>平滑光照。</summary>
    public bool SmoothLighting { get; set; } = true;

    /// <summary>实体阴影。</summary>
    public bool EntityShadows { get; set; } = true;

    /// <summary>视角摇晃幅度（0-1）。</summary>
    public double FovEffectScale { get; set; } = 1.0;

    /// <summary>屏幕扭曲幅度（0-1）。</summary>
    public double ScreenEffectScale { get; set; } = 1.0;

    // ------------------------- 控制设置 -------------------------

    /// <summary>鼠标灵敏度（0-1）。</summary>
    public double MouseSensitivity { get; set; } = 0.5;

    /// <summary>反转鼠标。</summary>
    public bool InvertMouse { get; set; }

    /// <summary>自动跳跃。</summary>
    public bool AutoJump { get; set; }

    /// <summary>主手：right / left。</summary>
    public string MainHand { get; set; } = "right";

    // ------------------------- 音乐和声音 -------------------------

    /// <summary>主音量（0-1）。</summary>
    public double MasterVolume { get; set; } = 1.0;

    /// <summary>音乐（0-1）。</summary>
    public double MusicVolume { get; set; } = 1.0;

    /// <summary>音符盒 / 唱片机（0-1）。</summary>
    public double RecordVolume { get; set; } = 1.0;

    /// <summary>天气（0-1）。</summary>
    public double WeatherVolume { get; set; } = 1.0;

    /// <summary>方块（0-1）。</summary>
    public double BlockVolume { get; set; } = 1.0;

    /// <summary>敌对生物（0-1）。</summary>
    public double HostileVolume { get; set; } = 1.0;

    /// <summary>中立生物（0-1）。</summary>
    public double NeutralVolume { get; set; } = 1.0;

    /// <summary>玩家（0-1）。</summary>
    public double PlayerVolume { get; set; } = 1.0;

    /// <summary>环境（0-1）。</summary>
    public double AmbientVolume { get; set; } = 1.0;

    /// <summary>语音（0-1）。</summary>
    public double VoiceVolume { get; set; } = 1.0;

    // ------------------------- 聊天设置 -------------------------

    /// <summary>聊天不透明度（0-1）。</summary>
    public double ChatOpacity { get; set; } = 1.0;

    /// <summary>聊天缩放（0.5-1）。</summary>
    public double ChatScale { get; set; } = 1.0;

    /// <summary>聊天宽度（0.5-1）。</summary>
    public double ChatWidth { get; set; } = 1.0;

    /// <summary>聊天高度（展开时，0-1）。</summary>
    public double ChatHeightFocused { get; set; } = 1.0;

    /// <summary>聊天高度（收起时，0-1）。</summary>
    public double ChatHeightUnfocused { get; set; } = 0.44366196;

    /// <summary>聊天显示：0 完整 / 1 仅命令 / 2 隐藏。</summary>
    public int ChatVisibility { get; set; }

    /// <summary>聊天颜色。</summary>
    public bool ChatColors { get; set; } = true;

    /// <summary>聊天链接。</summary>
    public bool ChatLinks { get; set; } = true;

    // ------------------------- 辅助功能 -------------------------

    /// <summary>字幕：0 关闭 / 1 全部 / 2 仅聊天 / 3 仅系统。</summary>
    public int Narrator { get; set; }

    /// <summary>减少动态效果。</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>强制使用 Unicode 字体。</summary>
    public bool ForceUnicodeFont { get; set; }

    /// <summary>隐藏闪烁标语。</summary>
    public bool HideSplashTexts { get; set; }

    // ------------------------- 语言 -------------------------

    /// <summary>游戏语言代码（如 zh_cn / en_us）。</summary>
    public string Language { get; set; } = "zh_cn";

    /// <summary>把配置值收敛到游戏可接受范围。</summary>
    public void Normalize()
    {
        RenderDistance = Math.Clamp(RenderDistance, 2, 32);
        SimulationDistance = Math.Clamp(SimulationDistance, 5, 32);
        Fov = Math.Clamp(Fov, 30, 110);
        GuiScale = Math.Clamp(GuiScale, 0, 4);
        MaxFps = Math.Clamp(MaxFps, 30, 260);
        Gamma = Math.Clamp(Gamma, 0, 1);
        FovEffectScale = Math.Clamp(FovEffectScale, 0, 1);
        ScreenEffectScale = Math.Clamp(ScreenEffectScale, 0, 1);
        MouseSensitivity = Math.Clamp(MouseSensitivity, 0, 1);
        MasterVolume = Math.Clamp(MasterVolume, 0, 1);
        MusicVolume = Math.Clamp(MusicVolume, 0, 1);
        RecordVolume = Math.Clamp(RecordVolume, 0, 1);
        WeatherVolume = Math.Clamp(WeatherVolume, 0, 1);
        BlockVolume = Math.Clamp(BlockVolume, 0, 1);
        HostileVolume = Math.Clamp(HostileVolume, 0, 1);
        NeutralVolume = Math.Clamp(NeutralVolume, 0, 1);
        PlayerVolume = Math.Clamp(PlayerVolume, 0, 1);
        AmbientVolume = Math.Clamp(AmbientVolume, 0, 1);
        VoiceVolume = Math.Clamp(VoiceVolume, 0, 1);
        ChatOpacity = Math.Clamp(ChatOpacity, 0, 1);
        ChatScale = Math.Clamp(ChatScale, 0.5, 1);
        ChatWidth = Math.Clamp(ChatWidth, 0.5, 1);
        ChatHeightFocused = Math.Clamp(ChatHeightFocused, 0, 1);
        ChatHeightUnfocused = Math.Clamp(ChatHeightUnfocused, 0, 1);
        ChatVisibility = Math.Clamp(ChatVisibility, 0, 2);
        Narrator = Math.Clamp(Narrator, 0, 3);
        Graphics = Graphics is "fast" or "fancy" or "fabulous" ? Graphics : "fancy";
        Particles = Particles is "all" or "decreased" or "minimal" ? Particles : "all";
        MainHand = MainHand is "left" ? "left" : "right";
        if (string.IsNullOrWhiteSpace(Language))
        {
            Language = "zh_cn";
        }
    }
}

/// <summary>
/// 单个实例的覆盖设置。数值为哨兵值时回退到全局配置：
/// 内存 0、初始内存 -1、窗口模式 -1、窗口宽高 0、JVM/Java/存档目录为空字符串。
/// </summary>
public sealed class InstanceSettings
{
    /// <summary>备注（仅启动器内显示，不改实例目录名）。</summary>
    public string Remark { get; set; } = "";

    /// <summary>版本隔离：开启时 saves/mods/resourcepacks/shaderpacks 位于实例目录下。</summary>
    public bool VersionIsolation { get; set; } = true;

    /// <summary>
    /// 游戏内设置来源：global 跟随全局（首次启动写入全局 options.txt 逻辑不变）/
    /// instance 使用本实例自己的 <see cref="GameSettings"/> 写入该实例目录的 options.txt。
    /// </summary>
    public string GameSettingsMode { get; set; } = "global";

    /// <summary>
    /// 实例级游戏内设置（GameSettingsMode = "instance" 时生效）；null = 不写入任何游戏内设置。
    /// 编辑器首次进入时由 UI 预填全局值，保存后即为该实例独立的 options.txt 基线。
    /// </summary>
    public GameSettingsConfig? GameSettings { get; set; }

    /// <summary>独立存档目录；空字符串表示使用版本隔离规则下的 saves。</summary>
    public string SavesDirectory { get; set; } = "";

    /// <summary>覆盖的 Java 路径；空字符串表示跟随全局。</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>覆盖的最大堆内存（MB）；0 表示跟随全局。</summary>
    public int MemoryMegabytes { get; set; }

    /// <summary>覆盖的初始堆内存（MB）；-1 表示跟随全局。</summary>
    public int InitialMemoryMegabytes { get; set; } = -1;

    /// <summary>覆盖的附加 JVM 参数；空字符串表示跟随全局。</summary>
    public string ExtraJvmArguments { get; set; } = "";

    /// <summary>覆盖的附加游戏参数；空字符串表示跟随全局。</summary>
    public string ExtraGameArguments { get; set; } = "";

    /// <summary>覆盖的窗口标题；空字符串表示跟随全局。</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>覆盖的自定义版本信息；空字符串表示跟随全局。</summary>
    public string TypeInfo { get; set; } = "";

    /// <summary>自动进入的服务器（host 或 host:port）；空字符串表示不自动进服。</summary>
    public string ServerToEnter { get; set; } = "";

    /// <summary>覆盖的窗口模式（0 默认 / 1 全屏 / 2 与启动器相同 / 3 自定义 / 4 最大化）；-1 表示跟随全局。</summary>
    public int WindowType { get; set; } = -1;

    /// <summary>覆盖的窗口宽度；0 表示跟随全局。</summary>
    public int WindowWidth { get; set; }

    /// <summary>覆盖的窗口高度；0 表示跟随全局。</summary>
    public int WindowHeight { get; set; }

    internal void Normalize()
    {
        Remark ??= "";
        SavesDirectory ??= "";
        JavaPath ??= "";
        ExtraJvmArguments ??= "";
        ExtraGameArguments ??= "";
        WindowTitle ??= "";
        TypeInfo ??= "";
        ServerToEnter ??= "";
        MemoryMegabytes = Math.Clamp(MemoryMegabytes, 0, 65536);
        InitialMemoryMegabytes = Math.Clamp(InitialMemoryMegabytes, -1, 65536);
        WindowType = Math.Clamp(WindowType, -1, 4);
        WindowWidth = Math.Clamp(WindowWidth, 0, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 0, 4320);
        GameSettings?.Normalize();
    }
}

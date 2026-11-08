using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>筛选分类项：显示名 + CurseForge 分类 Id / Modrinth 分类 slug 组合标签。</summary>
public sealed record CompCategoryOption(string Label, string Tag);

/// <summary>
/// 社区资源中文显示层（对应 PCL-CE 的 Comp.Category 资源键 / TranslatedName / ChineseDescription）：
/// 分类名走内置中文映射表，项目标题走内置 MC 百科译名库（PCL-CE Resources/mcmod.buf，Apache-2.0），
/// 简介优先取 mcimirror 在线翻译（无需密钥），失败回退原文；任何兜底都不会是空白或 null。
/// UI 只绑定这里产出的字符串。
/// </summary>
public static partial class CompLocalization
{
    private const string NameDatabaseResource = "Hycl.Launcher.Core.Resources.mcmod.buf";

    private const string DescriptionEndpoint = "https://mod.mcimirror.top/translate";

    private const string UnknownCategory = "其他";

    private const string EmptyDescription = "暂无简介";

    private const string EmptyTitle = "未命名资源";

    public static readonly string[] LoaderSlugs = ["forge", "fabric", "quilt", "neoforge"];

    /// <summary>
    /// 筛选用的 Minecraft 正式版本全集（静态来源，与搜索结果无关）。
    /// UI 会再把本机已安装版本并进来，避免筛选项被当前结果裁掉。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownGameVersions =
    [
        "1.0", "1.1", "1.2.1", "1.2.5", "1.3.1", "1.3.2", "1.4.2", "1.4.7", "1.5.1", "1.5.2",
        "1.6.1", "1.6.4", "1.7.2", "1.7.10", "1.8.8", "1.8.9", "1.9", "1.9.4", "1.10", "1.10.2",
        "1.11", "1.11.2", "1.12", "1.12.1", "1.12.2", "1.13", "1.13.1", "1.13.2", "1.14", "1.14.2",
        "1.14.3", "1.14.4", "1.15", "1.15.1", "1.15.2", "1.16", "1.16.1", "1.16.2", "1.16.4", "1.16.5",
        "1.17", "1.17.1", "1.18", "1.18.1", "1.18.2", "1.19", "1.19.2", "1.19.3", "1.19.4", "1.20",
        "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6", "1.21", "1.21.1", "1.21.2", "1.21.3",
        "1.21.4", "1.21.5", "1.21.6", "1.21.7", "1.21.8", "1.21.9", "1.21.10", "1.21.11"
    ];

    /// <summary>正式版版本标识（1.21 / 1.20.6 / 1.0）；排除快照、预发布与 CurseForge 的 Client/Server 伪版本。</summary>
    private static readonly Regex ReleaseGameVersionPattern = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    private static volatile IReadOnlyList<string> _releaseGameVersions = KnownGameVersions;

    /// <summary>
    /// 筛选用正式版全集：静态基线 + 版本清单动态合并。
    /// 新版本发布后由版本清单自动补入，无需改动代码（对应 PCL-CE 把最高 Drop 插入筛选的做法）。
    /// </summary>
    public static IReadOnlyList<string> ReleaseGameVersions => _releaseGameVersions;

    /// <summary>是否为正式版版本号（如 1.21、1.20.6）；快照（23w31a）、预发布（1.21-pre1）均返回 false。</summary>
    public static bool IsReleaseGameVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && ReleaseGameVersionPattern.IsMatch(version);

    /// <summary>把版本清单里的正式版并入筛选全集（去重并按版本号降序）。</summary>
    public static void MergeReleaseGameVersions(IEnumerable<string> versions)
    {
        _releaseGameVersions = _releaseGameVersions
            .Concat(versions)
            .Where(IsReleaseGameVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, VersionComparer)
            .ToList();
    }

    /// <summary>CurseForge 分类数字 Id → 中文（对照 PCL-CE CurseForgeCategoryLangKeys + zh-CN.xaml）。</summary>
    private static readonly Dictionary<long, string> CurseForgeCategories = new()
    {
        [406] = "世界元素",
        [407] = "生物群系",
        [410] = "维度",
        [408] = "矿物与资源",
        [409] = "天然结构",
        [412] = "科技",
        [415] = "管道与物流",
        [4843] = "自动化",
        [417] = "能源",
        [4558] = "红石",
        [436] = "食物与烹饪",
        [416] = "农业",
        [414] = "运输",
        [420] = "仓储",
        [419] = "魔法",
        [422] = "冒险",
        [424] = "装饰",
        [411] = "生物",
        [434] = "装备与工具",
        [6814] = "性能优化",
        [9026] = "创造模式",
        [423] = "信息显示",
        [435] = "服务器",
        [5191] = "改良",
        [421] = "支持库",
        [4484] = "多人",
        [4479] = "硬核",
        [4483] = "战斗",
        [4478] = "任务",
        [4472] = "科技",
        [4473] = "魔法",
        [4475] = "冒险",
        [4476] = "探索",
        [4477] = "小游戏",
        [4471] = "科幻",
        [4736] = "空岛",
        [5128] = "原版改良",
        [4487] = "FTB",
        [4480] = "基于地图",
        [4481] = "轻量整合",
        [4482] = "大型整合",
        [403] = "原版风",
        [400] = "写实风",
        [401] = "现代风",
        [402] = "中世纪",
        [399] = "蒸汽朋克",
        [5244] = "含字体",
        [404] = "动态效果",
        [4465] = "兼容模组",
        [393] = "16x",
        [394] = "32x",
        [395] = "64x",
        [396] = "128x",
        [397] = "256x",
        [398] = "512x 或更高",
        [5193] = "数据包",
        [6553] = "写实风",
        [6554] = "幻想风",
        [6555] = "原版风",
        [6948] = "冒险",
        [6949] = "幻想",
        [6950] = "支持库",
        [6952] = "魔法",
        [6946] = "模组相关",
        [6951] = "科技",
        [6953] = "实用",
        [248] = "冒险",
        [249] = "创造",
        [250] = "小游戏",
        [251] = "跑酷",
        [252] = "解谜",
        [253] = "生存",
        [4464] = "模组世界"
    };

    /// <summary>Modrinth 分类 slug → 中文（对照 PCL-CE ModrinthCategoryLangKeys + 资源包隐藏分类）。</summary>
    private static readonly Dictionary<string, string> ModrinthCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["technology"] = "科技",
        ["magic"] = "魔法",
        ["adventure"] = "冒险",
        ["utility"] = "实用",
        ["optimization"] = "性能优化",
        ["vanilla-like"] = "原版风",
        ["realistic"] = "写实风",
        ["worldgen"] = "世界元素",
        ["food"] = "食物与烹饪",
        ["game-mechanics"] = "游戏机制",
        ["transportation"] = "运输",
        ["storage"] = "仓储",
        ["social"] = "服务器",
        ["library"] = "支持库",
        ["multiplayer"] = "多人",
        ["challenging"] = "硬核",
        ["combat"] = "战斗",
        ["quests"] = "任务",
        ["kitchen-sink"] = "水槽包",
        ["lightweight"] = "轻量整合",
        ["simplistic"] = "简洁",
        ["tweaks"] = "改良",
        ["8x-"] = "8x 或更低",
        ["16x"] = "16x",
        ["32x"] = "32x",
        ["48x"] = "48x",
        ["64x"] = "64x",
        ["128x"] = "128x",
        ["256x"] = "256x",
        ["512x+"] = "512x 或更高",
        ["audio"] = "含声音",
        ["fonts"] = "含字体",
        ["models"] = "含模型",
        ["gui"] = "含 UI",
        ["locale"] = "含语言",
        ["core-shaders"] = "核心着色器",
        ["modded"] = "兼容模组",
        ["fantasy"] = "幻想风",
        ["semi-realistic"] = "半写实风",
        ["cartoon"] = "卡通风",
        ["colored-lighting"] = "彩色光照",
        ["path-tracing"] = "路径追踪",
        ["pbr"] = "PBR",
        ["reflections"] = "反射",
        ["iris"] = "Iris",
        ["optifine"] = "OptiFine",
        ["vanilla"] = "原版可用",
        ["decoration"] = "装饰",
        ["mobs"] = "生物",
        ["equipment"] = "装备与工具",
        ["cursed"] = "鬼畜",
        ["minigame"] = "小游戏",
        ["datapack"] = "数据包",
        ["world"] = "世界",
        ["themed"] = "主题化",
        ["style"] = "风格",
        ["modern"] = "现代风",
        ["medieval"] = "中世纪",
        ["steampunk"] = "蒸汽朋克",
        ["structures"] = "天然结构",
        ["dimensions"] = "维度",
        ["biomes"] = "生物群系",
        ["ores-resources"] = "矿物与资源",
        ["redstone"] = "红石",
        ["energy"] = "能源",
        ["farming"] = "农业",
        ["mobs-entities"] = "生物",
        ["animated"] = "动态效果",
        ["resolution"] = "分辨率",
        ["pipes-logistics"] = "管道与物流",
        ["automation"] = "自动化",
        ["display"] = "信息显示",
        ["server"] = "服务器",
        ["creative"] = "创造模式",
        ["performance"] = "性能负荷",
        ["low"] = "低",
        ["medium"] = "中",
        ["high"] = "高",
        ["potato"] = "极低",
        ["ftb"] = "FTB",
        ["exploration"] = "探索",
        ["sci-fi"] = "科幻",
        ["skyblock"] = "空岛",
        ["map-based"] = "基于地图",
        ["extra-large"] = "大型整合",
        ["vanilla-plus"] = "原版改良",
        ["parkour"] = "跑酷",
        ["puzzle"] = "解谜",
        ["survival"] = "生存",
        ["mod-world"] = "模组世界"
    };

    /// <summary>加载器显示名（品牌名保持原文，非品牌项补中文）。</summary>
    private static readonly Dictionary<string, string> LoaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = "Forge",
        ["fabric"] = "Fabric",
        ["quilt"] = "Quilt",
        ["neoforge"] = "NeoForge",
        ["liteloader"] = "LiteLoader",
        ["rift"] = "Rift",
        ["optifine"] = "OptiFine",
        ["iris"] = "Iris",
        ["canvas"] = "Canvas",
        ["folia"] = "Folia",
        ["datapack"] = "数据包",
        ["minecraft"] = "原版",
        ["bukkit"] = "Bukkit",
        ["paper"] = "Paper",
        ["purpur"] = "Purpur",
        ["sponge"] = "Sponge",
        ["velocity"] = "Velocity",
        ["bungeecord"] = "BungeeCord"
    };

    private static readonly ConcurrentDictionary<string, string> DescriptionCache = new();

    private static readonly Lazy<IReadOnlyDictionary<string, string>> NameDatabase =
        new(LoadNameDatabase, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>项目标题：模组/数据包优先查内置 MC 百科译名，其余回退源站标题。</summary>
    public static string DisplayTitle(CompProject project)
    {
        var raw = Normalize(project.Title);
        if (project.Type is CompResourceType.Mod or CompResourceType.DataPack)
        {
            var translated = LookupName(project.Slug);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        return raw.Length > 0 ? raw : EmptyTitle;
    }

    /// <summary>项目简介：源站中文本地化字段缺失时回退原文，空白回退固定文案。</summary>
    public static string DisplayDescription(CompProject project)
    {
        var raw = Normalize(project.Description);
        return raw.Length > 0 ? raw : EmptyDescription;
    }

    /// <summary>标签：加载器 + 中文分类，去重后最多 5 个，始终非空。</summary>
    public static IReadOnlyList<string> DisplayTags(CompProject project)
    {
        var tags = new List<string>();
        foreach (var loader in project.ModLoaders)
        {
            AddUnique(tags, LoaderName(loader));
        }

        if (project.FromCurseForge)
        {
            if (project.CategoryIds.Count > 0)
            {
                foreach (var id in project.CategoryIds)
                {
                    AddUnique(tags, CurseForgeCategories.TryGetValue(id, out var name) ? name : UnknownCategory);
                }
            }
            else
            {
                foreach (var slug in project.Categories)
                {
                    AddUnique(tags, CategorySlugName(slug));
                }
            }
        }
        else
        {
            foreach (var slug in project.Categories)
            {
                if (!LoaderSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    AddUnique(tags, CategorySlugName(slug));
                }
            }
        }

        if (tags.Count == 0)
        {
            tags.Add(UnknownCategory);
        }

        return tags.Count > 5 ? tags.GetRange(0, 5) : tags;
    }

    /// <summary>作者：源站字段缺失时回退固定文案，绝不为空。</summary>
    public static string DisplayAuthor(CompProject project)
    {
        var raw = Normalize(project.Author);
        return raw.Length > 0 ? raw : "未知作者";
    }

    /// <summary>分类（只含分类，不含加载器）：CurseForge 数字 Id → 中文，Modrinth slug → 中文，始终非空。</summary>
    public static string DisplayCategoryText(CompProject project)
    {
        var list = new List<string>();
        if (project.FromCurseForge)
        {
            if (project.CategoryIds.Count > 0)
            {
                foreach (var id in project.CategoryIds)
                {
                    AddUnique(list, CurseForgeCategories.TryGetValue(id, out var name) ? name : UnknownCategory);
                }
            }
            else
            {
                foreach (var slug in project.Categories)
                {
                    AddUnique(list, CategorySlugName(slug));
                }
            }
        }
        else
        {
            foreach (var slug in project.Categories)
            {
                if (!LoaderSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    AddUnique(list, CategorySlugName(slug));
                }
            }
        }

        return list.Count > 0 ? string.Join("、", list) : UnknownCategory;
    }

    /// <summary>加载器中文名；项目未标注加载器时给出通用兜底，避免空白。</summary>
    public static string DisplayLoaderText(CompProject project)
    {
        var list = new List<string>();
        foreach (var loader in project.ModLoaders)
        {
            AddUnique(list, LoaderName(loader));
        }

        return list.Count > 0 ? string.Join("、", list) : "原版 / 通用";
    }

    /// <summary>
    /// 项目支持的游戏版本：按版本号从大到小排序（首位就是最大版本），
    /// 过滤加载器等非版本项，过多时折叠为“等 N 个版本”。
    /// </summary>
    public static string DisplayGameVersions(CompProject project)
    {
        var list = project.GameVersions
            .Select(GameVersionName)
            .Where(name => name.Length > 0 && name.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name, VersionComparer)
            .ToList();

        if (list.Count == 0)
        {
            return "未知";
        }

        return list.Count <= 6
            ? string.Join("、", list)
            : string.Join("、", list.Take(5)) + $" 等 {list.Count} 个版本";
    }

    /// <summary>
    /// Minecraft 版本号比较（语义与 PCL-CE McVersionComparer 一致）：
    /// 版本号拆成字母段/数字段，数字段按数值比较，缺少的一段按 0 处理，
    /// 因此 1.10 > 1.9、1.21.1 > 1.21 > 1.20.6 > 1.20.1、26.3 > 26.2；
    /// pre / rc / snapshot / experimental 记为负值，低于同号正式版。
    /// </summary>
    public static int CompareGameVersion(string? left, string? right)
    {
        var a = VersionTokens(left);
        var b = VersionTokens(right);
        var count = Math.Max(a.Count, b.Count);
        for (var i = 0; i < count; i++)
        {
            var textA = i < a.Count ? a[i] : "";
            var textB = i < b.Count ? b[i] : "";
            var valueA = VersionTokenValue(textA);
            var valueB = VersionTokenValue(textB);
            if (valueA != valueB)
            {
                return valueA < valueB ? -1 : 1;
            }

            // 只有两侧都存在该段时才做文本比较；缺失的一段已经按 0 处理（1.21 == 1.21.0）
            if (valueA == 0 && i < a.Count && i < b.Count)
            {
                var textCompare = string.CompareOrdinal(textA, textB);
                if (textCompare != 0)
                {
                    return textCompare < 0 ? -1 : 1;
                }
            }
        }

        return 0;
    }

    /// <summary>按 Minecraft 版本号从大到小排序的比较器（供筛选下拉与版本展示复用）。</summary>
    public static readonly IComparer<string> VersionComparer =
        Comparer<string>.Create(CompareGameVersion);

    private static List<string> VersionTokens(string? version)
    {
        var text = Normalize(version)
            .ToLowerInvariant()
            .Replace("快照", "snapshot")
            .Replace("预览版", "pre")
            .Replace("实验性", "experimental");
        return VersionTokenPattern.Matches(text).Select(match => match.Value).ToList();
    }

    private static readonly Regex VersionTokenPattern =
        new("[a-z]+|[0-9]+", RegexOptions.Compiled);

    private static double VersionTokenValue(string token)
        => token switch
        {
            "rc" => -1,
            "pre" => -2,
            "snapshot" => -3,
            "experimental" => -4,
            _ => double.TryParse(token, out var value) ? value : 0
        };

    /// <summary>更新时间：缺失时返回固定文案。</summary>
    public static string DisplayUpdateTime(CompProject project)
        => project.LastUpdate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未知";

    /// <summary>加载器 slug → 显示名；未知值原文首字母大写，空值返回空串（由调用方跳过）。</summary>
    public static string LoaderName(string loader)
    {
        if (string.IsNullOrWhiteSpace(loader))
        {
            return "";
        }

        var key = loader.Trim();
        if (LoaderNames.TryGetValue(key, out var name))
        {
            return name;
        }

        return key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..];
    }

    /// <summary>文件发布类型中文（对应 PCL-CE ReleaseType 资源）。</summary>
    public static string FileStatusName(CompFileStatus status)
        => status switch
        {
            CompFileStatus.Beta => "测试版",
            CompFileStatus.Alpha => "早期测试版",
            _ => "正式版"
        };

    /// <summary>资源类型中文名（收藏夹分类与筛选用）。</summary>
    public static string TypeName(CompResourceType type)
        => type switch
        {
            CompResourceType.Mod => "模组",
            CompResourceType.ModPack => "整合包",
            CompResourceType.ResourcePack => "资源包",
            CompResourceType.Shader => "光影",
            CompResourceType.DataPack => "数据包",
            CompResourceType.World => "世界",
            _ => "资源"
        };

    /// <summary>
    /// 分类筛选项：固定全集（与 PCL-CE 各 PageDownload*.xaml 的 SearchTags 一一对应），
    /// 不随搜索结果变化；标签格式为 “CurseForge 分类 Id/Modrinth slug”，空的一侧表示该源不过滤。
    /// </summary>
    public static IReadOnlyList<CompCategoryOption> FilterCategories(CompResourceType type)
        => type switch
        {
            CompResourceType.Mod =>
            [
                new("世界元素", "406/worldgen"), new("生物群系", "407/"),
                new("维度", "410/"), new("矿物与资源", "408/"),
                new("天然结构", "409/"), new("科技", "412/technology"),
                new("管道与物流", "415/"), new("自动化", "4843/"),
                new("能源", "417/"), new("红石", "4558/"),
                new("食物与烹饪", "436/food"), new("农业", "416/"),
                new("游戏机制", "/game-mechanics"), new("运输", "414/transportation"),
                new("仓储", "420/storage"), new("魔法", "419/magic"),
                new("冒险", "422/adventure"), new("装饰", "424/decoration"),
                new("生物", "411/mobs"), new("实用", "5191/utility"),
                new("装备与工具", "434/equipment"), new("创造模式", "9026/"),
                new("性能优化", "6814/optimization"), new("信息显示", "423/"),
                new("服务器", "435/social"), new("支持库", "421/library")
            ],
            CompResourceType.ModPack =>
            [
                new("多人", "4484/"), new("性能优化", "/optimization"),
                new("硬核", "4479/challenging"), new("战斗", "4483/combat"),
                new("任务", "4478/quests"), new("科技", "4472/technology"),
                new("魔法", "4473/magic"), new("冒险", "4475/adventure"),
                new("水槽包", "/kitchen-sink"), new("探索", "4476/"),
                new("小游戏", "4477/"), new("科幻", "4471/"),
                new("空岛", "4736/"), new("原版改良", "5128/"),
                new("FTB", "4487/"), new("基于地图", "4480/"),
                new("轻量整合", "4481/lightweight"), new("大型整合", "4482/")
            ],
            CompResourceType.ResourcePack =>
            [
                new("原版风", "403/vanilla-like"), new("写实风", "400/realistic"),
                new("现代风", "401/"), new("中世纪", "402/"),
                new("蒸汽朋克", "399/"), new("主题化", "/themed"),
                new("简洁", "/simplistic"), new("装饰", "/decoration"),
                new("战斗", "/combat"), new("实用", "/utility"),
                new("改良", "/tweaks"), new("鬼畜", "/cursed"),
                new("生物", "/entities"), new("含声音", "/audio"),
                new("含字体", "5244/fonts"), new("含模型", "/models"),
                new("含语言", "/locale"), new("含 UI", "/gui"),
                new("核心着色器", "/core-shaders"), new("动态效果", "404/"),
                new("兼容模组", "4465/modded"), new("8x 或更低", "/8x-"),
                new("16x", "393/16x"), new("32x", "394/32x"), new("48x", "/48x"),
                new("64x", "395/64x"), new("128x", "396/128x"),
                new("256x", "397/256x"), new("512x 或更高", "398/512x+")
            ],
            CompResourceType.Shader =>
            [
                new("原版风", "6555/vanilla-like"), new("幻想风", "6554/fantasy"),
                new("写实风", "6553/realistic"), new("半写实风", "/semi-realistic"),
                new("卡通风", "/cartoon"), new("彩色光照", "/colored-lighting"),
                new("路径追踪", "/path-tracing"), new("PBR", "/pbr"),
                new("反射", "/reflections"), new("极低", "/potato"),
                new("低", "/low"), new("中", "/medium"), new("高", "/high")
            ],
            CompResourceType.DataPack =>
            [
                new("世界元素", "/worldgen"), new("科技", "6951/technology"),
                new("游戏机制", "/game-mechanics"), new("运输", "/transportation"),
                new("仓储", "/storage"), new("魔法", "6952/magic"),
                new("冒险", "6948/adventure"), new("幻想", "6949/"),
                new("装饰", "/decoration"), new("生物", "/mobs"),
                new("实用", "6953/utility"), new("装备与工具", "/equipment"),
                new("性能优化", "/optimization"), new("服务器", "/social"),
                new("支持库", "6950/library"), new("模组相关", "6946/")
            ],
            CompResourceType.World =>
            [
                new("冒险", "248/"), new("创造", "249/"),
                new("小游戏", "250/"), new("跑酷", "251/"),
                new("解谜", "252/"), new("生存", "253/"),
                new("模组世界", "4464/")
            ],
            _ => []
        };

    /// <summary>版本标识中文：CurseForge 的 Client/Server 伪版本显示为客户端/服务端。</summary>
    public static string GameVersionName(string? version)
    {
        var value = Normalize(version);
        return value.ToLowerInvariant() switch
        {
            "client" => "客户端",
            "server" => "服务端",
            _ => value
        };
    }

    /// <summary>文件标题：优先 DisplayName，回退文件名；把英文 " for " 连接词换成中文分隔点。</summary>
    public static string DisplayFileName(CompFile file)
    {
        var raw = Normalize(file.DisplayName);
        if (raw.Length == 0)
        {
            raw = Normalize(file.FileName);
        }

        if (raw.Length == 0)
        {
            return "未命名文件";
        }

        return raw.Replace(" for ", " · ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 简介中文翻译（PCL-CE 同源的 mcimirror 在线翻译，无需密钥）：
    /// 命中缓存直接返回；失败/空结果回退 <see cref="DisplayDescription"/>，不向 UI 抛出异常。
    /// </summary>
    public static async Task<string> TranslateDescriptionAsync(
        DownloadClient client,
        CompProject project,
        CancellationToken cancellationToken = default)
    {
        var fallback = DisplayDescription(project);
        if (string.IsNullOrWhiteSpace(project.Id))
        {
            return fallback;
        }

        var cacheKey = (project.FromCurseForge ? "curseforge:" : "modrinth:") + project.Id;
        if (DescriptionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached.Length > 0 ? cached : fallback;
        }

        try
        {
            var source = project.FromCurseForge ? "curseforge" : "modrinth";
            var text = await client
                .GetStringOnceAsync(
                    $"{DescriptionEndpoint}/{source}/{Uri.EscapeDataString(project.Id)}",
                    TimeSpan.FromSeconds(8),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize(text, TranslateJsonContext.Default.TranslateResponse);
            var translated = Normalize(response?.Translated);
            if (translated.Length > 0)
            {
                DescriptionCache[cacheKey] = translated;
                return translated;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 翻译是增强项：网络/解析失败时静默回退原文。
        }

        DescriptionCache.TryAdd(cacheKey, "");
        return fallback;
    }

    private static string CategorySlugName(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return UnknownCategory;
        }

        return ModrinthCategories.TryGetValue(slug.Trim(), out var name) ? name : UnknownCategory;
    }

    private static string LookupName(string slug)
        => string.IsNullOrWhiteSpace(slug) || !NameDatabase.Value.TryGetValue(slug, out var name)
            ? ""
            : name;

    private static void AddUnique(List<string> tags, string value)
    {
        if (value.Length == 0 || tags.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        tags.Add(value);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var parts = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).Trim();
    }

    // ---------- 内置译名库（PCL-CE Resources/mcmod.buf：GZip 压缩的 Protobuf List<CompDatabaseEntry>） ----------

    private static IReadOnlyDictionary<string, string> LoadNameDatabase()
    {
        try
        {
            using var raw = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(NameDatabaseResource);
            if (raw is null)
            {
                return new Dictionary<string, string>();
            }

            using var gzip = new GZipStream(raw, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            var data = buffer.ToArray();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;
            while (position < data.Length)
            {
                if (!TryReadVarint(data, ref position, out var key))
                {
                    break;
                }

                var field = (int)(key >> 3);
                var wire = (int)(key & 7);
                if (field != 1 || wire != 2)
                {
                    if (!SkipField(data, ref position, wire))
                    {
                        break;
                    }

                    continue;
                }

                if (!TryReadVarint(data, ref position, out var length))
                {
                    break;
                }

                var end = position + (int)length;
                if (end < position || end > data.Length)
                {
                    break;
                }

                string? chineseName = null;
                string? curseForgeSlug = null;
                string? modrinthSlug = null;
                while (position < end)
                {
                    if (!TryReadVarint(data, ref position, out var entryKey))
                    {
                        position = end;
                        break;
                    }

                    var entryField = (int)(entryKey >> 3);
                    var entryWire = (int)(entryKey & 7);
                    if (entryWire == 2 && entryField is 2 or 3 or 4)
                    {
                        if (!TryReadVarint(data, ref position, out var stringLength))
                        {
                            position = end;
                            break;
                        }

                        var text = Encoding.UTF8.GetString(data, position, (int)stringLength);
                        position += (int)stringLength;
                        switch (entryField)
                        {
                            case 2:
                                chineseName = text;
                                break;
                            case 3:
                                curseForgeSlug = text;
                                break;
                            default:
                                modrinthSlug = text;
                                break;
                        }
                    }
                    else if (!SkipField(data, ref position, entryWire))
                    {
                        position = end;
                        break;
                    }
                }

                position = end;
                var name = Normalize(chineseName);
                if (name.Length == 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(curseForgeSlug))
                {
                    names.TryAdd(curseForgeSlug!, name);
                }

                if (!string.IsNullOrWhiteSpace(modrinthSlug))
                {
                    names.TryAdd(modrinthSlug!, name);
                }
            }

            return names;
        }
        catch
        {
            // 资源缺失或数据损坏时退化为“无译名”，标题回退源站原文。
            return new Dictionary<string, string>();
        }
    }

    private static bool TryReadVarint(byte[] data, ref int position, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (position < data.Length)
        {
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
            if (shift > 63)
            {
                return false;
            }
        }

        return false;
    }

    private static bool SkipField(byte[] data, ref int position, int wire)
    {
        switch (wire)
        {
            case 0:
                return TryReadVarint(data, ref position, out _);
            case 1:
                position += 8;
                return position <= data.Length;
            case 2:
                if (!TryReadVarint(data, ref position, out var length))
                {
                    return false;
                }

                position += (int)length;
                return position <= data.Length;
            case 5:
                position += 4;
                return position <= data.Length;
            default:
                return false;
        }
    }

    private sealed class TranslateResponse
    {
        [JsonPropertyName("translated")]
        public string? Translated { get; set; }
    }

    /// <summary>翻译响应的 source-gen 元数据，支持裁剪。</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(TranslateResponse))]
    private sealed partial class TranslateJsonContext : JsonSerializerContext;
}

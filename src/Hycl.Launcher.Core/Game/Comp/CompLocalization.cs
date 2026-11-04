using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 社区资源中文显示层（对应 PCL-CE 的 Comp.Category 资源键 / TranslatedName / ChineseDescription）：
/// 分类名走内置中文映射表，项目标题走内置 MC 百科译名库（PCL-CE Resources/mcmod.buf，Apache-2.0），
/// 简介优先取 mcimirror 在线翻译（无需密钥），失败回退原文；任何兜底都不会是空白或 null。
/// UI 只绑定这里产出的字符串。
/// </summary>
public static class CompLocalization
{
    private const string NameDatabaseResource = "Hycl.Launcher.Core.Resources.mcmod.buf";

    private const string DescriptionEndpoint = "https://mod.mcimirror.top/translate";

    private const string UnknownCategory = "其他";

    private const string EmptyDescription = "暂无简介";

    private const string EmptyTitle = "未命名资源";

    public static readonly string[] LoaderSlugs = ["forge", "fabric", "quilt", "neoforge"];

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

    /// <summary>项目支持的游戏版本：过滤加载器等非版本项，过多时折叠为“等 N 个版本”。</summary>
    public static string DisplayGameVersions(CompProject project)
    {
        var list = new List<string>();
        foreach (var version in project.GameVersions)
        {
            var name = GameVersionName(version);
            if (name.Length > 0 && name.Any(char.IsDigit))
            {
                AddUnique(list, name);
            }
        }

        if (list.Count == 0)
        {
            return "未知";
        }

        return list.Count <= 6
            ? string.Join("、", list)
            : string.Join("、", list.Take(5)) + $" 等 {list.Count} 个版本";
    }

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
            var response = System.Text.Json.JsonSerializer.Deserialize<TranslateResponse>(text);
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
}

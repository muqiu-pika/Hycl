namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 分类筛选选项合并服务：静态中文常用表在前（CompLocalization.FilterCategories），
/// 再用 Modrinth /v2/tags/category 与 CurseForge /v1/categories 双 API 动态补齐其余全部分类，
/// 结果缓存 7 天（分类树极少变动）。任一 API 失败静默降级为静态表，绝不拦住搜索。
/// </summary>
public static class CompCategoryService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    public static async Task<IReadOnlyList<CompCategoryOption>> GetMergedOptionsAsync(
        CompResourceType type,
        CompRequester requester,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var key = $"categories-{type}";
        var cached = CompApiCache.TryGet<List<CompCategoryOption>>(cacheDirectory, key, Ttl);
        if (cached is { Count: > 0 })
        {
            return cached;
        }

        var merged = CompLocalization.FilterCategories(type).ToList();
        // 静态表两侧任一命中即视为已存在（"curseforgeId/modrinthSlug" 双段格式）
        var knownCf = staticsCfIds(merged);
        var knownMr = staticsModrinthSlugs(merged);
        var dynamicAdded = 0;

        try
        {
            var modrinthType = type switch
            {
                CompResourceType.Mod => "mod",
                CompResourceType.ResourcePack => "resourcepack",
                CompResourceType.Shader => "shader",
                CompResourceType.DataPack => "datapack",
                CompResourceType.ModPack => "modpack",
                _ => "" // Modrinth 不收录世界
            };
            if (modrinthType.Length > 0)
            {
                var tags = await new ModrinthClient(requester).GetTagsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var tag in tags.Where(tag => tag.ProjectType == modrinthType && !knownMr.Contains(tag.Category)))
                {
                    merged.Add(new CompCategoryOption(tag.Category, "/" + tag.Category));
                    knownMr.Add(tag.Category);
                    dynamicAdded++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Modrinth 分类补全失败：继续尝试 CurseForge
        }

        try
        {
            var classId = CurseForgeClient.ToClassId(type);
            var categories = await new CurseForgeClient(requester).GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var category in categories.Where(category => category.ClassId == classId && !knownCf.Contains(category.Id.ToString())))
            {
                merged.Add(new CompCategoryOption(category.Name, category.Id + "/"));
                knownCf.Add(category.Id.ToString());
                dynamicAdded++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // CurseForge 分类补全失败：保留静态表
        }

        // 只有真的补到了动态项才落盘，避免某次双 API 全挂时把纯静态表钉住 7 天
        if (dynamicAdded > 0)
        {
            CompApiCache.Save(cacheDirectory, key, merged);
        }

        return merged;
    }

    private static HashSet<string> staticsCfIds(IEnumerable<CompCategoryOption> options)
        => options
            .Select(option => option.Tag.Split('/', 2)[0])
            .Where(part => part.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> staticsModrinthSlugs(IEnumerable<CompCategoryOption> options)
        => options
            .Select(option => option.Tag.Split('/', 2) is { Length: 2 } parts ? parts[1] : "")
            .Where(part => part.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}

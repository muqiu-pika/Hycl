using System.Text.Json.Nodes;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 版本 JSON 深合并（移植自 PCL-CE MergeJson*.cs，用 System.Text.Json 重新实现）：
/// libraries 拼接、mainClass/覆盖项以加载器为准、minecraftArguments 合并去重、
/// LWJGL2/3 冲突清理、移除 _comment_/inheritsFrom/jar 并写入最终 id。
/// </summary>
public static class VersionJsonMerger
{
    /// <summary>
    /// 合并多个版本 JSON。合并顺序对应 PCL-CE：base 为原版，
    /// 随后依次 OptiFine → Forge → NeoForge → Cleanroom → LiteLoader → Fabric → LegacyFabric。
    /// 失败会抛出异常。
    /// </summary>
    /// <param name="outputId">最终版本的 id（同时决定输出文件名）。</param>
    /// <param name="minecraftJson">原版版本 JSON。</param>
    /// <param name="loaderJsons">按合并顺序排列的加载器 JSON。</param>
    public static JsonObject Merge(string outputId, JsonObject minecraftJson, params JsonObject?[] loaderJsons)
    {
        ArgumentNullException.ThrowIfNull(minecraftJson);
        var outputJson = minecraftJson.DeepClone().AsObject();

        // minecraftArguments 合并：原版 + 各加载器，去重（对应 PCL-CE _SplitMergeArguments）
        var allArguments = new List<string?> { GetMinecraftArguments(outputJson) };
        foreach (var loader in loaderJsons)
        {
            allArguments.Add(GetMinecraftArguments(loader));
        }

        var mergedArguments = SplitMergeArguments(string.Join(' ', allArguments))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var loaderJson in loaderJsons)
        {
            MergeLoaderJson(outputJson, loaderJson);
        }

        RemoveConflictingLwjgl2(outputJson);

        var realArguments = string.Join(' ', mergedArguments);
        if (!string.IsNullOrEmpty(realArguments.Replace(" ", "")))
        {
            outputJson["minecraftArguments"] = realArguments;
        }

        outputJson.Remove("_comment_");
        outputJson.Remove("inheritsFrom");
        outputJson.Remove("jar");
        outputJson["id"] = outputId;
        return outputJson;
    }

    /// <summary>JSON 深合并：对象递归合并，数组拼接（对应 Newtonsoft Merge 默认 Concat 行为）。</summary>
    public static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceNode) in source)
        {
            if (sourceNode is null)
            {
                continue;
            }

            if (target[key] is JsonObject targetObject && sourceNode is JsonObject sourceObject)
            {
                MergeInto(targetObject, sourceObject);
                continue;
            }

            if (target[key] is JsonArray targetArray && sourceNode is JsonArray sourceArray)
            {
                foreach (var item in sourceArray)
                {
                    targetArray.Add(item?.DeepClone());
                }

                continue;
            }

            target[key] = sourceNode.DeepClone();
        }
    }

    private static void MergeLoaderJson(JsonObject outputJson, JsonObject? loaderJson)
    {
        if (loaderJson is null)
        {
            return;
        }

        loaderJson.Remove("releaseTime");
        loaderJson.Remove("time");
        MergeInto(outputJson, loaderJson);
    }

    /// <summary>移除与 LWJGL3 冲突的 LWJGL2 支持库（对应 PCL-CE _RemoveConflictingLwjgl2）。</summary>
    public static void RemoveConflictingLwjgl2(JsonObject outputJson)
    {
        if (outputJson["libraries"] is not JsonArray finalLibs
            || !finalLibs.OfType<JsonObject>().Any(IsLwjgl3Library))
        {
            return;
        }

        for (var i = finalLibs.Count - 1; i >= 0; i--)
        {
            if (finalLibs[i] is JsonObject lib && IsLwjgl2Library(lib))
            {
                finalLibs.RemoveAt(i);
            }
        }
    }

    private static bool IsLwjgl3Library(JsonObject lib)
    {
        var segments = (lib["name"]?.ToString() ?? "").Split(':');
        return segments.Length >= 3
               && segments[0] == "org.lwjgl"
               && segments[2].StartsWith("3.", StringComparison.Ordinal);
    }

    private static bool IsLwjgl2Library(JsonObject lib)
    {
        var segments = (lib["name"]?.ToString() ?? "").Split(':');
        return segments.Length >= 3
               && (segments[0] == "org.lwjgl.lwjgl"
                   || (segments[0] == "org.lwjgl"
                       && segments[2].StartsWith("2.", StringComparison.Ordinal)));
    }

    /// <summary>参数串拆分：-Dxxx 之类带参选项与其值合并为一项（对应 PCL-CE _SplitMergeArguments）。</summary>
    public static List<string> SplitMergeArguments(string allArguments)
    {
        var rawArguments = allArguments
            .Split(' ')
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => l.Trim())
            .ToList();
        var splitArguments = new List<string>();
        foreach (var argument in rawArguments)
        {
            if (argument.StartsWith('-'))
            {
                splitArguments.Add(argument);
            }
            else if (splitArguments.Count != 0
                     && splitArguments[^1].StartsWith('-')
                     && !splitArguments[^1].Contains(' '))
            {
                splitArguments[^1] = $"{splitArguments[^1]} {argument}";
            }
            else
            {
                splitArguments.Add(argument);
            }
        }

        return splitArguments;
    }

    private static string GetMinecraftArguments(JsonObject? json)
        => json?["minecraftArguments"]?.ToString() ?? " ";

    /// <summary>
    /// 1.16.5 的 authlib 2.1.28 修复（对应 PCL-CE 的 ToolFixAuthlib，默认开启）：
    /// 替换为 2.3.31 版本并同步校验值。
    /// </summary>
    public static string FixAuthlib(string versionJson)
    {
        return versionJson
            .Replace("2.1.28/authlib-2.1.28.jar", "2.3.31/authlib-2.3.31.jar", StringComparison.Ordinal)
            .Replace("com.mojang:authlib:2.1.28", "com.mojang:authlib:2.3.31", StringComparison.Ordinal)
            .Replace("ad54da276bf59983d02d5ed16fc14541354c71fd", "bbd00ca33b052f73a6312254780fc580d2da3535", StringComparison.Ordinal)
            .Replace("\"76328\"", "\"87662\"", StringComparison.Ordinal)
            .Replace(":76328,", ":87662,", StringComparison.Ordinal);
    }
}

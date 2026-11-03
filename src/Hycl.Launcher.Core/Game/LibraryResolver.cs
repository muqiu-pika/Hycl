using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.Game;

/// <summary>库文件（artifact 或 native）；Urls 为按下载源策略排序的候选地址。</summary>
public sealed record LibraryFile(string RelativePath, IReadOnlyList<string> Urls, long Size, string? Sha1);

/// <summary>
/// Maven 坐标解析与 artifacts/natives 选择。
/// </summary>
public static class LibraryResolver
{
    public static string GetPathFromName(string name)
    {
        var extension = "jar";
        var atIndex = name.IndexOf('@');
        if (atIndex >= 0)
        {
            extension = name[(atIndex + 1)..];
            name = name[..atIndex];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return name;
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    public static LibraryFile? ResolveArtifact(LibraryInfo library, RuleEnvironment environment, DownloadPolicy policy)
    {
        var download = library.Downloads?.Artifact;
        var relativePath = download?.Path;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = GetPathFromName(library.Name);
        }

        var url = download?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            var baseUrl = string.IsNullOrWhiteSpace(library.Url) ? DownloadSources.OfficialLibraries + "/" : library.Url;
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }

            url = baseUrl + relativePath;
        }

        return new LibraryFile(relativePath, DownloadSources.GetLibraryUrls(url, policy), download?.Size ?? 0, download?.Sha1);
    }

    /// <summary>当前平台对应的 native 分类器（旧版本需要解压到 natives 目录）。</summary>
    public static LibraryFile? ResolveNative(LibraryInfo library, RuleEnvironment environment, DownloadPolicy policy)
    {
        if (library.Natives is null || library.Natives.Count == 0)
        {
            return null;
        }

        var osName = environment.OsName;
        if (!library.Natives.TryGetValue(osName, out var classifier) || string.IsNullOrWhiteSpace(classifier))
        {
            return null;
        }

        // ${arch} 替换为 32/64
        if (classifier.Contains("${arch}", StringComparison.Ordinal))
        {
            classifier = classifier.Replace("${arch}", environment.Arch is "x86" or "32" ? "32" : "64", StringComparison.Ordinal);
        }

        var download = library.Downloads?.Classifiers;
        if (download is not null && download.TryGetValue(classifier, out var exact))
        {
            var path = exact.Path ?? GetPathFromName(library.Name + ":" + classifier);
            return new LibraryFile(path, DownloadSources.GetLibraryUrls(exact.Url, policy), exact.Size, exact.Sha1);
        }

        var relativePath = GetPathFromName(library.Name + ":" + classifier);
        var baseUrl = string.IsNullOrWhiteSpace(library.Url) ? DownloadSources.OfficialLibraries + "/" : library.Url;
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new LibraryFile(relativePath, DownloadSources.GetLibraryUrls(baseUrl + relativePath, policy), 0, null);
    }

    /// <summary>某版本在指定下载源策略下需要下载的库文件（去重后）。</summary>
    public static IReadOnlyList<LibraryFile> CollectLibraries(
        VersionInfo version,
        RuleEnvironment environment,
        DownloadPolicy policy)
    {
        var results = new Dictionary<string, LibraryFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment))
            {
                continue;
            }

            // 带 natives 的旧式库只有分类器文件（无分类器 artifact 在 maven 上不存在），跳过 artifact
            var artifact = library.Natives is { Count: > 0 }
                ? null
                : ResolveArtifact(library, environment, policy);
            if (artifact is not null && !results.ContainsKey(artifact.RelativePath))
            {
                results[artifact.RelativePath] = artifact;
            }

            var native = ResolveNative(library, environment, policy);
            if (native is not null && !results.ContainsKey(native.RelativePath))
            {
                results[native.RelativePath] = native;
            }
        }

        return results.Values.ToList();
    }
}

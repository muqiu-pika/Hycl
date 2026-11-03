using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.Game;

/// <summary>库文件（artifact 或 native）。</summary>
public sealed record LibraryFile(string RelativePath, string Url, long Size, string? Sha1);

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

    public static LibraryFile? ResolveArtifact(LibraryInfo library, RuleEnvironment environment)
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

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new LibraryFile(relativePath, url, download?.Size ?? 0, download?.Sha1);
    }

    /// <summary>当前平台对应的 native 分类器（旧版本需要解压到 natives 目录）。</summary>
    public static LibraryFile? ResolveNative(LibraryInfo library, RuleEnvironment environment)
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
            return new LibraryFile(path, exact.Url, exact.Size, exact.Sha1);
        }

        var relativePath = GetPathFromName(library.Name + ":" + classifier);
        var baseUrl = string.IsNullOrWhiteSpace(library.Url) ? DownloadSources.OfficialLibraries + "/" : library.Url;
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new LibraryFile(relativePath, baseUrl + relativePath, 0, null);
    }

    /// <summary>某版本在指定下载源下需要下载的库文件（去重后）。</summary>
    public static IReadOnlyList<LibraryFile> CollectLibraries(
        VersionInfo version,
        RuleEnvironment environment,
        DownloadSourceKind source)
    {
        var results = new Dictionary<string, LibraryFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment))
            {
                continue;
            }

            var artifact = ResolveArtifact(library, environment);
            if (artifact is not null && !results.ContainsKey(artifact.RelativePath))
            {
                results[artifact.RelativePath] = artifact with
                {
                    Url = DownloadSources.RewriteResourceUrl(source, artifact.Url)
                };
            }

            var native = ResolveNative(library, environment);
            if (native is not null && !results.ContainsKey(native.RelativePath))
            {
                results[native.RelativePath] = native with
                {
                    Url = DownloadSources.RewriteResourceUrl(source, native.Url)
                };
            }
        }

        return results.Values.ToList();
    }
}

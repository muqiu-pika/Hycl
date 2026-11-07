using System.IO.Compression;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>实例内容目录中的一个条目（模组 / 资源包 / 光影 / 投影 / 截图）。</summary>
public sealed record LocalContentFile(
    string Name,
    string Path,
    long Size,
    DateTime Modified,
    bool IsDirectory,
    bool IsEnabled);

/// <summary>导出实例时可勾选的内容（对应 PCL-CE PageInstanceExport 的选项）。</summary>
public sealed record InstanceExportOptions(
    bool IncludeMods,
    bool IncludeResourcePacks,
    bool IncludeShaderPacks,
    bool IncludeSaves,
    bool IncludeScreenshots,
    bool IncludeSchematics,
    bool IncludeServers,
    bool IncludeConfig);

/// <summary>
/// 实例内容的通用管理：扫描目录、启用/禁用模组、删除到回收站、把实例导出为 zip。
/// 目录本身由实例设置（版本隔离 / 独立存档目录）决定，这里不猜路径。
/// </summary>
public static class InstanceContentService
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>扫描目录：只保留指定扩展名；支持禁用后缀（模组）与子目录（资源包/光影包）。</summary>
    public static IReadOnlyList<LocalContentFile> Scan(
        string directory,
        IReadOnlyCollection<string> extensions,
        bool includeDirectories = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var allowed = extensions
            .Select(extension => extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<LocalContentFile>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                var effectiveName = enabled ? name : name[..^DisabledSuffix.Length];
                if (!allowed.Contains(Path.GetExtension(effectiveName)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    results.Add(new LocalContentFile(
                        effectiveName,
                        path,
                        info.Length,
                        info.LastWriteTime,
                        false,
                        enabled));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个不可读文件不影响其余条目
                }
            }

            if (includeDirectories)
            {
                foreach (var path in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new DirectoryInfo(path);
                        results.Add(new LocalContentFile(
                            info.Name,
                            path,
                            GetDirectorySize(path),
                            info.LastWriteTime,
                            true,
                            true));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        return results
            .OrderByDescending(item => item.Modified)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>启用 / 禁用模组：在文件名后追加或移除 .disabled（与 PCL-CE 一致）。</summary>
    public static (bool Success, string Message) ToggleEnabled(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (false, "文件不存在，可能已被移动");
            }

            if (path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var target = path[..^DisabledSuffix.Length];
                if (File.Exists(target))
                {
                    return (false, $"无法启用：{Path.GetFileName(target)} 已存在");
                }

                File.Move(path, target);
                return (true, $"已启用：{Path.GetFileName(target)}");
            }

            var disabledPath = path + DisabledSuffix;
            if (File.Exists(disabledPath))
            {
                return (false, $"无法禁用：{disabledPath} 已存在");
            }

            File.Move(path, disabledPath);
            return (true, $"已禁用：{Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"切换失败：{ex.Message}");
        }
    }

    /// <summary>把文件/目录删除到回收站（Windows）或兜底目录。</summary>
    public static bool Delete(string path, string gameDirectory, LauncherLogger logger)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Directory.Exists(path)
            ? RecycleBin.DeleteDirectory(path, gameDirectory, name, logger)
            : RecycleBin.DeleteFile(path, gameDirectory, name, logger);
    }

    /// <summary>把实例导出为 zip，逐文件回报进度；先写临时文件再原子替换，避免产生半成品。</summary>
    public static async Task ExportAsync(
        string instanceDirectory,
        string contentRoot,
        string zipPath,
        InstanceExportOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(instanceDirectory))
        {
            throw new IOException($"实例目录不存在：{instanceDirectory}");
        }

        var entries = new List<(string Source, string Entry)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string source, string entry)
        {
            var normalized = entry.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                return;
            }

            entries.Add((source, normalized));
        }

        void AddDirectory(string root, string prefix, IReadOnlyCollection<string>? extensions = null)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (extensions is not null)
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (!extensions.Contains(extension))
                    {
                        continue;
                    }
                }

                var relative = Path.GetRelativePath(root, file);
                AddFile(file, $"{prefix}/{relative}");
            }
        }

        // 版本本体：versions/<实例>/ 下的 json 与 jar
        foreach (var file in Directory.EnumerateFiles(instanceDirectory))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".json" or ".jar")
            {
                AddFile(file, Path.GetFileName(file));
            }
        }

        if (options.IncludeMods)
        {
            AddDirectory(Path.Combine(contentRoot, "mods"), "mods");
        }

        if (options.IncludeResourcePacks)
        {
            AddDirectory(Path.Combine(contentRoot, "resourcepacks"), "resourcepacks");
        }

        if (options.IncludeShaderPacks)
        {
            AddDirectory(Path.Combine(contentRoot, "shaderpacks"), "shaderpacks");
        }

        if (options.IncludeSaves)
        {
            AddDirectory(Path.Combine(contentRoot, "saves"), "saves");
        }

        if (options.IncludeScreenshots)
        {
            AddDirectory(Path.Combine(contentRoot, "screenshots"), "screenshots");
        }

        if (options.IncludeSchematics)
        {
            AddDirectory(Path.Combine(contentRoot, "schematics"), "schematics");
        }

        if (options.IncludeConfig)
        {
            AddDirectory(Path.Combine(contentRoot, "config"), "config");
            foreach (var name in new[] { "options.txt", "optionsof.txt" })
            {
                var file = Path.Combine(contentRoot, name);
                if (File.Exists(file))
                {
                    AddFile(file, name);
                }
            }
        }

        if (options.IncludeServers)
        {
            var file = Path.Combine(contentRoot, "servers.dat");
            if (File.Exists(file))
            {
                AddFile(file, "servers.dat");
            }
        }

        if (entries.Count == 0)
        {
            throw new IOException("没有可导出的内容，请至少勾选一项");
        }

        var temporary = zipPath + ".hycl-tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var done = 0;
                foreach (var (source, entry) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var archiveEntry = archive.CreateEntry(entry, CompressionLevel.Optimal);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await using var output = archiveEntry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    done++;
                    progress?.Report(done * 100.0 / entries.Count);
                }
            }

            File.Move(temporary, zipPath, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>人类可读的文件大小（与实例管理页一致）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024:0.#} MB";
        }

        return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
    }

    private static long GetDirectorySize(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}

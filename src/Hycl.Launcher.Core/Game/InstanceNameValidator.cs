using System.IO;
using System.Text.RegularExpressions;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 实例名称校验：对齐 PCL-CE 的 FolderNameValidator（versions 目录下的实例名）。
/// 安装新版本/导入整合包前先校验，禁止覆盖已有实例，也避免创建非法目录名。
/// </summary>
public static class InstanceNameValidator
{
    /// <summary>Windows 保留字（PCL-CE FileSystemValidator 同一份列表）。</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "CLOCK$", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
        "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
        "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    ];

    /// <summary>Minecraft 额外禁止的字符（PCL-CE useMinecraftCharCheck）。</summary>
    private const string ExtraInvalidChars = "!;";

    private static readonly Regex Ntfs83Name = new(@".{2,}~\d", RegexOptions.Compiled);

    /// <summary>自动编号后缀：仅在名称末尾的“-数字”才剥离，避免误伤 1.21.5、26.3 这类版本号。</summary>
    private static readonly Regex TrailingNumberSuffix = new(@"-\d+$", RegexOptions.Compiled);

    /// <summary>只校验名称本身是否合法（不检查重名）。返回 null 表示合法。</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "实例名称不能为空。";
        }

        if (name.StartsWith(' '))
        {
            return "实例名称不能以空格开头。";
        }

        if (name.EndsWith(' '))
        {
            return "实例名称不能以空格结尾。";
        }

        if (name.EndsWith('.'))
        {
            return "实例名称不能以英文句点结尾。";
        }

        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal))
        {
            return "实例名称不能包含 ..。";
        }

        var invalid = Path.GetInvalidFileNameChars()
            .Select(c => c.ToString())
            .Concat(ExtraInvalidChars.Select(c => c.ToString()))
            .Where(name.Contains)
            .Distinct()
            .ToArray();
        if (invalid.Length > 0)
        {
            return $"实例名称包含非法字符：{string.Join("  ", invalid)}，请修改后重试。";
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Any(word => word.Equals(nameWithoutExtension, StringComparison.OrdinalIgnoreCase)))
        {
            return $"实例名称不能使用 Windows 保留字：{nameWithoutExtension}。";
        }

        if (Ntfs83Name.IsMatch(name))
        {
            return "实例名称不能使用 NTFS 8.3 短文件名格式（如 ABC~1）。";
        }

        return null;
    }

    /// <summary>检查 versions 目录下是否已存在同名实例（不区分大小写，含同名文件）。</summary>
    public static string? ValidateCollision(string gameDirectory, string name)
        => IsOccupied(gameDirectory, name) ? DuplicateMessage(name) : null;

    /// <summary>完整校验：名称合法且 versions 目录下无同名实例。</summary>
    public static string? Validate(string gameDirectory, string? name)
        => ValidateName(name) ?? (name is null ? null : ValidateCollision(gameDirectory, name));

    /// <summary>
    /// 返回 versions 目录下可用的实例名：未被占用时原样返回；
    /// 已占用时先剥掉名称末尾的“-数字”得到基名（避免在 26.3-1 上再叠成 26.3-1-1），
    /// 再按基名依次尝试 <c>基名-1</c>、<c>基名-2</c> …（跳过已占用的序号，大小写不敏感）。
    /// </summary>
    public static string GetAvailableName(string gameDirectory, string desiredName)
    {
        var trimmed = desiredName.Trim();
        if (trimmed.Length == 0 || !IsOccupied(gameDirectory, trimmed))
        {
            return trimmed;
        }

        // 已占用：剥掉自动编号后缀；基名本身可用时直接回到基名，否则从 1 开始找最小空位。
        var baseName = TrailingNumberSuffix.Replace(trimmed, "", 1);
        if (baseName.Length == 0)
        {
            baseName = trimmed;
        }

        if (!baseName.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            && !IsOccupied(gameDirectory, baseName))
        {
            return baseName;
        }

        for (var index = 1; ; index++)
        {
            var candidate = $"{baseName}-{index}";
            if (!IsOccupied(gameDirectory, candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>versions 目录下是否已有同名目录或文件（不区分大小写）。</summary>
    private static bool IsOccupied(string gameDirectory, string name)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return false;
        }

        var target = Path.Combine(versionsDirectory, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            return true;
        }

        // Windows 下 Directory.Exists 已不区分大小写；这里再显式枚举一次，
        // 覆盖大小写敏感的文件系统，保持与 PCL-CE ignoreCase = true 的语义一致。
        return Directory.EnumerateDirectories(versionsDirectory)
                   .Select(path => Path.GetFileName(path))
                   .Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase))
               || Directory.EnumerateFiles(versionsDirectory)
                   .Select(path => Path.GetFileName(path))
                   .Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string DuplicateMessage(string name)
        => $"同名实例已存在：{name}，请换一个名称或先删除旧实例。";
}

using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Java;

namespace Hycl.Launcher.Core.ViewModels;

public enum LauncherPage
{
    Home,
    Download,
    Instances,
    Settings,
    About
}

public enum DownloadCategory
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    OptiFine,
    Modpack,
    Mod,
    ResourcePack,
    Shader,
    World,
    Favorite
}

public enum SettingsCategory
{
    Launch,
    Java,
    Download,
    Launcher
}

/// <summary>侧栏分类项。</summary>
public sealed class SidebarItem
{
    public SidebarItem(string key, string title, string subtitle)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
    }

    public string Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Initial => Title.Length > 0 ? Title[..1] : "?";
}

/// <summary>下载列表中的版本项。</summary>
public sealed class VersionItem
{
    public VersionItem(VersionSummary summary, bool isLatestRelease = false)
    {
        Summary = summary;
        IsLatestRelease = isLatestRelease;
    }

    public VersionSummary Summary { get; }

    public bool IsLatestRelease { get; }

    public string Id => Summary.Id;

    public string Type => Summary.Type;

    public bool IsRelease => Summary.Type.Equals("release", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => Summary.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        var other => other
    };

    public string IconText => IsRelease ? "M" : Summary.Type.StartsWith("old", StringComparison.Ordinal) ? "O" : "S";

    public string ReleaseTimeText
    {
        get
        {
            if (DateTimeOffset.TryParse(Summary.ReleaseTime, out var time))
            {
                return time.ToLocalTime().ToString("yyyy-MM-dd");
            }

            return "";
        }
    }

    public string BadgeText => IsLatestRelease ? "最新版" : TypeLabel;
}

/// <summary>Java 下拉项。</summary>
public sealed class JavaChoice
{
    public JavaChoice(string path, string displayName, JavaInstallation? installation = null)
    {
        Path = path;
        DisplayName = displayName;
        Installation = installation;
    }

    public string Path { get; }

    public string DisplayName { get; }

    public JavaInstallation? Installation { get; }

    public static JavaChoice Auto { get; } = new("", "自动选择最合适的 Java");
}

/// <summary>已安装实例项的展示辅助。</summary>
public static class InstanceDisplay
{
    public static string TypeLabel(MinecraftInstance instance) => instance.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => instance.Type
    };

    public static string LastWriteText(MinecraftInstance instance)
        => instance.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Hycl.Launcher.Core.Models;

/// <summary>侧边导航分组（如“游戏 / 更多”），对应网页版 SideNav 的分组结构。</summary>
public sealed record NavGroupViewModel(string Title, IReadOnlyList<NavItemViewModel> Items)
{
    /// <summary>
    /// 是否显示分组标题。空标题用于“置顶且不属于任何分组”的条目
    /// （对应 PCL-CE 下载页侧栏顶部的 Minecraft 入口）。
    /// </summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// 侧边导航条目。IconKey 为 UI 层预置矢量图标的键名，Core 层不感知具体图形。
/// </summary>
public partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(string title, string iconKey, object? section = null)
    {
        Title = title;
        IconKey = iconKey;
        Section = section;
    }

    public string Title { get; }

    public string IconKey { get; }

    /// <summary>下载页分区数据；为 null 时表示安装向导入口。</summary>
    public object? Section { get; }

    /// <summary>当前分区是否为选中态（由页面 ViewModel 维护）。</summary>
    [ObservableProperty]
    private bool _isActive;
}

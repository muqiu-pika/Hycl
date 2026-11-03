using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 工具页 ViewModel：工具卡片与下载管理占位。
/// 修复 / 诊断 / 日志分析等能力后续在 Core 内以独立服务实现后再挂接。
/// </summary>
public partial class ToolsPageViewModel : ObservableObject
{
    private readonly Action<string>? _toast;

    public ToolsPageViewModel(Action<string>? toast = null)
    {
        _toast = toast;
    }

    public IReadOnlyList<ToolItemViewModel> Tools { get; } =
    [
        new("hammer", "游戏修复", "检测并修复游戏文件缺失或冲突。", "开始修复"),
        new("cpu", "内存清理", "释放启动器占用的多余内存。", "立即清理"),
        new("wifi", "网络诊断", "测试与下载服务器的连接质量。", "运行诊断"),
        new("scroll-text", "日志分析", "解析最近一次启动日志中的错误。", "查看日志"),
        new("rotate-ccw", "重置启动器", "将启动器设置恢复为默认值。", "重置"),
        new("shirt", "皮肤管理", "上传与管理你的正版角色皮肤。", "管理"),
    ];

    public string DownloadStatus => "当前没有进行中的下载任务。";

    [RelayCommand]
    private void InvokeTool(ToolItemViewModel? tool)
    {
        if (tool is not null)
        {
            _toast?.Invoke($"{tool.Title}（演示）");
        }
    }
}

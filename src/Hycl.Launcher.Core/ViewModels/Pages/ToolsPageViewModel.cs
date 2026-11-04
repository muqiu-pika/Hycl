using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Models;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 工具页 ViewModel：工具卡片与下载管理。
/// 内存清理/日志查看为真实实现，其余卡片待对应能力落地后挂接。
/// </summary>
public partial class ToolsPageViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;

    public ToolsPageViewModel(LauncherServices services, Action<string>? toast = null)
    {
        _services = services;
        _toast = toast;
        _memoryStatus = $"当前工作集 {SystemService.GetCurrentWorkingSetMb():0.0} MB";
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

    /// <summary>内存清理卡片的状态行。</summary>
    [ObservableProperty]
    private string _memoryStatus;

    [RelayCommand]
    private void InvokeTool(ToolItemViewModel? tool)
    {
        if (tool is null)
        {
            return;
        }

        switch (tool.Title)
        {
            case "内存清理":
                ReleaseMemory();
                break;
            case "日志分析":
                if (_services.System.OpenFolder(_services.Paths.LogDirectory))
                {
                    _toast?.Invoke("已打开日志目录");
                }

                break;
            default:
                _toast?.Invoke($"{tool.Title}（演示）");
                break;
        }
    }

    private void ReleaseMemory()
    {
        try
        {
            var before = SystemService.GetCurrentWorkingSetMb();
            var after = _services.System.ReleaseMemory();
            MemoryStatus = $"已释放内存：{before:0.0} MB → {after:0.0} MB";
            _toast?.Invoke(MemoryStatus);
            _services.Logger.Info($"内存清理完成：{before:0.0} MB → {after:0.0} MB");
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"内存清理失败：{ex.Message}");
            _services.Logger.Error("内存清理失败", ex);
        }
    }
}

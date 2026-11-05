using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.UI.Views;

/// <summary>独立启动中页面：只负责展示，失败判定与日志都在 Core 中完成。</summary>
public partial class LaunchProgressPage : UserControl
{
    public LaunchProgressPage()
    {
        InitializeComponent();
    }

    /// <summary>复制失败日志（剪贴板属于 UI 关切，Core 不直接持有）。</summary>
    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LaunchProgressViewModel vm)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(vm.Launch.LaunchLogText));
        await clipboard.SetDataAsync(data);
        vm.Launch.ShowToast("日志已复制到剪贴板");
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.UI.Views;

/// <summary>独立启动中页面：只负责展示，失败判定与日志都在 Core 中完成。</summary>
public partial class LaunchProgressPage : UserControl
{
    private TopLevel? _topLevel;

    public LaunchProgressPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => AttachKeyHandler();
        DetachedFromVisualTree += (_, _) => DetachKeyHandler();
    }

    /// <summary>Esc：失败页等同「返回修正」回到启动页；启动中页取消本次启动（进程已创建时一并结束）。</summary>
    private void AttachKeyHandler()
    {
        if (_topLevel is not null)
        {
            return;
        }

        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    private void DetachKeyHandler()
    {
        if (_topLevel is null)
        {
            return;
        }

        _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not LaunchProgressViewModel vm)
        {
            return;
        }

        if (vm.Launch.IsLaunchFailed)
        {
            vm.Launch.ReturnToLaunchPageCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.Launch.IsLaunching)
        {
            vm.Launch.CancelLaunchCommand.Execute(null);
            e.Handled = true;
        }
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

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hycl.Launcher.Core.ViewModels;

namespace Hycl.Launcher.UI.Views;

public partial class InstancesPage : UserControl
{
    public InstancesPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as InstancesViewModel);
    }

    private void AttachViewModel(InstancesViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.FolderPicker = PickFolderAsync;
        viewModel.ListResetRequested -= OnListResetRequested;
        viewModel.ListResetRequested += OnListResetRequested;
        viewModel.DetailResetRequested -= OnDetailResetRequested;
        viewModel.DetailResetRequested += OnDetailResetRequested;
    }

    private void OnListResetRequested(object? sender, EventArgs e)
    {
        // 删除实例后回到列表开头：滚动复位（选中项已由 VM 置为列表第一项），
        // 避免滚动位置停在旧实例附近让用户误以为还停留在旧实例
        if (this.FindControl<ScrollViewer>("InstanceScroll") is { } scroll)
        {
            scroll.Offset = new Vector(scroll.Offset.X, 0);
        }
    }

    /// <summary>
    /// 删除/复制/重命名导致选中实例被自动切换后，把右侧详情滚回顶部。
    /// 延后到布局完成再设置，避免内容尚未收缩时偏移被旧范围夹住；
    /// 手动点选列表不触发这里，保留用户自己的滚动位置。
    /// </summary>
    private void OnDetailResetRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("DetailScroll") is { } scroll)
            {
                scroll.Offset = new Vector(0, 0);
            }
        }, DispatcherPriority.Background);
    }

    private async Task<string?> PickFolderAsync(string? startDirectory)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? startFolder = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(startDirectory) && Directory.Exists(startDirectory))
            {
                startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(startDirectory));
            }
        }
        catch (ArgumentException)
        {
            // 起始目录无效时退回系统默认位置
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}

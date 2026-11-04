using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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

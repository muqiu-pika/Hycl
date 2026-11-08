using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.UI.Views;

public partial class DownloadPage : UserControl
{
    public DownloadPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as DownloadPageViewModel);
    }

    private void AttachViewModel(DownloadPageViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        // 社区资源下载前选择保存目录：把系统文件夹选择器交给 ViewModel
        foreach (var section in viewModel.CompSections)
        {
            section.FolderPicker = PickFolderAsync;
        }
    }

    /// <summary>底部任务条整块点击进入下载详情页；点取消等子按钮时交给按钮自身处理。</summary>
    private void OnTaskBarPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left)
        {
            return;
        }

        if (HasButtonAncestor(e.Source as Avalonia.Visual))
        {
            return;
        }

        (DataContext as DownloadPageViewModel)?.RequestOpenProgress();
    }

    private static bool HasButtonAncestor(Avalonia.Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is Button)
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private async Task<string?> PickFolderAsync(string startDirectory)
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
                startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(new System.Uri(startDirectory));
            }
        }
        catch (System.ArgumentException)
        {
            // 起始目录无效时退回系统默认位置
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择保存位置",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}

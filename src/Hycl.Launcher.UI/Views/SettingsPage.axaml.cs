using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.UI.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as SettingsPageViewModel);
    }

    private void AttachViewModel(SettingsPageViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.ImagePicker = PickImageAsync;
    }

    /// <summary>个性化-背景图片选择器（png/jpg/webp/bmp）。</summary>
    private async Task<string?> PickImageAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        var imageType = new FilePickerFileType("图片文件")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择启动器背景图片",
            AllowMultiple = false,
            FileTypeFilter = [imageType]
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

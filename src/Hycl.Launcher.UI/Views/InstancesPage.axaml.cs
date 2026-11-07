using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hycl.Launcher.Core.Platform;
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
        viewModel.ModpackFilePicker = PickModpackFileAsync;
        viewModel.JavaFilePicker = PickJavaFileAsync;
        viewModel.ExportFilePicker = PickExportSaveAsync;
        viewModel.Files.FilePicker = PickContentFileAsync;
        viewModel.Files.SaveInfoPicker = PickInfoSaveAsync;
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

    /// <summary>选择本地整合包文件（.zip / .mrpack / .rar，识别失败会给出提示）。</summary>
    private async Task<string?> PickModpackFileAsync(string? startDirectory)
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

        var modpackType = new FilePickerFileType("整合包")
        {
            Patterns = ["*.zip", "*.mrpack", "*.rar"]
        };
        var allFilesType = new FilePickerFileType("所有文件") { Patterns = ["*"] };
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入整合包",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = [modpackType, allFilesType]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>选择整合包导出的保存位置（Modrinth .mrpack 或 CurseForge / MultiMC .zip）。</summary>
    private async Task<string?> PickExportSaveAsync(string? suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        var suggested = string.IsNullOrWhiteSpace(suggestedName) ? "instance.zip" : suggestedName;
        var extension = Path.GetExtension(suggested).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0)
        {
            extension = "zip";
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出整合包",
            SuggestedFileName = suggested,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType("整合包") { Patterns = ["*.mrpack", "*.zip"] },
                new FilePickerFileType("Modrinth 整合包") { Patterns = ["*.mrpack"] },
                new FilePickerFileType("Zip 压缩包") { Patterns = ["*.zip"] }
            ]
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>选择要安装到当前资源分区的本地文件；类型是否匹配由 ViewModel 校验。</summary>
    private async Task<string?> PickContentFileAsync(string? startDirectory)
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

        var contentType = new FilePickerFileType("资源文件")
        {
            Patterns = ["*.jar", "*.zip", "*.schematic", "*.schem", "*.litematic", "*.nbt"]
        };
        var allFilesType = new FilePickerFileType("所有文件") { Patterns = ["*"] };
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "从文件安装资源",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = [contentType, allFilesType]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>选择资源列表信息的导出位置（CSV，UTF-8 BOM）。</summary>
    private async Task<string?> PickInfoSaveAsync(string? suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出资源列表信息",
            SuggestedFileName = string.IsNullOrWhiteSpace(suggestedName) ? "resources.csv" : suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV 表格") { Patterns = ["*.csv"] }]
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>选择 Java 可执行文件（javaw.exe / java），与保存时的 File.Exists 校验语义一致。</summary>
    private async Task<string?> PickJavaFileAsync(string? startDirectory)
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

        var javaExecutableType = new FilePickerFileType("Java 可执行文件")
        {
            Patterns = PlatformInfo.IsWindows ? ["javaw.exe", "java.exe"] : ["java"]
        };
        var allFilesType = new FilePickerFileType("所有文件") { Patterns = ["*"] };
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Java（javaw.exe / java）",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = [javaExecutableType, allFilesType]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

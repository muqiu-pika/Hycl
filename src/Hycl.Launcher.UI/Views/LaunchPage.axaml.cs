using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.UI.Views;

public partial class LaunchPage : UserControl
{
    private LaunchPageViewModel? _viewModel;
    private TopLevel? _topLevel;
    private bool _dialogKeyHandlerAttached;

    public LaunchPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as LaunchPageViewModel);
        AttachedToVisualTree += (_, _) => _topLevel = TopLevel.GetTopLevel(this);
        DetachedFromVisualTree += (_, _) =>
        {
            RemoveDialogKeyHandler();
            _topLevel = null;
        };
    }

    private void AttachViewModel(LaunchPageViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is null)
        {
            RefreshDialogState();
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshDialogState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LaunchPageViewModel.IsCloseGameConfirmVisible)
            or nameof(LaunchPageViewModel.IsMsCancelConfirmVisible))
        {
            RefreshDialogState();
        }
        else if (args.PropertyName == nameof(LaunchPageViewModel.MsUserCode)
                 && !string.IsNullOrEmpty(_viewModel?.MsUserCode))
        {
            // 设备码一出现就自动复制，玩家只需去浏览器粘贴/输入
            _ = CopyUserCodeAsync();
        }
    }

    private void OnUserCodeTapped(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        // PointerReleased 比 Tapped 对 Border+文本子树的命中更宽容
        _ = CopyUserCodeAsync();
    }

    /// <summary>把设备码复制到剪贴板；成功/失败都给 toast。</summary>
    private async Task CopyUserCodeAsync()
    {
        if (_viewModel is not { } viewModel || string.IsNullOrEmpty(viewModel.MsUserCode))
        {
            return;
        }

        try
        {
            var top = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("窗口未就绪");
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(viewModel.MsUserCode));
            await (top.Clipboard ?? throw new InvalidOperationException("剪贴板不可用")).SetDataAsync(data);
            viewModel.ShowToast($"已复制设备代码 {viewModel.MsUserCode}，请在浏览器中粘贴");
        }
        catch (Exception ex)
        {
            viewModel.ShowToast($"复制失败，请手动输入代码 {viewModel.MsUserCode}（{ex.Message}）");
        }
    }

    /// <summary>离线皮肤弹窗里的「本地皮肤文件」：选好 PNG 后交给 ViewModel 校验与应用。</summary>
    private async void OnPickLocalSkinClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var file = await PickLocalSkinFileAsync().ConfigureAwait(true);
        await viewModel.ApplyLocalSkinAsync(file).ConfigureAwait(true);
    }

    private async Task<string?> PickLocalSkinFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        var pngType = new Avalonia.Platform.Storage.FilePickerFileType("皮肤图片（PNG）")
        {
            Patterns = ["*.png"]
        };
        var allFilesType = new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*"] };
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择本地皮肤文件",
            AllowMultiple = false,
            FileTypeFilter = [pngType, allFilesType]
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>弹窗打开时把焦点移到确认按钮，并在 Tunnel 阶段拦截 Esc / Enter；两个确认弹窗同时只显示最上层的。</summary>
    private void RefreshDialogState()
    {
        var msConfirm = _viewModel?.IsMsCancelConfirmVisible == true;
        var anyDialog = msConfirm || _viewModel?.IsCloseGameConfirmVisible == true;
        if (_viewModel is null || !anyDialog)
        {
            RemoveDialogKeyHandler();
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("LaunchButton")?.Focus());
            return;
        }

        _topLevel ??= TopLevel.GetTopLevel(this);
        if (!_dialogKeyHandlerAttached && _topLevel is not null)
        {
            _topLevel.AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
            _dialogKeyHandlerAttached = true;
        }

        var focusName = msConfirm ? "ConfirmMsCancelButton" : "ConfirmCloseButton";
        Dispatcher.UIThread.Post(() => this.FindControl<Button>(focusName)?.Focus());
    }

    private void RemoveDialogKeyHandler()
    {
        if (!_dialogKeyHandlerAttached)
        {
            return;
        }

        _topLevel?.RemoveHandler(KeyDownEvent, OnDialogKeyDown);
        _dialogKeyHandlerAttached = false;
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs args)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        // 正版登录取消确认叠在最上层，优先响应
        if (viewModel.IsMsCancelConfirmVisible)
        {
            if (args.Key == Key.Escape)
            {
                viewModel.CancelMsCancelConfirmCommand.Execute(null);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter)
            {
                viewModel.ConfirmCancelMsLoginCommand.Execute(null);
                args.Handled = true;
            }

            return;
        }

        if (viewModel.IsCloseGameConfirmVisible != true)
        {
            return;
        }

        if (args.Key == Key.Escape)
        {
            viewModel.CancelCloseGameCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Enter)
        {
            viewModel.ConfirmCloseGameCommand.Execute(null);
            args.Handled = true;
        }
    }
}

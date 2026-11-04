using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            UpdateDialogState(false);
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDialogState(_viewModel.IsCloseGameConfirmVisible);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LaunchPageViewModel.IsCloseGameConfirmVisible))
        {
            UpdateDialogState(_viewModel?.IsCloseGameConfirmVisible == true);
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

    /// <summary>弹窗打开时把焦点移到确认按钮，并在 Tunnel 阶段拦截 Esc / Enter。</summary>
    private void UpdateDialogState(bool open)
    {
        if (open)
        {
            _topLevel ??= TopLevel.GetTopLevel(this);
            if (!_dialogKeyHandlerAttached && _topLevel is not null)
            {
                _topLevel.AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
                _dialogKeyHandlerAttached = true;
            }

            Dispatcher.UIThread.Post(() => this.FindControl<Button>("ConfirmCloseButton")?.Focus());
            return;
        }

        RemoveDialogKeyHandler();
        Dispatcher.UIThread.Post(() => this.FindControl<Button>("LaunchButton")?.Focus());
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
        if (_viewModel?.IsCloseGameConfirmVisible != true)
        {
            return;
        }

        if (args.Key == Key.Escape)
        {
            _viewModel.CancelCloseGameCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Enter)
        {
            _viewModel.ConfirmCloseGameCommand.Execute(null);
            args.Handled = true;
        }
    }
}

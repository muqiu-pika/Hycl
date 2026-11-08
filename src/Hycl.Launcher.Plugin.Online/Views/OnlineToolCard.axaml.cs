using Avalonia.Controls;
using Avalonia.Input;

namespace Hycl.Launcher.Plugin.Online.Views;

/// <summary>陶瓦联机工具卡：注入剪贴板能力后全部交互走 ViewModel。</summary>
public partial class OnlineToolCard : UserControl
{
    public OnlineToolCard()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => InjectClipboard();
    }

    private void InjectClipboard()
    {
        if (DataContext is OnlineToolCardViewModel viewModel && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            viewModel.Clipboard = new TopLevelClipboard(topLevel);
        }
    }

    private sealed class TopLevelClipboard(TopLevel topLevel) : ICardClipboard
    {
        public async Task CopyAsync(string text)
        {
            if (topLevel.Clipboard is not { } clipboard)
            {
                return;
            }

            // Avalonia 12 剪贴板为 DataTransfer API
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
    }
}

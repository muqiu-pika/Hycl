using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 文案变化时做一次短促淡入（与启动过程页的状态文案观感一致），
/// 用于下载/安装的当前步骤与文件名等高频刷新文本。重复刷新会取消上一段动画，不会堆积。
/// </summary>
public class FadeTextBlock : TextBlock
{
    private static readonly Animation FadeIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(220),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters = { new Setter(OpacityProperty, 0.3d) },
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                Setters = { new Setter(OpacityProperty, 1d) },
            },
        },
    };

    private CancellationTokenSource? _fadeCts;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            PlayFade();
        }
    }

    private void PlayFade()
    {
        _fadeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fadeCts = cts;
        _ = FadeAsync(cts.Token);
    }

    private async Task FadeAsync(CancellationToken token)
    {
        try
        {
            await FadeIn.RunAsync(this, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 文本在动画中途又变化：由新的动画接管
        }
        catch (Exception)
        {
            // 动画失败不影响进度展示
        }
    }
}

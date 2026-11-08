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
/// 文案变化时做一次短促淡入（与启动过程页的状态文案观感一致）。
/// 只在文本稳定一段时间后再次变化时才淡入：下载进度每 120ms 刷新一次文案时保持完全不透明，
/// 避免透明度反复跳动造成“闪烁”；动画失败或被打断都不影响文本最终显示。
/// </summary>
public class FadeTextBlock : TextBlock
{
    private const double QuietMilliseconds = 700;

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
    private DateTime _lastTextChangeAt = DateTime.MinValue;
    private bool _animating;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var quiet = (now - _lastTextChangeAt).TotalMilliseconds;
        _lastTextChangeAt = now;
        if (_animating || quiet < QuietMilliseconds)
        {
            // 高频刷新（下载进度）：不做淡入，直接显示新文案
            return;
        }

        PlayFade();
    }

    private void PlayFade()
    {
        _animating = true;
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
            // 用户切页/控件卸载：正常收尾即可
        }
        catch (Exception)
        {
            // 动画失败不影响进度展示
        }
        finally
        {
            _animating = false;
            if (_fadeCts?.Token == token)
            {
                _fadeCts = null;
            }

            // 动画收尾保证文本完全可见，不停在中间透明度
            Opacity = 1;
        }
    }
}

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Hycl.Launcher.Core.ViewModels;
using Hycl.Launcher.Core.ViewModels.Pages;
using Hycl.Launcher.UI.Views;

namespace Hycl.Launcher.UI;

public partial class MainWindow : Window
{
    /// <summary>页面视图缓存：同一页面只创建一次，切换标签页时复用（对应 PCL-CE 的页面单例）。</summary>
    private readonly Dictionary<object, Control> _pageCache = new();

    private readonly MainViewModel _viewModel;

    private readonly Panel _pageHost;

    private Control? _activeView;

    private CancellationTokenSource? _pageSwitchCts;

    private int _pageSwitchGeneration;

    // 与 PCL-CE 页面切换一致的节奏：内容进入 100~350ms 缓出，旧页退场 120ms 缓入
    private static readonly KeySpline EnterEaseOut = new(0.16, 1, 0.3, 1);

    private static readonly KeySpline ExitEaseIn = new(0.7, 0, 0.84, 0);

    private static readonly KeySpline EnterSettle = new(0.4, 0, 0.2, 1);

    /// <summary>主窗口 ViewModel 就绪回调（供主题等跨切面在启动时应用配置）。</summary>
    public static event Action<MainViewModel>? ViewModelReady;

    public MainWindow()
    {
        InitializeComponent();
        // UI 层只负责把视图绑定到 Core 层的 ViewModel，不写业务逻辑
        var services = Core.LauncherServices
            .CreateAsync(new AvaloniaDispatcher())
            .GetAwaiter()
            .GetResult();
        _viewModel = new MainViewModel(services);
        DataContext = _viewModel;
        _pageHost = this.FindControl<Panel>("PageHost")
            ?? throw new InvalidOperationException("缺少 PageHost 容器");

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            {
                ShowPage(_viewModel.CurrentPage);
            }
        };
        _viewModel.CloseRequested += (_, _) => Close();
        _viewModel.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        ShowPage(_viewModel.CurrentPage);

        // 窗口关闭：取消页面切换动画与所有页面级在途任务，关闭文件句柄
        Closing += (_, _) =>
        {
            _pageSwitchCts?.Cancel();
            _pageSwitchCts?.Dispose();
            _pageSwitchCts = null;
            _viewModel.Shutdown();
        };

        // 窗口显示后在后台预热其余页面，避免首次切换标签页时再实例化大页面造成卡顿。
        Opened += (_, _) => Dispatcher.UIThread.Post(PrewarmPages, DispatcherPriority.Background);

        SetupResizeHandles();
        UpdateFrameForState();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateFrameForState();
            }
        };

        // 构造完成：通知外部（App）应用持久化主题等跨切面配置
        ViewModelReady?.Invoke(_viewModel);
    }

    /// <summary>切换到指定页面；视图实例只创建一次并复用，切换只做可见性翻转。</summary>
    private void ShowPage(object? page)
    {
        if (page is null)
        {
            return;
        }

        var view = GetOrCreateView(page);
        if (view.DataContext != page)
        {
            view.DataContext = page;
        }

        if (view.Parent is not Panel)
        {
            _pageHost.Children.Add(view);
        }

        SetActiveView(view);
    }

    private void SetActiveView(Control view)
    {
        if (_activeView == view && view.IsVisible)
        {
            return;
        }

        var previous = _activeView;
        _activeView = view;

        // 快速连续切页：作废旧过渡，避免上一段的退场动画仍把新页压到 0 透明度
        _pageSwitchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pageSwitchCts = cts;
        var generation = ++_pageSwitchGeneration;

        foreach (var child in _pageHost.Children)
        {
            if (child != view && child != previous)
            {
                child.IsVisible = false;
                child.Opacity = 1;
                child.RenderTransform = null;
            }
        }

        // 新页置于上层但先保持完全透明：旧页退场期间画面上只有一层可见，避免双层叠影
        view.Opacity = 0;
        view.ZIndex = 1;
        view.IsVisible = true;
        if (previous is not null)
        {
            previous.ZIndex = 0;
        }

        _ = RunPageSwitchAsync(view, previous, generation, cts.Token);
    }

    /// <summary>
    /// 页面切换分两段串行执行，任一时刻屏幕上只有一层页面：
    /// 1. 旧页单独快速淡出（约 90ms 缓入，带轻微下移/缩小）；
    /// 2. 旧页彻底隐藏后，新页淡入 + 上移 + 轻微缩放回正（约 180ms 缓出）。
    /// 不做交叉淡入淡出，因此不会出现两层半透明页面叠在一起的拖影。
    /// </summary>
    private async Task RunPageSwitchAsync(Control view, Control? previous, int generation, CancellationToken token)
    {
        try
        {
            if (previous is not null && !ReferenceEquals(previous, view))
            {
                await PlayPageExitAsync(previous, token).ConfigureAwait(true);
                if (generation != _pageSwitchGeneration)
                {
                    return;
                }

                previous.IsVisible = false;
                previous.Opacity = 1;
                previous.RenderTransform = null;
            }

            await PlayPageEnterAsync(view, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被下一次切页接管：交由最新的过渡负责收尾
        }
        finally
        {
            if (generation == _pageSwitchGeneration)
            {
                view.Opacity = 1;
                view.RenderTransform = null;
                if (previous is not null && !ReferenceEquals(previous, _activeView))
                {
                    previous.IsVisible = false;
                    previous.Opacity = 1;
                    previous.RenderTransform = null;
                }
            }
        }
    }

    private static async Task PlayPageEnterAsync(Control view, CancellationToken token)
    {
        var scale = new ScaleTransform(0.99, 0.99);
        var translate = new TranslateTransform(0, 10);
        view.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        view.RenderTransform = new TransformGroup { Children = { scale, translate } };
        view.Opacity = 0;

        await Task.WhenAll(
            // 新页单独淡入并轻微归位：此时旧页已完全隐藏，不会与任何图层叠加
            RunAnimationAsync(view, token, Visual.OpacityProperty, TimeSpan.FromMilliseconds(150),
                (0d, 1d, null), (1d, 1d, EnterEaseOut)),
            RunAnimationAsync(view, token, TranslateTransform.YProperty, TimeSpan.FromMilliseconds(180),
                (0d, 10d, null), (1d, 0d, EnterEaseOut)),
            RunAnimationAsync(view, token, ScaleTransform.ScaleXProperty, TimeSpan.FromMilliseconds(180),
                (0d, 0.99d, null), (0.7d, 1.003d, EnterEaseOut), (1d, 1d, EnterSettle)),
            RunAnimationAsync(view, token, ScaleTransform.ScaleYProperty, TimeSpan.FromMilliseconds(180),
                (0d, 0.99d, null), (0.7d, 1.003d, EnterEaseOut), (1d, 1d, EnterSettle)))
            .ConfigureAwait(true);
    }

    private static async Task PlayPageExitAsync(Control view, CancellationToken token)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        view.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        view.RenderTransform = new TransformGroup { Children = { scale, translate } };

        await Task.WhenAll(
            // 旧页在单独一层上快速退场，不参与交叉混合
            RunAnimationAsync(view, token, Visual.OpacityProperty, TimeSpan.FromMilliseconds(90),
                (0d, 1d, null), (1d, 0d, ExitEaseIn)),
            RunAnimationAsync(view, token, TranslateTransform.YProperty, TimeSpan.FromMilliseconds(90),
                (0d, 0d, null), (1d, 6d, ExitEaseIn)),
            RunAnimationAsync(view, token, ScaleTransform.ScaleXProperty, TimeSpan.FromMilliseconds(90),
                (0d, 1d, null), (1d, 0.995d, ExitEaseIn)),
            RunAnimationAsync(view, token, ScaleTransform.ScaleYProperty, TimeSpan.FromMilliseconds(90),
                (0d, 1d, null), (1d, 0.995d, ExitEaseIn)))
            .ConfigureAwait(true);
    }

    private static Task RunAnimationAsync(
        Control control,
        CancellationToken token,
        AvaloniaProperty property,
        TimeSpan duration,
        params (double Cue, double Value, KeySpline? Spline)[] frames)
    {
        var animation = new Animation
        {
            Duration = duration,
            IterationCount = new IterationCount(1),
            FillMode = FillMode.Forward,
        };
        foreach (var (cue, value, spline) in frames)
        {
            var frame = new KeyFrame
            {
                Cue = new Cue(cue),
                Setters = { new Setter(property, value) },
            };
            if (spline is not null)
            {
                frame.KeySpline = spline;
            }

            animation.Children.Add(frame);
        }

        return animation.RunAsync(control, token);
    }

    /// <summary>
    /// 预热全部页面：先把视图挂进 PageHost（获得完整的样式环境），
    /// 再显式做一次测量/排列，让 DataTemplate、容器与编译期绑定提前落地。
    /// 仅实例化视图并不能完成这些工作，首次切页时仍要在 UI 线程补做（实测 150~600ms）。
    /// </summary>
    private void PrewarmPages()
    {
        var pages = new object[] { _viewModel.Launch, _viewModel.Download, _viewModel.Settings, _viewModel.Tools };
        foreach (var page in pages)
        {
            var view = GetOrCreateView(page);
            if (view.Parent is not Panel)
            {
                _pageHost.Children.Add(view);
            }
        }

        var size = _pageHost.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        foreach (var child in _pageHost.Children)
        {
            child.IsVisible = true;
        }

        _pageHost.Measure(size);
        _pageHost.Arrange(new Rect(size));

        // 立即恢复为仅当前页可见，中间不会产生渲染帧。
        foreach (var child in _pageHost.Children)
        {
            child.IsVisible = child == _activeView;
        }
    }

    private Control GetOrCreateView(object page)
    {
        if (_pageCache.TryGetValue(page, out var cached))
        {
            return cached;
        }

        Control view = page switch
        {
            LaunchPageViewModel => new LaunchPage(),
            DownloadPageViewModel => new DownloadPage(),
            SettingsPageViewModel => new SettingsPage(),
            ToolsPageViewModel => new ToolsPage(),
            _ => throw new NotSupportedException($"未知的页面类型：{page.GetType().Name}"),
        };
        view.DataContext = page;
        _pageCache[page] = view;
        return view;
    }

    // —— 以下为自定义标题栏的窗口行为，仅做极薄的事件转发 ——

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 只有按在标题栏空白处才拖动窗口；标签页、品牌、窗口按钮的点击交给子控件
        if (e.Source != sender)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (WindowState != WindowState.Maximized)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHelpClick(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.ShowToast("帮助文档（演示）");

    /// <summary>无边框窗口：把外围 8 个透明条绑到对应的缩放边缘。</summary>
    private void SetupResizeHandles()
    {
        SetupResize("rzLeft", StandardCursorType.LeftSide, WindowEdge.West);
        SetupResize("rzRight", StandardCursorType.RightSide, WindowEdge.East);
        SetupResize("rzTop", StandardCursorType.TopSide, WindowEdge.North);
        SetupResize("rzBottom", StandardCursorType.BottomSide, WindowEdge.South);
        SetupResize("rzTopLeft", StandardCursorType.TopLeftCorner, WindowEdge.NorthWest);
        SetupResize("rzTopRight", StandardCursorType.TopRightCorner, WindowEdge.NorthEast);
        SetupResize("rzBottomLeft", StandardCursorType.BottomLeftCorner, WindowEdge.SouthWest);
        SetupResize("rzBottomRight", StandardCursorType.BottomRightCorner, WindowEdge.SouthEast);
    }

    private void SetupResize(string name, StandardCursorType cursor, WindowEdge edge)
    {
        if (this.FindControl<Control>(name) is not { } handle)
        {
            return;
        }

        handle.Cursor = new Cursor(cursor);
        handle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(edge, e);
            }
        };
    }

    /// <summary>最大化时去除外留白与圆角，避免出现透明缝隙。</summary>
    private void UpdateFrameForState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MainFrame.Margin = maximized ? new Thickness(0) : new Thickness(18);
        MainFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
    }
}

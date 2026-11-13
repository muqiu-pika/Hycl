using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.Core.ViewModels;
using Hycl.Launcher.Core.ViewModels.Pages;
using Hycl.Launcher.UI.Views;

namespace Hycl.Launcher.UI;

public partial class MainWindow : Window
{
    /// <summary>页面视图缓存：同一页面只创建一次，切换标签页时复用（对应 PCL-CE 的页面单例）。</summary>
    private readonly Dictionary<object, Control> _pageCache = new();

    private MainViewModel? _viewModel;

    private readonly Panel _pageHost;

    private Control? _activeView;

    private CancellationTokenSource? _pageSwitchCts;

    private int _pageSwitchGeneration;

    /// <summary>启动动画逐帧计时器（主窗口内铺满播放）。</summary>
    private DispatcherTimer? _startupTimer;

    private Stopwatch? _startupClock;

    private readonly RotateTransform _startupRingRotation = new();

    private bool _startupDismissing;

    private bool _startupFinished;

    /// <summary>是否播放启动动画（设置-个性化「启动动画」，服务就绪后确认）。</summary>
    private bool _startupAnimationEnabled = true;

    private double _startupDismissAt;

    /// <summary>启动层最短展示时长（秒），避免窗口构造太快时动画一闪而过。</summary>
    private const double StartupMinShowSeconds = 1.6;

    // —— 主窗口尺寸记忆：非最大化时的尺寸单独保存，最大化期间仍能记住还原尺寸 ——
    private double _lastNormalWidth = 900;

    private double _lastNormalHeight = 570;

    private bool _restoreMaximized;

    /// <summary>退场第一段：启动层自身图标与文字淡出时长（实测原 0.26s，缩短后整体退场更快）。</summary>
    private const double StartupContentFadeSeconds = 0.18;

    /// <summary>退场第二段：启动层背景淡出、界面内容浮现时长。</summary>
    private const double StartupBackdropFadeSeconds = 0.22;

    private bool _startupBackdropFading;

    private double _startupBackdropAt;

    // 与 PCL-CE 页面切换一致的节奏：内容进入 100~350ms 缓出，旧页退场 120ms 缓入
    private static readonly KeySpline EnterEaseOut = new(0.16, 1, 0.3, 1);

    private static readonly KeySpline ExitEaseIn = new(0.7, 0, 0.84, 0);

    private static readonly KeySpline EnterSettle = new(0.4, 0, 0.2, 1);

    /// <summary>主窗口 ViewModel 就绪回调（供主题等跨切面在启动时应用配置）。</summary>
    public static event Action<MainViewModel>? ViewModelReady;

    public MainWindow()
    {
        PerfTrace.Mark("window_ctor");
        InitializeComponent();
        PerfTrace.Mark("xaml_loaded");

        // 恢复上次关闭时的窗口尺寸/最大化状态（文件读取失败则沿用 XAML 默认值）
        if (LauncherWindowState.Load() is { } savedState)
        {
            Width = Math.Max(MinWidth, savedState.Width);
            Height = Math.Max(MinHeight, savedState.Height);
            _lastNormalWidth = Width;
            _lastNormalHeight = Height;
            _restoreMaximized = savedState.Maximized;
        }

        // 启动层需要铺满整窗（含标题栏），而标题栏与内容分属 Grid 的不同行；
        // 挂到最外层 RootFrame 才能覆盖全部，圆角仍由 Win32 窗口区域裁切。
        var rootFrame = this.FindControl<Grid>("RootFrame");
        var startupOverlay = this.FindControl<Panel>("StartupOverlay");
        if (rootFrame is not null && startupOverlay?.Parent is Panel host)
        {
            host.Children.Remove(startupOverlay);
            rootFrame.Children.Add(startupOverlay);
        }

        // 首帧只搭骨架：服务与页面 ViewModel 在窗口显示后再接入（AttachServices），
        // 避免配置读取/VM 构造挡在首帧前。
        _pageHost = this.FindControl<Panel>("PageHost")
            ?? throw new InvalidOperationException("缺少 PageHost 容器");

        // 首帧前只读一个开关：关闭启动动画的用户不会看到静态启动层（与优化前一致）。
        // 正式配置加载仍在首帧之后，这里只流式解析 config.json 的 enableAnimations。
        var prefStart = PerfTrace.Now;
        _startupAnimationEnabled = LauncherPaths.ReadEnableAnimationsFlag();
        PerfTrace.MarkSince("prefs_read", prefStart);

        // 窗口关闭：取消页面切换动画与所有页面级在途任务，关闭文件句柄
        Closing += (_, _) =>
        {
            // 记住窗口尺寸：最大化时保存还原尺寸，下次打开仍能回到同样的窗口大小
            var maximized = WindowState == WindowState.Maximized;
            LauncherWindowState.Save(
                maximized ? _lastNormalWidth : Width,
                maximized ? _lastNormalHeight : Height,
                maximized);
            _pageSwitchCts?.Cancel();
            _pageSwitchCts?.Dispose();
            _pageSwitchCts = null;
            _startupTimer?.Stop();
            _startupTimer = null;
            _viewModel?.Shutdown();
        };

        // 首屏页面在窗口显示后构建：启动层先出帧，页面构建落在启动动画的最短展示窗口内
        Opened += (_, _) => PerfTrace.Mark("window_opened");
        Opened += (_, _) => Dispatcher.UIThread.Post(CreateInitialPage, DispatcherPriority.Background);
        Opened += (_, _) => ScheduleWindowRegionUpdate();
        Opened += (_, _) =>
        {
            if (_restoreMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        };
        // 启动动画在主窗口内铺满播放，结束后柔和淡出露出界面
        Opened += (_, _) => StartStartupAnimation();
        if (PerfTrace.IsEnabled)
        {
            Opened += (_, _) =>
            {
                var memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                memoryTimer.Tick += (_, _) =>
                {
                    memoryTimer.Stop();
                    PerfTrace.MarkMemory("idle_memory");
                };
                memoryTimer.Start();
            };
        }
        // 尺寸变化期间必须立刻撤掉圆角区域：SetWindowRgn 的区域不会随窗口伸缩，
        // 拖拽放大时旧区域会把新扩出的部分裁掉（对角内容缺失/残影），缩小则看不出来
        SizeChanged += (_, _) =>
        {
            OnWindowSizeChanged();
            if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
            {
                _lastNormalWidth = Width;
                _lastNormalHeight = Height;
            }
        };

        SetupResizeHandles();
        UpdateFrameForState();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateFrameForState();
            }
        };
    }

    /// <summary>用户是否关闭了启动动画（首帧前已读取，供 App 决定是否先备好服务再 Show）。</summary>
    internal bool StartupAnimationDisabled => !_startupAnimationEnabled;

    /// <summary>
    /// 窗口已显示后接入 Core 服务与页面 ViewModel（App 在 <see cref="Window.Show"/> 之后调用）。
    /// 这段工作落在启动层展示期内，不再挡在首帧前。
    /// </summary>
    internal void AttachServices(Core.LauncherServices services)
    {
        if (_viewModel is not null)
        {
            return;
        }

        var vm = new MainViewModel(services);
        PerfTrace.Mark("vm_ready");
        _viewModel = vm;
        DataContext = vm;

        // 用户在设置里关闭「启动动画」：启动层已按默认开始播放，立即收起并恢复界面
        if (!services.Config.EnableAnimations)
        {
            _startupAnimationEnabled = false;
            StopStartupAnimation();
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            {
                ShowPage(vm.CurrentPage);
            }
        };
        vm.CloseRequested += (_, _) => Close();
        vm.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;

        // 通知外部（App）应用持久化主题等跨切面配置
        ViewModelReady?.Invoke(vm);
        PerfTrace.Mark("window_ctor_done");

        // 关闭启动动画时窗口还没 Show：直接同步构建首屏，首帧即为完整界面；
        // 播放启动动画时放到下一轮，避免挡住启动层动画。
        if (StartupAnimationDisabled)
        {
            CreateInitialPage();
        }
        else
        {
            Dispatcher.UIThread.Post(CreateInitialPage, DispatcherPriority.Background);
        }
    }

    /// <summary>立即收起启动层并恢复标题栏/内容区透明度（用于关闭启动动画的场景）。</summary>
    private void StopStartupAnimation()
    {
        _startupTimer?.Stop();
        _startupTimer = null;
        _startupFinished = true;

        var overlay = this.FindControl<Panel>("StartupOverlay");
        if (overlay is not null)
        {
            overlay.IsVisible = false;
        }

        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar is not null)
        {
            titleBar.Opacity = 1;
        }

        var contentArea = this.FindControl<Grid>("ContentArea");
        if (contentArea is not null)
        {
            contentArea.Opacity = 1;
        }
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

        _ = RunPageSwitchAsync(view, previous, generation, cts.Token, PerfTrace.Now);
    }

    /// <summary>
    /// 页面切换分两段串行执行，任一时刻屏幕上只有一层页面：
    /// 1. 旧页单独快速淡出（约 55ms 缓入，带轻微下移/缩小）；
    /// 2. 旧页彻底隐藏后，新页淡入 + 上移 + 轻微缩放回正（约 120ms 缓出）。
    /// 不做交叉淡入淡出，因此不会出现两层半透明页面叠在一起的拖影。
    /// 原节奏为 90ms + 180ms（实测每次切页 274~307ms），缩短后每次少约 95ms。
    /// </summary>
    private async Task RunPageSwitchAsync(
        Control view,
        Control? previous,
        int generation,
        CancellationToken token,
        long switchStart)
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
                PerfTrace.MarkSince($"page_switch:{view.GetType().Name}", switchStart);
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

        await AnimateAsync(TimeSpan.FromMilliseconds(120), token, t =>
        {
            // 淡入在前 100ms 内完成，随后保持完全不透明
            var opacityT = Math.Min(1d, t * 1.2d);
            view.Opacity = EnterEaseOut.GetSplineProgress(opacityT);

            translate.Y = 10d * (1d - EnterEaseOut.GetSplineProgress(t));

            var scaleT = t < 0.7d
                ? 0.99d + 0.013d * EnterEaseOut.GetSplineProgress(t / 0.7d)
                : 1.003d - 0.003d * EnterSettle.GetSplineProgress((t - 0.7d) / 0.3d);
            scale.ScaleX = scaleT;
            scale.ScaleY = scaleT;
        }).ConfigureAwait(true);
    }

    private static async Task PlayPageExitAsync(Control view, CancellationToken token)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        view.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        view.RenderTransform = new TransformGroup { Children = { scale, translate } };

        await AnimateAsync(TimeSpan.FromMilliseconds(55), token, t =>
        {
            var eased = ExitEaseIn.GetSplineProgress(t);
            view.Opacity = 1d - eased;
            translate.Y = 6d * eased;
            var s = 1d - 0.005d * eased;
            scale.ScaleX = s;
            scale.ScaleY = s;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 手动逐帧驱动过渡：Avalonia 12 的 <c>Animation.RunAsync</c> 在本项目实测不产生中间帧
    /// （直接跳到终值，页面切换退化为硬切），因此改为按 60fps 回写属性，保证过渡真实可见。
    /// </summary>
    private static async Task AnimateAsync(TimeSpan duration, CancellationToken token, Action<double> apply)
    {
        var start = Stopwatch.GetTimestamp();
        var totalMs = duration.TotalMilliseconds;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            var t = Math.Clamp(elapsedMs / totalMs, 0d, 1d);
            apply(t);
            if (t >= 1d)
            {
                return;
            }

            await Task.Delay(14, token).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 首屏页面在窗口显示后才构建：窗口先出帧，页面构建落在启动动画的最短展示窗口内。
    /// </summary>
    private void CreateInitialPage()
    {
        if (_activeView is not null || _viewModel is null)
        {
            return;
        }

        var start = PerfTrace.Now;
        PerfTrace.Mark("initial_page_begin");
        ShowPage(_viewModel.CurrentPage);
        PerfTrace.MarkSince("initial_page_shown", start);
    }

    private Control GetOrCreateView(object page)
    {
        if (_pageCache.TryGetValue(page, out var cached))
        {
            return cached;
        }

        var createStart = PerfTrace.Now;
        Control view = page switch
        {
            LaunchPageViewModel => new LaunchPage(),
            LaunchProgressViewModel => new LaunchProgressPage(),
            DownloadPageViewModel => new DownloadPage(),
            DownloadProgressPageViewModel => new DownloadProgressPage(),
            InstancesViewModel => new InstancesPage(),
            SettingsPageViewModel => new SettingsPage(),
            ToolsPageViewModel => new ToolsPage(),
            _ => throw new NotSupportedException($"未知的页面类型：{page.GetType().Name}"),
        };
        PerfTrace.MarkSince($"view_create:{page.GetType().Name}", createStart);
        view.DataContext = page;
        _pageCache[page] = view;
        return view;
    }

    // —— 以下为自定义标题栏的窗口行为，仅做极薄的事件转发 ——

    // 触屏拖动状态：Avalonia 的 BeginMoveDrag 仅可靠响应鼠标（触屏指针被控件捕获后
    // Win32 移动循环收不到输入），触屏改为按下记录起点、移动中手动更新窗口位置
    private PixelPoint? _touchDragStartScreen;
    private PixelPoint _touchDragStartWindow;

    /// <summary>
    /// 把指针位置换算成屏幕像素坐标（触屏手动拖动/缩放用）。
    /// 直接以窗口自身为坐标系调用 <c>VisualExtensions.PointToScreen</c>，
    /// 不再把 <c>VisualRoot</c> 强转成 <see cref="Window"/>：Avalonia 12 下窗口的
    /// VisualRoot 可能是 TopLevelHost，强转会抛 InvalidCastException。
    /// </summary>
    private PixelPoint GetPointerScreenPoint(PointerEventArgs e)
        => this.PointToScreen(e.GetPosition(this));

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
            return;
        }

        // 鼠标：交给系统原生移动循环；最大化时 Windows 会先还原原尺寸再跟随拖动，
        // 与系统窗口行为一致。触屏：BeginMoveDrag 收不到输入，改为手动跟随，
        // 最大化状态下不支持拖动还原（双击标题栏可还原）。
        if (e.Pointer.Type == PointerType.Touch)
        {
            if (WindowState == WindowState.Maximized)
            {
                return;
            }

            _touchDragStartScreen = GetPointerScreenPoint(e);
            _touchDragStartWindow = Position;
            e.Pointer.Capture((IInputElement?)sender);
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnTitleBarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_touchDragStartScreen is not { } startScreen)
        {
            return;
        }

        var current = GetPointerScreenPoint(e);
        Position = new PixelPoint(
            _touchDragStartWindow.X + (current.X - startScreen.X),
            _touchDragStartWindow.Y + (current.Y - startScreen.Y));
    }

    private void OnTitleBarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_touchDragStartScreen is null)
        {
            return;
        }

        _touchDragStartScreen = null;
        e.Pointer.Capture(null);
    }

    private void OnTitleBarPointerCaptureLost(object? sender, RoutedEventArgs e)
    {
        _touchDragStartScreen = null;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnFloatingDownloadClick(object? sender, RoutedEventArgs e)
    {
        // 右下角悬浮下载按钮：进入独立下载进度页（下载任务本身不因切页中断），返回时回到进入前的页面
        (DataContext as MainViewModel)?.OpenDownloadProgress();
    }

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

    // 触屏缩放状态：与拖动同理，BeginResizeDrag 对触屏不可靠，手动计算新尺寸/位置
    private PixelPoint? _touchResizeStartScreen;
    private PixelPoint _touchResizeStartWindowPos;
    private Size _touchResizeStartSize;
    private WindowEdge _touchResizeEdge;

    private void SetupResize(string name, StandardCursorType cursor, WindowEdge edge)
    {
        if (this.FindControl<Control>(name) is not { } handle)
        {
            return;
        }

        handle.Cursor = new Cursor(cursor);
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (e.Pointer.Type == PointerType.Touch && WindowState == WindowState.Normal)
            {
                _touchResizeEdge = edge;
                _touchResizeStartScreen = GetPointerScreenPoint(e);
                _touchResizeStartWindowPos = Position;
                _touchResizeStartSize = new Size(Width, Height);
                e.Pointer.Capture(handle);
                e.Handled = true;
                return;
            }

            BeginResizeDrag(edge, e);
        };
        handle.PointerMoved += (_, e) => TouchResizeMove(edge, e);
        handle.PointerReleased += (_, e) => EndTouchResize(e);
        handle.PointerCaptureLost += (_, e) => EndTouchResize(e);
    }

    /// <summary>触屏缩放：以按下时的窗口尺寸/位置为基准，按边缘方向套用指针位移（DIP）。</summary>
    private void TouchResizeMove(WindowEdge edge, PointerEventArgs e)
    {
        if (_touchResizeStartScreen is not { } startScreen)
        {
            return;
        }

        var scale = RenderScaling;
        var current = GetPointerScreenPoint(e);
        var dx = (current.X - startScreen.X) / scale;
        var dy = (current.Y - startScreen.Y) / scale;

        var edgeName = edge.ToString();
        var newWidth = _touchResizeStartSize.Width;
        var newHeight = _touchResizeStartSize.Height;
        var pos = _touchResizeStartWindowPos;

        if (edgeName.Contains("East", StringComparison.Ordinal))
        {
            newWidth = Math.Max(MinWidth, _touchResizeStartSize.Width + dx);
        }

        if (edgeName.Contains("South", StringComparison.Ordinal))
        {
            newHeight = Math.Max(MinHeight, _touchResizeStartSize.Height + dy);
        }

        if (edgeName.Contains("West", StringComparison.Ordinal))
        {
            newWidth = Math.Max(MinWidth, _touchResizeStartSize.Width - dx);
            pos = new PixelPoint(
                pos.X + (int)Math.Round((_touchResizeStartSize.Width - newWidth) * scale),
                pos.Y);
        }

        if (edgeName.Contains("North", StringComparison.Ordinal))
        {
            newHeight = Math.Max(MinHeight, _touchResizeStartSize.Height - dy);
            pos = new PixelPoint(
                pos.X,
                pos.Y + (int)Math.Round((_touchResizeStartSize.Height - newHeight) * scale));
        }

        Width = newWidth;
        Height = newHeight;
        Position = pos;
    }

    private void EndTouchResize(PointerEventArgs e)
    {
        if (_touchResizeStartScreen is null)
        {
            return;
        }

        _touchResizeStartScreen = null;
        e.Pointer.Capture(null);
        // 缩放期间圆角区域更新被防抖延迟，结束后立即应用一次
        ApplyWindowRegion();
    }

    private void EndTouchResize(RoutedEventArgs e)
    {
        if (_touchResizeStartScreen is null)
        {
            return;
        }

        _touchResizeStartScreen = null;
        ApplyWindowRegion();
    }

    /// <summary>最大化时去除圆角，并停用边缘缩放手柄（避免与系统最大化行为冲突）。</summary>
    private void UpdateFrameForState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MainFrame.Margin = new Thickness(0);
        MainFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(10);
        if (this.FindControl<Control>("ResizeLayer") is { } layer)
        {
            layer.IsHitTestVisible = !maximized;
        }

        // 状态切换时先无条件撤掉旧区域：SizeChanged 在某些输入路径（触屏双击还原等）
        // 可能滞后甚至不触发，若跳过本步，最大化前残留的旧尺寸圆角区域会一直裁着
        // 还原后的窗口——露出底下图层（透底）。区域先归零再按新状态重设，任何路径都收敛。
        ClearWindowRegion();
        ScheduleWindowRegionUpdate();
        ScheduleRegionSettleCheck();
    }

    // —— Windows 圆角窗形区域：把圆角外的四角从窗口矩形中裁掉，点击可穿透到下层窗口 ——

    // 圆角区域重设（SetWindowRgn）在缩放过程中每帧调用会造成明显卡顿（触屏尤其明显），
    // 改为 120ms 防抖：连续变化只在停止后应用一次；触屏缩放结束时由 EndTouchResize 立即应用。
    // 但防抖期间若保留旧区域，窗口变大后新扩出的部分会被旧区域裁掉——所以尺寸一变就先把
    // 区域清空（无裁切），等尺寸稳定后再按最新尺寸重设圆角。
    private DispatcherTimer? _regionDebounceTimer;
    private bool _windowRegionApplied;

    // 区域收敛复核：防抖应用后 400ms 再校验一次区域是否与当前窗口矩形一致。
    // 还原动画（DWM）期间 GetWindowRect 可能取到中间帧尺寸，或事件交错导致防抖结果过期；
    // 这道兜底保证任何输入路径（触屏双击还原等）最迟 400ms 后区域与窗口对齐，透底不可能滞留。
    private DispatcherTimer? _regionSettleTimer;
    private int _lastAppliedLeft = int.MinValue;
    private int _lastAppliedTop = int.MinValue;
    private int _lastAppliedWidth = int.MinValue;
    private int _lastAppliedHeight = int.MinValue;

    private void ScheduleRegionSettleCheck()
    {
        if (_regionSettleTimer is null)
        {
            _regionSettleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _regionSettleTimer.Tick += (_, _) =>
            {
                _regionSettleTimer.Stop();
                // 幂等：区域已正确则跳过，过期/缺失则按当前矩形重设
                ApplyWindowRegion();
            };
        }

        _regionSettleTimer.Stop();
        _regionSettleTimer.Start();
    }

    /// <summary>窗口尺寸变化：先撤掉按旧尺寸生成的圆角区域，再防抖按新尺寸重设。</summary>
    private void OnWindowSizeChanged()
    {
        ClearWindowRegion();
        ScheduleWindowRegionUpdate();
        ScheduleRegionSettleCheck();
    }

    /// <summary>撤掉当前圆角窗形区域（恢复为完整矩形，不做任何裁切）。</summary>
    private void ClearWindowRegion()
    {
        if (!_windowRegionApplied)
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == 0)
        {
            return;
        }

        SetWindowRgn(handle.Value, IntPtr.Zero, true);
        _windowRegionApplied = false;
    }

    private void ScheduleWindowRegionUpdate()
    {
        if (_regionDebounceTimer is null)
        {
            _regionDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _regionDebounceTimer.Tick += (_, _) =>
            {
                _regionDebounceTimer.Stop();
                ApplyWindowRegion();
            };
        }

        _regionDebounceTimer.Stop();
        _regionDebounceTimer.Start();
    }

    private void ApplyWindowRegion()
    {
        // 非 Windows 没有设置窗形的 API；窗口已铺满整个矩形，不存在透明留白
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == 0)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            if (_windowRegionApplied)
            {
                SetWindowRgn(handle.Value, IntPtr.Zero, true);
                _windowRegionApplied = false;
            }

            return;
        }

        var rect = new WindowRect();
        if (!GetWindowRect(handle.Value, out rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 幂等：区域已应用且窗口矩形与上次应用时一致 → 跳过（避免 settle 复核与防抖
        // 重复 SetWindowRgn 引起 DWM 重算闪烁）；矩形变了才重设
        if (_windowRegionApplied
            && rect.Left == _lastAppliedLeft
            && rect.Top == _lastAppliedTop
            && width == _lastAppliedWidth
            && height == _lastAppliedHeight)
        {
            return;
        }

        var scale = GetDpiForWindow(handle.Value) / 96d;
        var radius = Math.Max(2, (int)Math.Round(10 * scale));
        // CreateRoundRectRgn 的 w/h 是圆角椭圆的直径，传 2r 才能得到半径 r；
        // 必须与 MainFrame 的 CornerRadius（10 逻辑像素）一致，否则裁切形状会和视觉圆角错位。
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // SetWindowRgn 成功后 region 归系统所有，不能再 DeleteObject
        if (SetWindowRgn(handle.Value, region, true) == 0)
        {
            DeleteObject(region);
            _windowRegionApplied = false;
            return;
        }

        _windowRegionApplied = true;
        _lastAppliedLeft = rect.Left;
        _lastAppliedTop = rect.Top;
        _lastAppliedWidth = width;
        _lastAppliedHeight = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 启动动画：主窗口首帧即在窗口内铺满播放，至少展示 1.6 秒后分两段退场——
    /// 先淡出启动层自身的图标与文字，再淡出背景并让标题栏与页面内容浮现，
    /// 避免两套文字在淡出过程中叠在一起。动画由 UI 线程逐帧驱动。
    /// </summary>
    private void StartStartupAnimation()
    {
        var overlay = this.FindControl<Panel>("StartupOverlay");
        if (!_startupAnimationEnabled)
        {
            // 设置-个性化关闭「启动动画」：直接收起启动层，界面立即完整显示
            if (overlay is not null)
            {
                overlay.IsVisible = false;
            }

            PerfTrace.Mark("splash_skipped");
            return;
        }

        var content = this.FindControl<Panel>("StartupOverlayContent");
        var logo = this.FindControl<Panel>("StartupLogo");
        var glow = this.FindControl<Ellipse>("StartupGlow");
        var ring = this.FindControl<Ellipse>("StartupRing");
        var ambient = this.FindControl<Ellipse>("StartupAmbient");
        var progress = this.FindControl<Border>("StartupProgress");
        var titleBar = this.FindControl<Border>("TitleBar");
        var contentArea = this.FindControl<Grid>("ContentArea");
        if (overlay is null || content is null || logo is null || glow is null
            || ring is null || ambient is null || progress is null)
        {
            return;
        }

        // 启动层完全遮住界面期间先隐藏下层文字，退场时再单独淡入
        if (titleBar is not null)
        {
            titleBar.Opacity = 0;
        }

        if (contentArea is not null)
        {
            contentArea.Opacity = 0;
        }

        ring.RenderTransform = _startupRingRotation;
        _startupClock = Stopwatch.StartNew();
        _startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _startupTimer.Tick += (_, _) => UpdateStartupAnimation(
            overlay, content, logo, glow, ring, ambient, progress, titleBar, contentArea);
        _startupTimer.Start();
    }

    private void UpdateStartupAnimation(
        Panel overlay,
        Panel content,
        Panel logo,
        Ellipse glow,
        Ellipse ring,
        Ellipse ambient,
        Border progress,
        Border? titleBar,
        Grid? contentArea)
    {
        if (_startupFinished || _startupClock is null)
        {
            return;
        }

        var t = _startupClock.Elapsed.TotalSeconds;

        if (!_startupDismissing)
        {
            // 入场：10px 上浮 + 轻微提亮，500ms 缓出
            var intro = EaseOutCubic(Math.Min(1, t / 0.5));
            content.Opacity = 0.9 + intro * 0.1;
            content.RenderTransform = TransformOperations.Parse(
                $"translateY({(1 - intro) * 10:0.##}px)");

            // 图标悬浮、双层辉光呼吸、光环匀速旋转（约 10.6 秒一圈）
            logo.RenderTransform = TransformOperations.Parse(
                $"translateY({Math.Sin(t * 2 * Math.PI / 3.6) * 4:0.##}px)");
            glow.Opacity = 0.3 + 0.16 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 2.8));
            ambient.Opacity = 0.42 + 0.1 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 6.5));
            _startupRingRotation.Angle = t * 34;

            // 进度光带正弦往复，1.9 秒一轮，两端自然减速
            var phase = t % 1.9 / 1.9;
            var travel = 0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI);
            progress.RenderTransform = TransformOperations.Parse(
                $"translateX({travel * 184:0.##}px)");
            progress.Opacity = 0.6 + 0.4 * (0.5 + 0.5 * Math.Sin(phase * 2 * Math.PI));

            if (t >= StartupMinShowSeconds)
            {
                _startupDismissing = true;
                _startupDismissAt = t;
                // 退场期间不再拦截鼠标
                overlay.IsHitTestVisible = false;
            }

            return;
        }

        // 第一段：启动层自己的图标与文字先淡出，避免与下方界面文字叠在一起
        var contentFade = EaseInOutCubic(Math.Min(1,
            (t - _startupDismissAt) / StartupContentFadeSeconds));
        content.Opacity = 1 - contentFade;
        if (contentFade < 1)
        {
            return;
        }

        if (!_startupBackdropFading)
        {
            _startupBackdropFading = true;
            _startupBackdropAt = t;
        }

        // 第二段：背景淡出，同时标题栏与页面内容柔和浮现（全程只有一套文字可见）
        var backdropFade = EaseInOutCubic(Math.Min(1,
            (t - _startupBackdropAt) / StartupBackdropFadeSeconds));
        overlay.Opacity = 1 - backdropFade;
        overlay.RenderTransform = TransformOperations.Parse(
            $"scale({1 + backdropFade * 0.012:0.####})");

        var reveal = EaseOutCubic(backdropFade);
        if (titleBar is not null)
        {
            titleBar.Opacity = reveal;
        }

        if (contentArea is not null)
        {
            contentArea.Opacity = reveal;
        }

        if (backdropFade >= 1)
        {
            _startupFinished = true;
            PerfTrace.Mark("splash_done");
            if (titleBar is not null)
            {
                titleBar.Opacity = 1;
            }

            if (contentArea is not null)
            {
                contentArea.Opacity = 1;
            }

            _startupTimer?.Stop();
            _startupTimer = null;
            _startupClock = null;

            // 启动层已完全不可见：从可视树移除，释放其渐变/图形资源
            overlay.IsVisible = false;
            if (overlay.Parent is Panel overlayHost)
            {
                overlayHost.Children.Remove(overlay);
            }
        }
    }

    private static double EaseOutCubic(double value)
    {
        var inverse = 1 - value;
        return 1 - inverse * inverse * inverse;
    }

    private static double EaseInOutCubic(double value)
    {
        return value < 0.5
            ? 4 * value * value * value
            : 1 - Math.Pow(-2 * value + 2, 3) / 2;
    }
}

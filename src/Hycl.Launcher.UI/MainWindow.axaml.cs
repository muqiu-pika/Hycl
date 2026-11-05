using System.Diagnostics;
using System.Runtime.InteropServices;
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
        Opened += (_, _) => ScheduleWindowRegionUpdate();
        SizeChanged += (_, _) => ScheduleWindowRegionUpdate();

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

        await AnimateAsync(TimeSpan.FromMilliseconds(180), token, t =>
        {
            // 淡入在 150ms 内完成，随后保持完全不透明
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

        await AnimateAsync(TimeSpan.FromMilliseconds(90), token, t =>
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
    /// 预热全部页面：先把视图挂进 PageHost（获得完整的样式环境），
    /// 再显式做一次测量/排列，让 DataTemplate、容器与编译期绑定提前落地。
    /// 仅实例化视图并不能完成这些工作，首次切页时仍要在 UI 线程补做（实测 150~600ms）。
    /// </summary>
    private void PrewarmPages()
    {
        var pages = new object[]
        {
            _viewModel.Launch, _viewModel.LaunchProgress, _viewModel.Download,
            _viewModel.Instances, _viewModel.Settings, _viewModel.Tools
        };
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
            LaunchProgressViewModel => new LaunchProgressPage(),
            DownloadPageViewModel => new DownloadPage(),
            InstancesViewModel => new InstancesPage(),
            SettingsPageViewModel => new SettingsPage(),
            ToolsPageViewModel => new ToolsPage(),
            _ => throw new NotSupportedException($"未知的页面类型：{page.GetType().Name}"),
        };
        view.DataContext = page;
        _pageCache[page] = view;
        return view;
    }

    // —— 以下为自定义标题栏的窗口行为，仅做极薄的事件转发 ——

    // 触屏拖动状态：Avalonia 的 BeginMoveDrag 仅可靠响应鼠标（触屏指针被控件捕获后
    // Win32 移动循环收不到输入），触屏改为按下记录起点、移动中手动更新窗口位置
    private PixelPoint? _touchDragStartScreen;
    private PixelPoint _touchDragStartWindow;

    private PixelPoint GetPointerScreenPoint(PointerEventArgs e, Visual? relativeTo)
        => ((Window)VisualRoot!).PointToScreen(e.GetCurrentPoint(relativeTo).Position);

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

        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchDragStartScreen = GetPointerScreenPoint(e, (Visual?)sender);
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

        var current = GetPointerScreenPoint(e, null);
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
        // 右下角悬浮下载按钮：切回下载页查看详情（下载任务本身不因切页中断）
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedTabIndex = 1;
        }
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
                _touchResizeStartScreen = GetPointerScreenPoint(e, handle);
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
        var current = GetPointerScreenPoint(e, null);
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

        ScheduleWindowRegionUpdate();
    }

    // —— Windows 圆角窗形区域：把圆角外的四角从窗口矩形中裁掉，点击可穿透到下层窗口 ——

    // 圆角区域重设（SetWindowRgn）在缩放过程中每帧调用会造成明显卡顿（触屏尤其明显），
    // 改为 120ms 防抖：连续变化只在停止后应用一次；触屏缩放结束时由 EndTouchResize 立即应用
    private DispatcherTimer? _regionDebounceTimer;

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
            SetWindowRgn(handle.Value, IntPtr.Zero, true);
            return;
        }

        var rect = new WindowRect();
        if (!GetWindowRect(handle.Value, out rect))
        {
            return;
        }

        var scale = GetDpiForWindow(handle.Value) / 96d;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

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
        }
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
}

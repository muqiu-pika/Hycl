using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        ShowPage(_viewModel.CurrentPage);

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

        foreach (var child in _pageHost.Children)
        {
            child.IsVisible = child == view;
        }

        _activeView = view;
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

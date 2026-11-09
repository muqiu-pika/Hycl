using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Plugins;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 工具页：只展示「已启用」插件的入口卡片；点进去切换到该插件自己的功能/设置页。
/// 插件的安装、更新、卸载、启用/禁用都在设置页「插件工坊」完成。
/// </summary>
public partial class ToolsPageViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private readonly Dictionary<string, object> _pageCache = new(StringComparer.Ordinal);
    private string? _currentPluginId;

    public ToolsPageViewModel(LauncherServices services, Action<string>? toast = null)
    {
        _services = services;
        _toast = toast;
        _services.Plugins.PluginsChanged += (_, _) => OnPluginsChanged();
        OnPluginsChanged();
    }

    /// <summary>已启用插件的入口（直接绑定 PluginManager 的可观察集合）。</summary>
    public ObservableCollection<PluginEntryInfo> EnabledPlugins => _services.Plugins.EnabledPlugins;

    [ObservableProperty]
    private object? _currentPluginPage;

    [ObservableProperty]
    private string _currentPluginTitle = "";

    [ObservableProperty]
    private bool _hasEnabledPlugins;

    /// <summary>供首页联机入口直接打开指定插件的功能设置页。</summary>
    public void OpenPluginEntry(PluginEntryInfo entry) => OpenPlugin(entry);

    [RelayCommand]
    private void OpenPlugin(PluginEntryInfo? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.FeaturePageFactory is null)
        {
            _toast?.Invoke($"{entry.Title} 未提供功能设置页（请更新插件）");
            return;
        }

        object page;
        try
        {
            if (!_pageCache.TryGetValue(entry.Id, out var cached))
            {
                cached = entry.FeaturePageFactory()
                    ?? throw new InvalidOperationException("插件返回了空的功能页");
                _pageCache[entry.Id] = cached;
            }

            page = cached;
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"{entry.Title} 功能页加载失败：{ex.Message}");
            return;
        }

        _currentPluginId = entry.Id;
        CurrentPluginTitle = entry.Title;
        CurrentPluginPage = page;
    }

    [RelayCommand]
    private void ClosePlugin()
    {
        _currentPluginId = null;
        CurrentPluginTitle = "";
        CurrentPluginPage = null;
    }

    /// <summary>插件被禁用/卸载时自动退出其功能页，避免停在已失效的界面上。</summary>
    private void OnPluginsChanged()
    {
        var enabled = _services.Plugins.EnabledPlugins;
        HasEnabledPlugins = enabled.Count > 0;

        // 只在插件真正卸载时丢弃功能页实例；禁用/启用复用同一控件，
        // 避免反复切换时重复构造 ViewModel（其订阅的服务事件不会重复叠加）
        foreach (var id in _pageCache.Keys
                     .Where(id => !_services.Plugins.IsLoaded(id))
                     .ToArray())
        {
            _pageCache.Remove(id);
        }

        if (_currentPluginId is not null && enabled.All(entry => entry.Id != _currentPluginId))
        {
            ClosePlugin();
        }
    }

    public void Activate()
    {
    }

    public void Deactivate()
    {
    }
}

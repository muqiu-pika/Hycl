using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 方块/加载器位图图标：Kind 为 ViewModel 的图标键名，
/// 渲染从 PCL-CE 迁移的方块 PNG（正式版=草方块、Forge=铁砧等）。
/// </summary>
public class BlockIcon : TemplatedControl
{
    private static readonly Dictionary<string, string> KindToAsset = new()
    {
        // 版本分类（对应 PCL-CE 的 MinecraftMenu 图标映射）。
        ["grass-block"] = "Grass",
        ["vanilla"] = "Grass",
        ["command-block"] = "CommandBlock",
        ["gold-block"] = "GoldBlock",
        ["cobblestone"] = "CobbleStone",
        // 社区资源导航（对应 PCL-CE 的 Blocks 位图）：收藏夹 = 红石灯（点亮/星标），数据包 = 红石块。
        ["star"] = "RedstoneLampOn",
        ["bookmark"] = "RedstoneBlock",
        // 加载器（对应 PCL-CE 的 PageDownloadInstall 图标映射）。
        ["forge"] = "Anvil",
        ["neoforge"] = "NeoForge",
        ["cleanroom"] = "Cleanroom",
        ["fabric"] = "Fabric",
        ["legacyfabric"] = "Fabric",
        ["quilt"] = "Quilt",
        ["liteloader"] = "Egg",
        ["optifine"] = "GrassPath",
        ["labymod"] = "LabyMod",
        // 整合包（PCL-CE 使用自定义 PCL\Logo.png；本工程暂无该文件时用命令方块作为整合包图标）。
        ["modpack"] = "GoldBlock",
        ["package"] = "CommandBlock",
    };

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<BlockIcon, string?>(nameof(Kind));

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<BlockIcon, Bitmap?>(nameof(Source));

    /// <summary>图标键名（如 "grass-block" / "forge"）。</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>由 Kind 解析出的位图，供模板中的 Image 使用。</summary>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            SetCurrentValue(SourceProperty, Resolve(Kind));
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap?> BitmapCache = new();

    private static Bitmap? Resolve(string? kind)
    {
        if (kind is null || !KindToAsset.TryGetValue(kind, out var assetName))
        {
            return null;
        }

        // 位图按资源名缓存，避免大量版本行各自重复解码 PNG 导致页面卡顿。
        return BitmapCache.GetOrAdd(assetName, static name =>
        {
            try
            {
                return new Bitmap(
                    AssetLoader.Open(new Uri($"avares://Hycl.Launcher.UI/Assets/Blocks/{name}.png")));
            }
            catch (Exception)
            {
                return null;
            }
        });
    }
}

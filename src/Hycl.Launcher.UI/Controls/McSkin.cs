using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Hycl.Launcher.UI.Controls;

public enum McSkinMode
{
    Body,
    Head,
}

/// <summary>
/// 从 64x64 的 Minecraft 皮肤贴图中裁剪并拼出「纸娃娃」全身像 / 头像。
/// 对应网页版 McSkin 组件：正面视图中角色的右臂/右腿在观者左侧。
/// </summary>
public class McSkin : Control
{
    public static readonly StyledProperty<string?> SkinKeyProperty =
        AvaloniaProperty.Register<McSkin, string?>(nameof(SkinKey));

    public static readonly StyledProperty<int> ScaleProperty =
        AvaloniaProperty.Register<McSkin, int>(nameof(Scale), 4);

    public static readonly StyledProperty<McSkinMode> ModeProperty =
        AvaloniaProperty.Register<McSkin, McSkinMode>(nameof(Mode), McSkinMode.Body);

    private static readonly Dictionary<string, Bitmap?> BitmapCache = new();

    // 贴图正面区域：[texX, texY, w, h]（标准 64x64 皮肤布局）
    private static readonly Dictionary<string, (int X, int Y, int W, int H)> Texture = new()
    {
        ["head"] = (8, 8, 8, 8),
        ["headOverlay"] = (40, 8, 8, 8),
        ["body"] = (20, 20, 8, 12),
        ["bodyOverlay"] = (20, 36, 8, 12),
        ["armR"] = (44, 20, 4, 12),
        ["armROverlay"] = (44, 36, 4, 12),
        ["armL"] = (36, 52, 4, 12),
        ["armLOverlay"] = (52, 52, 4, 12),
        ["legR"] = (4, 20, 4, 12),
        ["legROverlay"] = (4, 36, 4, 12),
        ["legL"] = (20, 52, 4, 12),
        ["legLOverlay"] = (4, 52, 4, 12),
    };

    // 在 16 x 32 画布中的位置：[left, top]
    private static readonly Dictionary<string, (int X, int Y)> Placement = new()
    {
        ["head"] = (4, 0),
        ["body"] = (4, 8),
        ["armR"] = (0, 8),
        ["armL"] = (12, 8),
        ["legR"] = (4, 20),
        ["legL"] = (8, 20),
    };

    private static readonly string[] BodyPartOrder =
    [
        "armR", "legR", "legL", "body", "armL", "head",
        "armROverlay", "legROverlay", "legLOverlay", "bodyOverlay", "armLOverlay", "headOverlay",
    ];

    private Bitmap? _bitmap;

    public McSkin()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    /// <summary>皮肤贴图的逻辑名（对应 Assets/skins/{SkinKey}.png）。</summary>
    public string? SkinKey
    {
        get => GetValue(SkinKeyProperty);
        set => SetValue(SkinKeyProperty, value);
    }

    /// <summary>像素放大倍数。</summary>
    public int Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public McSkinMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var scale = Math.Max(1, Scale);
        return Mode == McSkinMode.Head
            ? new Size(8 * scale, 8 * scale)
            : new Size(16 * scale, 32 * scale);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SkinKeyProperty)
        {
            _bitmap = LoadBitmap(SkinKey);
            InvalidateVisual();
        }
        else if (change.Property == ScaleProperty || change.Property == ModeProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null)
        {
            return;
        }

        var scale = Math.Max(1, Scale);

        if (Mode == McSkinMode.Head)
        {
            var bounds = new Rect(0, 0, 8 * scale, 8 * scale);
            using (context.PushClip(new RoundedRect(bounds, 4)))
            {
                DrawPart(context, "head", scale);
                DrawPart(context, "headOverlay", scale);
            }

            return;
        }

        foreach (var key in BodyPartOrder)
        {
            DrawPart(context, key, scale);
        }
    }

    private void DrawPart(DrawingContext context, string key, int scale)
    {
        if (_bitmap is null)
        {
            return;
        }

        var texture = Texture[key];
        var isOverlay = key.EndsWith("Overlay", StringComparison.Ordinal);
        var placementKey = isOverlay ? key[..^"Overlay".Length] : key;
        var placement = Placement[placementKey];

        var source = new Rect(texture.X, texture.Y, texture.W, texture.H);
        var destination = new Rect(
            placement.X * scale,
            placement.Y * scale,
            texture.W * scale,
            texture.H * scale);

        context.DrawImage(_bitmap, source, destination);
    }

    private static Bitmap? LoadBitmap(string? skinKey)
    {
        if (string.IsNullOrWhiteSpace(skinKey))
        {
            return null;
        }

        if (BitmapCache.TryGetValue(skinKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Hycl.Launcher.UI/Assets/skins/{skinKey}.png"));
            var bitmap = new Bitmap(stream);
            BitmapCache[skinKey] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            BitmapCache[skinKey] = null;
            return null;
        }
    }
}

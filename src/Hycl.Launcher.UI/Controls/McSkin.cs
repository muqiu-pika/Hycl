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
/// 同时兼容 64x32 旧版皮肤（四肢贴图与 64x64 不同，另一侧为镜像）与 Alex(slim) 模型（手臂 3 宽）。
/// </summary>
public class McSkin : Control
{
    public static readonly StyledProperty<string?> SkinKeyProperty =
        AvaloniaProperty.Register<McSkin, string?>(nameof(SkinKey));

    /// <summary>皮肤贴图本地文件路径；非空时优先于 SkinKey（正版/皮肤站下载的缓存贴图）。</summary>
    public static readonly StyledProperty<string?> SkinPathProperty =
        AvaloniaProperty.Register<McSkin, string?>(nameof(SkinPath));

    public static readonly StyledProperty<int> ScaleProperty =
        AvaloniaProperty.Register<McSkin, int>(nameof(Scale), 4);

    public static readonly StyledProperty<McSkinMode> ModeProperty =
        AvaloniaProperty.Register<McSkin, McSkinMode>(nameof(Mode), McSkinMode.Body);

    /// <summary>Alex(slim) 模型：手臂宽度为 3 像素（仅 64x64 皮肤生效）。</summary>
    public static readonly StyledProperty<bool> IsSlimProperty =
        AvaloniaProperty.Register<McSkin, bool>(nameof(IsSlim));

    // 键为「路径 + 修改时间 + 大小」/「内置皮肤名」；文件被重新下载（换皮肤）后键会变化，
    // 避免继续命中旧的位图。加载失败不写缓存，下次属性变化时可以重试。
    private static readonly Dictionary<string, Bitmap> BitmapCache = new();

    // 头像（头顶面）：8x8 纹理，两种皮肤格式位置相同
    private const int HeadX = 8, HeadY = 8;
    private const int HeadOverlayX = 40, HeadOverlayY = 8;

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

    /// <summary>皮肤贴图本地文件路径（非空时优先于 SkinKey）。</summary>
    public string? SkinPath
    {
        get => GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
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

    /// <summary>皮肤是否为 Alex(slim) 模型（来自档案接口的 SKIN.metadata.model）。</summary>
    public bool IsSlim
    {
        get => GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
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

        if (change.Property == SkinPathProperty || change.Property == SkinKeyProperty)
        {
            _bitmap = LoadBitmap();
            InvalidateVisual();
        }
        else if (change.Property == ScaleProperty
                 || change.Property == ModeProperty
                 || change.Property == IsSlimProperty)
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
            // 头像：取头部正面的 8×8 纹理并按正方形等比缩放到控件可用区域，
            // 不做任何裁切；控件宽高不一致时以较短边为准居中。
            var side = Math.Max(1d, Math.Min(Bounds.Width, Bounds.Height));
            var bounds = new Rect(
                (Bounds.Width - side) / 2,
                (Bounds.Height - side) / 2,
                side,
                side);
            DrawPart(context, new SkinPart(HeadX, HeadY, 8, 8, 4, 0, false), scale, bounds);
            DrawPart(context, new SkinPart(HeadOverlayX, HeadOverlayY, 8, 8, 4, 0, false), scale, bounds);
            return;
        }

        foreach (var part in BuildBodyParts())
        {
            DrawPart(context, part, scale, null);
        }
    }

    /// <summary>一个部件的贴图矩形（X/Y/W/H）、在 16×32 画布中的位置（PlaceX/PlaceY）与是否水平镜像。</summary>
    private readonly record struct SkinPart(
        int X, int Y, int W, int H,
        int PlaceX, int PlaceY,
        bool Mirror);

    /// <summary>
    /// 按贴图格式生成部件列表：
    /// 64×64 现代皮肤直接取左臂/左腿各自的贴图；64×32 旧版皮肤没有左臂/左腿贴图，
    /// 分别用右臂(44,20)/右腿(4,20) 的正脸镜像绘制。旧版没有身体/四肢的第二层，只保留帽子层。
    /// </summary>
    private List<SkinPart> BuildBodyParts()
    {
        var legacy = _bitmap is { } bitmap && bitmap.PixelSize.Width == bitmap.PixelSize.Height * 2;
        var slim = !legacy && IsSlim;
        var armWidth = slim ? 3 : 4;
        // slim 手臂 3 宽，左右两侧各留 1 像素空隙，画布仍为 16 宽
        var leftArmX = slim ? 13 : 12;

        var parts = new List<SkinPart>(12);
        // 与原网页版叠加顺序一致：四肢 → 身体 → 头，最后叠加第二层
        parts.Add(new SkinPart(44, 20, armWidth, 12, 0, 8, false));   // 右臂
        parts.Add(new SkinPart(4, 20, 4, 12, 4, 20, false));          // 右腿
        parts.Add(legacy
            ? new SkinPart(4, 20, 4, 12, 8, 20, true)                 // 左腿：旧皮肤镜像右腿
            : new SkinPart(20, 52, 4, 12, 8, 20, false));             // 左腿：现代皮肤独立贴图
        parts.Add(new SkinPart(20, 20, 8, 12, 4, 8, false));           // 身体
        parts.Add(legacy
            ? new SkinPart(44, 20, armWidth, 12, leftArmX, 8, true)   // 左臂：旧皮肤镜像右臂
            : new SkinPart(36, 52, armWidth, 12, leftArmX, 8, false)); // 左臂：现代皮肤独立贴图
        parts.Add(new SkinPart(HeadX, HeadY, 8, 8, 4, 0, false));      // 头
        parts.Add(new SkinPart(HeadOverlayX, HeadOverlayY, 8, 8, 4, 0, false)); // 帽子（两种格式都存在）

        if (!legacy)
        {
            parts.Add(new SkinPart(44, 36, armWidth, 12, 0, 8, false));   // 右臂外层
            parts.Add(new SkinPart(4, 36, 4, 12, 4, 20, false));          // 右腿外层
            parts.Add(new SkinPart(4, 52, 4, 12, 8, 20, false));          // 左腿外层
            parts.Add(new SkinPart(20, 36, 8, 12, 4, 8, false));          // 身体外层
            parts.Add(new SkinPart(52, 52, armWidth, 12, leftArmX, 8, false)); // 左臂外层
        }

        return parts;
    }

    /// <summary>
    /// 绘制贴图的一个部件。<paramref name="headBounds"/> 非空时（头像模式）忽略像素放置表，
    /// 直接把部件铺满该正方形。
    /// </summary>
    private void DrawPart(DrawingContext context, SkinPart part, int scale, Rect? headBounds)
    {
        if (_bitmap is null)
        {
            return;
        }

        // 高清皮肤（128×128 等）按与 64×64 的比例换算 UV，避免只取到左上角一小块（表现为头像被裁切）
        var uv = _bitmap.PixelSize.Width / 64.0;
        if (uv <= 0)
        {
            uv = 1d;
        }

        var source = new Rect(part.X * uv, part.Y * uv, part.W * uv, part.H * uv);
        var destination = headBounds ?? new Rect(
            part.PlaceX * scale,
            part.PlaceY * scale,
            part.W * scale,
            part.H * scale);

        if (part.Mirror)
        {
            // 旧版皮肤的另一侧四肢是同一贴图的水平镜像：绕部件左边缘翻转绘制
            using (context.PushTransform(new Matrix(-1, 0, 0, 1, destination.X * 2 + destination.Width, 0)))
            {
                context.DrawImage(_bitmap, source, destination);
            }

            return;
        }

        context.DrawImage(_bitmap, source, destination);
    }

    private Bitmap? LoadBitmap()
    {
        // 本地文件（登录后下载的正版/皮肤站皮肤）优先于内置逻辑名
        if (!string.IsNullOrWhiteSpace(SkinPath))
        {
            var path = SkinPath;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return null;
                }

                var cacheKey = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
                if (BitmapCache.TryGetValue(cacheKey, out var cachedPath))
                {
                    return cachedPath;
                }

                var bitmap = new Bitmap(path);
                BitmapCache[cacheKey] = bitmap;
                return bitmap;
            }
            catch (Exception)
            {
                // 文件可能刚被替换/尚未写好：不缓存失败结果，属性再次变化时可以重试
                return null;
            }
        }

        var skinKey = SkinKey;
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
            return null;
        }
    }
}

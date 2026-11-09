using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 可拖拽旋转的 3D 方块人预览：头 / 身体 / 双手 / 双腿 + 第二层（帽、外套、袖、裤腿）+ 披风。
/// <para>
/// Avalonia 没有内建 3D 管线（KAMUCL 的 three.js/WebGL 不能直接复用），
/// 这里采用「正交投影 + 仿射贴图」：整块模型只做绕 Y 轴的偏航与绕 X 轴的俯仰，
/// 每个立方体面在正交投影下仍是平行四边形，于是可以用 <see cref="DrawingContext.PushTransform(Matrix)"/>
/// 把 64×64 皮肤 UV 区域精确贴到该面上。这样既保留了 Minecraft 的像素观感，
/// 又避免逐像素软件光栅化带来的拖拽卡顿。
/// </para>
/// <para>
/// 性能约定：位图只在属性变化时解码一次；模型面片在贴图/模型变化时重建一次；
/// 每帧只做轻量的顶点变换与面片深度排序；没有拖动且缓动收敛后停止计时器，不空转 CPU。
/// </para>
/// </summary>
public class McSkin3D : Control
{
    public static readonly StyledProperty<string?> SkinKeyProperty =
        AvaloniaProperty.Register<McSkin3D, string?>(nameof(SkinKey));

    /// <summary>皮肤贴图本地文件路径；非空时优先于 SkinKey。</summary>
    public static readonly StyledProperty<string?> SkinPathProperty =
        AvaloniaProperty.Register<McSkin3D, string?>(nameof(SkinPath));

    /// <summary>披风贴图本地文件路径（64x32）；空字符串表示当前账号没有披风。</summary>
    public static readonly StyledProperty<string?> CapePathProperty =
        AvaloniaProperty.Register<McSkin3D, string?>(nameof(CapePath));

    /// <summary>Alex(slim) 模型：手臂宽度为 3 像素（仅 64x64 现代皮肤生效）。</summary>
    public static readonly StyledProperty<bool> IsSlimProperty =
        AvaloniaProperty.Register<McSkin3D, bool>(nameof(IsSlim));

    /// <summary>俯仰角限制（±75°）：保证任何角度都能看到完整人偶，不会转到看不到人的死角。</summary>
    private static readonly double PitchLimit = Math.PI * 5 / 12;

    /// <summary>拖动灵敏度：每像素对应的弧度。</summary>
    private const double DragSensitivity = 0.012;

    /// <summary>缓动系数；越大越跟手，松手后仍会平滑收敛（惯性缓停）。</summary>
    private const double EaseRate = 14;

    /// <summary>面片边缘外扩（皮肤像素），遮挡贴图边界的亚像素缝隙。</summary>
    private const double FaceBleed = 0.15;

    /// <summary>头部旋转的颈部枢轴高度（模型坐标：脚底 0，颈部 24）。</summary>
    private const double HeadPivotY = 24;

    /// <summary>头部跟随鼠标的最大偏航 / 俯仰（弧度）。</summary>
    private const double MaxHeadYaw = Math.PI / 3;

    private const double MaxHeadPitch = Math.PI / 4;

    /// <summary>头部跟随的缓动系数；比整体拖动更跟手一些。</summary>
    private const double HeadEaseRate = 18;

    /// <summary>模型几何中心高度（脚底 0、头顶 32），拖动旋转以此为中心。</summary>
    private const float ModelCenterY = 16;

    /// <summary>投影后面积小于该值（px²）的面片视为已退化成边缘线，不再绘制，避免转正时出现发丝黑线。</summary>
    private const double MinFaceArea = 3;

    private readonly List<Face> _faces = new();
    private readonly DispatcherTimer _timer;
    private int[] _order = Array.Empty<int>();
    private double[] _depths = Array.Empty<double>();
    private Vector3[] _rotatedA = Array.Empty<Vector3>();
    private Vector3[] _rotatedB = Array.Empty<Vector3>();
    private Vector3[] _rotatedC = Array.Empty<Vector3>();
    private Vector3[] _rotatedD = Array.Empty<Vector3>();
    private bool[] _visible = Array.Empty<bool>();

    private Bitmap? _skinBitmap;
    private Bitmap? _capeBitmap;

    // 初始朝向固定为正视前方（yaw/pitch 均为 0），且不持久化：每次冷启动都从正面开始。
    private double _yaw;
    private double _pitch;
    private double _targetYaw;
    private double _targetPitch;

    // 头部局部朝向（相对身体）：鼠标悬停时像 MC 物品栏里的小人一样转头，指针离开后回正。
    private double _headYaw;
    private double _headPitch;
    private double _targetHeadYaw;
    private double _targetHeadPitch;

    private bool _dragging;
    private Point _lastPointer;
    private long _lastTick;
    private TopLevel? _topLevel;

    public McSkin3D()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Tick();
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

    /// <summary>披风贴图本地文件路径；绑定为空时立即清除上一账号残留的披风。</summary>
    public string? CapePath
    {
        get => GetValue(CapePathProperty);
        set => SetValue(CapePathProperty, value);
    }

    /// <summary>皮肤是否为 Alex(slim) 模型（来自档案接口的 SKIN.metadata.model）。</summary>
    public bool IsSlim
    {
        get => GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 首页玩家卡给的是固定 168×178 的区域；无界时用同样的默认值，避免布局塌陷。
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 168d;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 178d;
        return new Size(Math.Max(64, width), Math.Max(64, height));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SkinPathProperty
            || change.Property == SkinKeyProperty
            || change.Property == CapePathProperty
            || change.Property == IsSlimProperty)
        {
            ReloadModel();
            // 换皮肤 / 切账号 / 披风变化后回到正视前方，避免带上一次拖拽的角度。
            ResetOrientation();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 朝向复位到正视前方（yaw=0、pitch=0）。仅在控件内部使用，不写入任何配置，
    /// 因此冷启动永远是正面，切换账号或重新加载皮肤也会回到正面。
    /// </summary>
    private void ResetOrientation()
    {
        _yaw = 0;
        _targetYaw = 0;
        _pitch = 0;
        _targetPitch = 0;
        _headYaw = 0;
        _targetHeadYaw = 0;
        _headPitch = 0;
        _targetHeadPitch = 0;

        // 正在拖拽时保留动画计时器，避免打断当前手势；其余情况停止缓动。
        if (!_dragging)
        {
            _timer.Stop();
            _lastTick = 0;
        }
    }

    /// <summary>非有限值（指针坐标异常等）会让旋转矩阵变成 NaN；这里统一收敛，避免人偶整块消失。</summary>
    private void SanitizeOrientations()
    {
        if (!double.IsFinite(_yaw))
        {
            _yaw = 0;
        }

        if (!double.IsFinite(_pitch))
        {
            _pitch = 0;
        }

        if (!double.IsFinite(_targetYaw))
        {
            _targetYaw = _yaw;
        }

        if (!double.IsFinite(_targetPitch))
        {
            _targetPitch = _pitch;
        }

        if (!double.IsFinite(_headYaw))
        {
            _headYaw = 0;
        }

        if (!double.IsFinite(_headPitch))
        {
            _headPitch = 0;
        }

        if (!double.IsFinite(_targetHeadYaw))
        {
            _targetHeadYaw = _headYaw;
        }

        if (!double.IsFinite(_targetHeadPitch))
        {
            _targetHeadPitch = _headPitch;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 头部跟随作用于整个窗口：把处理器挂在 TopLevel 上，指针在任意位置移动都能驱动转头。
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved, RoutingStrategies.Bubble, true);
            _topLevel.AddHandler(PointerExitedEvent, OnTopLevelPointerExited, RoutingStrategies.Bubble, true);
        }
    }

    /// <summary>窗口内任意位置的指针移动都映射到头部朝向，超出控件范围时自然收敛到最大角度。</summary>
    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
        => UpdateHeadTarget(e.GetPosition(this));

    private void OnTopLevelPointerExited(object? sender, PointerEventArgs e)
        => ResetHeadTarget();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
            _topLevel.RemoveHandler(PointerExitedEvent, OnTopLevelPointerExited);
            _topLevel = null;
        }

        _timer.Stop();
        _lastTick = 0;
        _dragging = false;
        base.OnDetachedFromVisualTree(e);
    }

    // ===== 交互：水平旋转 + 限制俯仰，松手后指数缓动收敛 =====

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        var isTouch = e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen;
        if (!point.Properties.IsLeftButtonPressed && !isTouch)
        {
            return;
        }

        if (!double.IsFinite(point.Position.X) || !double.IsFinite(point.Position.Y))
        {
            return;
        }

        _dragging = true;
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        StartAnimation();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        // 指针被捕获到窗口外时坐标可能不可用，忽略非有限值，避免 NaN 污染旋转状态导致人偶消失。
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            return;
        }

        // 悬停即转头：与 MC 物品栏里的小人一致，指针在预览区域内移动时头部跟随。
        UpdateHeadTarget(position);

        if (!_dragging)
        {
            return;
        }

        var dx = position.X - _lastPointer.X;
        var dy = position.Y - _lastPointer.Y;
        _lastPointer = position;

        if (dx != 0 || dy != 0)
        {
            _targetYaw += dx * DragSensitivity;
            _targetPitch = Math.Clamp(_targetPitch + dy * DragSensitivity, -PitchLimit, PitchLimit);
            StartAnimation();
        }

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // 拖拽中指针可能移出控件，此时保持头部朝向，避免手势中途突然回正。
        ResetHeadTarget();
    }

    private void ResetHeadTarget()
    {
        if (_dragging || (_targetHeadYaw == 0 && _targetHeadPitch == 0))
        {
            return;
        }

        _targetHeadYaw = 0;
        _targetHeadPitch = 0;
        StartAnimation();
    }

    /// <summary>把指针位置映射为头部相对身体的偏航/俯仰，越靠边越接近最大值。</summary>
    private void UpdateHeadTarget(Point position)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
        {
            return;
        }

        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            return;
        }

        var scale = Math.Min((width - 16) / 20.0, (height - 12) / 34.0);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        // 头部中心（正视时）：模型头部中点在 y=28，投影基线在 height-8。
        var headCenterY = height - 8 - 28 * scale;
        var nx = (position.X - width / 2) / Math.Max(1, width * 0.5);
        var ny = (position.Y - headCenterY) / Math.Max(1, height * 0.5);
        var yaw = Math.Clamp(nx, -1, 1) * MaxHeadYaw;
        var pitch = Math.Clamp(ny, -1, 1) * MaxHeadPitch;

        if (!double.IsFinite(yaw) || !double.IsFinite(pitch))
        {
            return;
        }

        if (Math.Abs(yaw - _targetHeadYaw) < 1e-4 && Math.Abs(pitch - _targetHeadPitch) < 1e-4)
        {
            return;
        }

        _targetHeadYaw = yaw;
        _targetHeadPitch = pitch;
        StartAnimation();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        base.OnPointerCaptureLost(e);
    }

    private void StartAnimation()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void Tick()
    {
        SanitizeOrientations();

        var dt = _lastTick == 0
            ? 1d / 60
            : Math.Min(0.05, Stopwatch.GetElapsedTime(_lastTick).TotalSeconds);
        _lastTick = Stopwatch.GetTimestamp();

        var k = 1 - Math.Exp(-EaseRate * dt);
        _yaw += (_targetYaw - _yaw) * k;
        _pitch += (_targetPitch - _pitch) * k;

        var headK = 1 - Math.Exp(-HeadEaseRate * dt);
        _headYaw += (_targetHeadYaw - _headYaw) * headK;
        _headPitch += (_targetHeadPitch - _headPitch) * headK;

        InvalidateVisual();

        if (!_dragging
            && Math.Abs(_targetYaw - _yaw) < 1e-4
            && Math.Abs(_targetPitch - _pitch) < 1e-4
            && Math.Abs(_targetHeadYaw - _headYaw) < 1e-4
            && Math.Abs(_targetHeadPitch - _headPitch) < 1e-4)
        {
            _yaw = _targetYaw;
            _pitch = _targetPitch;
            _headYaw = _targetHeadYaw;
            _headPitch = _targetHeadPitch;
            _timer.Stop();
            _lastTick = 0;
        }
    }

    // ===== 渲染 =====

    /// <summary>以颈部 (0, 24, 0) 为枢轴旋转头部顶点：先绕 Y 偏航，再绕 X 俯仰。</summary>
    private static Vector3 RotateHead(Vector3 p, float cy, float sy, float cp, float sp)
    {
        var x1 = p.X * cy + p.Z * sy;
        var z1 = -p.X * sy + p.Z * cy;
        var y = p.Y - (float)HeadPivotY;
        var y2 = y * cp - z1 * sp;
        var z2 = y * sp + z1 * cp;
        return new Vector3(x1, y2 + (float)HeadPivotY, z2);
    }

    /// <summary>只旋转方向向量（法线），不含枢轴平移。</summary>
    private static Vector3 RotateHeadDirection(Vector3 d, float cy, float sy, float cp, float sp)
    {
        var x1 = d.X * cy + d.Z * sy;
        var z1 = -d.X * sy + d.Z * cy;
        var y2 = d.Y * cp - z1 * sp;
        var z2 = d.Y * sp + z1 * cp;
        return new Vector3(x1, y2, z2);
    }

    public override void Render(DrawingContext context)
    {
        SanitizeOrientations();

        if (_skinBitmap is null || _faces.Count == 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
        {
            return;
        }

        // 保持人偶在卡片里接近原 2D 尺寸（32 皮肤像素高），并留出边距。
        var scale = Math.Min((width - 16) / 20.0, (height - 12) / 34.0);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        var view = new View(
            width / 2,
            height - 8,
            scale,
            MathF.Cos((float)_yaw),
            MathF.Sin((float)_yaw),
            MathF.Cos((float)_pitch),
            MathF.Sin((float)_pitch));

        // 第一遍：旋转四个角、算深度与法线朝向（正交投影下相机沿 -Z 观察，法线 Z>0 才可见）。
        // 只有头部（含帽子第二层）做局部偏航/俯仰，身体保持原样。
        var headCy = MathF.Cos((float)_headYaw);
        var headSy = MathF.Sin((float)_headYaw);
        var headCp = MathF.Cos((float)_headPitch);
        var headSp = MathF.Sin((float)_headPitch);
        var count = _faces.Count;
        for (var i = 0; i < count; i++)
        {
            var face = _faces[i];
            var a = view.Rotate(face.IsHead ? RotateHead(face.A, headCy, headSy, headCp, headSp) : face.A);
            var b = view.Rotate(face.IsHead ? RotateHead(face.B, headCy, headSy, headCp, headSp) : face.B);
            var c = view.Rotate(face.IsHead ? RotateHead(face.C, headCy, headSy, headCp, headSp) : face.C);
            var d = view.Rotate(face.IsHead ? RotateHead(face.D, headCy, headSy, headCp, headSp) : face.D);
            _rotatedA[i] = a;
            _rotatedB[i] = b;
            _rotatedC[i] = c;
            _rotatedD[i] = d;
            _depths[i] = (a.Z + b.Z + c.Z + d.Z) * 0.25;

            // 法线是方向向量，必须用不含模型中心平移的旋转。
            // 复用 Rotate 会把 -ModelCenterY 带进 Y 分量，使 normal.Z 出现 -16*sin(pitch)：
            // 俯仰只要超过 ~3.6° 所有面都会被误判为背面，表现为「一转人偶就整块消失」。
            var normal = view.RotateDirection(
                face.IsHead ? RotateHeadDirection(face.Normal, headCy, headSy, headCp, headSp) : face.Normal);

            // 用投影后两条边的叉积面积判定退化面：斜 45° 时对角线会把包围盒撑大，
            // 只看包围盒短边会漏掉已经压成一条线的面片（奇异变换 + 发丝黑线）。
            var pa = view.Project(a);
            var pb = view.Project(b);
            var pc = view.Project(c);
            var cross = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);

            _visible[i] = normal.Z > 0.002f && Math.Abs(cross) >= MinFaceArea;
        }

        // 第二遍：可见面按深度从后往前排序（画家算法），背面剔除后每块立方体约 3 个面。
        var visibleCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (_visible[i])
            {
                _order[visibleCount++] = i;
            }
        }

        for (var i = 1; i < visibleCount; i++)
        {
            var key = _order[i];
            var keyDepth = _depths[key];
            var j = i - 1;
            while (j >= 0 && _depths[_order[j]] > keyDepth)
            {
                _order[j + 1] = _order[j];
                j--;
            }
            _order[j + 1] = key;
        }

        for (var i = 0; i < visibleCount; i++)
        {
            DrawFace(context, _faces[_order[i]], view);
        }
    }

    private void DrawFace(DrawingContext context, in Face face, in View view)
    {
        var bitmap = face.IsCape ? _capeBitmap : _skinBitmap;
        if (bitmap is null)
        {
            return;
        }

        var uvScale = bitmap.PixelSize.Width / 64.0;
        if (uvScale <= 0)
        {
            uvScale = 1;
        }

        var source = new Rect(
            face.Uv.X * uvScale,
            face.Uv.Y * uvScale,
            face.Uv.Width * uvScale,
            face.Uv.Height * uvScale);

        var index = face.Index;
        var a = view.Project(_rotatedA[index]);
        var b = view.Project(_rotatedB[index]);
        var c = view.Project(_rotatedC[index]);
        var d = view.Project(_rotatedD[index]);

        // FlipU 用于旧版皮肤镜像出的左臂/左腿：把贴图 U 方向反着映射到同一块几何上。
        var p0 = face.FlipU ? b : a;
        var p1 = face.FlipU ? a : b;
        var p2 = face.FlipU ? d : c;

        var localWidth = face.Uv.Width;
        var localHeight = face.Uv.Height;
        var matrix = new Matrix(
            (p1.X - p0.X) / localWidth, (p1.Y - p0.Y) / localWidth,
            (p2.X - p0.X) / localHeight, (p2.Y - p0.Y) / localHeight,
            p0.X, p0.Y);

        using (context.PushTransform(matrix))
        {
            context.DrawImage(
                bitmap,
                source,
                new Rect(-FaceBleed, -FaceBleed, localWidth + FaceBleed * 2, localHeight + FaceBleed * 2));
        }
    }

    // ===== 模型 =====

    private void ReloadModel()
    {
        _skinBitmap = McSkin.LoadSkinBitmap(SkinPath, SkinKey);
        if (_skinBitmap is null)
        {
            // 缺皮肤时退化为默认史蒂夫，保证首页不会出现空白或布局塌陷。
            _skinBitmap = McSkin.LoadAssetBitmap("steve");
        }

        _capeBitmap = string.IsNullOrWhiteSpace(CapePath) ? null : McSkin.LoadFileBitmap(CapePath!);

        BuildModel();
    }

    private void BuildModel()
    {
        _faces.Clear();

        if (_skinBitmap is null)
        {
            EnsureBuffers();
            return;
        }

        var legacy = IsLegacy(_skinBitmap);
        // 旧版 64x32 没有左臂/左腿贴图，按原版行为镜像右臂/右腿；slim 只对现代皮肤有效。
        var slim = !legacy
                  && (IsSlim
                      || (string.IsNullOrWhiteSpace(SkinPath)
                          && string.Equals(SkinKey, "alex", StringComparison.OrdinalIgnoreCase)));
        var armWidth = slim ? 3 : 4;

        // 坐标：脚底 y=0，腿部 0..12，身体 12..24，头 24..32；+X 为玩家左侧。
        AddBox(-4, 4, 24, 32, -4, 4, 0, 0, 8, 8, 8, false, true);          // 头
        AddBox(-4.5f, 4.5f, 23.5f, 32.5f, -4.5f, 4.5f, 32, 0, 8, 8, 8, false, true); // 帽子

        AddBox(-4, 4, 12, 24, -2, 2, 16, 16, 8, 12, 4, false);       // 身体
        if (!legacy)
        {
            AddBox(-4.25f, 4.25f, 11.75f, 24.25f, -2.25f, 2.25f, 16, 32, 8, 12, 4, false); // 外套
        }

        AddBox(-4 - armWidth, -4, 12, 24, -2, 2, 40, 16, armWidth, 12, 4, false); // 右臂
        AddBox(4, 4 + armWidth, 12, 24, -2, 2,
               legacy ? 40 : 32, legacy ? 16 : 48, armWidth, 12, 4, legacy); // 左臂

        if (!legacy)
        {
            AddBox(-4.25f - armWidth, -3.75f, 11.75f, 24.25f, -2.25f, 2.25f, 40, 32, armWidth, 12, 4, false);
            AddBox(3.75f, 4.25f + armWidth, 11.75f, 24.25f, -2.25f, 2.25f, 48, 48, armWidth, 12, 4, false);
        }

        AddBox(-3.9f, 0.1f, 0, 12, -2.1f, 1.9f, 0, 16, 4, 12, 4, false); // 右腿
        AddBox(-0.1f, 3.9f, 0, 12, -2.1f, 1.9f,
               legacy ? 0 : 16, legacy ? 16 : 48, 4, 12, 4, legacy); // 左腿

        if (!legacy)
        {
            AddBox(-4.15f, 0.35f, -0.25f, 12.25f, -2.35f, 2.15f, 0, 32, 4, 12, 4, false);
            AddBox(-0.35f, 4.15f, -0.25f, 12.25f, -2.35f, 2.15f, 0, 48, 4, 12, 4, false);
        }

        if (_capeBitmap is not null)
        {
            AddCape();
        }

        EnsureBuffers();
    }

    private static bool IsLegacy(Bitmap bitmap)
        => bitmap.PixelSize.Width > 0
           && bitmap.PixelSize.Width % 64 == 0
           && bitmap.PixelSize.Height == bitmap.PixelSize.Width / 2;

    private void EnsureBuffers()
    {
        var count = _faces.Count;
        if (_depths.Length == count)
        {
            return;
        }

        _depths = new double[count];
        _visible = new bool[count];
        _order = new int[count];
        _rotatedA = new Vector3[count];
        _rotatedB = new Vector3[count];
        _rotatedC = new Vector3[count];
        _rotatedD = new Vector3[count];
    }

    /// <summary>
    /// 添加一个立方体：UV 按 skinview3d/skinview-utils 的标准 64×64 布局展开。
    /// <paramref name="mirror"/> 为 true 时用右肢贴图镜像出左肢（旧版 64x32 皮肤），
    /// 同时交换 ±X 面所用的 UV 区域并翻转 U 方向。
    /// </summary>
    private void AddBox(
        float x0, float x1, float y0, float y1, float z0, float z1,
        int u, int v, int width, int height, int depth, bool mirror, bool isHead = false)
    {
        var topU = u + depth;
        var bottomU = u + width + depth;
        var leftU = u;
        var frontU = u + depth;
        var rightU = u + width + depth;
        var backU = u + width + depth * 2;
        var sideV = v + depth;

        // +Z 正面
        AddFace(
            new Vector3(x0, y1, z1), new Vector3(x1, y1, z1),
            new Vector3(x0, y0, z1), new Vector3(x1, y0, z1),
            frontU, sideV, width, height, mirror, false, isHead);

        // -Z 背面
        AddFace(
            new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
            new Vector3(x1, y0, z0), new Vector3(x0, y0, z0),
            backU, sideV, width, height, mirror, false, isHead);

        // +X / -X；镜像左肢时区域互换（左肢的 +X 面对应右肢 -X 面的贴图）。
        AddFace(
            new Vector3(x1, y1, z1), new Vector3(x1, y1, z0),
            new Vector3(x1, y0, z1), new Vector3(x1, y0, z0),
            mirror ? leftU : rightU, sideV, depth, height, mirror, false, isHead);

        AddFace(
            new Vector3(x0, y1, z0), new Vector3(x0, y1, z1),
            new Vector3(x0, y0, z0), new Vector3(x0, y0, z1),
            mirror ? rightU : leftU, sideV, depth, height, mirror, false, isHead);

        // +Y 顶面
        AddFace(
            new Vector3(x0, y1, z0), new Vector3(x1, y1, z0),
            new Vector3(x0, y1, z1), new Vector3(x1, y1, z1),
            topU, v, width, depth, mirror, false, isHead);

        // -Y 底面
        AddFace(
            new Vector3(x1, y0, z0), new Vector3(x0, y0, z0),
            new Vector3(x1, y0, z1), new Vector3(x0, y0, z1),
            bottomU, v, width, depth, mirror, false, isHead);
    }

    /// <summary>
    /// 披风：10×16×1 的薄方块，挂在背部（z=-3..-2），顶部对齐肩部 y=24，
    /// 并以肩部为轴后仰约 12°，看起来像自然下垂；它属于模型的一部分，随人偶一起旋转。
    /// 外侧使用披风图 (1,1) 的 10×16 区域，内侧使用 (12,1)。
    /// </summary>
    private void AddCape()
    {
        const float x0 = -5, x1 = 5;
        const float yTop = 24, yBottom = 8;
        const float zOuter = -3, zInner = -2;
        var tilt = MathF.Sin((float)(Math.PI / 15));
        var tiltCos = MathF.Cos((float)(Math.PI / 15));

        Vector3 Tilt(float x, float y, float z)
        {
            var dy = y - yTop;
            var dz = z + 2.5f;
            return new Vector3(x, yTop + dy * tiltCos - dz * tilt, -2.5f + dy * tilt + dz * tiltCos);
        }

        // 外侧（-Z）与内侧（+Z）
        AddFace(
            Tilt(x1, yTop, zOuter), Tilt(x0, yTop, zOuter),
            Tilt(x1, yBottom, zOuter), Tilt(x0, yBottom, zOuter),
            1, 1, 10, 16, false, true);

        AddFace(
            Tilt(x0, yTop, zInner), Tilt(x1, yTop, zInner),
            Tilt(x0, yBottom, zInner), Tilt(x1, yBottom, zInner),
            12, 1, 10, 16, false, true);

        // 顶面 / 底面
        AddFace(
            Tilt(x0, yTop, zOuter), Tilt(x1, yTop, zOuter),
            Tilt(x0, yTop, zInner), Tilt(x1, yTop, zInner),
            1, 0, 10, 1, false, true);

        AddFace(
            Tilt(x1, yBottom, zOuter), Tilt(x0, yBottom, zOuter),
            Tilt(x1, yBottom, zInner), Tilt(x0, yBottom, zInner),
            11, 0, 10, 1, false, true);

        // 左右侧面
        AddFace(
            Tilt(x0, yTop, zOuter), Tilt(x0, yTop, zInner),
            Tilt(x0, yBottom, zOuter), Tilt(x0, yBottom, zInner),
            0, 1, 1, 16, false, true);

        AddFace(
            Tilt(x1, yTop, zInner), Tilt(x1, yTop, zOuter),
            Tilt(x1, yBottom, zInner), Tilt(x1, yBottom, zOuter),
            11, 1, 1, 16, false, true);
    }

    private void AddFace(
        Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        int u, int v, int width, int height, bool flipU, bool isCape, bool isHead = false)
    {
        // 几何法线由 (C-A)×(B-A) 得到（纹理 U 沿 A→B、V 沿 A→C，叉乘朝外），
        // 披风倾斜后的面也因此自动获得正确的法线。
        var normal = Vector3.Cross(c - a, b - a);
        if (normal.LengthSquared() > 0)
        {
            normal = Vector3.Normalize(normal);
        }
        else
        {
            normal = Vector3.UnitZ;
        }

        _faces.Add(new Face(
            _faces.Count,
            a, b, c, d,
            new Rect(u, v, width, height),
            normal,
            flipU,
            isCape,
            isHead));
    }

    private readonly record struct Face(
        int Index,
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector3 D,
        Rect Uv,
        Vector3 Normal,
        bool FlipU,
        bool IsCape,
        bool IsHead);

    /// <summary>正交视图：模型点先绕 Y 轴偏航、再绕 X 轴俯仰，然后丢弃 Z 做正交投影。</summary>
    private readonly record struct View(
        double OriginX,
        double BaseY,
        double Scale,
        float Cy,
        float Sy,
        float Cp,
        float Sp)
    {
        public Vector3 Rotate(Vector3 p)
        {
            var y = p.Y - ModelCenterY;
            var x1 = p.X * Cy + p.Z * Sy;
            var z1 = -p.X * Sy + p.Z * Cy;
            var y2 = y * Cp - z1 * Sp;
            var z2 = y * Sp + z1 * Cp;
            return new Vector3(x1, y2 + ModelCenterY, z2);
        }

        /// <summary>
        /// 只旋转方向向量。与 <see cref="Rotate"/> 的区别是不带模型中心平移：
        /// 法线没有"位置"，若把 -ModelCenterY 计入 Y，pitch 会把 Z 分量整体拉偏。
        /// </summary>
        public Vector3 RotateDirection(Vector3 d)
        {
            var x1 = d.X * Cy + d.Z * Sy;
            var z1 = -d.X * Sy + d.Z * Cy;
            return new Vector3(x1, d.Y * Cp - z1 * Sp, d.Y * Sp + z1 * Cp);
        }

        public Point Project(Vector3 p)
            => new(OriginX + Scale * p.X, BaseY - Scale * p.Y);
    }
}

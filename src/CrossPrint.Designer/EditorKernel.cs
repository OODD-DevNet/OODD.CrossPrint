using CrossPrint.Core;
using CrossPrint.Engine;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 设计器内核：交互状态机 + 命中测试调度 + 命令生成。架构 §4.1 的"唯一交互大脑"。
/// </summary>
/// <remarks>
/// 三条纪律：
/// <list type="bullet">
/// <item><description><b>只有 <see cref="Viewport"/> 碰像素。</b>入口事件（宿主设备 px）一进来就换成 mm，
/// 之后几何、命中、命令参数全是 mm（<see cref="CrossPrint.Core"/> 的单位纪律）。</description></item>
/// <item><description><b>拖拽中途直改模型，松手才出命令。</b>拖拽要实时重绘，等松手再改就变成"拖完才跳"；
/// 代价是中途状态不在命令里，所以按下时抓一份几何快照（<see cref="_drag"/>），
/// <see cref="OnKeyDown"/> 的 Esc 用快照回滚——回滚不是撤销，所以不发命令。</description></item>
/// <item><description><b>所有变更都过 <see cref="EditorCommand"/>。</b>v0.1 故意不做撤销栈（架构 §4.2），
/// 只把命令通过 <see cref="CommandExecuted"/> 交给宿主；P1 加栈时内核一行不改。</description></item>
/// </list>
/// </remarks>
public sealed class EditorKernel
{
    /// <summary>方向键单步位移（mm），无修饰键时使用。</summary>
    public const double NudgeStepMm = 1.0;

    /// <summary>元素最小宽/高（mm）：缩到 1mm 以下就无法再点中或打印，一律夹紧。</summary>
    public const double MinElementMm = 1.0;

    /// <summary>滚轮与键盘缩放一档的乘数（放大 ×1.2，缩小 ÷1.2）。</summary>
    public const double ZoomStepFactor = 1.2;

    /// <summary>Shift 方向键的粗调步长（mm）。10 档 = 1mm × 10。</summary>
    public const double CoarseNudgeStepMm = 10.0;

    /// <summary>Ctrl 方向键的微调步长（mm）。打印对位要 0.1mm 级。</summary>
    public const double FineNudgeStepMm = 0.1;

    /// <summary>手柄命中的默认半径（宿主设备 px）。不随缩放变：放大后手柄在屏幕上的视觉尺寸已经变大，
    /// 若半径也跟着 zoom 走，命中区会胖成一片、相邻手柄互相抢点。</summary>
    public const double DefaultHandleHitRadiusHostPx = 6.0;

    /// <summary>创建副本时相对源元素的偏移（mm）。</summary>
    public const double DuplicateOffsetMm = 5.0;

    // 判定"这次拖拽到底改没改几何"的容差：纯点击（按下即松开）不该产生一条移动命令。
    const double GeometryEpsilonMm = 1e-6;

    InteractionState _state = InteractionState.Idle;
    ResizeHandle _activeHandle = ResizeHandle.None;
    Point2D _pressMm;
    readonly List<DragEntry> _drag = [];
    ElementBoxMm _pressBox;
    LineDirection? _resizeLineDirection;

    Point2D _panStartHostPx;
    Point2D _panStartPan;

    ScrollAxis _scrollAxis;
    double _scrollPressAlongHostPx;
    double _scrollStartScroll;
    Point2D _scrollStartPan;

    string? _placingTypeKey;
    PaperElement? _pendingElement;
    SKRect? _placingGhostMm;

    int _issuedIdFloor;

    /// <summary>用模板构造内核。内核直接编辑传入的模板对象图（不拷贝），宿主保存的就是这一个实例。</summary>
    /// <param name="template">待编辑模板。</param>
    public EditorKernel(PrintTemplate template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        Viewport = new Viewport();
        Selection = new Selection();
    }

    /// <summary>正在编辑的模板。</summary>
    public PrintTemplate Template { get; }

    /// <summary>当前选中集合。</summary>
    public Selection Selection { get; }

    /// <summary>视口（缩放/平移/坐标换算）。</summary>
    public Viewport Viewport { get; }

    /// <summary>交互状态机当前状态。</summary>
    public InteractionState State => _state;

    /// <summary>正在拖动的缩放手柄；仅 <see cref="InteractionState.Resizing"/> 期间有意义，其余时候为 <see cref="ResizeHandle.None"/>。</summary>
    public ResizeHandle ActiveHandle => _activeHandle;

    /// <summary>放置预览框（mm）：工具箱拖入期间叠加层要画的幽灵盒。非放置状态为 null。</summary>
    public SKRect? PlacingGhostMm => _placingGhostMm;

    /// <summary>手柄命中半径（宿主设备 px），默认 <see cref="DefaultHandleHitRadiusHostPx"/>。宿主可按触屏加宽。</summary>
    public double HandleHitRadiusHostPx { get; set; } = DefaultHandleHitRadiusHostPx;

    /// <summary>
    /// 滚动条厚度（宿主设备 px），默认 <see cref="EditorScrollBars.DefaultThicknessHostPx"/>。
    /// 命中判定与叠加层绘制必须用同一个数：各用各的，看得见的滑块抓不住，看不见的窄条还会抢走元素的点击。
    /// 由 <see cref="EditorCanvas.Draw"/> 每帧从渲染参数带进来，宿主一般不用单独赋值。
    /// </summary>
    public double ScrollBarThicknessHostPx { get; set; } = EditorScrollBars.DefaultThicknessHostPx;

    /// <summary>
    /// Shift 是否按住，决定拖拽缩放是否等比。
    /// <see cref="PointerPoint"/> 契约里不带修饰键（各宿主的修饰键 API 差异太大），
    /// 所以鼠标拖拽期间的修饰态由宿主在按键变化时同步这一个布尔值；内核不猜。
    /// </summary>
    public bool ShiftPressed { get; set; }

    bool _autoFitDone;
    double _viewWidthHostPx;
    double _viewHeightHostPx;

    /// <summary>
    /// 宿主设为 true：画布尺寸第一次就位（启动首帧）自动「整页适配」一次，页面默认居中可见；
    /// 之后用户自己滚/缩，宿主 resize 不再打断。宿主在构造画布控件时赋值一次。
    /// </summary>
    public bool AutoFitOnFirstLayout { get; set; }

    /// <summary>宿主控件宽（宿主设备 px）。居中放置与整页适配都要它——内核不知道"看得见多大一块"就没法居中。宿主在尺寸变化时赋值。</summary>
    public double ViewWidthHostPx
    {
        get => _viewWidthHostPx;
        set { _viewWidthHostPx = value; MaybeAutoFit(); }
    }

    /// <summary>宿主控件高（宿主设备 px）。同上。</summary>
    public double ViewHeightHostPx
    {
        get => _viewHeightHostPx;
        set { _viewHeightHostPx = value; MaybeAutoFit(); }
    }

    // 尺寸第一次齐全时做一次整页适配（页面默认居中）。默认 false：无头测试直接赋尺寸不该悄悄改视口。
    void MaybeAutoFit()
    {
        if (!AutoFitOnFirstLayout || _autoFitDone)
            return;
        if (_viewWidthHostPx <= 0 || _viewHeightHostPx <= 0)
            return;

        _autoFitDone = true;
        FitToPage();
    }

    /// <summary>视口变了（缩放/平移/选中/拖拽预览），宿主据此 Invalidate。</summary>
    public event Action? ViewChanged;

    /// <summary>模板内容变了，宿主据此重绘画布并刷新属性面板。</summary>
    public event Action? TemplateChanged;

    /// <summary>一条命令执行完毕。v0.1 用它把撤销能力交给宿主（<c>command.Revert()</c>），P1 收进内核栈。</summary>
    public event Action<EditorCommand>? CommandExecuted;

    /// <summary>按下。优先级：中键平移 &gt; 主键命中手柄缩放 &gt; 主键命中元素选中+移动 &gt; 主键空白清选中；右键不绑定动作。</summary>
    /// <param name="p">指针事件（宿主设备 px + 该事件的 DPI + 按键）。</param>
    public void OnPointerPressed(PointerPoint p)
    {
        // 换手势或重抓拖拽前，先把在途的移动/缩放如实落地成一条命令：
        // 此刻模型已被 ApplyDrag 改写，若不提交就切走（中键平移、或主键二次按下重抓基线），
        // 这段位移会在宿主撤销栈里凭空消失，用户按撤销时跳回更早的状态。
        FinishInFlightDrag();

        if (p.Button == PointerButton.Middle)
        {
            _state = InteractionState.Panning;
            _panStartHostPx = new Point2D(p.X, p.Y);
            _panStartPan = Viewport.Pan;
            return;
        }

        if (p.Button != PointerButton.Primary)
            return;

        // (a0) 滚动条：内容超出视口时，贴边窄条优先于手柄/元素命中——与桌面软件的覆盖式滚动条一致。
        // 只在 Idle 态接手（拖拽中途不换手势）；中键平移的优先级仍在其上（上面已 return）。
        if (_state == InteractionState.Idle && TryBeginScrollBarInteraction(p))
            return;

        var mm = ToMm(p);

        // (a) 手柄优先：手柄就在选中框上，若先判元素，点右下角手柄会同时命中元素本体而变成移动。
        var handle = HitHandle(p);
        if (handle != ResizeHandle.None)
        {
            _activeHandle = handle;
            _pressBox = ElementHitTester.UnionBox(SelectedElements);
            _resizeLineDirection = SingleSelectedLineDirection();
            CaptureDrag();
            _pressMm = mm;
            _state = InteractionState.Resizing;
            return;
        }

        // (b) 元素：命中即选中并进入移动。已选中则保留选中（多选的 Shift 点选归宿主，见 Selection.Add）。
        var hit = ElementHitTester.HitTest(Template.Elements, mm.X, mm.Y);
        if (hit is not null)
        {
            if (!Selection.Contains(hit.Id))
            {
                Selection.Select(hit.Id);
                ViewChanged?.Invoke();
            }

            _activeHandle = ResizeHandle.None;
            _pressBox = ElementHitTester.UnionBox(SelectedElements);
            _resizeLineDirection = null;
            CaptureDrag();
            _pressMm = mm;
            _state = InteractionState.Moving;
            return;
        }

        // (c) 空白：清选中且不进入任何拖拽（v0.1 不做框选）。
        _activeHandle = ResizeHandle.None;
        _drag.Clear();
        if (!Selection.IsEmpty)
        {
            Selection.Clear();
            ViewChanged?.Invoke();
        }

        _state = InteractionState.Idle;
    }

    /// <summary>移动。按当前状态推进预览：移动/缩放直接写模型（画布要实时重绘），平移改视口，放置改幽灵框。</summary>
    /// <param name="p">指针事件。</param>
    public void OnPointerMoved(PointerPoint p)
    {
        switch (_state)
        {
            case InteractionState.Idle:
                return;

            case InteractionState.Panning:
            {
                // Pan 活在 96 基准 px 层，所以设备 px 差要先乘 96/dpi；外接屏换 DPI 时平移量才不会自己漂。
                var k = Viewport.BaseDpi / p.Dpi;
                Viewport.Pan = new Point2D(
                    (float)(_panStartPan.X + (p.X - _panStartHostPx.X) * k),
                    (float)(_panStartPan.Y + (p.Y - _panStartHostPx.Y) * k));
                ViewChanged?.Invoke();
                return;
            }

            case InteractionState.Placing:
                _placingGhostMm = PlacementDefaults
                    .BoxCenteredAt(_placingTypeKey!, ToMm(p).X, ToMm(p).Y)
                    .ToSkRect();
                ViewChanged?.Invoke();
                return;

            case InteractionState.Scrolling:
                ApplyScrollBarDrag(p);
                return;

            default:
                ApplyDrag(ToMm(p));
                return;
        }
    }

    /// <summary>松开。拖拽结束才生成并执行一条命令；纯点击（位移低于容差）不发命令。</summary>
    /// <param name="p">指针事件。</param>
    public void OnPointerReleased(PointerPoint p)
    {
        switch (_state)
        {
            case InteractionState.Moving:
            case InteractionState.Resizing:
            {
                ApplyDrag(ToMm(p));
                CommitDrag();
                _drag.Clear();
                _activeHandle = ResizeHandle.None;
                _state = InteractionState.Idle;
                ViewChanged?.Invoke();
                return;
            }

            case InteractionState.Panning:
                _state = InteractionState.Idle;
                return;

            case InteractionState.Scrolling:
                // 拖滑块不发命令（视口变化不是模型变更），松手即结束。
                _state = InteractionState.Idle;
                return;

            case InteractionState.Placing:
                CommitPlacement(ToMm(p));
                return;
        }
    }

    /// <summary>滚轮缩放，以光标为锚（光标底下的 mm 在缩放前后不变）。</summary>
    /// <param name="delta">滚动量，只用符号：正=放大一档，负=缩小一档。WinForms 的 ±120 与 Avalonia 的 ±1 都吃得下。</param>
    /// <param name="p">事件位置（宿主设备 px + DPI）。</param>
    /// <returns>已消费返回 true；已到缩放极限（倍数不变）返回 false，宿主可以拿这次滚动去翻页。</returns>
    /// <remarks>不带修饰键信息的旧宿主仍可调这个重载（滚轮即缩放）；新宿主请走
    /// <see cref="OnMouseWheel(double, PointerPoint, bool, bool)"/>——普通滚轮是滚动，Ctrl+滚轮才缩放。</remarks>
    public bool OnMouseWheel(double delta, PointerPoint p)
    {
        var factor = delta > 0 ? ZoomStepFactor : 1.0 / ZoomStepFactor;
        var before = Viewport.Zoom;
        var after = Viewport.ZoomAt(factor, p.X, p.Y, p.Dpi);
        if (after == before)
            return false;

        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 滚轮统一入口：Ctrl+滚轮 = 以光标为锚缩放；Shift+滚轮 = 横向滚动；
    /// 普通滚轮 = 纵向滚动（一步 1/4 视口，见 <see cref="WheelScrollFraction"/>）。
    /// </summary>
    /// <param name="delta">滚动量，只用符号。</param>
    /// <param name="p">事件位置（宿主设备 px + DPI），Ctrl 缩放时作为锚点。</param>
    /// <param name="control">Ctrl/Cmd 是否按下。</param>
    /// <param name="shift">Shift 是否按下。</param>
    /// <returns>已消费返回 true；该轴没有滚动条（或已滚到头）返回 false，宿主可自行处理。</returns>
    public bool OnMouseWheel(double delta, PointerPoint p, bool control, bool shift)
    {
        if (control)
            return OnMouseWheel(delta, p);

        // 滚轮向下（delta<0）= 内容向下走 = 滚动位置增大，与浏览器/编辑器一致
        var direction = -Math.Sign(delta);
        return shift
            ? ScrollHorizontalBy(direction)
            : ScrollVerticalBy(direction);
    }

    /// <summary>滚轮滚动一步占视口的比例：一步滚 1/4 屏，与滚动条的手感刚好衔接。</summary>
    public const double WheelScrollFraction = 0.25;

    bool ScrollVerticalBy(double direction)
    {
        if (EditorScrollBars.Compute(this, ScrollBarThicknessHostPx).Vertical is not { } axis)
            return false;

        // Pan 走 float，贴边时重算的滚动位置会有 ~1e-5 的噪声：容差 1e-3，别把"已到头"当"还能滚"
        var target = Math.Clamp(axis.ScrollPos + direction * WheelScrollFraction * ViewHeightHostPx, 0, axis.ScrollMax);
        if (Math.Abs(target - axis.ScrollPos) < 1e-3)
            return false;

        ScrollVerticalTo(axis, target);
        return true;
    }

    bool ScrollHorizontalBy(double direction)
    {
        if (EditorScrollBars.Compute(this, ScrollBarThicknessHostPx).Horizontal is not { } axis)
            return false;

        var target = Math.Clamp(axis.ScrollPos + direction * WheelScrollFraction * ViewWidthHostPx, 0, axis.ScrollMax);
        if (Math.Abs(target - axis.ScrollPos) < 1e-3)
            return false;

        ScrollHorizontalTo(axis, target);
        return true;
    }

    // ---------------- 滚动条：叠加层画轨道/滑块，内核负责命中与拖拽（几何算法共用 EditorScrollBars） ----------------

    /// <summary>按下落在滚动条上：滑块进入拖拽，轨道空白整页翻滚。命中返回 true 并消费本次按下。</summary>
    bool TryBeginScrollBarInteraction(PointerPoint p)
    {
        var metrics = EditorScrollBars.Compute(this, ScrollBarThicknessHostPx);

        // 垂直轴优先：右缘窄条（含右下角）归垂直轴，与多数桌面软件的角落归属一致。
        if (metrics.Vertical is { } vertical && p.X >= vertical.Track.Left)
        {
            if (p.Y < vertical.Thumb.Top || p.Y > vertical.Thumb.Bottom)
            {
                // 轨道点击 = 整页翻滚：点在滑块哪一侧就朝哪边翻一屏（留 10% 重叠帮用户定位）。
                var step = (p.Y < vertical.Thumb.Top ? -1 : 1) * vertical.ViewLength * 0.9;
                ScrollVerticalTo(vertical, vertical.ScrollPos + step);
                return true;
            }

            BeginScrollBarDrag(ScrollAxis.Vertical, p);
            return true;
        }

        if (metrics.Horizontal is { } horizontal && p.Y >= horizontal.Track.Top)
        {
            if (p.X < horizontal.Thumb.Left || p.X > horizontal.Thumb.Right)
            {
                var step = (p.X < horizontal.Thumb.Left ? -1 : 1) * horizontal.ViewLength * 0.9;
                ScrollHorizontalTo(horizontal, horizontal.ScrollPos + step);
                return true;
            }

            BeginScrollBarDrag(ScrollAxis.Horizontal, p);
            return true;
        }

        return false;
    }

    void BeginScrollBarDrag(ScrollAxis axis, PointerPoint p)
    {
        _scrollAxis = axis;
        _scrollStartPan = Viewport.Pan;
        _scrollPressAlongHostPx = axis == ScrollAxis.Vertical ? p.Y : p.X;
        // 按下时的滚动位置：拖拽按"起点 + 位移 × 压缩比"推进，不依赖抓点在滑块内的位置也能保持 1:1 手感。
        var metrics = EditorScrollBars.Compute(this, ScrollBarThicknessHostPx);
        _scrollStartScroll = axis == ScrollAxis.Vertical
            ? metrics.Vertical!.Value.ScrollPos
            : metrics.Horizontal!.Value.ScrollPos;
        _state = InteractionState.Scrolling;
    }

    void ApplyScrollBarDrag(PointerPoint p)
    {
        var metrics = EditorScrollBars.Compute(this, ScrollBarThicknessHostPx);
        var along = _scrollAxis == ScrollAxis.Vertical ? p.Y : p.X;
        var delta = along - _scrollPressAlongHostPx;

        if (_scrollAxis == ScrollAxis.Vertical)
        {
            if (metrics.Vertical is not { } axis)
                return;
            ScrollVerticalTo(axis, _scrollStartScroll + delta * HostPxPerScrollPx(axis));
        }
        else
        {
            if (metrics.Horizontal is not { } axis)
                return;
            ScrollHorizontalTo(axis, _scrollStartScroll + delta * HostPxPerScrollPx(axis));
        }
    }

    /// <summary>滑块行程压缩了整个滚动范围：拖滑块 1px 对应的滚动量。</summary>
    static double HostPxPerScrollPx(EditorScrollBars.Axis axis)
    {
        var usable = axis.IsVertical
            ? axis.Track.Height - axis.Thumb.Height
            : axis.Track.Width - axis.Thumb.Width;
        return usable > 0.5 ? axis.ScrollMax / usable : 0;
    }

    /// <summary>滚动位置 → 平移。s 是视口顶/左边在内容盒里的偏移，换算式见 <see cref="EditorScrollBars"/> 的 remarks。</summary>
    void ScrollVerticalTo(EditorScrollBars.Axis axis, double scrollPos)
    {
        var s = Math.Clamp(scrollPos, 0, axis.ScrollMax);
        var pageTopHostPx = EditorScrollBars.ContentPaddingHostPx - s;
        Viewport.Pan = new Point2D(
            Viewport.Pan.X,
            (float)(pageTopHostPx * Viewport.BaseDpi / Viewport.Dpi));
        ViewChanged?.Invoke();
    }

    void ScrollHorizontalTo(EditorScrollBars.Axis axis, double scrollPos)
    {
        var s = Math.Clamp(scrollPos, 0, axis.ScrollMax);
        var pageLeftHostPx = EditorScrollBars.ContentPaddingHostPx - s;
        Viewport.Pan = new Point2D(
            (float)(pageLeftHostPx * Viewport.BaseDpi / Viewport.Dpi),
            Viewport.Pan.Y);
        ViewChanged?.Invoke();
    }

    /// <summary>滚动条拖拽的轴。</summary>
    enum ScrollAxis
    {
        Horizontal,
        Vertical,
    }

    /// <summary>键盘动作。删除/取消/微调/缩放四档。</summary>
    /// <param name="k">语义动作 + 修饰键。</param>
    /// <returns>已处理返回 true；<see cref="KeyAction.None"/> 或无事可做返回 false，宿主可继续用这次按键。</returns>
    public bool OnKeyDown(KeyInfo k)
    {
        switch (k.Action)
        {
            case KeyAction.Delete:
                if (Selection.IsEmpty)
                    return false;
                DeleteSelection();
                return true;

            case KeyAction.Escape:
                CancelOrClear();
                return true;

            case KeyAction.NudgeLeft:
            case KeyAction.NudgeRight:
            case KeyAction.NudgeUp:
            case KeyAction.NudgeDown:
                return Nudge(k);

            case KeyAction.ZoomIn:
                return StepZoomAtViewCenter(ZoomStepFactor);

            case KeyAction.ZoomOut:
                return StepZoomAtViewCenter(1.0 / ZoomStepFactor);

            case KeyAction.ZoomFit:
                FitToPage();
                return true;

            case KeyAction.ZoomActual:
                Viewport.Reset();
                ViewChanged?.Invoke();
                return true;

            default:
                return false;
        }
    }

    /// <summary>从工具箱/菜单添加元素：按类型默认尺寸摆到"当前可见的页面区域"中心，选中它并发一条放置命令。</summary>
    /// <param name="typeKey">类型键，见 <see cref="CrossPrint.Core.ElementTypeKeys"/>。未知键不抛，直接返回。</param>
    public void AddElementAtCenter(string typeKey)
    {
        var element = CreatePending(typeKey);
        if (element is null)
            return;

        var center = VisiblePageCenterMm();
        Place(element, PlacementDefaults.BoxCenteredAt(typeKey, center.X, center.Y));
    }

    /// <summary>开始"从工具箱拖入"：进入放置态并把首个幽灵框落在起点。松手时元素落在拖出点。</summary>
    /// <param name="typeKey">类型键。</param>
    /// <param name="start">按下点（宿主设备 px）。</param>
    /// <returns>类型键未知返回 false 且不改变状态。</returns>
    public bool BeginPlacing(string typeKey, PointerPoint start)
    {
        var element = CreatePending(typeKey);
        if (element is null)
            return false;

        _pendingElement = element;
        _placingTypeKey = typeKey;
        _state = InteractionState.Placing;
        var mm = ToMm(start);
        _placingGhostMm = PlacementDefaults.BoxCenteredAt(typeKey, mm.X, mm.Y).ToSkRect();
        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 右键场景的命中与选中：命中元素则单选它，命中空白则清空选中。返回命中的元素（空白为 null）。
    /// </summary>
    /// <param name="p">右键位置（宿主设备 px + DPI）。</param>
    /// <returns>命中的元素或 null。</returns>
    public PaperElement? SelectAt(PointerPoint p)
    {
        var mm = ToMm(p);
        var hit = ElementHitTester.HitTest(Template.Elements, mm.X, mm.Y);
        if (hit is not null)
        {
            if (!Selection.Contains(hit.Id))
            {
                Selection.Select(hit.Id);
                ViewChanged?.Invoke();
            }
        }
        else if (!Selection.IsEmpty)
        {
            Selection.Clear();
            ViewChanged?.Invoke();
        }

        return hit;
    }

    /// <summary>
    /// 创建副本：深拷贝选中元素、分配新 id、偏移 <see cref="DuplicateOffsetMm"/> 并夹在页内，
    /// 追加到模板尾部后改选中副本。副本沿用源 ZIndex，因此在同一层里压在源元素上面。
    /// </summary>
    public void DuplicateSelection()
    {
        var sources = SelectedElements;
        if (sources.Count == 0)
            return;

        var pageWidth = Template.Paper.PageWidthMm;
        var continuous = Template.Paper.IsContinuous;
        var pageHeight = continuous ? 0 : Template.Paper.PageHeightMm;

        var clones = new List<PaperElement>(sources.Count);
        foreach (var source in sources)
        {
            var clone = source.CloneDeep();
            clone.Id = AssignElementId();
            clone.XMm = pageWidth > 0
                ? Math.Clamp(source.XMm + DuplicateOffsetMm, 0, Math.Max(0, pageWidth - clone.WidthMm))
                : source.XMm + DuplicateOffsetMm;
            clone.YMm = continuous
                ? source.YMm + DuplicateOffsetMm
                : Math.Clamp(source.YMm + DuplicateOffsetMm, 0, Math.Max(0, pageHeight - clone.HeightMm));
            clones.Add(clone);
        }

        Execute(new DuplicateElementsCommand(Template, Selection, clones));
        ViewChanged?.Invoke();
    }

    /// <summary>把选中元素在页面上水平居中（多选时逐个居中，不做整组外接框居中）。</summary>
    public void CenterSelectionHorizontally()
    {
        var elements = SelectedElements;
        if (elements.Count == 0)
            return;

        var pageWidth = Template.Paper.PageWidthMm;
        var entries = new List<GeometryCommand.Entry>(elements.Count);
        foreach (var element in elements)
        {
            var before = ElementBoxMm.FromElement(element);
            var x = pageWidth > 0 ? Math.Max(0, (pageWidth - before.Width) / 2) : before.X;
            entries.Add(new GeometryCommand.Entry(
                element,
                before,
                new ElementBoxMm(x, before.Y, before.Width, before.Height)));
        }

        Execute(new GeometryCommand($"水平居中 {entries.Count} 个元素", entries));
        ViewChanged?.Invoke();
    }

    /// <summary>把选中元素在页面上垂直居中；连续纸没有固定页高，调用无效。</summary>
    public void CenterSelectionVertically()
    {
        var elements = SelectedElements;
        if (elements.Count == 0 || Template.Paper.IsContinuous)
            return;

        var pageHeight = Template.Paper.PageHeightMm;
        var entries = new List<GeometryCommand.Entry>(elements.Count);
        foreach (var element in elements)
        {
            var before = ElementBoxMm.FromElement(element);
            var y = pageHeight > 0 ? Math.Max(0, (pageHeight - before.Height) / 2) : before.Y;
            entries.Add(new GeometryCommand.Entry(
                element,
                before,
                new ElementBoxMm(before.X, y, before.Width, before.Height)));
        }

        Execute(new GeometryCommand($"垂直居中 {entries.Count} 个元素", entries));
        ViewChanged?.Invoke();
    }

    /// <summary>切换纸张预设（A3/A4/A5/B5/连续纸/面单）。保留当前方向；连续纸强制纵向。</summary>
    /// <param name="preset">预设键，见 <see cref="PaperTypePresets"/>。</param>
    /// <exception cref="ArgumentException">未知预设。</exception>
    public void SetPaperPreset(string preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!PaperTypePresets.TryGet(preset, out _))
        {
            throw new ArgumentException(
                $"未知纸张预设 '{preset}'。可用：{string.Join(", ", PaperTypePresets.Names)}",
                nameof(preset));
        }

        var before = Template.Paper;
        var after = PaperSpec.FromPreset(preset);
        after.Orientation = after.HeightMm <= 0 ? PaperOrientation.Portrait : before.Orientation;
        CommitPaper($"设置纸张 {preset}", before, after);
    }

    /// <summary>自定义纸张尺寸（mm）。高度 0 表示连续纸，此时强制纵向。</summary>
    /// <param name="widthMm">纵向纸宽，必须大于 0。</param>
    /// <param name="heightMm">纵向纸高，0 表示连续纸。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸非法。</exception>
    public void SetCustomPaper(double widthMm, double heightMm)
    {
        if (!double.IsFinite(widthMm) || widthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthMm), widthMm, "纸宽必须是有限正数。");
        if (!double.IsFinite(heightMm) || heightMm < 0)
            throw new ArgumentOutOfRangeException(nameof(heightMm), heightMm, "纸高必须是非负有限数（0 表示连续纸）。");

        var before = Template.Paper;
        var after = before.Clone();
        after.Preset = PaperTypePresets.Custom;
        after.WidthMm = widthMm;
        after.HeightMm = heightMm;
        after.Orientation = heightMm <= 0 ? PaperOrientation.Portrait : before.Orientation;
        CommitPaper($"自定义纸张 {widthMm:0.##}×{heightMm:0.##}mm", before, after);
    }

    /// <summary>设置纸张方向。连续纸没有固定页高，不允许旋转。</summary>
    /// <param name="orientation">方向。</param>
    /// <exception cref="InvalidOperationException">当前是连续纸。</exception>
    public void SetPaperOrientation(PaperOrientation orientation)
    {
        if (!Enum.IsDefined(orientation))
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "未知纸张方向。");
        if (Template.Paper.IsContinuous)
            throw new InvalidOperationException("连续纸没有固定页高，不能旋转。");

        var before = Template.Paper;
        if (before.Orientation == orientation)
            return;

        var after = before.Clone();
        after.Orientation = orientation;
        CommitPaper(orientation == PaperOrientation.Landscape ? "纸张横向" : "纸张纵向", before, after);
    }

    /// <summary>切换纵向/横向。连续纸调用会抛 <see cref="InvalidOperationException"/>。</summary>
    public void RotatePaper()
        => SetPaperOrientation(Template.Paper.Orientation == PaperOrientation.Landscape
            ? PaperOrientation.Portrait
            : PaperOrientation.Landscape);

    void CommitPaper(string label, PaperSpec before, PaperSpec after)
    {
        if (SamePaper(before, after))
            return;

        Execute(new PaperCommand(Template, label, before, after));
        FitToPage();
        ViewChanged?.Invoke();
    }

    static bool SamePaper(PaperSpec a, PaperSpec b)
        => string.Equals(a.Preset, b.Preset, StringComparison.Ordinal)
        && a.WidthMm.Equals(b.WidthMm)
        && a.HeightMm.Equals(b.HeightMm)
        && a.Orientation == b.Orientation;

    /// <summary>删除选中元素。命令 Revert 会把元素插回原索引（同 Id 同 ZIndex）。</summary>
    public void DeleteSelection()
    {
        var elements = SelectedElements;
        if (elements.Count == 0)
        {
            Selection.Clear();
            ViewChanged?.Invoke();
            return;
        }

        Execute(new DeleteElementsCommand(Template, Selection, elements));
        ViewChanged?.Invoke();
    }

    /// <summary>选中元素整体上移一层：与绘制序中的下一个非选中元素交换层次。同 ZIndex 时交换数组位置（否则等于没动）。</summary>
    public void BringForward() => ChangeZOrder("上移一层", (sequence, z, selected) =>
        SwapNeighbours(sequence, z, selected, backward: false));

    /// <summary>选中元素整体下移一层。</summary>
    public void SendBackward() => ChangeZOrder("下移一层", (sequence, z, selected) =>
        SwapNeighbours(sequence, z, selected, backward: true));

    /// <summary>置顶：取全表最大 ZIndex+1（多选按当前层次递增，保持选中元素之间的相对次序）。</summary>
    public void BringToFront() => ChangeZOrder("置于顶层", (sequence, z, selected) =>
        PushToEdge(sequence, z, selected, toTop: true));

    /// <summary>置底：取全表最小 ZIndex-1。</summary>
    public void SendToBack() => ChangeZOrder("置于底层", (sequence, z, selected) =>
        PushToEdge(sequence, z, selected, toTop: false));

    /// <summary>
    /// 执行一条外部构造的命令（属性面板写回走这里，见 <see cref="PropertyChangeCommand"/>）。
    /// 内核不暴露撤销栈，但"任何变更都得过命令"这条对外也要成立，否则宿主自改模型会绕过广播。
    /// </summary>
    /// <param name="command">命令。执行后照常发 <see cref="TemplateChanged"/> 与 <see cref="CommandExecuted"/>。</param>
    public void RunCommand(EditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Execute(command);
    }

    /// <summary>写入一条属性变更命令：前向闭包 + 反向闭包。属性面板每次提交调一次。</summary>
    /// <param name="label">动作名（如"修改 文本字号"）。</param>
    /// <param name="apply">前向写入。</param>
    /// <param name="revert">撤销写入。</param>
    public void CommitPropertyChange(string label, Action apply, Action revert)
        => Execute(new PropertyChangeCommand(label, apply, revert));

    /// <summary>
    /// 当前属性通道：<b>恰好选中一个元素</b>时返回它的描述符集合，空选与多选返回 null。
    /// </summary>
    /// <remarks>
    /// 多选为什么不给：描述符是按元素类型生成的（文本框没有 <c>barcode.symbology</c>），
    /// 而各宿主的属性面板都靠"遍历描述符生成表单"，多选时把它们的交集拼出来会让面板在
    /// 选中集合变化时整块换形；v0.1 面板只服务单选，多选的批量编辑走后续的"对齐/分布"命令。
    /// 每次访问都重新构造——表格列数会变，描述符是快照（见 <see cref="ElementProperties"/>）。
    /// </remarks>
    public ElementProperties? SelectedProperties
    {
        get
        {
            if (Selection.Ids.Count != 1)
                return null;

            var element = Template.FindElement(Selection.PrimaryId!);
            return element is null ? null : ElementProperties.For(element);
        }
    }

    /// <summary>
    /// 属性面板提交一格：值先经过描述符校验/规范化，再作为 <see cref="EditorCommand"/> 落到模型上。
    /// </summary>
    /// <param name="key">描述符键，如 <c>text.fontSizePt</c>。</param>
    /// <param name="value">面板送来的值（字符串、数字、枚举成员名、颜色等，见 <see cref="PropertyDescriptor"/>）。</param>
    /// <exception cref="InvalidOperationException">没有恰好选中一个元素，或该属性只读。</exception>
    /// <exception cref="ArgumentException">未知属性键，或值无法转换。</exception>
    public void SetSelectedProperty(string key, object? value)
    {
        var properties = SelectedProperties
            ?? throw new InvalidOperationException("属性面板需要恰好选中一个元素（当前选中 " + Selection.Ids.Count + " 个）。");

        var descriptor = properties.Find(key)
            ?? throw new ArgumentException($"未知属性键：{key}", nameof(key));

        if (descriptor.ReadOnly)
            throw new InvalidOperationException($"属性 {key} 只读，不能写回。");

        // 先取旧值再写：命令的 Revert 要还原"写之前"的取值。
        // 然后写一次拿到规范化后的值（"12.5" → 12.5，枚举名 → 枚举值）：
        // SetValue 的转换在赋值之前完成，转换失败时模型还没被碰过，异常可以原样抛给面板。
        var original = descriptor.GetValue(properties.Element);
        descriptor.SetValue(properties.Element, value);
        var applied = descriptor.GetValue(properties.Element);

        // 命令里的两份值都用规范化后的口径；before 只可能是 GetValue 给出的类型（如 SKColor，不会是原始字符串）。
        CommitPropertyChange(
            $"修改 {descriptor.DisplayName}",
            () => descriptor.SetValue(properties.Element, applied),
            () => descriptor.SetValue(properties.Element, original));
    }

    // ---------------- 样式库管理（模板级，与元素属性通道同走命令体系） ----------------

    /// <summary>单条样式引用：哪个元素在哪个消费点用了该样式。删除拦截与宿主提示共用。</summary>
    /// <param name="ElementId">引用方元素 id。</param>
    /// <param name="Location">消费点描述，如"元素样式""表格列2""条件样式1"。</param>
    public sealed record StyleUsage(string ElementId, string Location);

    /// <summary>
    /// 新增样式到模板样式库并返回它。id 由 <see cref="PrintTemplate.NextStyleId"/> 生成（<c>st-N</c> 递增），
    /// 引用按 id 不随改名断链；撤销时整体移除。
    /// </summary>
    public NamedStyle AddStyle()
    {
        var ordinal = 1;
        var id = Template.NextStyleId();
        if (id.StartsWith("st-", StringComparison.Ordinal) && int.TryParse(id.AsSpan(3), out var n))
            ordinal = n;

        var style = new NamedStyle { Id = id, Name = $"新样式{ordinal}" };
        CommitPropertyChange(
            $"新增样式 \"{style.Name}\"",
            () => Template.Styles.Add(style),
            () => Template.Styles.Remove(style));
        return style;
    }

    /// <summary>
    /// 删除模板样式库里的样式。仍被引用时抛 <see cref="InvalidOperationException"/>——
    /// 引用断链在渲染期只表现为"样式悄悄失效"，设计器里报错并指出引用者比事后排查外观变化直接得多。
    /// </summary>
    /// <param name="styleId">要删除的样式 id。</param>
    /// <exception cref="InvalidOperationException">样式不存在，或仍被元素/表格列/规则引用。</exception>
    public void RemoveStyle(string styleId)
    {
        var style = Template.FindStyle(styleId)
            ?? throw new InvalidOperationException($"要删除的样式 \"{styleId}\" 不在模板样式库里。");

        var usage = GetStyleUsage(styleId);
        if (usage.Count > 0)
        {
            var sample = string.Join("、", usage.Take(3).Select(u => $"{u.ElementId}·{u.Location}"));
            throw new InvalidOperationException(
                $"样式 \"{style.Name}\"（{styleId}）仍被 {usage.Count} 处引用（{sample}{(usage.Count > 3 ? "…" : string.Empty)}），先取消引用再删除。");
        }

        var index = Template.Styles.IndexOf(style);
        CommitPropertyChange(
            $"删除样式 \"{style.Name}\"",
            () => Template.Styles.RemoveAt(index),
            () => Template.Styles.Insert(Math.Min(index, Template.Styles.Count), style));
    }

    /// <summary>
    /// 修改样式属性：<paramref name="mutate"/> 里随便改字段，内核取改前/改后快照进撤销栈。
    /// 快照整体替换（按 id 定位），样式实例的引用可能变化，宿主在命令执行后要重新取样式对象。
    /// </summary>
    /// <param name="styleId">目标样式 id。</param>
    /// <param name="label">撤销菜单里的动作名（如"修改样式 字号"）。</param>
    /// <param name="mutate">对样式对象的就地修改。</param>
    /// <exception cref="InvalidOperationException">样式不存在。</exception>
    public void UpdateStyle(string styleId, string label, Action<NamedStyle> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var style = Template.FindStyle(styleId)
            ?? throw new InvalidOperationException($"要修改的样式 \"{styleId}\" 不在模板样式库里。");

        var before = style.Clone();
        mutate(style);
        var after = style.Clone();
        CommitPropertyChange(
            label,
            () => ReplaceStyleSnapshot(styleId, after),
            () => ReplaceStyleSnapshot(styleId, before));
    }

    /// <summary>
    /// 样式引用清单：模板里所有还在用该样式的消费点（元素级 StyleId、表格级、列/表头格/条件/关键词规则）。
    /// 删除拦截与宿主"哪些地方在用"提示共用同一份口径。
    /// </summary>
    /// <param name="styleId">样式 id。</param>
    public IReadOnlyList<StyleUsage> GetStyleUsage(string styleId)
    {
        var usage = new List<StyleUsage>();
        if (string.IsNullOrEmpty(styleId))
            return usage;

        foreach (var element in Template.Elements)
        {
            if (element.StyleId == styleId)
                usage.Add(new StyleUsage(element.Id, "元素框样式"));

            // 载荷级引用：文本/线条/形状/二维码/条码各自的 StyleId（图片载荷没有）。
            var payloadStyleId = element switch
            {
                TextElement t => t.Text.StyleId,
                LineElement l => l.Line.StyleId,
                RectangleElement r => r.Rectangle.StyleId,
                EllipseElement e2 => e2.Ellipse.StyleId,
                QrCodeElement q => q.QrCode.StyleId,
                BarcodeElement b => b.Barcode.StyleId,
                _ => null,
            };
            if (payloadStyleId == styleId)
                usage.Add(new StyleUsage(element.Id, PayloadStyleName(element.TypeKey)));

            if (element is not TableElement table)
                continue;

            if (table.Table.StyleId == styleId)
                usage.Add(new StyleUsage(table.Id, "表格样式"));
            for (var i = 0; i < table.Table.Columns.Count; i++)
                if (table.Table.Columns[i].StyleId == styleId)
                    usage.Add(new StyleUsage(table.Id, $"表格列{i + 1}"));
            for (var i = 0; i < table.Table.HeaderRows.Count; i++)
                if (table.Table.HeaderRows[i].StyleId == styleId)
                    usage.Add(new StyleUsage(table.Id, $"表头格{i + 1}"));
            for (var i = 0; i < table.Table.ConditionalStyles.Count; i++)
                if (table.Table.ConditionalStyles[i].StyleId == styleId)
                    usage.Add(new StyleUsage(table.Id, $"条件样式{i + 1}"));
            for (var i = 0; i < table.Table.Highlights.Count; i++)
                if (table.Table.Highlights[i].StyleId == styleId)
                    usage.Add(new StyleUsage(table.Id, $"关键词样式{i + 1}"));
        }

        return usage;
    }

    /// <summary>按 id 定位样式库条目并整体替换为快照（撤销/重做的还原口径）。</summary>
    void ReplaceStyleSnapshot(string styleId, NamedStyle snapshot)
    {
        var index = Template.Styles.FindIndex(s => s.Id == styleId);
        if (index >= 0)
            Template.Styles[index] = snapshot;
    }

    /// <summary>载荷级引用在引用清单里的位置名（按元素类型）。</summary>
    static string PayloadStyleName(string typeKey) => typeKey switch
    {
        ElementTypeKeys.Text => "文本样式",
        ElementTypeKeys.Line => "线条样式",
        ElementTypeKeys.Rectangle or ElementTypeKeys.Ellipse => "形状样式",
        ElementTypeKeys.QrCode => "二维码样式",
        ElementTypeKeys.Barcode => "条码样式",
        _ => "载荷样式",
    };


    /// <summary>
    /// 执行属性通道声明的结构动作（<see cref="ElementProperties.Actions"/> 的键）。
    /// 列与规则类列表（条件样式/关键词样式/静态行合并）的增删是结构变更而非单格写回，
    /// 所以不走 <see cref="SetSelectedProperty"/>；命令（<see cref="TableColumnCommand"/> /
    /// <see cref="TableListItemCommand{T}"/>）整体快照表格载荷，撤销原样还原。
    /// </summary>
    /// <param name="actionKey">动作键，如 <c>table.appendColumn</c>、<c>table.columns[0].remove</c>、<c>table.appendConditional</c>。</param>
    /// <exception cref="InvalidOperationException">没有恰好选中一个元素、选中的不是表格元素，
    /// 或动作指向的列/条目已不存在——动作清单与描述符一样是快照，宿主须按最新选中重建面板。</exception>
    /// <exception cref="ArgumentException">未知动作键。</exception>
    public void RunElementAction(string actionKey)
    {
        ArgumentNullException.ThrowIfNull(actionKey);

        var element = Selection.Ids.Count == 1 ? Template.FindElement(Selection.PrimaryId!) : null;
        if (element is null)
            throw new InvalidOperationException($"结构动作需要恰好选中一个元素（当前选中 {Selection.Ids.Count} 个）。");

        if (element is not TableElement table)
            throw new InvalidOperationException($"结构动作只对表格元素有效（当前选中的是 {element.TypeKey}）。");

        const string columnsPrefix = ElementTypeKeys.Table + ".columns[";
        const string conditionalPrefix = ElementTypeKeys.Table + ".conditionalStyles[";
        const string highlightPrefix = ElementTypeKeys.Table + ".highlights[";
        const string rowSpanPrefix = ElementTypeKeys.Table + ".rowSpans[";

        EditorCommand command;
        if (actionKey == ElementTypeKeys.Table + ".appendColumn")
        {
            command = TableColumnCommand.Insert(table, table.Table.Columns.Count, NewColumn(table.Table), TableMaxWidthMm(table));
        }
        else if (actionKey.StartsWith(columnsPrefix, StringComparison.Ordinal) && actionKey.EndsWith("].insertBefore", StringComparison.Ordinal))
        {
            var index = ParseActionIndex(actionKey, columnsPrefix, "].insertBefore");
            // 位置过期就夹成末尾追加：插入不会误伤别的列，夹紧比报错友好。
            command = TableColumnCommand.Insert(table, index, NewColumn(table.Table), TableMaxWidthMm(table));
        }
        else if (actionKey.StartsWith(columnsPrefix, StringComparison.Ordinal) && actionKey.EndsWith("].remove", StringComparison.Ordinal))
        {
            var index = ParseActionIndex(actionKey, columnsPrefix, "].remove");
            if (index >= table.Table.Columns.Count)
                throw new InvalidOperationException(
                    $"动作 \"{actionKey}\" 指向的列已不存在（当前 {table.Table.Columns.Count} 列）。" +
                    "动作清单与描述符一样是快照，面板必须按最新选中重建。");

            command = TableColumnCommand.Remove(table, index);
        }
        else if (actionKey == ElementTypeKeys.Table + ".appendConditional")
        {
            command = TableListItemCommand<TableConditionalStyle>.Append(
                table, t => t.ConditionalStyles, s => s.Clone(), NewConditionalStyle(), "条件样式");
        }
        else if (actionKey.StartsWith(conditionalPrefix, StringComparison.Ordinal) && actionKey.EndsWith("].remove", StringComparison.Ordinal))
        {
            var index = ParseActionIndex(actionKey, conditionalPrefix, "].remove");
            if (index >= table.Table.ConditionalStyles.Count)
                throw new InvalidOperationException(
                    $"动作 \"{actionKey}\" 指向的条件样式已不存在（当前 {table.Table.ConditionalStyles.Count} 条）。" +
                    "动作清单与描述符一样是快照，面板必须按最新选中重建。");

            command = TableListItemCommand<TableConditionalStyle>.Remove(
                table, t => t.ConditionalStyles, s => s.Clone(), index, "条件样式");
        }
        else if (actionKey == ElementTypeKeys.Table + ".appendHighlight")
        {
            command = TableListItemCommand<TableHighlightRule>.Append(
                table, t => t.Highlights, h => h.Clone(), NewHighlight(), "关键词样式");
        }
        else if (actionKey.StartsWith(highlightPrefix, StringComparison.Ordinal) && actionKey.EndsWith("].remove", StringComparison.Ordinal))
        {
            var index = ParseActionIndex(actionKey, highlightPrefix, "].remove");
            if (index >= table.Table.Highlights.Count)
                throw new InvalidOperationException(
                    $"动作 \"{actionKey}\" 指向的关键词样式已不存在（当前 {table.Table.Highlights.Count} 条）。" +
                    "动作清单与描述符一样是快照，面板必须按最新选中重建。");

            command = TableListItemCommand<TableHighlightRule>.Remove(
                table, t => t.Highlights, h => h.Clone(), index, "关键词样式");
        }
        else if (actionKey == ElementTypeKeys.Table + ".appendRowSpan")
        {
            command = TableListItemCommand<TableRowSpan>.Append(
                table, t => t.RowSpans, s => s.Clone(), NewRowSpan(), "静态行合并");
        }
        else if (actionKey.StartsWith(rowSpanPrefix, StringComparison.Ordinal) && actionKey.EndsWith("].remove", StringComparison.Ordinal))
        {
            var index = ParseActionIndex(actionKey, rowSpanPrefix, "].remove");
            if (index >= table.Table.RowSpans.Count)
                throw new InvalidOperationException(
                    $"动作 \"{actionKey}\" 指向的静态行合并已不存在（当前 {table.Table.RowSpans.Count} 条）。" +
                    "动作清单与描述符一样是快照，面板必须按最新选中重建。");

            command = TableListItemCommand<TableRowSpan>.Remove(
                table, t => t.RowSpans, s => s.Clone(), index, "静态行合并");
        }
        else
        {
            throw new ArgumentException($"未知结构动作键：{actionKey}", nameof(actionKey));
        }

        Execute(command);
    }

    /// <summary>
    /// 新列的默认形态：宽度用模型默认（20mm，可再在面板里改），标题按现有"列N"的最大序号续号——
    /// 按插入位置取名会在既有的"列2"旁边再造一条"列2"，面板里两条同名根本分不出改的是哪一列。
    /// </summary>
    static TableColumn NewColumn(TableOptions table)
    {
        var ordinal = 0;
        foreach (var column in table.Columns)
            if (column.Title.Length > 1 && column.Title[0] == '列' && int.TryParse(column.Title.AsSpan(1), out var parsed))
                ordinal = Math.Max(ordinal, parsed);

        return new TableColumn { Title = $"列{Math.Max(ordinal, table.Columns.Count) + 1}" };
    }

    /// <summary>表格元素能加宽到的上限（mm）：右缘到纸边为止；纸宽未知（连续纸无固定宽的脏数据）就不设限。</summary>
    double TableMaxWidthMm(TableElement table)
    {
        var pageWidth = Template.Paper.PageWidthMm;
        return pageWidth > 0 ? Math.Max(0, pageWidth - table.XMm) : double.PositiveInfinity;
    }

    /// <summary>
    /// 新条件样式的默认形态：底色给一个可见的示例色——追加后阈值一填就见效果，
    /// 不至于"加了条规则却看不出哪里变了"。
    /// </summary>
    static TableConditionalStyle NewConditionalStyle() => new() { BackgroundColor = new SKColor(242, 208, 107) };

    /// <summary>新关键词样式的默认形态：关键词占位（等用户填），底色给一个淡黄示例色。</summary>
    static TableHighlightRule NewHighlight() => new() { Keyword = "关键词", BackgroundColor = new SKColor(255, 235, 156) };

    /// <summary>新静态行合并的默认形态：全 0 占位（等用户改成目标行列与跨度）。</summary>
    static TableRowSpan NewRowSpan() => new();

    static int ParseActionIndex(string actionKey, string prefix, string suffix)
    {
        var inner = actionKey[prefix.Length..^suffix.Length];
        if (!int.TryParse(inner, out var index) || index < 0)
            throw new ArgumentException($"未知结构动作键：{actionKey}", nameof(actionKey));

        return index;
    }

    void Execute(EditorCommand command)
    {
        command.Apply();
        TemplateChanged?.Invoke();
        CommandExecuted?.Invoke(command);
    }

    Point2D ToMm(PointerPoint p) => Viewport.ToMm(p.X, p.Y, p.Dpi);

    /// <summary>
    /// 当前选中的元素实例（按模板数组顺序）。叠加层与属性面板都读它；
    /// 返回的是<b>模型里的活引用</b>，只读用途，别改它。
    /// </summary>
    public IReadOnlyList<PaperElement> SelectedElements
        => Template.Elements.Where(e => Selection.Contains(e.Id)).ToList();

    /// <summary>当前页面高度（mm）：固定纸取纸张高度；连续纸用引擎的"内容自适应"判据，
    /// 这样叠加层的页框与导出/打印画出来的其实是同一张页。</summary>
    public double PageHeightMm => Template.Paper.IsContinuous
        ? TemplateRenderer.ResolvePageHeightMm(Template, new RenderOptions { Dpi = Viewport.Dpi })
        : Template.Paper.PageHeightMm;

    /// <summary>换手势/重抓前收尾在途拖拽：把模型当前（已由 OnPointerMoved 实时写好）的几何按松手同样的流程提交，
    /// 清快照、回空闲。不套用新按下的点——那是另一个手势的坐标，拿它再 ApplyDrag 会把元素拽到打断手势的位置。</summary>
    void FinishInFlightDrag()
    {
        if (_state is not (InteractionState.Moving or InteractionState.Resizing))
            return;

        CommitDrag();
        _drag.Clear();
        _activeHandle = ResizeHandle.None;
        _state = InteractionState.Idle;
        ViewChanged?.Invoke();
    }

    void CaptureDrag()
    {
        _drag.Clear();
        foreach (var element in SelectedElements)
            _drag.Add(new DragEntry(element, ElementBoxMm.FromElement(element), element.ZIndex));
    }

    /// <summary>拖拽期间的几何：一律从按下时的快照 + 累计位移算，不做增量累加——
    /// 增量累加会把每次 float/double 舍入误差留在模型里，拖一次偏一次。</summary>
    void ApplyDrag(Point2D mm)
    {
        var dx = mm.X - _pressMm.X;
        var dy = mm.Y - _pressMm.Y;

        // 宿主送来非有限的点（事件 DPI 是 NaN/0 就会算出 NaN），这次拖拽就整步作废：
        // 写进 XMm/YMm 的 NaN 会跟着命令进撤销栈、被 JSON 持久化，比"这一帧没动"难查得多。
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
            return;

        if (_state == InteractionState.Moving)
        {
            foreach (var entry in _drag)
            {
                entry.Element.XMm = entry.Box.X + dx;
                entry.Element.YMm = entry.Box.Y + dy;
            }
        }
        else
        {
            var target = ComputeResizeBox(_pressBox, _activeHandle, dx, dy, ShiftPressed, _resizeLineDirection);
            ResizeTo(target);
        }

        ViewChanged?.Invoke();
    }

    void ResizeTo(ElementBoxMm target)
    {
        if (_drag.Count == 1)
        {
            var element = _drag[0].Element;
            element.XMm = target.X;
            element.YMm = target.Y;
            element.WidthMm = target.Width;
            element.HeightMm = target.Height;
            return;
        }

        // 多选：把外接框的缩放仿射作用到每个元素上（v0.1 的交互路径进不来这条，接口按多选写好）。
        var sx = _pressBox.Width > GeometryEpsilonMm ? target.Width / _pressBox.Width : 1.0;
        var sy = _pressBox.Height > GeometryEpsilonMm ? target.Height / _pressBox.Height : 1.0;
        foreach (var entry in _drag)
        {
            entry.Element.XMm = target.X + (entry.Box.X - _pressBox.X) * sx;
            entry.Element.YMm = target.Y + (entry.Box.Y - _pressBox.Y) * sy;
            entry.Element.WidthMm = entry.Box.Width * sx;
            entry.Element.HeightMm = entry.Box.Height * sy;
        }
    }

    void CommitDrag()
    {
        var changed = _drag.Any(e =>
            Math.Abs(e.Element.XMm - e.Box.X) > GeometryEpsilonMm
            || Math.Abs(e.Element.YMm - e.Box.Y) > GeometryEpsilonMm
            || Math.Abs(e.Element.WidthMm - e.Box.Width) > GeometryEpsilonMm
            || Math.Abs(e.Element.HeightMm - e.Box.Height) > GeometryEpsilonMm);

        if (!changed)
            return;

        var entries = _drag
            .Select(e => new GeometryCommand.Entry(e.Element, e.Box, ElementBoxMm.FromElement(e.Element)))
            .ToList();

        var label = _state == InteractionState.Moving
            ? $"移动 {entries.Count} 个元素"
            : $"缩放 {entries.Count} 个元素（{_activeHandle}）";

        Execute(new GeometryCommand(label, entries));
    }

    void CancelOrClear()
    {
        switch (_state)
        {
            case InteractionState.Moving:
            case InteractionState.Resizing:
            {
                // 回到按下时的快照。拖拽中途从没发过命令，所以这是"取消"而不是"撤销"，不发 CommandExecuted。
                foreach (var entry in _drag)
                {
                    entry.Element.XMm = entry.Box.X;
                    entry.Element.YMm = entry.Box.Y;
                    entry.Element.WidthMm = entry.Box.Width;
                    entry.Element.HeightMm = entry.Box.Height;
                    entry.Element.ZIndex = entry.ZIndex;
                }

                _drag.Clear();
                _activeHandle = ResizeHandle.None;
                _state = InteractionState.Idle;
                TemplateChanged?.Invoke();
                ViewChanged?.Invoke();
                return;
            }

            case InteractionState.Panning:
                Viewport.Pan = _panStartPan;
                _state = InteractionState.Idle;
                ViewChanged?.Invoke();
                return;

            case InteractionState.Scrolling:
                // 与 Panning 同样按"取消"处理：回到按下时的平移，不发命令。
                Viewport.Pan = _scrollStartPan;
                _state = InteractionState.Idle;
                ViewChanged?.Invoke();
                return;

            case InteractionState.Placing:
                AbandonPlacing();
                return;

            default:
                if (!Selection.IsEmpty)
                {
                    Selection.Clear();
                    ViewChanged?.Invoke();
                }
                else if (_placingGhostMm is not null)
                {
                    AbandonPlacing();
                }

                return;
        }
    }

    void AbandonPlacing()
    {
        _pendingElement = null;
        _placingTypeKey = null;
        _placingGhostMm = null;
        _state = InteractionState.Idle;
        ViewChanged?.Invoke();
    }

    void CommitPlacement(Point2D centerMm)
    {
        var element = _pendingElement;
        var typeKey = _placingTypeKey;
        if (element is null || typeKey is null)
        {
            AbandonPlacing();
            return;
        }

        _pendingElement = null;
        _placingTypeKey = null;
        _placingGhostMm = null;
        _state = InteractionState.Idle;

        Place(element, PlacementDefaults.BoxCenteredAt(typeKey, centerMm.X, centerMm.Y));
    }

    PaperElement? CreatePending(string typeKey)
    {
        if (ElementFactory.Find(typeKey) is null)
            return null;

        var element = ElementFactory.Create(typeKey);
        element.Id = AssignElementId();
        return element;
    }

    void Place(PaperElement element, ElementBoxMm box)
    {
        element.XMm = box.X;
        element.YMm = box.Y;
        element.WidthMm = box.Width;
        element.HeightMm = box.Height;

        // 新元素必须压在所有现存元素之上：渲染按 ZIndex 升序（同值才看数组序），
        // 只靠数组序会被「置顶/上移」过的高层元素压住——用户看到的就是"后放的跑到最底层"。
        element.ZIndex = Template.Elements.Count == 0 ? 0 : Template.Elements.Max(e => e.ZIndex) + 1;

        Execute(new PlaceElementCommand(Template, Selection, element, Template.Elements.Count));
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// 分配元素 id。
    /// Core 的 <see cref="PrintTemplate.NextElementId"/> 只看现存元素，所以"删掉最大号的元素再新建"
    /// 会把同一个号再发一次——对宿主就是 id 突然复用。内核记住本会话发过的最大号取更严的那个。
    /// </summary>
    string AssignElementId()
    {
        var raw = Template.NextElementId();
        var next = int.TryParse(raw.AsSpan(3), out var parsed) ? parsed : 1;
        next = Math.Max(next, _issuedIdFloor + 1);
        while (Template.FindElement($"el-{next}") is not null)
            next++;

        _issuedIdFloor = Math.Max(_issuedIdFloor, next);
        return $"el-{next}";
    }

    bool Nudge(KeyInfo k)
    {
        if (Selection.IsEmpty)
            return false;

        // 步长档位：无修饰 1mm；Shift 粗调 10mm；Ctrl 微调 0.1mm。
        // 同时按 Shift+Ctrl 取粗调（谁优先都行，但绝不能相乘成 1mm——那会让"微调键"看起来没反应）。
        var step = k.Shift ? CoarseNudgeStepMm : k.Control ? FineNudgeStepMm : NudgeStepMm;
        var dx = k.Action switch
        {
            KeyAction.NudgeLeft => -step,
            KeyAction.NudgeRight => step,
            _ => 0.0,
        };
        var dy = k.Action switch
        {
            KeyAction.NudgeUp => -step,
            KeyAction.NudgeDown => step,
            _ => 0.0,
        };

        var entries = new List<GeometryCommand.Entry>();
        foreach (var element in SelectedElements)
        {
            var before = ElementBoxMm.FromElement(element);
            element.XMm += dx;
            element.YMm += dy;
            entries.Add(new GeometryCommand.Entry(element, before, ElementBoxMm.FromElement(element)));
        }

        Execute(new GeometryCommand($"微调 {entries.Count} 个元素（{step:0.###}mm）", entries));
        ViewChanged?.Invoke();
        return true;
    }

    bool StepZoomAtViewCenter(double factor)
    {
        var before = Viewport.Zoom;
        var after = Viewport.ZoomAt(factor, ViewWidthHostPx / 2, ViewHeightHostPx / 2, Viewport.Dpi);
        if (after == before)
            return false;

        ViewChanged?.Invoke();
        return true;
    }

    void FitToPage()
    {
        if (ViewWidthHostPx <= 0 || ViewHeightHostPx <= 0)
            return;

        // 连续纸没有固定页高，交给引擎的同一套判据（内容底边 + 下留白 + 2mm），
        // 这样"整页适配"框住的正是渲染器真正会画出来的那张页（见 PageHeightMm）。
        Viewport.FitToPage(Template.Paper.PageWidthMm, PageHeightMm, ViewWidthHostPx, ViewHeightHostPx, Viewport.Dpi);
        ViewChanged?.Invoke();
    }

    /// <summary>当前可见的页面区域中心（mm）：视口四角换算成 mm 后与纸张求交。放置的新元素要落在用户正看着的地方。</summary>
    Point2D VisiblePageCenterMm()
    {
        var pageWidth = Template.Paper.PageWidthMm;

        // 连续纸没有固定页高（只有内容自适应出来的页高），所以纵向不按页底裁：
        // 按自适应页高裁会让第一个新元素被顶到纸外面去——用户明明看着一整段空白可放。
        var pageHeight = Template.Paper.IsContinuous ? 0 : Template.Paper.PageHeightMm;

        if (ViewWidthHostPx <= 0 || ViewHeightHostPx <= 0)
            return new Point2D(pageWidth / 2, pageHeight > 0 ? pageHeight / 2 : 0);

        var a = Viewport.ToMm(0, 0, Viewport.Dpi);
        var b = Viewport.ToMm(ViewWidthHostPx, ViewHeightHostPx, Viewport.Dpi);
        var visibleLeft = Math.Min(a.X, b.X);
        var visibleRight = Math.Max(a.X, b.X);
        var visibleTop = Math.Min(a.Y, b.Y);
        var visibleBottom = Math.Max(a.Y, b.Y);

        var left = Math.Max(visibleLeft, 0);
        var top = Math.Max(visibleTop, 0);
        var right = pageWidth > 0 ? Math.Min(visibleRight, pageWidth) : visibleRight;
        var bottom = pageHeight > 0 ? Math.Min(visibleBottom, pageHeight) : visibleBottom;

        // 完全没交到页面（平移到手稿外了）就退回页面中心，别让新元素落在负坐标上。
        if (right <= left || bottom <= top)
            return new Point2D(pageWidth / 2, pageHeight > 0 ? pageHeight / 2 : Math.Max(0, (visibleTop + visibleBottom) / 2));

        return new Point2D((left + right) / 2, (top + bottom) / 2);
    }

    ResizeHandle HitHandle(PointerPoint p)
    {
        if (Selection.IsEmpty)
            return ResizeHandle.None;

        var box = ElementHitTester.UnionBox(SelectedElements);
        var radius = HandleHitRadiusHostPx;

        foreach (var (handle, hx, hy) in ElementHitTester.HandleAnchorsMm(box))
        {
            // 手柄锚点 mm → 设备 px，再按固定半径（px）比距离：半径不随 zoom 变，见属性文档。
            var host = Viewport.ToHostPx(hx, hy, p.Dpi);
            if (Math.Abs(host.X - p.X) <= radius && Math.Abs(host.Y - p.Y) <= radius)
                return handle;
        }

        return ResizeHandle.None;
    }

    LineDirection? SingleSelectedLineDirection()
    {
        if (Selection.Ids.Count != 1)
            return null;

        var element = Template.FindElement(Selection.PrimaryId!);
        return element is LineElement line ? line.Line.Direction : null;
    }

    /// <summary>
    /// 缩放的盒计算：拖动边/角，对边（或对角）就是锚点，永不移动；宽高夹紧到 <see cref="MinElementMm"/>。
    /// </summary>
    /// <param name="baseBox">按下时的外接框（mm）。</param>
    /// <param name="handle">拖动手柄。</param>
    /// <param name="dx">横向位移（mm）。</param>
    /// <param name="dy">纵向位移（mm）。</param>
    /// <param name="preserveAspect">Shift 等比。</param>
    /// <param name="lineDirection">单选元素是线段时其方向；线段只沿自身方向改长度，另一轴保持原样（垂直线的宽恒为 0）。</param>
    /// <returns>目标盒（mm）。</returns>
    internal static ElementBoxMm ComputeResizeBox(
        ElementBoxMm baseBox,
        ResizeHandle handle,
        double dx,
        double dy,
        bool preserveAspect,
        LineDirection? lineDirection)
    {
        var hasLeft = handle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft or ResizeHandle.Left;
        var hasRight = handle is ResizeHandle.TopRight or ResizeHandle.BottomRight or ResizeHandle.Right;
        var hasTop = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        var hasBottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;

        // 线段沿方向锁死另一轴：水平线的高度、垂直线的宽度不参与缩放。
        if (lineDirection == LineDirection.Horizontal)
            hasTop = hasBottom = false;
        else if (lineDirection == LineDirection.Vertical)
            hasLeft = hasRight = false;

        var left = baseBox.X;
        var right = baseBox.RightMm;
        var top = baseBox.Y;
        var bottom = baseBox.BottomMm;

        // 拖动边越过对边时把拖动边钉在"对边 − min"上：这就是"拖过对面"的夹紧，
        // 也是"往回缩过头"的夹紧——同一条不等式，不需要区分两种情况。
        if (hasLeft)
            left = Math.Min(baseBox.X + dx, right - MinElementMm);
        if (hasRight)
            right = Math.Max(baseBox.RightMm + dx, left + MinElementMm);
        if (hasTop)
            top = Math.Min(baseBox.Y + dy, bottom - MinElementMm);
        if (hasBottom)
            bottom = Math.Max(baseBox.BottomMm + dy, top + MinElementMm);

        var isCorner = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight or ResizeHandle.BottomRight or ResizeHandle.BottomLeft;
        if (preserveAspect && isCorner && lineDirection is null)
        {
            var oldWidth = Math.Max(baseBox.Width, MinElementMm);
            var oldHeight = Math.Max(baseBox.Height, MinElementMm);
            var ratio = oldWidth / oldHeight;
            var width = right - left;
            var height = bottom - top;

            // 以"相对变化更大"的那条轴为准反算另一条，手感与主流设计器一致。
            if (Math.Abs(width / oldWidth - 1) >= Math.Abs(height / oldHeight - 1))
                height = width / ratio;
            else
                width = height * ratio;

            width = Math.Max(width, MinElementMm);
            height = Math.Max(height, MinElementMm);

            // 锚在诊断：拖左上角时右下角不动，拖右下角时左上角不动。
            var anchorRight = hasLeft ? baseBox.RightMm : right;
            var anchorBottom = hasTop ? baseBox.BottomMm : bottom;
            left = hasLeft ? anchorRight - width : baseBox.X;
            right = hasLeft ? anchorRight : left + width;
            top = hasTop ? anchorBottom - height : baseBox.Y;
            bottom = hasTop ? anchorBottom : top + height;
        }

        return new ElementBoxMm(left, top, right - left, bottom - top);
    }

    /// <summary>层次操作的一次改写：给出数组顺序（可就地交换）与 ZIndex 表，返回是否真的发生了变化。</summary>
    delegate bool ZOrderEdit(List<PaperElement> sequence, Dictionary<PaperElement, int> z, HashSet<PaperElement> selected);

    void ChangeZOrder(string label, ZOrderEdit edit)
    {
        var selected = SelectedElements;
        if (selected.Count == 0)
            return;

        var before = Template.Elements.Select(e => (e, e.ZIndex)).ToList();
        var sequence = Template.Elements.ToList();
        var z = sequence.ToDictionary(e => e, e => e.ZIndex);
        if (!edit(sequence, z, new HashSet<PaperElement>(selected)))
            return;

        var after = sequence.Select(e => (e, z[e])).ToList();
        Execute(new ZOrderCommand(Template, label, before, after));
        ViewChanged?.Invoke();
    }

    /// <summary>置顶/置底：把选中元素整段贴到全表最高/最低的一侧，多选按当前层次依次 +1/−1 以保持彼此次序。</summary>
    static bool PushToEdge(List<PaperElement> sequence, Dictionary<PaperElement, int> z, HashSet<PaperElement> selected, bool toTop)
    {
        var draw = sequence.OrderBy(e => z[e]).ToList();
        var edge = toTop
            ? draw.Max(e => z[e]) + 1
            : draw.Min(e => z[e]) - 1;

        var step = toTop ? 1 : -1;
        foreach (var element in draw.Where(selected.Contains))
        {
            z[element] = edge;
            edge += step;
        }

        return true;
    }

    /// <summary>上移/下移一层：与绘制序里相邻的非选中元素交换。ZIndex 不同就换号；相同则换数组位置（同值时数组顺序才是绘制次序，换号等于没动）。</summary>
    static bool SwapNeighbours(List<PaperElement> sequence, Dictionary<PaperElement, int> z, HashSet<PaperElement> selected, bool backward)
    {
        var changed = false;
        var draw = sequence.OrderBy(e => z[e]).ToList();

        // 上移时从最上面的选中元素开始处理，否则两个相邻的选中元素会互相交换、白跑一趟。
        var targets = backward
            ? draw.Where(selected.Contains).ToList()
            : draw.Where(selected.Contains).Reverse().ToList();

        foreach (var element in targets)
        {
            var index = draw.IndexOf(element);
            var neighbourIndex = backward ? index - 1 : index + 1;
            if (neighbourIndex < 0 || neighbourIndex >= draw.Count)
                continue;

            var neighbour = draw[neighbourIndex];
            if (selected.Contains(neighbour))
                continue;

            if (z[neighbour] != z[element])
            {
                (z[element], z[neighbour]) = (z[neighbour], z[element]);
            }
            else
            {
                var a = sequence.IndexOf(element);
                var b = sequence.IndexOf(neighbour);
                (sequence[a], sequence[b]) = (sequence[b], sequence[a]);
            }

            draw[neighbourIndex] = element;
            draw[index] = neighbour;
            changed = true;
        }

        return changed;
    }

    /// <summary>按下时的几何快照条目。</summary>
    /// <param name="Element">元素实例。</param>
    /// <param name="Box">按下时几何（mm）。</param>
    /// <param name="ZIndex">按下时层次。</param>
    readonly record struct DragEntry(PaperElement Element, ElementBoxMm Box, int ZIndex);
}

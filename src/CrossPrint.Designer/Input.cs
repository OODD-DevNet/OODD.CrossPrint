namespace CrossPrint.Designer;

/// <summary>
/// 一次指针事件。<b>坐标是宿主设备像素</b>，<see cref="Dpi"/> 是宿主逻辑 DPI——
/// 内核据此换算，宿主不许自己先除以缩放（架构 §5 的 DPI 纪律：每宿主只允许换算一次）。
/// </summary>
/// <param name="X">宿主设备横坐标（控件内）。</param>
/// <param name="Y">宿主设备纵坐标（控件内）。</param>
/// <param name="Dpi">宿主逻辑 DPI。96 = 100%；Avalonia/WPF 传 96×scaling，WinForms 传 DeviceDpi。</param>
/// <param name="Button">触发事件的按键。</param>
public readonly record struct PointerPoint(double X, double Y, double Dpi, PointerButton Button);

/// <summary>指针按键。各宿主枚举不同，适配层负责映射；中键固定用于平移。</summary>
public enum PointerButton
{
    /// <summary>无按键（移动事件）。</summary>
    None,

    /// <summary>左键/主键：选中、移动、缩放、框选。</summary>
    Primary,

    /// <summary>右键：v0.1 不绑定编辑动作，宿主可用作自己的上下文菜单。</summary>
    Secondary,

    /// <summary>中键：按住即平移，不需要命中任何东西。</summary>
    Middle,
}

/// <summary>一次键盘动作。宿主把各自的 Key/Keys 枚举翻译成这里的一个动作 + 修饰键状态。</summary>
/// <param name="Action">语义动作。</param>
/// <param name="Shift">Shift 是否按下。</param>
/// <param name="Control">Ctrl/Cmd 是否按下。</param>
/// <param name="Alt">Alt/Option 是否按下。</param>
public readonly record struct KeyInfo(KeyAction Action, bool Shift = false, bool Control = false, bool Alt = false);

/// <summary>
/// 键盘语义。刻意不传"字符"，宿主差异（Avalonia 的 Key、WPF 的 Key、WinForms 的 Keys）
/// 留给适配层折叠，内核只认动作。
/// </summary>
public enum KeyAction
{
    /// <summary>无对应动作，内核忽略并返回 false。</summary>
    None,

    /// <summary>Delete/Backspace：删除选中元素。</summary>
    Delete,

    /// <summary>Esc：取消当前拖拽（回到按下前）并清空选中。</summary>
    Escape,

    /// <summary>左方向键：选中元素左移 <see cref="EditorKernel.NudgeStepMm"/>。</summary>
    NudgeLeft,

    /// <summary>右方向键。</summary>
    NudgeRight,

    /// <summary>上方向键。</summary>
    NudgeUp,

    /// <summary>下方向键。</summary>
    NudgeDown,

    /// <summary>放大一档（以视口中心为锚）。</summary>
    ZoomIn,

    /// <summary>缩小一档（以视口中心为锚）。</summary>
    ZoomOut,

    /// <summary>整页适配窗口。</summary>
    ZoomFit,

    /// <summary>回到 100%。</summary>
    ZoomActual,
}

namespace CrossPrint.Designer;

/// <summary>
/// 编辑命令。内核<b>所有</b>变更（移动、缩放、删除、属性写回、zIndex、放置）都构造一个命令再执行，
/// v0.1 不暴露撤销栈，但结构已经就位：P1 加 <c>UndoStack</c> 就是 <c>Apply()</c> 入栈、
/// <c>Revert()</c> 出栈，内核代码一行不改（架构 §4.2）。
/// </summary>
public interface EditorCommand
{
    /// <summary>人类可读的动作名，撤销菜单与调试用（如"移动 3 个元素"）。</summary>
    string Label { get; }

    /// <summary>前向执行。必须幂等于"从 <see cref="Revert"/> 之后的状态再走一次"。</summary>
    void Apply();

    /// <summary>撤销到 <see cref="Apply"/> 之前的状态。</summary>
    void Revert();
}

/// <summary>
/// 交互状态机的状态。<see cref="Idle"/> 之外内核都在"一次拖拽"里，
/// 中途的 <see cref="PointerPoint"/> 只更新预览，不落模型；松手才生成命令。
/// </summary>
public enum InteractionState
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>左键按住元素在移动。</summary>
    Moving,

    /// <summary>按住某个缩放手柄改尺寸。</summary>
    Resizing,

    /// <summary>中键（或空格+左键）平移视口。</summary>
    Panning,

    /// <summary>从工具箱拖入，正在放置（叠加层画幽灵框）。</summary>
    Placing,

    /// <summary>按住滚动条滑块滚动视口（内容超出视口时叠加层画出的轨道条）。</summary>
    Scrolling,
}

/// <summary>
/// 八向手柄。<see cref="None"/> 表示不在手柄上。
/// 排列顺序即叠加层的绘制顺序，也用于 <see cref="EditorKernel"/> 的命中优先级（角柄先于边柄）。
/// </summary>
public enum ResizeHandle
{
    /// <summary>不在任何手柄上。</summary>
    None,

    /// <summary>左上角。</summary>
    TopLeft,

    /// <summary>上边中点。</summary>
    Top,

    /// <summary>右上角。</summary>
    TopRight,

    /// <summary>右边中点。</summary>
    Right,

    /// <summary>右下角。</summary>
    BottomRight,

    /// <summary>下边中点。</summary>
    Bottom,

    /// <summary>左下角。</summary>
    BottomLeft,

    /// <summary>左边中点。</summary>
    Left,
}

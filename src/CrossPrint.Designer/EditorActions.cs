using System.Globalization;

namespace CrossPrint.Designer;

/// <summary>
/// 宿主工具条/状态栏的共享动作口径：撤销、重做、四档缩放。
/// 各宿主只负责搭控件并订阅 <see cref="EditorKernel.UndoStateChanged"/> 刷新可用态；
/// 缩放统一走 <see cref="EditorKernel.OnKeyDown(KeyInfo)"/>，避免有的宿主用
/// <c>Viewport.Reset()</c>、有的用 KeyAction 导致「100%」语义分叉。
/// </summary>
public static class EditorActions
{
    /// <summary>撤销一步；返回是否真的撤销了命令。</summary>
    public static bool Undo(EditorKernel kernel) => kernel.Undo();

    /// <summary>重做一步；返回是否真的重做了命令。</summary>
    public static bool Redo(EditorKernel kernel) => kernel.Redo();

    /// <summary>缩小一档。</summary>
    public static void ZoomOut(EditorKernel kernel) => Zoom(kernel, KeyAction.ZoomOut);

    /// <summary>放大一档。</summary>
    public static void ZoomIn(EditorKernel kernel) => Zoom(kernel, KeyAction.ZoomIn);

    /// <summary>实际大小（100%）。</summary>
    public static void ZoomActual(EditorKernel kernel) => Zoom(kernel, KeyAction.ZoomActual);

    /// <summary>适合窗口：内核 KeyAction.ZoomFit 与宿主 DesignerControl.FitToPage 等价，
    /// 但宿主画布尺寸口径更准，状态栏按钮优先调 <c>_designer.FitToPage()</c>。</summary>
    public static void ZoomFit(EditorKernel kernel) => Zoom(kernel, KeyAction.ZoomFit);

    /// <summary>按内核键动作执行缩放（−/+/100%/适合窗口统一入口）。</summary>
    public static void Zoom(EditorKernel kernel, KeyAction action)
        => kernel.OnKeyDown(new KeyInfo(action));

    /// <summary>状态栏比例读数（如 100% / 120%）。</summary>
    public static string ZoomPercent(EditorKernel kernel)
        => (kernel.Viewport.Zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

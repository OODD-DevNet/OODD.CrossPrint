namespace CrossPrint.Designer;

/// <summary>
/// 属性编辑器种类。宿主属性面板据此决定"用哪个控件、怎么校验"，
/// 但<b>不</b>据此猜控件类型——枚举里不出现任何宿主词汇（架构 §1.1 铁律 1）。
/// 一条通道要同时喂饱 Avalonia/WPF/WinForms 三套表单生成器，所以这里只表达
/// "语义种类 + 取值域"，具体控件留给宿主。
/// </summary>
public enum PropertyKind
{
    /// <summary>单行文本：字体名、数据字段路径、资源键等。</summary>
    Text,

    /// <summary>多行文本：元素正文、Base64 数据、表格静态行。</summary>
    MultilineText,

    /// <summary>浮点数：毫米几何、磅字号、倍数行距。</summary>
    Number,

    /// <summary>整数：绘制层级。</summary>
    Integer,

    /// <summary>布尔：加粗、换行、显示表头等开关。</summary>
    Boolean,

    /// <summary>枚举单选，候选项在 <see cref="PropertyDescriptor.Options"/>（CLR 成员名原样）。</summary>
    Enum,

    /// <summary>颜色，规范串形如 <c>#RRGGBB</c> / <c>#AARRGGBB</c>。</summary>
    Color,

    /// <summary>虚线段长循环（mm），值类型为 <c>double[]</c>，null 表示实线。</summary>
    DashPattern,

    /// <summary>
    /// 模板样式库引用：值是 <see cref="CrossPrint.Core.PrintTemplate.Styles"/> 里的样式 id（string），
    /// null/空串表示不引用。候选项是模板当前样式清单（运行时数据），宿主据此填下拉框——
    /// 与 <see cref="Enum"/> 的静态成员名不同，这里给的是 <see cref="CrossPrint.Core.NamedStyle"/> 的 id。
    /// 引用不存在的样式在渲染期静默忽略（只影响外观，不报错），所以宿主不必校验取值。
    /// </summary>
    StyleRef,
}

using System;
using System.Collections.Generic;
using System.Linq;
using CrossPrint.Core;

namespace CrossPrint.Designer;

/// <summary>
/// 属性面板下拉的<b>统一值口径</b>：候选项、当前选中值、以及"描述符结构是否变了"的签名。
/// 放 Designer 层是为了让七个宿主共用同一份判据，避免每个宿主各写一套 `ToString()` 匹配
/// 后，某个枚举（尤其是 [Flags]）在个别宿主里悄悄显示空白或提交不生效。
/// </summary>
/// <remarks>
/// <para><b>值语义不变</b>：<see cref="Values"/> 里永远是描述符声明的 CLR 成员名（与模板 JSON 同口径），
/// 宿主负责显示翻译（Avalonia 用 ItemTemplate 走 <c>PropertyText.Option</c>），提交时交回的仍是 CLR 名。
/// 绝不能把翻译后的中文串塞进 ItemsSource——<c>PropertyValues.ToEnum</c> 只认成员名。</para>
/// <para><b>为什么要把当前值补进候选项</b>：<c>[Flags] BorderSide</c> 允许组合值，
/// <c>"Top, Right"</c> 的 <c>ToString()</c> 不在 <c>Enum.GetNames</c> 里；样式引用指向一个已从样式库删除的 id
/// 同理。旧实现里这两种情况都让下拉空白（或误落到第一项），用户看到的就是"选了没变化"。
/// 补进候选项后既能正确显示，<c>PropertyValues.TryParseFlags</c> 也能把它原样提交回模型。</para>
/// <para><b>签名为什么只含 Key + Hidden，不含值</b>：拖拽元素时每帧发 ViewChanged，
/// 坐标值每帧都变；签名若含值，属性面板会被逐帧整块重建，正在输入的文本框会被拆掉。
/// Hidden 位则必须进签名——表格列切换 <c>cellRender</c> 后「码制/纠错等级/图片数据类型」是
/// 靠 Hidden 变化出现的（描述符是快照），撤销/重做也走这条路径。</para>
/// </remarks>
public static class PropertyOptions
{
    /// <summary>下拉候选项（值语义）。当前值不在候选项里时补一项，避免空白/误选。</summary>
    public static IReadOnlyList<string> Values(PropertyDescriptor descriptor, PaperElement element)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        if (element is null) throw new ArgumentNullException(nameof(element));

        var options = descriptor.Options ?? Array.Empty<string>();
        var current = Current(descriptor, element);
        if (string.IsNullOrEmpty(current) || Contains(options, current))
            return options;

        // 只在枚举/样式引用上补：其它 Kind 的 Options 为 null，没有"多出一个值"的语义。
        if (descriptor.Kind != PropertyKind.Enum && descriptor.Kind != PropertyKind.StyleRef)
            return options;

        var extended = new List<string>(options.Count + 1);
        extended.AddRange(options);
        extended.Add(current);
        return extended;
    }

    /// <summary>当前选中项（值语义）：枚举成员名、Flags 组合串，或样式 id。</summary>
    public static string? Current(PropertyDescriptor descriptor, PaperElement element)
        => descriptor.GetValue(element)?.ToString();

    /// <summary>
    /// 描述符结构签名：元素 Id + 每条描述符的 Key 与可见性。
    /// 宿主在 ViewChanged / 提交后比较它，变了就重建面板（让条件属性即时显隐、让下拉从活模型回读）。
    /// </summary>
    public static string StructureSignature(ElementProperties? properties)
        => properties is null
            ? string.Empty
            : properties.Element.Id + "|" + string.Join("|",
                properties.Descriptors.Select(d => d.Key + (d.Hidden ? ":h" : ":v")));

    /// <summary>宿主直接从内核取签名的便捷入口。</summary>
    public static string StructureSignature(EditorKernel kernel)
        => StructureSignature(kernel.SelectedProperties);

    /// <summary>候选项里是否已有该值（区分大小写，与 ToEnum 的 Ordinal 口径一致）。</summary>
    public static bool Contains(IReadOnlyList<string> options, string? value)
    {
        if (value is null || options.Count == 0)
            return false;

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>按值找下标；找不到返回 -1（Eto / WinForms 用 SelectedIndex 口径）。</summary>
    public static int IndexOf(IReadOnlyList<string> options, string? value)
    {
        if (value is null)
            return -1;

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

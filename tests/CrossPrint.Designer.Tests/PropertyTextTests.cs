using System.Globalization;
using System.Text.RegularExpressions;
using CrossPrint.Core;
using CrossPrint.I18n;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>
/// 属性面板英文文案的覆盖度门：描述符/分组/类型名/结构动作的中文原文由 Designer 生成，
/// 英文界面靠 <see cref="PropertyText"/> 的对照表翻译。漏一条，英文界面就会冒出一行中文，
/// 而用户无从知道该去哪里改——所以这里对着 <see cref="ElementProperties.For"/> 的
/// <b>真实输出</b>逐条验，不抄产品侧清单。
/// </summary>
public class PropertyTextTests
{
    /// <summary>中文字符、CJK 标点与全角符号；ASCII 括号、° 和 · 不算。</summary>
    static readonly Regex Cjk = new(@"[\u3000-\u303F\u4E00-\u9FFF\uFF00-\uFFEF]", RegexOptions.CultureInvariant);

    [Fact]
    public void 英文界面下八类元素的描述组动作全部有英文()
    {
        using var _ = new UiCultureScope("en-US");

        var failures = new List<string>();
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            var properties = ElementProperties.For(element);

            Check(failures, typeKey, "类型名", typeKey, PropertyText.ElementType(typeKey, ChineseTypeName(typeKey)));

            foreach (var descriptor in properties.Descriptors)
                Check(failures, typeKey, descriptor.Key, descriptor.DisplayName, PropertyText.Label(descriptor.DisplayName));

            foreach (var group in properties.Descriptors.Select(d => d.Group).Distinct(StringComparer.Ordinal))
                Check(failures, typeKey, "group", group, PropertyText.Group(group));

            foreach (var action in properties.Actions)
                Check(failures, typeKey, action.Key, action.DisplayName, PropertyText.Action(action.DisplayName));
        }

        failures.Should().BeEmpty("英文界面里属性面板不应残留中文：{0}", string.Join("; ", failures));
    }

    [Fact]
    public void 中文界面原样返回描述符原文()
    {
        using var _ = new UiCultureScope("zh-CN");

        foreach (var typeKey in ElementTypeKeys.All)
        {
            var properties = ElementProperties.For(NewElement(typeKey));
            foreach (var descriptor in properties.Descriptors)
            {
                PropertyText.Label(descriptor.DisplayName).Should().Be(
                    descriptor.DisplayName, "中文界面必须原样显示（翻译层不许篡改中文原文）");
            }

            foreach (var action in properties.Actions)
                PropertyText.Action(action.DisplayName).Should().Be(action.DisplayName);
        }
    }

    [Fact]
    public void 带序号的标签翻译后序号仍原样保留()
    {
        using var _ = new UiCultureScope("en-US");

        PropertyText.Label("列2·标题").Should().Be("Column 2 · Title");
        PropertyText.Label("表头3·字号 (pt)（留空继承表头）").Should().Be("Header 3 · Font size (pt) (empty = inherit header)");
        PropertyText.Label("条件1·比较值").Should().Be("Condition 1 · Comparison value");
        PropertyText.Label("关键词1·关键词（空=整格）").Should().Be("Highlight 1 · Keyword (empty = whole cell)");
        PropertyText.Label("合并2·跨行数").Should().Be("Merge 2 · Row span");
    }

    [Fact]
    public void 对照表漏词时原样返回中文而不是内部键()
    {
        using var _ = new UiCultureScope("en-US");

        // 宁可露出中文让人发现，也不要把找不到的文案换成 key 之外的乱码或空串。
        PropertyText.Label("某个还没翻译的新字段").Should().Be("某个还没翻译的新字段");
        PropertyText.Action("某个还没翻译的新动作").Should().Be("某个还没翻译的新动作");
        PropertyText.Group("某个还没翻译的新分组").Should().Be("某个还没翻译的新分组");
    }

    static void Check(List<string> failures, string typeKey, string scope, string chinese, string translated)
    {
        if (Cjk.IsMatch(translated))
            failures.Add($"{typeKey}/{scope}: {chinese} → {translated}");
    }

    static string ChineseTypeName(string typeKey) => typeKey switch
    {
        ElementTypeKeys.Text => "文字",
        ElementTypeKeys.Line => "线段",
        ElementTypeKeys.Rectangle => "矩形",
        ElementTypeKeys.Ellipse => "椭圆",
        ElementTypeKeys.Image => "图片",
        ElementTypeKeys.QrCode => "二维码",
        ElementTypeKeys.Barcode => "一维码",
        ElementTypeKeys.Table => "表格",
        _ => typeKey,
    };

    /// <summary>与 ElementPropertiesTests 同构的样本：把表格的列/表头/规则类列表都撑开，才验得到全部标签。</summary>
    static PaperElement NewElement(string typeKey)
    {
        var element = ElementFactory.Create(typeKey);
        element.Id = $"el-{typeKey}";
        if (element is TableElement table)
        {
            table.Table.Columns.Add(new TableColumn { Title = "品名", Field = "items.name" });
            table.Table.Columns.Add(new TableColumn { Title = "数量", Field = "items.qty", FontSizePt = 8 });
            table.Table.Rows.Add(["静态行", "1"]);

            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 0, Title = "信息", ColSpan = 2 });
            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 2, Title = "数量" });

            table.Table.ConditionalStyles.Add(new TableConditionalStyle
            {
                Field = "amount",
                Operator = TableConditionOperator.GreaterThan,
                Value = "50",
                BackgroundColor = new SKColor(217, 217, 217),
            });
            table.Table.Highlights.Add(new TableHighlightRule { Keyword = "缺货", BackgroundColor = new SKColor(255, 235, 156) });
            table.Table.RowSpans.Add(new TableRowSpan { Row = 0, Column = 0, RowSpan = 1, ColSpan = 2 });
        }

        return element;
    }

    sealed class UiCultureScope : IDisposable
    {
        readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCultureScope(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}

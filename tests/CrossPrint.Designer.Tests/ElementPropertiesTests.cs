using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Designer.Tests;

/// <summary>未注册的元素类型，用来验证属性通道对未知类型的降级。</summary>
public sealed class CustomElement : PaperElement
{
    /// <inheritdoc/>
    public override string TypeKey => "custom";

    /// <inheritdoc/>
    public override PaperElement CloneDeep()
    {
        var copy = new CustomElement();
        CopyCommonFrom(this);
        return copy;
    }
}

/// <summary>
/// 属性通道测试（docs/02-architecture.md §4.1）。
/// 重点不是"我列的字段对不对"，而是拦住<b>"模型里有、通道里没有"</b>这种静默漏项：
/// 宿主面板是数据驱动的，通道少一条，界面上就物理上少一个编辑框，用户只能去改 JSON。
/// 所以完备性门从 <see cref="CrossPrint.Core"/> 的模型类 + <see cref="TemplateJson.PayloadOptions"/>
/// 真正吐出的字段名反推期望键，绝不抄产品侧的清单——抄来的清单只会和实现一起漏。
/// </summary>
public class ElementPropertiesTests
{
    static readonly string[] CommonKeys =
    [
        "id", "xMm", "yMm", "widthMm", "heightMm", "zIndex",
        "backgroundColor", "borderColor", "borderWidthMm", "borderDashPatternMm", "borderSides", "rotationDegrees", "opacity",
        "styleId",
        "formatter", "styler",
        "testData",
    ];

    // ---------------- 1. 完备性门 ----------------

    [Fact]
    public void 八类元素的每个可写属性都恰好有一条描述符()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            var expected = ExpectedKeys(element);
            var actual = ElementProperties.For(element).Descriptors.Select(d => d.Key).ToList();

            var missing = expected.Where(k => !actual.Contains(k, StringComparer.Ordinal)).ToList();
            var unexpected = actual.Where(k => !expected.Contains(k, StringComparer.Ordinal)).ToList();
            var duplicated = actual.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            missing.Should().BeEmpty($"类型 {typeKey} 的属性通道漏了这些可写字段，宿主面板将永远编辑不到它们：{Join(missing)}");
            unexpected.Should().BeEmpty($"类型 {typeKey} 的属性通道出现了模型里不存在的键：{Join(unexpected)}");
            duplicated.Should().BeEmpty($"类型 {typeKey} 的属性通道有重复键（Find 会随机命中一条）：{Join(duplicated)}");
        }
    }

    [Fact]
    public void 载荷可写属性的驼峰名就是序列化字段名()
    {
        // 完备性门用"驼峰(CLR 名)"推导期望键，这个前提本身也要验：
        // 谁给载荷属性加了 [JsonPropertyName]/[JsonIgnore]，这里先红，而不是让门悄悄放行。
        foreach (var typeKey in ElementTypeKeys.All)
            AssertFieldNames(PayloadProperty(typeKey).PropertyType);

        AssertFieldNames(typeof(TableColumn));

        static void AssertFieldNames(Type payloadType)
        {
            var camel = SettableProperties(payloadType).Select(JsonName).ToList();
            var written = SerializedFieldNames(payloadType);

            written.Should().BeEquivalentTo(camel,
                $"类型 {payloadType.Name} 的序列化字段名与可写属性的驼峰名必须一致（{payloadType.Name} 写出的 JSON 就是设计器要编辑的东西）");
        }
    }

    [Fact]
    public void 键的最后一段等于模型字段的序列化名()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            foreach (var descriptor in ElementProperties.For(element).Descriptors)
            {
                var last = descriptor.Key.Split('.')[^1];
                var bracket = last.IndexOf('[');
                last = bracket < 0 ? last : last[(bracket + 1)..];
                var property = Locate(element, descriptor.Key).Property;

                last.Should().Be(JsonName(property), $"描述符 {descriptor.Key} 的末段必须就是 JSON 字段名");
            }
        }
    }

    // ---------------- 2. 逐条读写往返 ----------------

    [Fact]
    public void 每条描述符都能写入并读回()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            foreach (var descriptor in ElementProperties.For(element).Descriptors)
            {
                if (descriptor.ReadOnly)
                    continue;

                var value = FreshValue(descriptor, element);
                descriptor.SetValue(element, value);
                var read = descriptor.GetValue(element);

                // 枚举的写入形状是宿主下拉给的成员名，读回来是强类型枚举值——比一下才叫"落到枚举上"。
                var expected = descriptor.Kind == PropertyKind.Enum
                    ? Enum.Parse(Locate(element, descriptor.Key).Property.PropertyType, (string)value!)
                    : value;

                read.Should().Be(expected, $"描述符 {descriptor.Key}（{descriptor.Kind}）写入后应能原样读回");

                // table.rows 是唯一"转码"的描述符：模型里是二维表，通道里是制表符文本，
                // 两者的等价关系由 表格行用换行分行制表符分列 专门验，不在这个通用循环里比对象。
                if (descriptor.Key != "table.rows")
                {
                    var located = Locate(element, descriptor.Key);
                    located.Property.GetValue(located.Owner)
                        .Should().Be(read, $"描述符 {descriptor.Key} 必须真的写进模型，而不是只存在委托的闭包里");
                }
            }
        }
    }

    [Fact]
    public void 可空字段传null即清空且非可空字段传null报错()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            foreach (var descriptor in ElementProperties.For(element).Descriptors)
            {
                var nullable = IsNullableModelProperty(Locate(element, descriptor.Key).Property);
                if (descriptor.ReadOnly)
                {
                    nullable.Should().BeFalse("只读通道只给 id，它是非可空字符串");
                }

                if (nullable)
                {
                    descriptor.SetValue(element, null);
                    descriptor.GetValue(element).Should().BeNull($"描述符 {descriptor.Key} 对应的模型字段可空，null 就是清空");
                }
                else
                {
                    var act = () => descriptor.SetValue(element, null);
                    act.Should().Throw<ArgumentException>().WithMessage($"*{descriptor.Key}*", $"描述符 {descriptor.Key} 对应的模型字段不可空，null 属宿主接线错误必须抛");
                }
            }
        }
    }

    [Fact]
    public void 数值描述符只报取值域不做夹紧()
    {
        // 夹紧是 EditorKernel 的职责：属性通道再夹一次就出现"面板落库的值"和"撤销栈记的值"两套口径。
        // 既然声明了不夹，就得断言不夹——否则 Min/Max 会退化成没人知道的隐形规则。
        var element = NewElement(ElementTypeKeys.Text);
        var props = ElementProperties.For(element);

        var width = props.Find("widthMm")!;
        width.Min.Should().Be(0, "几何下限是编辑器的提示");
        width.SetValue(element, -5);
        element.WidthMm.Should().Be(-5, "范围外的值照样落进模型");

        var fontSize = props.Find("text.fontSizePt")!;
        fontSize.Min.Should().Be(1);
        fontSize.Max.Should().Be(1000);
        fontSize.SetValue(element, 9999);
        element.As<TextElement>().Text.FontSizePt.Should().Be(9999);
    }

    // ---------------- 3. 枚举候选项 ----------------

    [Fact]
    public void 枚举描述符的候选项就是CLR成员名且逐个可写()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            foreach (var descriptor in ElementProperties.For(element).Descriptors.Where(d => d.Kind == PropertyKind.Enum))
            {
                var property = Locate(element, descriptor.Key).Property;
                var enumType = property.PropertyType;

                descriptor.Options.Should().NotBeNull($"枚举描述符 {descriptor.Key} 必须给出候选项，宿主据此填下拉框");
                var options = descriptor.Options!;
                options.Should().Equal(Enum.GetNames(enumType),
                    $"枚举描述符 {descriptor.Key} 的候选项必须与 CLR 成员名逐字一致（JsonStringEnumConverter 写的就是成员名）");

                foreach (var option in options)
                {
                    descriptor.SetValue(element, option);
                    var landed = Enum.Parse(enumType, option);
                    descriptor.GetValue(element).Should().Be(landed, $"候选项 {option} 必须能写进 {descriptor.Key}");
                }
            }
        }
    }

    [Fact]
    public void 条码码制候选项用Ean13而不是EAN13()
    {
        // docs/template-schema.md §4.6 记的坑：写成 EAN13 的模板在加载期就被枚举解析拒绝。
        // 下拉框里出现"看着对但印不出来"的那一项，是这条通道最典型的坏法。
        var element = NewElement(ElementTypeKeys.Barcode);
        var descriptor = ElementProperties.For(element).Find("barcode.symbology")!;

        descriptor.Options!.Should().Contain("Ean13");
        descriptor.Options!.Should().NotContain("EAN13");
        // 候选项必须覆盖模型里全部码制（码制增删时下拉框自动跟上，不抄写死清单）。
        descriptor.Options.Should().Equal(Enum.GetNames<BarcodeSymbology>());

        descriptor.SetValue(element, "Ean13");
        element.As<BarcodeElement>().Barcode.Symbology.Should().Be(BarcodeSymbology.Ean13);

        var act = () => descriptor.SetValue(element, "EAN13");
        act.Should().Throw<ArgumentException>().WithMessage("*EAN13*");
    }

    [Fact]
    public void 边框边描述符接受多选组合串()
    {
        // [Flags] 字段（BorderSide）的多选面板交回的是 "Top, Right" 这样的组合串；
        // 每个分段仍按成员名逐字、区分大小写校验，非法成员必须点名键报错。
        var element = NewElement(ElementTypeKeys.Rectangle);
        var descriptor = ElementProperties.For(element).Find("borderSides")!;

        descriptor.Kind.Should().Be(PropertyKind.Enum);
        descriptor.SetValue(element, "Top, Right");
        element.BorderSides.Should().Be(BorderSide.Top | BorderSide.Right, "组合串要合成为位组合");
        descriptor.GetValue(element).Should().Be(BorderSide.Top | BorderSide.Right);

        var bad = () => descriptor.SetValue(element, "Top, Middle");
        bad.Should().Throw<ArgumentException>().WithMessage("*borderSides*");
    }

    // ---------------- 4. 类型隔离 ----------------

    [Fact]
    public void 属性通道按元素类型隔离()
    {
        var text = ElementProperties.For(NewElement(ElementTypeKeys.Text));
        text.Descriptors.Select(d => d.Key).Should().NotContain("barcode.symbology");
        text.Find("text.fontSizePt").Should().NotBeNull();

        var barcode = ElementProperties.For(NewElement(ElementTypeKeys.Barcode));
        barcode.Find("barcode.symbology").Should().NotBeNull();

        // 更一般的判据：任何类型的描述符都不该出现别的类型前缀的键。
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var foreignPrefixes = ElementTypeKeys.All.Where(k => k != typeKey).Select(k => k + ".").ToArray();
            foreach (var descriptor in ElementProperties.For(NewElement(typeKey)).Descriptors)
            {
                foreignPrefixes.Any(p => descriptor.Key.StartsWith(p, StringComparison.Ordinal))
                    .Should().BeFalse($"类型 {typeKey} 的通道不该出现 {descriptor.Key}");
            }
        }
    }

    [Fact]
    public void 未注册元素类型只给公共字段()
    {
        var props = ElementProperties.For(new CustomElement { Id = "c-1" });
        props.Descriptors.Select(d => d.Key).Should().BeEquivalentTo(CommonKeys);
    }

    // ---------------- 5/6. 键唯一 + 公共几何字段 ----------------

    [Fact]
    public void 描述符键在全清单内唯一()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var keys = ElementProperties.For(NewElement(typeKey)).Descriptors.Select(d => d.Key).ToList();
            keys.Distinct(StringComparer.Ordinal).Count().Should().Be(keys.Count, $"类型 {typeKey} 有重复的描述符键：{Join(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key))}");
        }
    }

    [Fact]
    public void 八类元素都有几何描述符且id只读()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            var props = ElementProperties.For(element);

            foreach (var key in CommonKeys)
                props.Find(key).Should().NotBeNull($"类型 {typeKey} 缺公共字段描述符 {key}");

            props.Find("xMm")!.Kind.Should().Be(PropertyKind.Number);
            props.Find("zIndex")!.Kind.Should().Be(PropertyKind.Integer);
            props.Find("id")!.ReadOnly.Should().BeTrue("id 由内核生成，面板只展示不给编辑");
            props.Find("id")!.GetValue(element).Should().Be(element.Id);

            Action act = () => props.Find("id")!.SetValue(element, "hacked");
            act.Should().Throw<ArgumentException>();

            var geometry = new[] { "xMm", "yMm", "widthMm", "heightMm" };
            foreach (var key in geometry)
                props.Find(key)!.ReadOnly.Should().BeFalse($"几何字段 {key} 必须可编辑");
        }
    }

    [Fact]
    public void 松散的宿主形状都被接受()
    {
        // 宿主表单交回来的类型五花八门：数值框给 int、下拉给 string、色板给 SKColor 或串。
        var element = NewElement(ElementTypeKeys.Text);
        var props = ElementProperties.For(element);

        props.Find("text.fontSizePt")!.SetValue(element, 14);
        element.As<TextElement>().Text.FontSizePt.Should().Be(14);
        props.Find("text.fontSizePt")!.SetValue(element, 14L);
        props.Find("text.fontSizePt")!.SetValue(element, 14f);
        props.Find("text.fontSizePt")!.SetValue(element, "14.5");
        element.As<TextElement>().Text.FontSizePt.Should().Be(14.5);

        props.Find("zIndex")!.SetValue(element, "3");
        element.ZIndex.Should().Be(3);
        var badInt = () => props.Find("zIndex")!.SetValue(element, 2.5);
        badInt.Should().Throw<ArgumentException>();

        props.Find("text.bold")!.SetValue(element, "true");
        element.As<TextElement>().Text.Bold.Should().BeTrue();
        props.Find("text.bold")!.SetValue(element, "false");
        element.As<TextElement>().Text.Bold.Should().BeFalse();

        props.Find("text.color")!.SetValue(element, "#112233");
        element.As<TextElement>().Text.Color.Should().Be(new SKColor(0x11, 0x22, 0x33));
        props.Find("text.color")!.SetValue(element, "#80112233");
        element.As<TextElement>().Text.Color.Should().Be(new SKColor(0x11, 0x22, 0x33, 0x80));

        var line = ElementProperties.For(NewElement(ElementTypeKeys.Line));
        var dashed = NewLine();
        line.Find("line.dashPatternMm")!.SetValue(dashed, "1.5,1");
        dashed.As<LineElement>().Line.DashPatternMm.Should().Equal(1.5, 1);
        line.Find("line.dashPatternMm")!.SetValue(dashed, new double[] { 2, 0.5 });
        dashed.As<LineElement>().Line.DashPatternMm.Should().Equal(2, 0.5);
    }

    [Fact]
    public void 坏输入抛异常且消息点名键与值()
    {
        var element = NewElement(ElementTypeKeys.Text);
        var props = ElementProperties.For(element);

        var act = () => props.Find("text.fontSizePt")!.SetValue(element, "大号");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*text.fontSizePt*")
            .WithMessage("*大号*");

        var badColor = () => props.Find("text.color")!.SetValue(element, "不是颜色");
        badColor.Should().Throw<ArgumentException>().WithMessage("*text.color*");

        var badEnum = () => props.Find("text.horizontalAlign")!.SetValue(element, "middle");
        badEnum.Should().Throw<ArgumentException>().WithMessage("*Center*");

        var badText = () => props.Find("text.fontFamily")!.SetValue(element, 42);
        badText.Should().Throw<ArgumentException>().WithMessage("*text.fontFamily*");
    }

    // ---------------- 表格列：按列展开 ----------------

    [Fact]
    public void 表格列按下标展开为逐列描述符()
    {
        // 本任务的取舍：列是变长子对象，没有单一控件能表达，所以按 table.columns[i].字段 展开；
        // 列的增删属于结构命令（EditorKernel），属性通道只编辑已有列。
        var element = NewElement(ElementTypeKeys.Table);
        var props = ElementProperties.For(element);

        props.Find("table.columns").Should().BeNull("列不是单条描述符，宿主不能用一个框糊住整个数组");
        props.Find("table.rows").Should().NotBeNull("行是纯文本网格，一条多行描述符最顺手");

        props.Find("table.columns[0].title")!.DisplayName.Should().Be("列1·标题");
        props.Find("table.columns[1].fontSizePt")!.Kind.Should().Be(PropertyKind.Number);

        props.Find("table.columns[0].title")!.SetValue(element, "规格");
        element.As<TableElement>().Table.Columns[0].Title.Should().Be("规格");

        var columnCount = props.Descriptors.Count(d => d.Key.StartsWith("table.columns[", StringComparison.Ordinal));
        columnCount.Should().Be(element.As<TableElement>().Table.Columns.Count * 13, "每列 13 个可写字段（含样式引用、同值合并开关、格式化/样式脚本）");
    }

    [Fact]
    public void 列数变化后旧描述符读为空写则报错()
    {
        // 描述符是快照：读不炸面板，写不下哑巴亏（"改了没反应"最难查）。
        var element = NewElement(ElementTypeKeys.Table);
        var stale = ElementProperties.For(element).Find("table.columns[1].title")!;

        element.As<TableElement>().Table.Columns.RemoveAt(1);

        stale.GetValue(element).Should().BeNull();
        var act = () => stale.SetValue(element, "没了这列");
        act.Should().Throw<InvalidOperationException>().WithMessage("*table.columns[1].title*");

        ElementProperties.For(element).Find("table.columns[1].title").Should().BeNull("重新取快照后旧列的键不再出现");
    }

    [Fact]
    public void 表头单元格按下标展开且单元格删除后旧描述符失效()
    {
        // 多行表头与列同构：按下标展开、快照语义一致（读 null、写抛）。
        var element = NewElement(ElementTypeKeys.Table);
        var props = ElementProperties.For(element);
        var title = props.Find("table.headerRows[0].title")!;

        title.Kind.Should().Be(PropertyKind.Text);
        title.Group.Should().Be("表头", "表头键必须归到独立分组，且排在 table. 前缀判断之前");
        title.SetValue(element, "订单信息");
        element.As<TableElement>().Table.HeaderRows[0].Title.Should().Be("订单信息");

        var cellCount = props.Descriptors.Count(d => d.Key.StartsWith("table.headerRows[", StringComparison.Ordinal));
        cellCount.Should().Be(element.As<TableElement>().Table.HeaderRows.Count * 8, "每个表头单元格 8 个可写字段（含样式引用）");

        // 删掉表头行（清空后 0 号键指向的单元格已不存在）：读不炸面板，写不下哑巴亏。
        element.As<TableElement>().Table.HeaderRows.Clear();
        title.GetValue(element).Should().BeNull();
        var act = () => title.SetValue(element, "没了");
        act.Should().Throw<InvalidOperationException>().WithMessage("*table.headerRows[0].title*");

        ElementProperties.For(element).Find("table.headerRows[0].title").Should().BeNull("重新取快照后旧单元格的键不再出现");
    }

    [Fact]
    public void 条件样式按下标展开且条目删除后旧描述符失效()
    {
        // 条件样式与列/表头同构：按下标展开、快照语义一致（读 null、写抛）。
        var element = NewElement(ElementTypeKeys.Table);
        var props = ElementProperties.For(element);
        var field = props.Find("table.conditionalStyles[0].field")!;

        field.Kind.Should().Be(PropertyKind.Text);
        field.DisplayName.Should().Be("条件1·取值字段（空=用格内文本）");
        field.Group.Should().Be("条件样式", "条件样式键必须归到独立分组，且排在 table. 前缀判断之前");
        field.SetValue(element, "qty");
        element.As<TableElement>().Table.ConditionalStyles[0].Field.Should().Be("qty");

        props.Find("table.conditionalStyles[0].operator")!.Kind.Should().Be(PropertyKind.Enum);
        props.Find("table.conditionalStyles[0].value")!.Kind.Should().Be(PropertyKind.Text);
        props.Find("table.conditionalStyles[0].column")!.Kind.Should().Be(PropertyKind.Text);
        props.Find("table.conditionalStyles[0].backgroundColor")!.Kind.Should().Be(PropertyKind.Color);
        props.Find("table.conditionalStyles[0].textColor")!.Kind.Should().Be(PropertyKind.Color);
        props.Find("table.conditionalStyles[0].bold")!.Kind.Should().Be(PropertyKind.Boolean);
        props.Find("table.conditionalStyles[0].italic")!.Kind.Should().Be(PropertyKind.Boolean);
        props.Find("table.conditionalStyles[0].fontSizePt")!.Kind.Should().Be(PropertyKind.Number);
        props.Find("table.conditionalStyles[0].fontFamily")!.Kind.Should().Be(PropertyKind.Text);

        var styleCount = props.Descriptors.Count(d => d.Key.StartsWith("table.conditionalStyles[", StringComparison.Ordinal));
        styleCount.Should().Be(element.As<TableElement>().Table.ConditionalStyles.Count * 11, "每条条件样式 11 个可写字段（含样式引用）");

        // 样式被删后旧描述符：读不炸面板，写不下哑巴亏。
        element.As<TableElement>().Table.ConditionalStyles.Clear();
        field.GetValue(element).Should().BeNull();
        var act = () => field.SetValue(element, "没了");
        act.Should().Throw<InvalidOperationException>().WithMessage("*table.conditionalStyles[0].field*");

        ElementProperties.For(element).Find("table.conditionalStyles[0].field").Should().BeNull("重新取快照后旧样式的键不再出现");
    }

    [Fact]
    public void 关键词样式与静态行合并按下标展开()
    {
        var element = NewElement(ElementTypeKeys.Table);
        var props = ElementProperties.For(element);

        var keyword = props.Find("table.highlights[0].keyword")!;
        keyword.Kind.Should().Be(PropertyKind.Text);
        keyword.Group.Should().Be("关键词样式", "关键词样式键必须归到独立分组");
        keyword.SetValue(element, "缺货");
        element.As<TableElement>().Table.Highlights[0].Keyword.Should().Be("缺货");

        var highlightCount = props.Descriptors.Count(d => d.Key.StartsWith("table.highlights[", StringComparison.Ordinal));
        highlightCount.Should().Be(element.As<TableElement>().Table.Highlights.Count * 10, "每条关键词样式 10 个可写字段（含样式引用）");

        var rowSpan = props.Find("table.rowSpans[0].row")!;
        rowSpan.Kind.Should().Be(PropertyKind.Integer);
        rowSpan.Group.Should().Be("静态行合并", "静态行合并键必须归到独立分组");
        rowSpan.SetValue(element, 2);
        element.As<TableElement>().Table.RowSpans[0].Row.Should().Be(2);

        var spanCount = props.Descriptors.Count(d => d.Key.StartsWith("table.rowSpans[", StringComparison.Ordinal));
        spanCount.Should().Be(element.As<TableElement>().Table.RowSpans.Count * 4, "每条静态行合并 4 个可写字段");
    }

    [Fact]
    public void 表格行用换行分行制表符分列()
    {
        var element = NewElement(ElementTypeKeys.Table);
        var rows = ElementProperties.For(element).Find("table.rows")!;
        rows.Kind.Should().Be(PropertyKind.MultilineText);

        rows.SetValue(element, "甲\t乙\n丙\t丁");
        element.As<TableElement>().Table.Rows.Should().HaveCount(2);
        element.As<TableElement>().Table.Rows[1].Should().Equal("丙", "丁");
        rows.GetValue(element).Should().Be("甲\t乙\n丙\t丁");

        // 空行丢弃：否则清空文本框会留下 [""] 行，渲染出一张空表而不是无表。
        rows.SetValue(element, "甲\n\n\n乙");
        element.As<TableElement>().Table.Rows.Should().HaveCount(2);
        rows.SetValue(element, string.Empty);
        element.As<TableElement>().Table.Rows.Should().BeEmpty();
    }

    // ---------------- 7. 与序列化器不许脱节 ----------------

    [Fact]
    public void 描述符写入经序列化往返后原值仍在()
    {
        foreach (var typeKey in ElementTypeKeys.All)
        {
            var element = NewElement(typeKey);
            var props = ElementProperties.For(element);
            var template = PrintTemplate.CreateDefault();
            template.Add(element);

            var written = new List<KeyValuePair<string, object?>>();
            foreach (var descriptor in props.Descriptors)
            {
                if (descriptor.ReadOnly)
                    continue;

                descriptor.SetValue(element, FreshValue(descriptor, element));
                written.Add(new(descriptor.Key, descriptor.GetValue(element)));
            }

            var reloaded = TemplateJson.Deserialize(TemplateJson.Serialize(template));
            var restored = reloaded.Elements.Single();
            var reloadedProps = ElementProperties.For(restored);

            foreach (var pair in written)
            {
                var descriptor = reloadedProps.Find(pair.Key);
                descriptor.Should().NotBeNull($"字段 {pair.Key} 在往返后仍应有描述符（否则通道与序列化器已经漂移）");
                var actual = descriptor!.GetValue(restored);

                if (pair.Value is double[])
                    actual.Should().BeEquivalentTo((double[])pair.Value, $"字段 {pair.Key} 的虚线段长应无损往返");
                else
                    actual.Should().Be(pair.Value, $"字段 {pair.Key} 经 Serialize/Deserialize 后应回到写入值（模板 JSON 才是唯一真源）");
            }
        }
    }

    // ---------------- 反射基础设施：期望键从模型反推 ----------------

    static PaperElement NewElement(string typeKey)
    {
        var element = ElementFactory.Create(typeKey);
        element.Id = $"el-{typeKey}";
        if (element is TableElement table)
        {
            table.Table.Columns.Add(new TableColumn { Title = "品名", Field = "items.name" });
            table.Table.Columns.Add(new TableColumn { Title = "数量", Field = "items.qty", FontSizePt = 8 });
            table.Table.Rows.Add(["静态行", "1"]);

            // 两行表头（带跨列），让完备性门真的展开 table.headerRows[i].* 这组键；
            // 表头单元格为空的话门与通道"两边都没东西"，会放过漏项。
            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 0, Title = "信息", ColSpan = 2 });
            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 0, Column = 2, Title = "数量" });
            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 1, Column = 0, Title = "品名" });
            table.Table.HeaderRows.Add(new TableHeaderCell { Row = 1, Column = 1, Title = "规格" });

            // 三条规则类条目，让完备性门真的展开 table.conditionalStyles[i].* / table.highlights[i].* /
            // table.rowSpans[i].* 这三组键；与表头同理，列表为空的话门与通道"两边都没东西"，会放过漏项。
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

    static LineElement NewLine() => (LineElement)ElementFactory.Create(ElementTypeKeys.Line);

    static SortedSet<string> ExpectedKeys(PaperElement element)
    {
        // 期望集合完全由模型推出：公共字段 + 载荷可写属性；载荷里的"对象集合"（列）按下标展开。
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in CommonKeys)
            set.Add(key);

        var payload = PayloadProperty(element.GetType());
        var payloadValue = payload.GetValue(element)!;
        var prefix = element.TypeKey + ".";

        foreach (var property in SettableProperties(payload.PropertyType))
        {
            var name = JsonName(property);
            if (!IsObjectCollection(property))
            {
                set.Add(prefix + name);
                continue;
            }

            var list = (IList)property.GetValue(payloadValue)!;
            var innerProperties = SettableProperties(property.PropertyType.GetGenericArguments()[0]);
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var inner in innerProperties)
                    set.Add($"{prefix}{name}[{i}].{JsonName(inner)}");
            }
        }

        return set;
    }

    /// <summary>元素上挂载荷的那个属性（<c>TextOptions</c> / <c>ShapeOptions</c> / ...）。</summary>
    static PropertyInfo PayloadProperty(Type elementType)
        => elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Single(p => p.PropertyType.Name.EndsWith("Options", StringComparison.Ordinal));

    static PropertyInfo PayloadProperty(string typeKey) => PayloadProperty(ElementFactory.Create(typeKey).GetType());

    static PropertyInfo[] SettableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>元素类型上的载荷属性对宿主表单而言是"一路可写属性"，但集合里装 POCO 的要按实例展开。</summary>
    static bool IsObjectCollection(PropertyInfo property)
    {
        if (!property.PropertyType.IsGenericType)
            return false;

        var inner = property.PropertyType.GetGenericArguments()[0];
        return inner.Namespace == "CrossPrint.Core" && SettableProperties(inner).Length > 0;
    }

    /// <summary>取序列化器真正写出的字段名——不靠驼峰规则推断，直接让 Core 序列化一遍读回来。</summary>
    static string[] SerializedFieldNames(Type payloadType)
    {
        var sample = Activator.CreateInstance(payloadType)!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sample, payloadType, TemplateJson.PayloadOptions));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    static string JsonName(PropertyInfo property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    static (object? Owner, PropertyInfo Property) Locate(PaperElement element, string key)
    {
        var segments = key.Split('.');
        if (segments.Length == 1)
        {
            // 公共字段的键就是 JSON 名（"xMm"），CLR 名是 "XMm"，按策略名匹配而不是直接查。
            var common = typeof(PaperElement).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(p => JsonName(p) == segments[0])
                ?? throw new InvalidOperationException($"公共字段 {key} 在 PaperElement 上不存在");
            return (element, common);
        }

        object? current = PayloadProperty(element.GetType()).GetValue(element);
        object? owner = current;
        PropertyInfo? property = null;

        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];

            owner = current;
            property = current!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(p => JsonName(p) == name)
                ?? throw new InvalidOperationException($"类型 {current.GetType().Name} 上没有字段 {name}（描述符 {key} 与模型对不上）");

            var value = property.GetValue(current);
            current = bracket < 0
                ? value
                : ((IList)value!)[int.Parse(segment[(bracket + 1)..^1], CultureInfo.InvariantCulture)];
        }

        return (owner!, property!);
    }

    static bool IsNullableModelProperty(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            return true;
        if (property.PropertyType.IsValueType)
            return false;

        return new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;
    }

    /// <summary>按描述符种类造一个"与当前值不同"的新值，保证往返断言不是原地写了个同值糊过去。</summary>
    static object? FreshValue(PropertyDescriptor descriptor, PaperElement element)
    {
        var current = descriptor.GetValue(element);
        return descriptor.Kind switch
        {
            PropertyKind.Number => 12.5,
            PropertyKind.Integer => 7,
            PropertyKind.Boolean => !Equals(current, true),
            PropertyKind.Text => current is "样例文本" ? "另一个文本" : "样例文本",
            PropertyKind.MultilineText => "第一行\n第二行",
            PropertyKind.Color => Equals(current, SKColors.Magenta) ? SKColors.Cyan : SKColors.Magenta,
            PropertyKind.DashPattern => new double[] { 1.5, 1 },
            PropertyKind.Enum => descriptor.Options!.First(o => !Equals(current?.ToString(), o)),
            PropertyKind.StyleRef => Equals(current, "st-sample") ? "st-other" : "st-sample",
            _ => throw new NotSupportedException($"未覆盖的描述符种类 {descriptor.Kind}"),
        };
    }

    static string Join(IEnumerable<string> values) => string.Join(", ", values);
}

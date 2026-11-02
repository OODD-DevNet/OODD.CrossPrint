using CrossPrint.Core;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>模板级 JSON 读写：信封字段、别名兼容、元素集合完整性。</summary>
public class TemplateJsonTests
{
    static PrintTemplate BuildKitchenSink()
    {
        var t = new PrintTemplate
        {
            Paper = new PaperSpec { Preset = PaperTypePresets.A4, WidthMm = 210, HeightMm = 297, Orientation = PaperOrientation.Landscape },
            Margins = new ThicknessMm(11, 12, 13, 14),
        };

        t.Add(new TextElement
        {
            XMm = 20.5,
            YMm = 30.25,
            WidthMm = 80,
            HeightMm = 10,
            ZIndex = 3,
            Text = new TextOptions
            {
                Content = "订单 {{order.no}}",
                FontFamily = "SimHei",
                FontSizePt = 10.5,
                Bold = true,
                Italic = true,
                Color = new SKColor(0x11, 0x22, 0x33),
                HorizontalAlign = HorizontalAlign.Center,
                VerticalAlign = VerticalAlign.Middle,
                Wrap = false,
                LineSpacing = 1.5,
            },
        });

        t.Add(new LineElement
        {
            XMm = 5, YMm = 50, WidthMm = 100, HeightMm = 0,
            Line = new LineOptions { Direction = LineDirection.Vertical, StrokeWidthMm = 0.35, DashPatternMm = [1, 0.5, 0.2, 0.5] },
        });

        t.Add(new RectangleElement
        {
            XMm = 10, YMm = 60, WidthMm = 40, HeightMm = 20,
            Rectangle = new ShapeOptions { CornerRadiusMm = 2, FillColor = new SKColor(0xEE, 0xEE, 0xEE), DashPatternMm = [2, 1] },
        });

        t.Add(new EllipseElement
        {
            XMm = 60, YMm = 60, WidthMm = 20, HeightMm = 20,
            Ellipse = new ShapeOptions { FillColor = null, StrokeColor = SKColors.Red, StrokeWidthMm = 0.5 },
        });

        t.Add(new ImageElement
        {
            XMm = 5, YMm = 90, WidthMm = 30, HeightMm = 30,
            Image = new ImageOptions { Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", FitMode = ImageFitMode.Cover },
        });

        t.Add(new QrCodeElement
        {
            XMm = 40, YMm = 90, WidthMm = 25, HeightMm = 25,
            QrCode = new QrCodeOptions { Content = "https://qoder.com", ErrorCorrection = QrErrorCorrection.H, QuietZoneMm = 2, BackgroundColor = null },
        });

        t.Add(new BarcodeElement
        {
            XMm = 70, YMm = 90, WidthMm = 40, HeightMm = 15,
            Barcode = new BarcodeOptions { Content = "ABC-1234", Symbology = BarcodeSymbology.Code39, ShowText = false, FontSizePt = 6.5 },
        });

        t.Add(new TableElement
        {
            XMm = 5, YMm = 120, WidthMm = 200, HeightMm = 60,
            Table = new TableOptions
            {
                Columns = [new TableColumn { Title = "品名", WidthMm = 60, Field = "items.name", Align = HorizontalAlign.Right, FontSizePt = 8 }, new TableColumn { Title = "数量", WidthMm = -1, Field = "items.qty" }],
                Rows = [new List<string> { "螺丝", "10" }, new List<string> { "垫片", "20" }],
                StripeColor = new SKColor(250, 250, 250),
                RowHeightMm = 6,
                CellPaddingMm = 1.5,
                GridWidthMm = 0.2,
                HeaderBold = false,
            },
        });

        return t;
    }

    [Fact]
    public void Kitchen_sink_template_round_trips_field_for_field()
    {
        var json = TemplateJson.Serialize(BuildKitchenSink());
        var back = TemplateJson.Deserialize(json);

        back.SchemaVersion.Should().Be(TemplateSchema.CurrentVersion);
        back.Paper.Preset.Should().Be(PaperTypePresets.A4);
        back.Paper.WidthMm.Should().Be(210);
        back.Paper.HeightMm.Should().Be(297);
        back.Paper.Orientation.Should().Be(PaperOrientation.Landscape);
        back.Margins.Top.Should().Be(11);
        back.Margins.Right.Should().Be(12);
        back.Margins.Bottom.Should().Be(13);
        back.Margins.Left.Should().Be(14);
        back.Elements.Should().HaveCount(8);

        var text = back.Elements[0].Should().BeOfType<TextElement>().Subject;
        text.Id.Should().Be("el-1");
        text.XMm.Should().Be(20.5);
        text.YMm.Should().Be(30.25);
        text.WidthMm.Should().Be(80);
        text.HeightMm.Should().Be(10);
        text.ZIndex.Should().Be(3);
        text.Text.Content.Should().Be("订单 {{order.no}}");
        text.Text.FontFamily.Should().Be("SimHei");
        text.Text.FontSizePt.Should().Be(10.5);
        text.Text.Bold.Should().BeTrue();
        text.Text.Italic.Should().BeTrue();
        text.Text.Color.Should().Be(new SKColor(0x11, 0x22, 0x33));
        text.Text.HorizontalAlign.Should().Be(HorizontalAlign.Center);
        text.Text.VerticalAlign.Should().Be(VerticalAlign.Middle);
        text.Text.Wrap.Should().BeFalse();
        text.Text.LineSpacing.Should().Be(1.5);

        var line = back.Elements[1].Should().BeOfType<LineElement>().Subject;
        line.TypeKey.Should().Be(ElementTypeKeys.Line);
        line.Line.Direction.Should().Be(LineDirection.Vertical);
        line.Line.DashPatternMm.Should().Equal(1, 0.5, 0.2, 0.5);

        var rect = back.Elements[2].Should().BeOfType<RectangleElement>().Subject;
        rect.Rectangle.CornerRadiusMm.Should().Be(2);
        rect.Rectangle.FillColor.Should().Be(new SKColor(0xEE, 0xEE, 0xEE));

        var ellipse = back.Elements[3].Should().BeOfType<EllipseElement>().Subject;
        ellipse.Ellipse.FillColor.Should().BeNull();
        ellipse.Ellipse.StrokeColor.Should().Be(SKColors.Red);

        var image = back.Elements[4].Should().BeOfType<ImageElement>().Subject;
        image.Image.FitMode.Should().Be(ImageFitMode.Cover);
        image.Image.Data.Should().StartWith("iVBOR");

        var qr = back.Elements[5].Should().BeOfType<QrCodeElement>().Subject;
        qr.QrCode.ErrorCorrection.Should().Be(QrErrorCorrection.H);
        qr.QrCode.QuietZoneMm.Should().Be(2);
        qr.QrCode.BackgroundColor.Should().BeNull();

        var barcode = back.Elements[6].Should().BeOfType<BarcodeElement>().Subject;
        barcode.Barcode.Symbology.Should().Be(BarcodeSymbology.Code39);
        barcode.Barcode.ShowText.Should().BeFalse();
        barcode.Barcode.FontSizePt.Should().Be(6.5);

        var table = back.Elements[7].Should().BeOfType<TableElement>().Subject;
        table.Table.Columns.Should().HaveCount(2);
        table.Table.Columns[0].Field.Should().Be("items.name");
        table.Table.Columns[0].Align.Should().Be(HorizontalAlign.Right);
        table.Table.Columns[0].FontSizePt.Should().Be(8);
        table.Table.Columns[1].WidthMm.Should().Be(-1);
        table.Table.Rows.Should().HaveCount(2);
        table.Table.Rows[1][1].Should().Be("20");
        table.Table.StripeColor.Should().Be(new SKColor(250, 250, 250));
        table.Table.RowHeightMm.Should().Be(6);
        table.Table.HeaderBold.Should().BeFalse();
    }

    [Fact]
    public void Serialization_is_byte_stable()
    {
        var first = TemplateJson.Serialize(BuildKitchenSink());
        var second = TemplateJson.Serialize(TemplateJson.Deserialize(first));
        second.Should().Be(first, "序列化必须稳定，否则模板 diff 无法评审");
    }

    [Fact]
    public void Default_serialization_is_indented()
    {
        // 六宿主的底部 JSON 面板用默认重载展示模板，缩进是"人能直接改"的前提。
        var json = TemplateJson.Serialize(BuildKitchenSink());
        json.Should().Contain("\n  \"schemaVersion\": \"0.1\"");
        json.Replace("\r\n", "\n").Split('\n').Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void All_eight_type_keys_appear_in_json()
    {
        var compact = TemplateJson.Serialize(BuildKitchenSink(), indented: false);
        foreach (var key in ElementTypeKeys.All)
            compact.Should().Contain("\"type\":\"" + key + "\"");

        // 载荷子对象必须按类型键分组
        compact.Should().Contain("\"qrcode\":");
        compact.Should().Contain("\"barcode\":");
    }

    [Fact]
    public void Prd_style_page_and_marginmm_aliases_are_accepted()
    {
        const string json = """
        {
          "schemaVersion": "0.1",
          "page": { "widthMm": 210, "heightMm": 297, "orientation": "Portrait" },
          "marginMm": { "top": 9, "right": 8, "bottom": 7, "left": 6 },
          "elements": [
            { "id": "el-1", "type": "text", "xMm": 20, "yMm": 30, "widthMm": 80, "heightMm": 10, "zIndex": 0,
              "text": { "content": "示例文本", "fontSizePt": 14 } }
          ]
        }
        """;

        var t = TemplateJson.Deserialize(json);
        t.Paper.WidthMm.Should().Be(210);
        t.Margins.Top.Should().Be(9);
        t.Margins.Left.Should().Be(6);
        t.Elements.Should().ContainSingle();
        ((TextElement)t.Elements[0]).Text.Content.Should().Be("示例文本");
        ((TextElement)t.Elements[0]).Text.FontSizePt.Should().Be(14);
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        const string json = """
        {
          "schemaVersion": "0.1",
          "paper": { "widthMm": 210, "heightMm": 297, "futureThing": { "a": 1 } },
          "elements": [
            { "id": "el-1", "type": "text", "xMm": 1, "yMm": 2, "widthMm": 3, "heightMm": 4, "addedInV2": true,
              "text": { "content": "x", "brandNewProp": "y" } }
          ]
        }
        """;

        var load = TemplateJson.Load(json);
        load.Template.Elements.Should().ContainSingle();
        ((TextElement)load.Template.Elements[0]).Text.Content.Should().Be("x");
    }

    [Fact]
    public void Missing_payload_object_uses_defaults()
    {
        const string json = """
        { "schemaVersion": "0.1", "paper": { "widthMm": 100, "heightMm": 100 },
          "elements": [ { "id": "el-1", "type": "rectangle", "xMm": 0, "yMm": 0, "widthMm": 10, "heightMm": 10 } ] }
        """;

        var rect = TemplateJson.Deserialize(json).Elements.Should().ContainSingle().Subject.Should().BeOfType<RectangleElement>().Subject;
        rect.Rectangle.StrokeWidthMm.Should().Be(0.2);
        rect.Rectangle.StrokeColor.Should().Be(SKColors.Black);
    }

    [Fact]
    public void Unknown_enum_value_is_rejected_not_silently_defaulted()
    {
        // docs/template-schema.md §4.6 承诺"未实现的码制会在加载期被拒"。这条把承诺钉死：
        // 未知枚举绝不能安静地落成默认值——现场会印出一条谁也扫不出来的 Code128。
        // PDF417 是 ZXing.Net 里确实存在、但我们没有建模的码制，拿来当反例正好。
        const string json = """
        { "schemaVersion": "0.1", "paper": { "widthMm": 100, "heightMm": 60 },
          "elements": [ { "id": "b1", "type": "barcode", "xMm": 5, "yMm": 5, "widthMm": 60, "heightMm": 20,
                          "barcode": { "symbology": "PDF417" } } ] }
        """;

        var act = () => TemplateJson.Deserialize(json);
        act.Should().Throw<TemplateValidationException>().WithMessage("*b1*");

        var tolerant = TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant);
        tolerant.Template.Elements.Should().BeEmpty("坏枚举的元素要整条跳过，不能留一个默认成 Code128 的在页上");
        tolerant.Warnings.Should().ContainSingle()
            .Which.Should().Contain("b1").And.Contain("symbology", "警告要能指回是哪个元素的哪个字段，否则两百个元素的模板没法定位");
    }

    [Fact]
    public void Empty_template_round_trips()
    {
        var t = PrintTemplate.CreateDefault();
        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));
        back.Elements.Should().BeEmpty();
        back.Paper.PageWidthMm.Should().Be(210);
    }

    [Fact]
    public void Design_data_round_trips_and_only_keeps_scalar_values()
    {
        const string json = """
        {
          "schemaVersion": "0.1",
          "paper": { "preset": "A4", "widthMm": 210, "heightMm": 297, "orientation": "Portrait" },
          "designData": {
            "barcode": "8801234567890",
            "qty": 2,
            "ok": true,
            "nested": { "a": 1 },
            "none": null
          },
          "elements": []
        }
        """;

        var t = TemplateJson.Deserialize(json);
        t.DesignData.Should().HaveCount(3, "对象/数组/null 不是可绑定的标量预览值，读取时跳过而不是报错");
        t.DesignData["barcode"].Should().Be("8801234567890");
        t.DesignData["qty"].Should().Be("2");
        t.DesignData["ok"].Should().Be("true");
        t.DesignData.Should().NotContainKeys("nested", "none");

        var text = TemplateJson.Serialize(t);
        text.Should().Contain("\"designData\"", "设计数据不写回 JSON，宿主的底部面板一改就丢");
        var back = TemplateJson.Deserialize(text);
        back.DesignData.Should().BeEquivalentTo(t.DesignData);
    }

    [Fact]
    public void Clone_copies_design_data_independently()
    {
        var t = PrintTemplate.CreateDefault();
        t.DesignData["barcode"] = "8801";

        var copy = t.Clone();
        copy.DesignData["barcode"] = "8802";

        t.DesignData["barcode"].Should().Be("8801", "设计数据必须按值拷贝，否则改副本会污染原模板");
    }

    [Fact]
    public void Stream_api_matches_text_api()
    {
        using var ms = new MemoryStream();
        TemplateJson.WriteTo(BuildKitchenSink(), ms);
        ms.Position = 0;
        var fromStream = TemplateJson.Load(ms).Template;
        fromStream.Elements.Should().HaveCount(8);
    }

    [Fact]
    public void Ids_are_assigned_incrementally_and_not_reused_after_delete()
    {
        var t = PrintTemplate.CreateDefault();
        t.Add(new TextElement { WidthMm = 10, HeightMm = 5 }).Id.Should().Be("el-1");
        t.Add(new LineElement { WidthMm = 10 }).Id.Should().Be("el-2");
        t.Remove(t.Elements[0]);
        t.Add(new RectangleElement { WidthMm = 10, HeightMm = 10 }).Id.Should().Be("el-3");
    }

    [Fact]
    public void Duplicate_id_is_rejected_on_add()
    {
        var t = PrintTemplate.CreateDefault();
        t.Add(new TextElement { Id = "x1", WidthMm = 1, HeightMm = 1 });
        var dup = new LineElement { Id = "x1", WidthMm = 1 };
        var act = () => t.Add(dup);
        act.Should().Throw<ArgumentException>().WithMessage("*x1*");
    }

    [Fact]
    public void Element_lookup_finds_and_misses()
    {
        var t = BuildKitchenSink();
        t.FindElement("el-3").Should().BeSameAs(t.Elements[2]);
        t.FindElement("nope").Should().BeNull();
    }

    [Fact]
    public void Clone_deep_is_independent_of_source()
    {
        var t = BuildKitchenSink();
        var copy = t.Clone();
        copy.Elements[0].Should().NotBeSameAs(t.Elements[0]);
        ((TextElement)copy.Elements[0]).Text.Content = "changed";
        ((TextElement)t.Elements[0]).Text.Content.Should().Be("订单 {{order.no}}");

        ((TableElement)copy.Elements[7]).Table.Columns[0].Title = "改";
        ((TableElement)t.Elements[7]).Table.Columns[0].Title.Should().Be("品名");
    }

    [Fact]
    public void Test_data_round_trips_and_stays_absent_when_not_set()
    {
        var t = new PrintTemplate();
        t.Add(new TextElement { XMm = 10, YMm = 10, WidthMm = 40, HeightMm = 8, TestData = "测试单号" });
        t.Add(new TextElement { XMm = 10, YMm = 30, WidthMm = 40, HeightMm = 8 });

        var back = TemplateJson.Deserialize(TemplateJson.Serialize(t));

        back.Elements[0].TestData.Should().Be("测试单号");
        back.Elements[1].TestData.Should().BeNull("没写 testData 的元素不该被反序列化成空串");
    }

    // ---------------- id 的读写契约 ----------------

    [Fact]
    public void 空id元素序列化补齐id并且往返幂等()
    {
        var t = PrintTemplate.CreateDefault();
        // 绕开 Add 直接塞集合：迁移脚本、手改 JSON、克隆没带 id 都可能留下空 id。
        t.Elements.Add(new TextElement { Id = "", XMm = 1, YMm = 2, WidthMm = 10, HeightMm = 5 });
        t.Elements.Add(new TextElement { Id = "keep", XMm = 1, YMm = 20, WidthMm = 10, HeightMm = 5 });

        var first = TemplateJson.Serialize(t, indented: false);
        first.Should().NotContain("\"id\":\"\"", "写出空 id 等于写出一份严格模式读不回来的非法文档（实测改前就是这样）");

        var back = TemplateJson.Deserialize(first);
        back.Elements.Should().HaveCount(2);
        // 补 id 的规则要和 Add 一致：el-N 递增，已有的 id 不动。
        back.Elements.Select(e => e.Id).Should().Equal("el-1", "keep");
        TemplateJson.Serialize(back, indented: false).Should().Be(first, "序列化→反序列化→序列化必须幂等");

        t.Elements[0].Id.Should().Be("", "序列化不能顺手改用户的对象图，撤销栈还持有原对象");
    }

    [Fact]
    public void 重复id元素序列化时按Add的规则重新分配()
    {
        var t = PrintTemplate.CreateDefault();
        t.Elements.Add(new TextElement { Id = "d", XMm = 1, YMm = 2, WidthMm = 10, HeightMm = 5 });
        t.Elements.Add(new TextElement { Id = "d", XMm = 1, YMm = 20, WidthMm = 10, HeightMm = 5 });

        var first = TemplateJson.Serialize(t, indented: false);
        var back = TemplateJson.Deserialize(first);

        // 首个 id 保留，后来的重复项重新分配。
        back.Elements.Select(e => e.Id).Should().Equal("d", "el-1");
        TemplateJson.Serialize(back, indented: false).Should().Be(first);
    }

    [Fact]
    public void 空id与重复id的样式序列化补齐id并且往返幂等()
    {
        var t = PrintTemplate.CreateDefault();
        t.Styles.Add(new NamedStyle { Id = "", Name = "甲" });
        t.Styles.Add(new NamedStyle { Id = "", Name = "乙" });
        t.Styles.Add(new NamedStyle { Id = "brand", Name = "丙" });
        t.Styles.Add(new NamedStyle { Id = "brand", Name = "丁" });

        var first = TemplateJson.Serialize(t, indented: false);
        first.Should().NotContain("\"id\":\"\"", "空样式 id 会让引用它的元素永远匹配不上");

        var back = TemplateJson.Deserialize(first);
        back.Styles.Should().HaveCount(4);
        // 补样式 id 走 NextStyleId 的同一条规则。
        back.Styles.Select(s => s.Id).Should().Equal("st-1", "st-2", "brand", "st-3");
        back.Styles.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        TemplateJson.Serialize(back, indented: false).Should().Be(first);
    }

    [Fact]
    public void 宽容加载补id和改id时记录警告()
    {
        // docs/template-schema.md §7 承诺"缺失 → 自动补 el-N + 警告""重复 → 重新分配 + 警告"，
        // 实测改前一条警告都没记，用户根本不知道自己的元素被改了 id。
        const string json = """
        { "schemaVersion": "0.1", "paper": { "widthMm": 100, "heightMm": 100 },
          "elements": [
            { "type": "text", "xMm": 1, "yMm": 2, "widthMm": 10, "heightMm": 5 },
            { "id": "", "type": "text", "xMm": 1, "yMm": 8, "widthMm": 10, "heightMm": 5 },
            { "id": "dup", "type": "text", "xMm": 1, "yMm": 14, "widthMm": 10, "heightMm": 5 },
            { "id": "dup", "type": "text", "xMm": 1, "yMm": 20, "widthMm": 10, "heightMm": 5 }
          ] }
        """;

        var load = TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant);

        load.Template.Elements.Select(e => e.Id).Should().Equal("el-1", "el-2", "dup", "el-3");
        load.Warnings.Should().HaveCount(3, "三个被补/改的 id 要各留一条警告");
        load.Warnings.Should().OnlyContain(w => w.Contains("id", StringComparison.Ordinal));
        load.Warnings[2].Should().Contain("dup", "重新分配的警告要带上原来那个重复的 id，用户才找得到是哪一条");

        // 严格模式仍然拒绝，这条是既有契约（docs §7 第一列）。
        var act = () => TemplateJson.Deserialize(json);
        act.Should().Throw<TemplateValidationException>().WithMessage("*id*");
    }

    [Fact]
    public void 严格读回的模板再序列化读回完全一致()
    {
        var source = TemplateJson.Serialize(BuildKitchenSink(), indented: false);
        var again = TemplateJson.Serialize(TemplateJson.Deserialize(source), indented: false);

        again.Should().Be(source);
        // 补齐后的 id 不会让元素计数或顺序发生变化。
        TemplateJson.Deserialize(again).Elements.Select(e => e.Id).Should().Equal(
            Enumerable.Range(1, 8).Select(i => $"el-{i}"));
    }

    [Fact]
    public void Legacy_rowFillRules_are_migrated_to_conditional_styles()
    {
        // 旧规则只是新条件样式的特例（大于 + 整行底色）：加载时自动迁移，语义不变。
        const string legacy = """
        {
          "schemaVersion": "0.1",
          "paper": { "widthMm": 100, "heightMm": 60 },
          "elements": [
            { "id": "t1", "type": "table", "xMm": 5, "yMm": 5, "widthMm": 90, "heightMm": 40,
              "table": {
                "columns": [ { "title": "金额", "widthMm": 90 } ],
                "rows": [ [ "1" ] ],
                "rowFillRules": [ { "field": "amount", "min": 50, "color": "#D9D9D9" } ]
              } }
          ]
        }
        """;

        var template = TemplateJson.Deserialize(legacy);
        var table = template.Elements.Should().ContainSingle().Subject.Should().BeOfType<TableElement>().Subject;

        var style = table.Table.ConditionalStyles.Should().ContainSingle("旧规则迁移成一条条件样式").Subject;
        style.Field.Should().Be("amount");
        style.Operator.Should().Be(TableConditionOperator.GreaterThan, "旧规则就是'大于'语义");
        style.Value.Should().Be("50", "阈值转为字符串比较值（不变文化）");
        style.BackgroundColor.Should().Be(new SKColor(0xD9, 0xD9, 0xD9));

        // 迁移后的模板再序列化不再出现旧字段。
        var json = TemplateJson.Serialize(template);
        json.Should().Contain("\"conditionalStyles\"");
        json.Should().NotContain("rowFillRules");
    }
}

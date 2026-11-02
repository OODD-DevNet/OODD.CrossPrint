using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Core.Tests;

/// <summary>非法模板必须给出可定位的异常，或在宽容模式下降级为警告。</summary>
public class TemplateValidationTests
{
    const string Head = """{ "schemaVersion": "0.1", "paper": { "widthMm": 210, "heightMm": 297 },""";

    static string WithElements(string elementsBody) => Head + " \"elements\": [" + elementsBody + "] }";

    static Action Act(string json) => () => TemplateJson.Load(json);

    [Fact]
    public void Non_object_root_is_rejected()
        => Act("{").Should().Throw<Exception>();

    [Fact]
    public void Missing_schemaVersion_is_rejected()
        => Act("""{ "paper": { "widthMm": 210, "heightMm": 297 } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*schemaVersion*");

    [Fact]
    public void Minor_schema_newer_than_this_build_is_rejected()
        => Act("""{ "schemaVersion": "0.2", "paper": { "widthMm": 210, "heightMm": 297 } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*高于*");

    [Fact]
    public void Major_schema_newer_than_this_build_is_rejected()
        => Act("""{ "schemaVersion": "1.0", "paper": { "widthMm": 210, "heightMm": 297 } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*不兼容*");

    [Theory]
    [InlineData("0.1.0")]
    [InlineData(" 0.1 ")]
    [InlineData("0.0")]
    public void Schema_version_variants_within_support_are_accepted(string version)
    {
        var json = """{ "schemaVersion": "__V__", "paper": { "widthMm": 210, "heightMm": 297 } }""".Replace("__V__", version);
        var t = TemplateJson.Deserialize(json);
        t.SchemaVersion.Should().Be(version.Trim());
    }

    [Fact]
    public void Missing_paper_is_rejected()
        => Act("""{ "schemaVersion": "0.1" }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*paper*");

    [Fact]
    public void Non_positive_paper_width_is_rejected()
        => Act("""{ "schemaVersion": "0.1", "paper": { "widthMm": 0, "heightMm": 297 } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*widthMm*");

    [Fact]
    public void Unknown_paper_preset_is_rejected_with_available_list()
        => Act("""{ "schemaVersion": "0.1", "paper": { "preset": "A99", "widthMm": 210, "heightMm": 297 } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*A99*")
            .Which.FieldName.Should().Be("paper.preset");

    [Fact]
    public void Custom_preset_with_explicit_size_is_allowed()
    {
        var t = TemplateJson.Deserialize("""{ "schemaVersion": "0.1", "paper": { "preset": "Custom", "widthMm": 123, "heightMm": 45 } }""");
        t.Paper.Preset.Should().Be(PaperTypePresets.Custom);
        t.Paper.PageWidthMm.Should().Be(123);
    }

    [Fact]
    public void Invalid_orientation_value_is_rejected()
        => Act("""{ "schemaVersion": "0.1", "paper": { "widthMm": 210, "heightMm": 297, "orientation": "Sideways" } }""")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*orientation*");

    [Fact]
    public void Elements_must_be_array()
        => Act(Head + " \"elements\": {} }")
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*elements*");

    [Fact]
    public void Element_must_be_object()
        => Act(WithElements("\"just-a-string\""))
            .Should().Throw<TemplateValidationException>();

    [Fact]
    public void Element_without_type_is_rejected()
        => Act(WithElements("""{ "id": "el-1", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }"""))
            .Should().Throw<TemplateValidationException>()
            .Which.FieldName.Should().Be("type");

    [Fact]
    public void Unknown_element_type_names_the_type()
        => Act(WithElements("""{ "id": "el-9", "type": "sparkline", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }"""))
            .Should().Throw<UnknownElementTypeException>()
            .Which.TypeKey.Should().Be("sparkline");

    [Fact]
    public void Unknown_element_type_can_be_skipped_in_tolerant_mode()
    {
        var json = WithElements(
            """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 10, "heightMm": 5 },""" +
            """{ "id": "el-2", "type": "sparkline", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }""");

        var load = TemplateJson.Load(json, TemplateJsonLoadOptions.Tolerant);
        load.Template.Elements.Should().ContainSingle();
        load.Warnings.Should().ContainSingle().Which.Should().Contain("sparkline");
    }

    [Theory]
    [InlineData("xMm", """{ "id": "el-1", "type": "text", "yMm": 0, "widthMm": 10, "heightMm": 5 }""")]
    [InlineData("yMm", """{ "id": "el-1", "type": "text", "xMm": 0, "widthMm": 10, "heightMm": 5 }""")]
    [InlineData("widthMm", """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "heightMm": 5 }""")]
    [InlineData("heightMm", """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 10 }""")]
    public void Missing_geometry_is_rejected_naming_the_field(string field, string element)
    {
        var act = Act(WithElements(element));
        act.Should().Throw<TemplateValidationException>().Which.ElementId.Should().Be("el-1");
        act.Should().Throw<TemplateValidationException>().Which.FieldName.Should().Be(field);
    }

    [Theory]
    [InlineData("xMm", """{ "id": "el-1", "type": "text", "yMm": 0, "widthMm": 10, "heightMm": 5 }""")]
    [InlineData("widthMm", """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "heightMm": 5 }""")]
    public void 宽容模式缺几何字段保住元素并点名留痕(string field, string element)
    {
        // 实测改前：宽容模式把缺失的几何字段静默写成 0mm，一条警告都不记。
        var load = TemplateJson.Load(WithElements(element), TemplateJsonLoadOptions.Tolerant);

        var kept = load.Template.Elements.Should().ContainSingle("宽容模式要保住元素内容，不能整条丢掉").Subject;
        if (field == "widthMm")
            kept.WidthMm.Should().Be(0, "兜底口径不变，改的只是留痕");
        else
            kept.XMm.Should().Be(0);

        load.Warnings.Should().ContainSingle("零尺寸元素在页面上既看不见也点不中，必须告诉用户是哪个字段兜的底")
            .Which.Should().Contain("el-1").And.Contain(field);
    }

    [Fact]
    public void Geometry_as_string_is_rejected()
        => Act(WithElements("""{ "id": "el-1", "type": "text", "xMm": "20", "yMm": 0, "widthMm": 10, "heightMm": 5 }"""))
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*xMm*");

    [Fact]
    public void Negative_geometry_is_rejected()
        => Act(WithElements("""{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "widthMm": -5, "heightMm": 5 }"""))
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*不能为负*");

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var one = """{ "id": "same", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }""";
        Act(WithElements(one + "," + one))
            .Should().Throw<TemplateValidationException>()
            .WithMessage("*重复*");
    }

    [Fact]
    public void Missing_id_is_rejected_in_strict_mode()
        => Act(WithElements("""{ "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }"""))
            .Should().Throw<TemplateValidationException>()
            .Which.FieldName.Should().Be("id");

    [Fact]
    public void Missing_id_is_generated_in_tolerant_mode()
    {
        var load = TemplateJson.Load(WithElements("""{ "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1 }"""),
            TemplateJsonLoadOptions.Tolerant);
        load.Template.Elements[0].Id.Should().StartWith("el-");
        // docs/template-schema.md §7 承诺"缺失 → 自动补 el-N + 警告"：改了用户的 id 就必须留痕。
        load.Warnings.Should().ContainSingle()
            .Which.Should().Contain("id").And.Contain(load.Template.Elements[0].Id);
    }

    [Fact]
    public void Malformed_color_reports_the_element_and_field()
        => Act(WithElements(
            """{ "id": "el-7", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1,""" +
            """ "text": { "content": "x", "color": "#ZZZZZZ" } }"""))
            .Should().Throw<TemplateValidationException>()
            .Which.ElementId.Should().Be("el-7");

    [Fact]
    public void Color_of_wrong_length_is_rejected()
        => Act(WithElements(
            """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1,""" +
            """ "text": { "color": "#FFF" } }"""))
            .Should().Throw<TemplateValidationException>();

    [Fact]
    public void Eight_digit_color_with_alpha_is_accepted()
    {
        var e = TemplateJson.Deserialize(WithElements(
            """{ "id": "el-1", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 1, "heightMm": 1,""" +
            """ "text": { "color": "#80FF0000" } }""")).Elements[0];
        ((TextElement)e).Text.Color.Alpha.Should().Be(0x80);
        ((TextElement)e).Text.Color.Red.Should().Be(0xFF);
    }

    [Fact]
    public void Broken_json_syntax_throws_json_exception()
        => Act("""{ "schemaVersion": """).Should().Throw<System.Text.Json.JsonException>();

    [Fact]
    public void Null_json_throws_argument_null()
    {
        Action act = () => TemplateJson.Load((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Null_stream_throws_argument_null()
    {
        Action act = () => TemplateJson.Load((Stream)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Unknown_element_type_factory_create_throws()
    {
        Action act = () => ElementFactory.Create("nope");
        act.Should().Throw<UnknownElementTypeException>();
    }

    [Fact]
    public void Factory_creates_every_registered_type()
    {
        foreach (var key in ElementTypeKeys.All)
            ElementFactory.Create(key).TypeKey.Should().Be(key);
    }
}

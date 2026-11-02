using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossPrint.Core;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Engine.Tests;

/// <summary>
/// 数据绑定取证。这一层的原则是"缺字段就印空白并记告警，绝不把 {{}} 留在纸上"，
/// 所以每条断言都同时看**替换结果**和**告警内容**——只看结果会漏掉静默失败。
/// </summary>
public class DataBinderTests
{
    static readonly Customer Ada = new("Ada Lovelace", null);
    static readonly Order Sample = new(
        "CP-2026-0001",
        Ada,
        [new Item("SKU-A", 2, 19.90m), new Item("SKU-B", 1, 5m)],
        new DateTime(2026, 3, 5, 14, 30, 0));

    [Fact]
    public void 点分路径取到嵌套属性()
    {
        DataBinder.TryResolve(Sample, "customer.name", out var value).Should().BeTrue();
        value.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void 下标取列表元素()
    {
        DataBinder.TryResolve(Sample, "items[1].sku", out var value).Should().BeTrue();
        value.Should().Be("SKU-B");
    }

    [Fact]
    public void 链式下标逐层取到内层元素而不是静默丢一层()
    {
        // 修复前实测：matrix[0][1] 只解析第一个方括号，取到的是内层数组，纸上印出 "System.String[]"
        object matrix = new { matrix = new[] { new[] { "a", "b" } } };
        DataBinder.TryResolve(matrix, "matrix[0][1]", out var value).Should().BeTrue();
        value.Should().Be("b");

        var report = new RenderReport();
        DataBinder.BindText("{{matrix[0][1]}}", matrix, report, "e").Should().Be("b");
        report.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("matrix[]")]      // 空下标
    [InlineData("matrix[0")]      // 方括号未闭合
    [InlineData("matrix[abc]")]   // 下标不是整数
    [InlineData("matrix[0]junk")] // 下标后面挂着尾巴：修复前静默当 matrix[0] 用
    public void 畸形下标段算缺失而不是返回整个集合(string path)
    {
        // 修复前实测：{{matrix[]}} 印出 "System.String[][]"——集合的 ToString 是最典型的"看着没错的错单"
        object matrix = new { matrix = new[] { new[] { "a", "b" } } };
        DataBinder.TryResolve(matrix, path, out _).Should().BeFalse(path);

        var report = new RenderReport();
        DataBinder.BindText("{{" + path + "}}", matrix, report, "e").Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField && i.Message.Contains(path));
    }

    [Fact]
    public void 未闭合占位符原样打印但记告警()
    {
        // 修复前实测："金额 {{orderNo}" 一字不动印到纸上（含花括号），零告警——语法错误静默上桌
        var report = new RenderReport();
        DataBinder.BindText("金额 {{orderNo}", Sample, report, "e")
            .Should().Be("金额 {{orderNo}", "猜用户想要空串还是字面花括号都会猜错，内容原样保留");
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField && i.Message.Contains("未闭合"));

        // 闭合正确的占位符不陪绑：混排时只报未闭合的那一个
        var mixed = new RenderReport();
        DataBinder.BindText("{{orderNo}} 与 {{customer.name", Sample, mixed, "e")
            .Should().Be("CP-2026-0001 与 {{customer.name");
        mixed.Issues.Should().ContainSingle(i => i.Message.Contains("未闭合"));
    }

    [Fact]
    public void 未闭合占位符确实把花括号印到了纸上()
    {
        // "纸上"取证：渲染文本元素，未闭合占位符的墨迹必须存在（原样打印），同时报告里有告警——两者缺一不成立
        var report = new RenderReport();
        using var bitmap = Fixture.RenderElement(Fixture.Text("{{orderNo}", 5, 5, 40, 10), 150, report: report, data: Sample);
        Ink.BoundingBoxMm(bitmap, 150).Should().NotBeNull("字面花括号真的打印在纸上");
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField && i.Message.Contains("未闭合"));
    }

    [Fact]
    public void 大小写口径_POCO反射不敏感而泛型字典按宿主比较器()
    {
        // 钉住实测口径（docs/template-schema.md §5 未承诺大小写行为）：两种宿主数据源走的是两套比较器
        DataBinder.TryResolve(new { Name = "x" }, "NAME", out var poco).Should().BeTrue("POCO 属性映射用 OrdinalIgnoreCase");
        poco.Should().Be("x");

        DataBinder.TryResolve(new Dictionary<string, object?>(StringComparer.Ordinal) { ["Name"] = "x" }, "NAME", out _)
            .Should().BeFalse("IDictionary<string,object?> 按宿主自己的比较器查键，Ordinal 大小写敏感是宿主语义，引擎不悄悄改写");

        DataBinder.TryResolve(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "x" }, "NAME", out var insensitive).Should().BeTrue();
        insensitive.Should().Be("x", "宿主想要不敏感就给不敏感字典，两边口径一致");
    }

    [Fact]
    public void 越界下标算缺失而不是异常()
    {
        DataBinder.TryResolve(Sample, "items[9].sku", out _).Should().BeFalse();

        var report = new RenderReport();
        DataBinder.BindText("{{items[9].sku}}", Sample, report, "e1").Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.ElementId == "e1" && i.Kind == RenderIssueKind.BindingMissingField);
    }

    [Fact]
    public void 属性名大小写不敏感()
    {
        DataBinder.TryResolve(Sample, "ORDERNO", out var value).Should().BeTrue();
        value.Should().Be("CP-2026-0001");

        DataBinder.TryResolve(Sample, "Customer.Name", out _).Should().BeTrue();
    }

    [Fact]
    public void 路径里的手写空格不影响取值与首段判定()
    {
        // 手写 JSON 常出现 " customer . name "；若首段带空格，表格会误判成"两列不同源"而不展开数据行
        DataBinder.TryResolve(Sample, " customer . name ", out var value).Should().BeTrue();
        value.Should().Be("Ada Lovelace");

        DataBinder.FirstSegment(" customer . name ").Should().Be("customer");
        DataBinder.StripFirstSegment(" customer . name ").Should().Be("name");
    }

    [Fact]
    public void 字典与ExpandoObject按键取值()
    {
        var dict = new Dictionary<string, object?> { ["order"] = new { No = "D-1" }, ["total"] = 42 };
        DataBinder.TryResolve(dict, "order.No", out var nested).Should().BeTrue();
        nested.Should().Be("D-1");
        DataBinder.TryResolve(dict, "total", out var total).Should().BeTrue();
        total.Should().Be(42);

        dynamic expando = new System.Dynamic.ExpandoObject();
        expando.waybill = "E-9";
        DataBinder.TryResolve((object)expando, "waybill", out var waybill).Should().BeTrue();
        waybill.Should().Be("E-9");
    }

    [Fact]
    public void 宿主自定义取值接口优先于反射()
    {
        // Provider 同时有名为 Token 的属性：接口命中说明"实现 IDictionaryProvider 就绕开反射"这条承诺成立
        DataBinder.TryResolve(new SecretProvider(), "token", out var value).Should().BeTrue();
        value.Should().Be("FROM-PROVIDER");

        DataBinder.TryResolve(new SecretProvider(), "Token", out var prop).Should().BeTrue();
        prop.Should().Be("FROM-PROVIDER", "接口先命中，反射属性名同样大小写不敏感");

        DataBinder.TryResolve(new SecretProvider(), "unknown", out _).Should().BeFalse();
    }

    [Fact]
    public void 缺失路径替换成空串并逐条记告警()
    {
        var report = new RenderReport();
        var text = DataBinder.BindText("单号 {{orderNo}}，客户 {{customer.miss}}，收件人 {{receiver}}", Sample, report, "txt-1");

        text.Should().Be("单号 CP-2026-0001，客户 ，收件人 ", "缺字段只能留空白，不能把花括号印到面单上");
        report.Issues.Should().HaveCount(2);
        report.Issues.Select(i => i.Message).Should().Contain(m => m.Contains("customer.miss")).And.Contain(m => m.Contains("receiver"));
        report.Issues.Should().OnlyContain(i => i.ElementId == "txt-1" && i.Kind == RenderIssueKind.BindingMissingField);
    }

    [Fact]
    public void 没有占位符时原样返回且零告警()
    {
        var report = new RenderReport();
        const string text = "普通文本 { 单个花括号 } 与 100% 完成";

        DataBinder.BindText(text, Sample, report, "e").Should().Be(text);
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void 空路径与空白数据源都算缺失()
    {
        var report = new RenderReport();
        DataBinder.BindText("{{   }}", Sample, report, "e").Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField);

        var report2 = new RenderReport();
        DataBinder.BindText("{{customer.name}}", null, report2, "e").Should().BeEmpty();
        report2.Issues.Should().ContainSingle();
    }

    [Fact]
    public void 值本身为null与DBNull都印空串()
    {
        var report = new RenderReport();
        DataBinder.BindText("[{{customer.city}}]", new { customer = new { city = (string?)null } }, report, "e")
            .Should().Be("[]", "字段存在但值为空是常态，不该记成缺失");
        report.Issues.Should().BeEmpty("值存在即为空串，与路径缺失是两回事");

        DataBinder.BindText("[{{customer.city}}]", new { customer = new { city = DBNull.Value } }, new RenderReport(), "e")
            .Should().Be("[]");
    }

    [Fact]
    public void 数字与日期按宿主当前文化格式化()
    {
        // 面单给人看，分隔符跟系统区域走。这里用同一个 CurrentCulture 计算期望值，
        // 是把"委托给当前文化"这条契约钉住，而不是硬编码某台机器的分隔符。
        const decimal amount = 1234.5678m;
        DataBinder.ToString(amount).Should().Be(amount.ToString(CultureInfo.CurrentCulture));

        var date = new DateTime(2026, 3, 5);
        DataBinder.ToString(date).Should().Be(date.ToString(CultureInfo.CurrentCulture));

        DataBinder.ToString(true).Should().Be("True");
        DataBinder.ToString(null).Should().BeEmpty();
        DataBinder.ToString(DBNull.Value).Should().BeEmpty();
    }

    [Fact]
    public void 字符串不当作集合展开()
    {
        DataBinder.TryResolveCollection(Sample, "customer.name", out _).Should().BeFalse("否则 \"Ada\" 会变成 3 行");
        DataBinder.TryResolveCollection(Sample, "items", out var items).Should().BeTrue();
        items!.Should().HaveCount(2);
        DataBinder.TryResolveCollection(Sample, "nothing", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("items", "items")]
    [InlineData("items[0].sku", "items")]
    [InlineData(" customer . name ", "customer")]
    [InlineData("", "")]
    public void 取首段(string path, string expected)
        => DataBinder.FirstSegment(path).Should().Be(expected);

    [Theory]
    [InlineData("items.name", "name")]
    [InlineData("items[0].sku", "sku")]
    [InlineData("items[0]", "")]
    [InlineData("items", "")]
    [InlineData("customer.address.city", "address.city")]
    [InlineData("items[0", "")]
    public void 剥掉首段(string path, string expected)
        => DataBinder.StripFirstSegment(path).Should().Be(expected);

    [Theory]
    [InlineData("{{a}}", true)]
    [InlineData("无占位符", false)]
    [InlineData("{a}", false)]
    [InlineData(null, false)]
    public void 是否含占位符(string? text, bool expected)
        => DataBinder.HasPlaceholder(text).Should().Be(expected);

    // ---------------- 设计态数据兜底 ----------------

    [Fact]
    public void 设计数据只补宿主缺失的字段()
    {
        var design = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["orderNo"] = "DESIGN",
            ["barcode"] = "8801234567890",
            ["customer"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["city"] = "上海" },
        };

        var merged = DataBinder.WithDesignData(new { orderNo = "HOST" }, design);
        var report = new RenderReport();

        DataBinder.BindText("{{orderNo}}|{{barcode}}|{{customer.city}}", merged, report, "e")
            .Should().Be("HOST|8801234567890|上海", "宿主数据优先，设计数据只补缺；嵌套路径也要能回落到设计数据");
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void 宿主字段值为null时算命中而不回落设计数据()
    {
        var design = new Dictionary<string, object?> { ["city"] = "上海" };
        var merged = DataBinder.WithDesignData(new { city = (string?)null }, design);

        var report = new RenderReport();
        DataBinder.BindText("[{{city}}]", merged, report, "e").Should().Be("[]",
            "字段存在但值为空是宿主的选择，不能悄悄换成样例值印到纸上");
        report.Issues.Should().BeEmpty("路径存在（值为 null）就不算缺字段");
    }

    [Fact]
    public void 没有宿主数据时直接用设计数据()
    {
        var design = new Dictionary<string, object?> { ["barcode"] = "8801" };
        var merged = DataBinder.WithDesignData(null, design);

        var report = new RenderReport();
        DataBinder.BindText("{{barcode}}", merged, report, "e").Should().Be("8801");
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void 设计数据为空时不改变原数据源()
    {
        var host = new { title = "T" };

        DataBinder.WithDesignData(host, null).Should().BeSameAs(host);
        DataBinder.WithDesignData(host, new Dictionary<string, object?>()).Should().BeSameAs(host, "空设计数据不该包一层没用的代理");
        DataBinder.WithDesignData(null, null).Should().BeNull();
    }

    [Fact]
    public void 含下标的设计数据按路径可解析()
    {
        var template = PrintTemplate.CreateDefault();
        template.DesignData["items[0].sku"] = "SKU-A";

        var merged = DataBinder.WithDesignData(null, template.BuildDesignBindingData());

        DataBinder.TryResolve(merged, "items[0].sku", out var value).Should().BeTrue("设计数据要展开成绑定器认识的字典 + 列表");
        value.Should().Be("SKU-A");
    }

    // ---------------- 元素级测试数据（missingFallback） ----------------

    [Fact]
    public void 测试数据只兜缺失的占位符且不打告警()
    {
        var report = new RenderReport();
        DataBinder.BindText("单号 {{orderNo}} / 会员 {{member.no}}", Sample, report, "e", missingFallback: "TEST-001")
            .Should().Be("单号 CP-2026-0001 / 会员 TEST-001", "宿主数据命中的占位符优先，测试数据只替'解析不到'的");

        report.Issues.Should().BeEmpty("测试数据是设计态的预期兜底，打告警会把正常的设计预览当成渲染故障");
    }

    [Fact]
    public void 设计画布上测试数据优先于宿主数据()
    {
        var report = new RenderReport();
        DataBinder.BindText("单号 {{orderNo}}", Sample, report, "e", missingFallback: "TEST-999", preferTestData: true)
            .Should().Be("单号 TEST-999", "用户输入测试值就是要看到测试值，宿主样例数据不能盖住它");
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void 设计画布没填测试数据时仍显示宿主数据()
    {
        var report = new RenderReport();
        DataBinder.BindText("{{orderNo}}", Sample, report, "e", missingFallback: null, preferTestData: true)
            .Should().Be("CP-2026-0001", "没填测试数据就不该干扰正常预览");
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void 没有测试数据时保持缺失告警行为()
    {
        var report = new RenderReport();
        DataBinder.BindText("{{member.no}}", Sample, report, "e", missingFallback: string.Empty).Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField);
    }

    /// <summary>
    /// 宿主把 `JsonDocument.RootElement` 直接递进来是最常见的用法；修复前反射兜底只会读到
    /// `JsonElement` 自己的属性（ValueKind/Length），`{{orderNo}}` 必然空串 + BindingMissingField。
    /// </summary>
    [Fact]
    public void JsonElement直接当数据源绑定值与打印文本()
    {
        using var document = JsonDocument.Parse(
            """{"orderNo":"CP-2026-0001","amount":19.90,"paid":true,"note":null}""");
        var report = new RenderReport();

        var text = DataBinder.BindText("{{orderNo}}|{{amount}}|{{paid}}|{{note}}", document.RootElement, report, "e");

        // 数字保留 JSON 原文（19.90 不缩成 19.9），布尔按 .NET 口径，null 印空串
        text.Should().Be("CP-2026-0001|19.90|True|");
        report.Issues.Should().BeEmpty("字段都在，不该有任何缺字段告警");
    }

    [Fact]
    public void JsonElement的嵌套路径与数组下标()
    {
        using var document = JsonDocument.Parse(
            """{"customer":{"name":"Ada"},"items":[{"sku":"SKU-A"},{"sku":"SKU-B"}]}""");
        var root = document.RootElement;

        DataBinder.TryResolve(root, "customer.name", out var name).Should().BeTrue();
        DataBinder.ToString(name).Should().Be("Ada");

        DataBinder.TryResolve(root, "items[1].sku", out var sku).Should().BeTrue();
        DataBinder.ToString(sku).Should().Be("SKU-B");
    }

    [Fact]
    public void JsonElement数组可按行展开且越界算缺失()
    {
        using var document = JsonDocument.Parse("""{"items":[{"sku":"A"},{"sku":"B"}]}""");

        DataBinder.TryResolveCollection(document.RootElement, "items", out var items).Should().BeTrue();
        items.Should().HaveCount(2, "JsonElement 数组不实现 IEnumerable，修复前这里返回 false");

        DataBinder.TryResolve(document.RootElement, "items[2].sku", out _).Should().BeFalse();
    }

    [Fact]
    public void JsonElement缺字段照常记告警且属性名区分大小写()
    {
        using var document = JsonDocument.Parse("""{"Name":"Ada"}""");
        var report = new RenderReport();

        // JSON 本身区分大小写：Name 与 name 是两个字段，不做"悄悄大小写不敏感"的猜测
        DataBinder.BindText("{{name}}", document.RootElement, report, "e").Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField);
    }

    // ── JsonNode：页面/接口往往先 JsonNode.Parse 再交给渲染，口径必须与 JsonElement 一致 ──

    [Fact]
    public void JsonNode直接当数据源绑定值与打印文本()
    {
        var root = JsonNode.Parse("""{"orderNo":"CP-2026-0001","amount":19.90,"paid":true,"note":null}""")!;
        var report = new RenderReport();

        var text = DataBinder.BindText("{{orderNo}}|{{amount}}|{{paid}}|{{note}}", root, report, "e");

        text.Should().Be("CP-2026-0001|19.90|True|");
        report.Issues.Should().BeEmpty("字段都在，不该有任何缺字段告警");
    }

    [Fact]
    public void JsonNode的嵌套路径与数组下标()
    {
        var root = JsonNode.Parse(
            """{"customer":{"name":"Ada"},"items":[{"sku":"SKU-A"},{"sku":"SKU-B"}]}""")!;

        DataBinder.TryResolve(root, "customer.name", out var name).Should().BeTrue();
        DataBinder.ToString(name).Should().Be("Ada");

        DataBinder.TryResolve(root, "items[1].sku", out var sku).Should().BeTrue();
        DataBinder.ToString(sku).Should().Be("SKU-B");
    }

    [Fact]
    public void JsonNode数组可按行展开且越界算缺失()
    {
        var root = JsonNode.Parse("""{"items":[{"sku":"A"},{"sku":"B"}]}""")!;

        DataBinder.TryResolveCollection(root, "items", out var items).Should().BeTrue();
        items.Should().HaveCount(2);

        DataBinder.TryResolve(root, "items[2].sku", out _).Should().BeFalse();
    }

    [Fact]
    public void JsonNode缺字段照常记告警且属性名区分大小写()
    {
        var root = JsonNode.Parse("""{"Name":"Ada"}""")!;
        var report = new RenderReport();

        DataBinder.BindText("{{name}}", root, report, "e").Should().BeEmpty();
        report.Issues.Should().ContainSingle(i => i.Kind == RenderIssueKind.BindingMissingField);
    }

    sealed record Customer(string Name, string? City);

    sealed record Item(string Sku, int Qty, decimal Price);

    sealed record Order(string OrderNo, Customer Customer, IReadOnlyList<Item> Items, DateTime CreatedOn);

    sealed class SecretProvider : IDictionaryProvider
    {
        /// <summary>同名属性存在，用来证明接口优先级。</summary>
        public string Token { get; } = "FROM-PROPERTY";

        public bool TryGetMember(string name, out object? value)
        {
            if (name.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                value = "FROM-PROVIDER";
                return true;
            }

            value = null;
            return false;
        }
    }
}

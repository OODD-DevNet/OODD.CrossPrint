using System.Collections;
using System.Reflection;
using System.Text;
using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Printing;

namespace CrossPrint.Demo.DataTest;

/// <summary>订单行：集合元素，模板表格按 <c>items.name / items.qty / items.amount</c> 逐行展开。</summary>
sealed class ReceiptItem
{
    public string Name { get; init; } = "";

    public int Qty { get; init; }

    public decimal Amount { get; init; }

    public override string ToString() => $"{Name} ×{Qty} = {Amount:0.00}";
}

/// <summary>会员卡：嵌套对象。本套模板绑定的是扁平 <c>memberNo</c>，嵌套结构在数据清单里展示形态。</summary>
sealed class MemberCard
{
    public string No { get; init; } = "";

    public string Level { get; init; } = "";

    public bool Active { get; init; }

    public override string ToString() => $"{Level} · {No} · {(Active ? "有效" : "停用")}";
}

/// <summary>支付渠道：枚举也是常见数据形态，随 IFormattable 走当前文化 ToString。</summary>
enum PayChannel
{
    Cash,
    WeChat,
    Alipay,
    Card,
}

/// <summary>
/// 一张小票的全部数据。故意凑齐各种类型：
/// string / int / long / double / decimal / bool / char / DateTime / Guid / 枚举 / 嵌套对象 / 集合 / 多行长文本。
/// 模板绑定路径与属性名大小写不敏感匹配（shopName ↔ ShopName）；
/// 绑定时 IFormattable 值用宿主当前文化格式化（见 DataBinder.ToString）。
/// </summary>
sealed class OrderData
{
    // ── 模板绑定的字段 ──
    public string ShopName { get; init; } = "";
    public string Slogan { get; init; } = "";
    public string OrderNo { get; init; } = "";
    public DateTime CreatedOn { get; init; }
    public string Cashier { get; init; } = "";
    public string MemberNo { get; init; } = "";
    public List<ReceiptItem> Items { get; init; } = [];
    public decimal Subtotal { get; init; }
    public decimal Discount { get; init; }
    public decimal Total { get; init; }
    public int Points { get; init; }
    public string CouponCode { get; init; } = "";
    public string Address { get; init; } = "";
    public string ServicePhone { get; init; } = "";

    // ── kitchen-sink 综合样张绑定的字段 ──
    public string Title { get; init; } = "";
    public string Sku { get; init; } = "";

    // ── 模板没绑、只出现在数据清单里的"其他类型" ──
    public long SequenceNo { get; init; }
    public double WeightKg { get; init; }
    public bool PaidByCard { get; init; }
    public char Grade { get; init; }
    public Guid TrackingId { get; init; }
    public PayChannel Channel { get; init; }
    public MemberCard Member { get; init; } = new();
    public string Remark { get; init; } = "";
}

/// <summary>发票抬头主体：嵌套对象，模板用 <c>buyer.name</c> / <c>seller.taxId</c> 这类点分路径绑定。</summary>
sealed class Company
{
    public string Name { get; init; } = "";

    public string TaxId { get; init; } = "";

    public string Address { get; init; } = "";

    public string Bank { get; init; } = "";

    public override string ToString() => $"{Name}（{TaxId}）";
}

/// <summary>发票明细行：表格按 <c>lines.name / lines.spec / lines.qty / lines.unitPrice / lines.amount</c> 展开。</summary>
sealed class InvoiceLine
{
    public string Name { get; init; } = "";

    public string Spec { get; init; } = "";

    public int Qty { get; init; }

    public decimal UnitPrice { get; init; }

    public decimal Amount { get; init; }

    public override string ToString() => $"{Name} {Spec} ×{Qty} @ {UnitPrice:0.00} = {Amount:0.00}";
}

/// <summary>A4 增值税发票（演示）数据：嵌套对象 + 大集合 + 中文大写金额。PageNo/PageCount 供多页对账单模板使用。</summary>
sealed class InvoiceData
{
    public string InvoiceNo { get; init; } = "";
    public DateTime InvoiceDate { get; init; }
    public Company Buyer { get; init; } = new();
    public Company Seller { get; init; } = new();
    public List<InvoiceLine> Lines { get; init; } = [];
    public decimal TotalAmount { get; init; }
    public string AmountInWords { get; init; } = "";
    public string Payee { get; init; } = "";
    public string Checker { get; init; } = "";
    public string Clerk { get; init; } = "";
    public int PageNo { get; init; }
    public int PageCount { get; init; }
}

/// <summary>联系人：面单的收件人/寄件人，嵌套对象按 <c>recipient.name</c> 等路径绑定。</summary>
sealed class Contact
{
    public string Name { get; init; } = "";

    public string Phone { get; init; } = "";

    public string Address { get; init; } = "";

    public override string ToString() => $"{Name} {Phone}";
}

/// <summary>100×150 物流面单数据：条码（运单号）+ 二维码（取件码）+ 嵌套联系人 + double/bool。</summary>
sealed class LabelData
{
    public string PickupCode { get; init; } = "";
    public Contact Recipient { get; init; } = new();
    public Contact Sender { get; init; } = new();
    public string Contents { get; init; } = "";
    public int Pieces { get; init; }
    public double WeightKg { get; init; }
    public bool Insured { get; init; }
    public string WaybillNo { get; init; } = "";
    public string Remark { get; init; } = "";
}

/// <summary>账单消费/付款行：一个模板里两张表各绑一个集合（charges[] 与 payments[]）。</summary>
sealed class FolioLine
{
    public string Date { get; init; } = "";

    public string Item { get; init; } = "";

    public decimal Amount { get; init; }

    public override string ToString() => $"{Date} {Item} = {Amount:0.00}";
}

/// <summary>账单付款行：方式 + 凭证号 + 金额。</summary>
sealed class PaymentLine
{
    public string Method { get; init; } = "";

    public string Reference { get; init; } = "";

    public decimal Amount { get; init; }

    public override string ToString() => $"{Method}({Reference}) = {Amount:0.00}";
}

/// <summary>酒店前台账单数据：同页双集合表格 + 负数调整行（调价退差）。</summary>
sealed class HotelFolioData
{
    public string FolioNo { get; init; } = "";
    public string GuestName { get; init; } = "";
    public string RoomNo { get; init; } = "";
    public string CheckIn { get; init; } = "";
    public string CheckOut { get; init; } = "";
    public int Nights { get; init; }
    public List<FolioLine> Charges { get; init; } = [];
    public List<PaymentLine> Payments { get; init; } = [];
    public decimal ChargesTotal { get; init; }
    public decimal PaymentsTotal { get; init; }
    public decimal Due { get; init; }
    public string VerifyCode { get; init; } = "";
    public string Clerk { get; init; } = "";
    public DateTime PrintedAt { get; init; }
}

/// <summary>50×50 圆形瓶贴数据：极小字号 + 小 QR（约 8mm 见方）压力。</summary>
sealed class StickerData
{
    public string ProductName { get; init; } = "";
    public string ProductNameEn { get; init; } = "";
    public decimal Price { get; init; }
    public string BatchNo { get; init; } = "";
    public string NetContent { get; init; } = "";
}

/// <summary>
/// A6 边界案例页数据：故意喂"坏数据"把降级通道一条条踩响——
/// 空二维码内容、校验位错误的 EAN-13、null 值、零/负数/长整最大值、无空格长词。
/// </summary>
sealed class EdgeCaseData
{
    public string QrContent { get; init; } = "";
    public string BadEan { get; init; } = "";
    public string LongWord { get; init; } = "";
    public string? MissingValue { get; init; }
    public int Zero { get; init; }
    public decimal Negative { get; init; }
    public long Big { get; init; }
}

/// <summary>拣货单行：应拣有值、实拣留白（打印后手写）。</summary>
sealed class PickLine
{
    public string Location { get; init; } = "";

    public string Name { get; init; } = "";

    public string Spec { get; init; } = "";

    public int Planned { get; init; }

    public string Picked { get; init; } = "";

    public string Unit { get; init; } = "";

    public override string ToString() => $"{Location} {Name} ×{Planned}{Unit}";
}

/// <summary>A5 横向拣货单数据：6 列表格 + 大行高（手写空间）。</summary>
sealed class PickListData
{
    public string PickNo { get; init; } = "";
    public DateTime CreatedOn { get; init; }
    public string Zone { get; init; } = "";
    public List<PickLine> Lines { get; init; } = [];
    public string Picker { get; init; } = "";
    public string Checker { get; init; } = "";
}

/// <summary>演示用图片解析器：只认 logo-ok，其余一律 null（触发 ImageMissing 告警）。</summary>
sealed class DemoImageResolver : IImageResolver
{
    // 1×1 橙色 PNG（与模板内嵌样张同源的最小合法位图）。
    const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    public byte[]? Resolve(string key) => key == "logo-ok" ? Convert.FromBase64String(TinyPng) : null;
}

/// <summary>
/// 数据测试 demo：构造各类文档（购物小票 / A4 发票 / 物流面单 / 超长小票）的真实数据，
/// 套上 docs/samples 里的模板走完整打印链路。
/// 「打印」= <see cref="PdfFilePrintTarget"/>（v0.1 的 PDF 落盘即打印，FR-PRT-01），
/// 产物在输出目录的 PrintOut\ 下，用任意 PDF 阅读器打开验收。
/// 场景 16 是可选交互：<see cref="SystemPrintTarget"/> 真调起系统打印管线（FR-PRT-03），
/// 无人值守（CI）默认跳过，不弹窗不真出纸。
/// </summary>
internal static class Program
{
    /// <summary>
    /// PDF 文字转轮廓：SkiaSharp 2.88.9 不做字体子集化，一份中文 PDF 会把整份字库（雅黑每字重约 19MB）嵌进去，
    /// 40MB 一张小票没法发。转轮廓后体积按字形数线性走；代价是文字不可选中/检索——打印小票不需要。
    /// </summary>
    static readonly RenderOptions OutlineTextOptions = new() { PdfTextAsOutlines = true };

    static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var templates = LoadTemplates();
        var outDir = Path.Combine(AppContext.BaseDirectory, "PrintOut");
        Directory.CreateDirectory(outDir);

        var target = new PdfFilePrintTarget();
        var failures = 0;

        // ── 购物小票 ──
        var data = MakeOrder(1);
        DumpData(data, "购物小票数据（string / int / long / double / decimal / bool / char / DateTime / Guid / 枚举 / 嵌套对象 / 集合 / 多行文本）");

        Header("场景 1：单文档打印 —— 80mm 连续纸小票 + 完整 POCO 数据");
        failures += await PrintOneAsync(target, "receipt-single.pdf",
            new PrintDocument(templates.Receipt, data), outDir);

        Header("场景 2：换模板不换数据 —— A4 综合样张绑同一份 POCO（字段名大小写不敏感）");
        failures += await PrintOneAsync(target, "kitchen-sink.pdf",
            new PrintDocument(templates.KitchenSink, data), outDir);

        Header("场景 3：批量打印 —— 3 单不同数据合成一份 3 页 PDF（字库只嵌一次）");
        var batch = Enumerable.Range(1, 3)
            .Select(i => new PrintDocument(templates.Receipt, MakeOrder(i)))
            .ToList();
        failures += await PrintBatchAsync(target, "receipt-batch.pdf", batch, outDir);

        Header("场景 4：联单 —— 同一单印 2 份（份数挂在文档上，一份文件连续两页）");
        failures += await PrintOneAsync(target, "receipt-copies.pdf",
            new PrintDocument(templates.Receipt, MakeOrder(2), copies: 2), outDir);

        Header("场景 5：无数据打印 —— 缺字段替换空串并记 BindingMissingField 告警，绝不抛异常");
        failures += await PrintOneAsync(target, "receipt-no-data.pdf",
            new PrintDocument(templates.Receipt), outDir);

        // ── A4 发票 ──
        var invoice = MakeInvoice(24);
        DumpData(invoice, "发票数据（嵌套 Company 对象 + 24 行明细集合 + 中文大写金额）");

        Header("场景 6：A4 增值税发票（演示）—— buyer.name / seller.taxId 嵌套路径绑定 + 24 行明细表格");
        failures += await PrintOneAsync(target, "invoice-a4.pdf",
            new PrintDocument(templates.Invoice, invoice), outDir);

        // ── 物流面单 ──
        var label = MakeLabel();
        DumpData(label, "面单数据（嵌套收件人/寄件人 + double 重量 + bool 保价 + 条码/二维码内容）");

        Header("场景 7：物流面单标签（100×150）—— Code128 运单条码 + 取件码二维码");
        failures += await PrintOneAsync(target, "label-100x150.pdf",
            new PrintDocument(templates.Label, label), outDir);

        // ── 超长小票 ──
        var longOrder = MakeLongOrder(60);
        DumpData(longOrder, $"超长小票数据（{longOrder.Items.Count} 行商品，集合越界力压连续纸自动增高）");

        Header("场景 8：超长小票 —— 60 行商品，80mm 连续纸整页约 490mm（比 A4 长一倍半）");
        failures += await PrintOneAsync(target, "receipt-long.pdf",
            new PrintDocument(templates.ReceiptLong, longOrder), outDir);

        Header("场景 9：混合纸张批量 —— 长小票 + A4 发票 + 100×150 面单合成一份 PDF（页页各用各的纸张）");
        var mixed = new List<PrintDocument>
        {
            new(templates.ReceiptLong, longOrder),
            new(templates.Invoice, invoice),
            new(templates.Label, label),
        };
        failures += await PrintBatchAsync(target, "mixed-papers.pdf", mixed, outDir);

        // ── 酒店账单 ──
        var folio = MakeFolio();
        DumpData(folio, "酒店账单数据（同页双集合：charges[] 与 payments[]，负数调整行）");

        Header("场景 10：酒店前台账单 —— 两张表各绑一个集合，含调价退差负数行与扫码核销 QR");
        failures += await PrintOneAsync(target, "hotel-folio.pdf",
            new PrintDocument(templates.HotelFolio, folio), outDir);

        // ── 圆形瓶贴 ──
        var sticker = new StickerData
        {
            ProductName = "桂花酿桂花酿",
            ProductNameEn = "Osmanthus Brew",
            Price = 39.90m,
            BatchNo = "B20261031",
            NetContent = "330ml",
        };
        Header("场景 11：50×50 圆形瓶贴 —— 大圆角矩形 + 4.5~14pt 字号跨度 + 8mm 微型 QR");
        failures += await PrintOneAsync(target, "sticker-50.pdf",
            new PrintDocument(templates.Sticker, sticker), outDir);

        // ── 告警全打样 ──
        var edge = new EdgeCaseData
        {
            QrContent = "",
            BadEan = "1234567890125", // EAN-13 校验位错误（合法应为 …0128）
            LongWord = "Donaudampfschifffahrtsgesellschaftskapitaen-Supercalifragilisticexpialidocious-0123456789",
            MissingValue = null,
            Zero = 0,
            Negative = -1234.50m,
            Big = long.MaxValue,
        };
        Header("场景 12：告警通道全打样（A6）—— 缺字体/缺字段/空QR/坏EAN/图片缺key/表格溢出/越界元素，全部降级不抛异常");
        failures += await PrintOneAsync(target, "edge-case-a6.pdf",
            new PrintDocument(templates.EdgeCase, edge), outDir, images: new DemoImageResolver());

        // ── A5 横向拣货单 ──
        var pick = MakePickList();
        DumpData(pick, "拣货单数据（6 列表格，实拣列留白手写，A5 横向）");

        Header("场景 13：A5 横向拣货单 —— Landscape 方向 + 8mm 大行高留手写空间");
        failures += await PrintOneAsync(target, "pick-list-a5.pdf",
            new PrintDocument(templates.PickList, pick), outDir);

        // ── 100 单大批量 ──
        Header("场景 14：100 单大批量 —— 100 条不同数据 × 小票模板 = 100 页 PDF（引擎 M6 验收口径）");
        var bigBatch = Enumerable.Range(1, 100)
            .Select(i => new PrintDocument(templates.Receipt, MakeOrder(i % 3 + 1)))
            .ToList();
        failures += await PrintBatchAsync(target, "receipt-batch-100.pdf", bigBatch, outDir);

        // ── 500+ 行 A4 大对账单 ──
        const int bigLineCount = 520;
        const int rowsPerPage = 24; // 表格区 158mm：6mm 表头 + 24 行 × 6mm + 小计行 ≈ 155.6mm ≤ 158mm，不触发溢出
        var bigLines = MakeBigLines(bigLineCount);
        var pageCount = (int)Math.Ceiling(bigLines.Count / (double)rowsPerPage);
        var bigTotal = bigLines.Sum(l => l.Amount);
        DumpData(new { PageCount = pageCount, RowsPerPage = rowsPerPage, Total = bigTotal },
            $"大对账单数据（{bigLineCount} 行明细 → 每页 {rowsPerPage} 行 × {pageCount} 页；行底色规则：金额 >50 灰 / >60 蓝 / >90 金）");

        Header($"场景 15：500+ 行 A4 大对账单 —— 同模板切页成 {pageCount} 份文档批量合成，每页页眉完整、金额列出本页小计，行金额按阈值上条件底色");
        var bigDocs = new List<PrintDocument>(pageCount);
        for (var p = 0; p < pageCount; p++)
        {
            bigDocs.Add(new PrintDocument(templates.Invoice500, new InvoiceData
            {
                InvoiceNo = $"INV-20261031-{bigLineCount:D4}",
                InvoiceDate = new DateTime(2026, 10, 31, 0, 0, 0),
                Buyer = DemoBuyer(),
                Seller = DemoSeller(),
                Lines = bigLines.Skip(p * rowsPerPage).Take(rowsPerPage).ToList(),
                TotalAmount = bigTotal,
                AmountInWords = "贰万捌仟陆佰肆拾贰元柒角伍分",
                Payee = "王五",
                Checker = "李四",
                Clerk = "张三",
                PageNo = p + 1,
                PageCount = pageCount,
            }));
        }

        failures += await PrintBatchAsync(target, "invoice-a4-500rows.pdf", bigDocs, outDir);

        // ── 调起系统打印（可选交互）──
        Header("场景 16（可选交互）：调起系统打印 —— SystemPrintTarget 渲染后交 OS 管线（Windows ShellExecute 动词 / macOS·Linux lp）");
        failures += await InvokeSystemPrintAsync(templates);

        Console.WriteLine(failures == 0
            ? $"── 全部场景成功，产物在：{outDir}"
            : $"── {failures} 个场景失败，详见上方日志。");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>全部样例模板的引用：字段名即模板文件名（去后缀），按需取用。</summary>
    sealed record SampleTemplates
    {
        public PrintTemplate Receipt { get; init; } = null!;
        public PrintTemplate KitchenSink { get; init; } = null!;
        public PrintTemplate Invoice { get; init; } = null!;
        public PrintTemplate Label { get; init; } = null!;
        public PrintTemplate ReceiptLong { get; init; } = null!;
        public PrintTemplate HotelFolio { get; init; } = null!;
        public PrintTemplate Sticker { get; init; } = null!;
        public PrintTemplate EdgeCase { get; init; } = null!;
        public PrintTemplate PickList { get; init; } = null!;
        public PrintTemplate Invoice500 { get; init; } = null!;
    }

    /// <summary>严格加载 docs/samples 的全部样例模板（这些样例承诺零加载告警）。</summary>
    static SampleTemplates LoadTemplates() => new()
    {
        Receipt = Load("complex-receipt.template.json"),
        KitchenSink = Load("kitchen-sink.template.json"),
        Invoice = Load("invoice-a4.template.json"),
        Invoice500 = Load("invoice-a4-500row.template.json"),
        Label = Load("label-100x150.template.json"),
        ReceiptLong = Load("receipt-long.template.json"),
        HotelFolio = Load("hotel-folio.template.json"),
        Sticker = Load("sticker-50.template.json"),
        EdgeCase = Load("edge-case-a6.template.json"),
        PickList = Load("pick-list-a5.template.json"),
    };

    static PrintTemplate Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到样例模板 {name}（csproj 的 None Include 应把它复制到输出目录）。", path);

        var result = TemplateJson.Load(File.ReadAllText(path));
        if (result.Warnings.Count > 0)
            throw new InvalidDataException($"样例模板 {name} 加载有告警：{string.Join("; ", result.Warnings)}");

        return result.Template;
    }

    /// <summary>造一单数据：行数、金额、字段值随序号变化，凑出各种类型与中文/长名/多行文本。</summary>
    static OrderData MakeOrder(int seq)
    {
        ReceiptItem[] menu =
        [
            new() { Name = "拿铁咖啡", Qty = 1, Amount = 32.00m },
            new() { Name = "美式咖啡（大杯·双份浓缩）", Qty = 2, Amount = 36.00m },
            new() { Name = "冰博克厚牛乳生椰拿铁（会员日特价）", Qty = 1, Amount = 25.50m },
            new() { Name = "可颂", Qty = 1, Amount = 10.00m },
            new() { Name = "曲奇礼盒（含手提袋·附贺卡）", Qty = 1, Amount = 18.00m },
        ];

        // 每单行数不同：3 / 4 / 5 行；数量与金额随之变化，decimal 加整仍保留两位小数。
        var items = menu.Take(2 + seq)
            .Select(m => new ReceiptItem { Name = m.Name, Qty = m.Qty + seq - 1, Amount = m.Amount + seq })
            .ToList();

        var subtotal = items.Sum(i => i.Amount);
        var discount = seq % 2 == 0 ? 8.00m : 5.00m;
        var total = subtotal - discount;
        var channels = (PayChannel[])Enum.GetValues(typeof(PayChannel));

        return new OrderData
        {
            Title = $"CrossPrint 综合样张 · 订单 {seq}",
            Sku = $"69{seq:D11}",
            ShopName = seq switch
            {
                1 => "拾光咖啡 · 万象城店",
                2 => "拾光咖啡 · 高新店",
                _ => "拾光咖啡 · 机场 T2 店",
            },
            Slogan = "一杯咖啡的时间，把自己还给生活",
            OrderNo = $"CP-20261031-{seq:D4}",
            CreatedOn = new DateTime(2026, 10, 31, 15, 42, 0).AddMinutes(17 * seq),
            Cashier = new[] { "小雨", "阿伦", "大周" }[(seq - 1) % 3],
            MemberNo = $"880123456789{seq % 10}",
            Member = new MemberCard
            {
                No = $"880123456789{seq % 10}",
                Level = seq switch { 1 => "金卡", 2 => "银卡", _ => "普卡" },
                Active = seq != 2,
            },
            Items = items,
            Subtotal = subtotal,
            Discount = discount,
            Total = total,
            Points = (int)total,
            CouponCode = $"CPN-{8862 + seq}",
            Address = "高新区万象城 L3-12（近 3 号电梯）",
            ServicePhone = "400-123-4567",
            SequenceNo = 20261031000000L + seq,
            WeightKg = 0.48 + 0.01 * seq,
            PaidByCard = seq != 1,
            Grade = (char)('A' + seq - 1),
            TrackingId = new Guid($"00000000-0000-0000-0000-{seq:D12}"),
            Channel = channels[seq % channels.Length],
            Remark = "备注：\n· 不要奶油，少冰\n· 发票抬头：拾光（测试）有限公司\n· 门店自提，预计 18:30 到店",
        };
    }

    /// <summary>造一张 A4 发票：lineCount 行明细（品名/规格/数量/单价/金额），合计与大写自动累计。</summary>
    static InvoiceData MakeInvoice(int lineCount)
    {
        string[] goods =
        [
            "咖啡豆（云南保山日晒）", "咖啡豆（埃塞俄比亚水洗）", "挂耳咖啡礼盒", "冷萃液（无糖）",
            "燕麦奶 1L", "可颂（冷冻生胚）", "曲奇礼盒", "马克杯 350ml",
        ];
        string[] specs = ["250g/袋", "250g/袋", "10 片/盒", "18 袋/箱", "1L/盒", "20 个/袋", "8 块/盒", "350ml/只"];

        var lines = Enumerable.Range(1, lineCount)
            .Select(i =>
            {
                var qty = i % 4 + 1;
                var unit = 12.50m + i * 3.75m;
                return new InvoiceLine
                {
                    Name = goods[(i - 1) % goods.Length],
                    Spec = specs[(i - 1) % specs.Length],
                    Qty = qty,
                    UnitPrice = unit,
                    Amount = unit * qty,
                };
            })
            .ToList();

        var total = lines.Sum(l => l.Amount);
        return new InvoiceData
        {
            InvoiceNo = $"INV-20261031-{lineCount:D4}",
            InvoiceDate = new DateTime(2026, 10, 31, 0, 0, 0),
            Buyer = DemoBuyer(),
            Seller = DemoSeller(),
            Lines = lines,
            TotalAmount = total,
            AmountInWords = "壹万叁仟捌佰伍拾陆元贰角伍分",
            Payee = "王五",
            Checker = "李四",
            Clerk = "张三",
        };
    }

    static Company DemoBuyer() => new()
    {
        Name = "拾光商贸有限公司",
        TaxId = "91510100MA6TEST10X",
        Address = "成都市高新区天府大道 1 号 028-88886666",
        Bank = "工行高新支行 4402 0100 0999 888",
    };

    static Company DemoSeller() => new()
    {
        Name = "拾光咖啡供应链有限公司",
        TaxId = "91510100MA6TEST20Y",
        Address = "成都市高新区万象城 L3-12 028-88887777",
        Bank = "建行城南支行 5100 1999 2222 333",
    };

    /// <summary>
    /// 造 500+ 行大对账单明细：固定随机种子保证产物可复现，
    /// 金额 8~192 随机分布，把行底色规则的每一档（&lt;50 / 50~60 / 60~90 / &gt;90）都凑出大量样本。
    /// </summary>
    static List<InvoiceLine> MakeBigLines(int lineCount)
    {
        string[] goods =
        [
            "咖啡豆（云南保山日晒）", "咖啡豆（埃塞俄比亚水洗）", "挂耳咖啡礼盒", "冷萃液（无糖）",
            "燕麦奶 1L", "可颂（冷冻生胚）", "曲奇礼盒", "马克杯 350ml",
        ];
        string[] specs = ["250g/袋", "250g/袋", "10 片/盒", "18 袋/箱", "1L/盒", "20 个/袋", "8 块/盒", "350ml/只"];

        var rng = new Random(20261031);
        var lines = new List<InvoiceLine>(lineCount);
        for (var i = 1; i <= lineCount; i++)
        {
            var qty = rng.Next(1, 5);
            var unit = Math.Round((decimal)(rng.NextDouble() * 40 + 8), 2);
            lines.Add(new InvoiceLine
            {
                Name = $"{goods[(i - 1) % goods.Length]}#{i:D3}",
                Spec = specs[(i - 1) % specs.Length],
                Qty = qty,
                UnitPrice = unit,
                Amount = Math.Round(unit * qty, 2),
            });
        }

        return lines;
    }

    /// <summary>造一张物流面单：嵌套收件/寄件人 + double 重量 + bool 保价 + 13 位运单号。</summary>
    static LabelData MakeLabel() => new()
    {
        PickupCode = "8-2-306",
        Recipient = new Contact
        {
            Name = "陈晓晓",
            Phone = "138 0000 1234",
            Address = "成都市高新区天府三街 199 号 A 座 1206 室（近地铁 1 号线）",
        },
        Sender = new Contact
        {
            Name = "拾光咖啡仓储中心",
            Phone = "028-8888 6666",
            Address = "双流区物流大道 88 号 3 号库",
        },
        Contents = "咖啡豆×2、挂耳礼盒×1",
        Pieces = 2,
        WeightKg = 1.35,
        Insured = true,
        WaybillNo = "8812345678901",
        Remark = "轻拿轻放，易碎品；周末无人收货请勿派送",
    };

    /// <summary>造一单超长小票：rowCount 行商品，菜单循环带序号，金额累计到小票合计。</summary>
    static OrderData MakeLongOrder(int rowCount)
    {
        ReceiptItem[] menu =
        [
            new() { Name = "拿铁咖啡", Qty = 1, Amount = 32.00m },
            new() { Name = "美式咖啡", Qty = 2, Amount = 36.00m },
            new() { Name = "冰博克生椰拿铁", Qty = 1, Amount = 25.50m },
            new() { Name = "可颂", Qty = 1, Amount = 10.00m },
            new() { Name = "曲奇礼盒", Qty = 1, Amount = 18.00m },
        ];

        // 品名带序号（10 字内保证不折行），数量 1~3、金额逐行递增，decimal 保留两位小数。
        var items = Enumerable.Range(1, rowCount)
            .Select(i => new ReceiptItem
            {
                Name = $"{menu[(i - 1) % menu.Length].Name}#{i:D2}",
                Qty = i % 3 + 1,
                Amount = menu[(i - 1) % menu.Length].Amount + i * 0.75m,
            })
            .ToList();

        var subtotal = items.Sum(i => i.Amount);
        var discount = 60.00m;
        var total = subtotal - discount;

        return new OrderData
        {
            ShopName = "拾光咖啡 · 万象城店（团购批量出票）",
            OrderNo = $"CP-20261031-{rowCount:D4}",
            CreatedOn = new DateTime(2026, 10, 31, 18, 30, 0),
            Cashier = "大周",
            MemberNo = "8801234567899",
            Items = items,
            Subtotal = subtotal,
            Discount = discount,
            Total = total,
            Points = (int)total,
        };
    }

    /// <summary>造一份酒店账单：两晚房费 + 餐饮/洗衣 + 一笔调价退差（负数），预授权与现金两笔付款。</summary>
    static HotelFolioData MakeFolio()
    {
        var charges = new List<FolioLine>
        {
            new() { Date = "10-29", Item = "高级大床房（含双早）", Amount = 488.00m },
            new() { Date = "10-29", Item = "迷你吧 · 矿泉水×2", Amount = 20.00m },
            new() { Date = "10-30", Item = "高级大床房（含双早）", Amount = 488.00m },
            new() { Date = "10-30", Item = "客房送餐 · 牛肉面", Amount = 58.00m },
            new() { Date = "10-30", Item = "洗衣 · 衬衫×2", Amount = 40.00m },
            new() { Date = "10-30", Item = "调价退差（周末价差）", Amount = -50.00m },
            new() { Date = "10-31", Item = "延迟退房（14:00 前）", Amount = 100.00m },
            new() { Date = "10-31", Item = "迷你吧 · 咖啡", Amount = 28.00m },
        };
        var payments = new List<PaymentLine>
        {
            new() { Method = "预授权（微信）", Reference = "PA-8899001", Amount = 1000.00m },
            new() { Method = "现金（人民币）", Reference = "CS-000277", Amount = 500.00m },
        };
        var chargesTotal = charges.Sum(c => c.Amount);
        var paymentsTotal = payments.Sum(p => p.Amount);
        return new HotelFolioData
        {
            FolioNo = "FOL-20261031-0077",
            GuestName = "陈晓晓",
            RoomNo = "1206",
            CheckIn = "2026-10-29",
            CheckOut = "2026-10-31",
            Nights = 2,
            Charges = charges,
            Payments = payments,
            ChargesTotal = chargesTotal,
            PaymentsTotal = paymentsTotal,
            Due = chargesTotal - paymentsTotal,
            VerifyCode = "FOL-20261031-0077",
            Clerk = "前台 · 小唐",
            PrintedAt = new DateTime(2026, 10, 31, 12, 0, 0),
        };
    }

    /// <summary>造一张 A5 横向拣货单：冷链区 8 行，实拣列留白。</summary>
    static PickListData MakePickList()
    {
        string[] locations = ["B-01-03", "B-01-07", "B-02-08", "B-03-01", "B-03-05", "B-04-02", "B-04-09", "B-05-04"];
        string[] goods = ["冰博克牛奶 1L", "无糖酸奶 150g", "鲜橙汁 300ml", "冷萃咖啡液", "燕麦奶 1L", "希腊酸奶 200g", "豆奶 250ml", "气泡水 330ml"];
        string[] specs = ["1L/盒", "150g/杯", "300ml/瓶", "18 袋/箱", "1L/盒", "200g/杯", "250ml/盒", "330ml/罐"];
        string[] units = ["盒", "杯", "瓶", "箱", "盒", "杯", "盒", "罐"];

        var lines = Enumerable.Range(0, 8)
            .Select(i => new PickLine
            {
                Location = locations[i],
                Name = goods[i],
                Spec = specs[i],
                Planned = (i + 2) * 3,
                Picked = "",
                Unit = units[i],
            })
            .ToList();

        return new PickListData
        {
            PickNo = "PK-20261031-03",
            CreatedOn = new DateTime(2026, 10, 31, 8, 0, 0),
            Zone = "B 区 · 冷链货架",
            Lines = lines,
            Picker = "仓管 · 老赵",
            Checker = "复核 · 小何",
        };
    }

    /// <summary>把数据清单打到控制台：属性名 / 类型 / 值，一眼看清"各种数据"（集合只列前 5 个）。 </summary>
    static void DumpData(object data, string title)
    {
        Header(title);
        foreach (var p in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = p.GetValue(data);
            var text = value switch
            {
                null => "(null)",
                string s => s.Replace("\n", " ⏎ "),
                IEnumerable and not string => Summarize((IEnumerable)value),
                _ => value.ToString() ?? "",
            };
            Console.WriteLine($"  {p.Name,-14} [{p.PropertyType.Name,-9}] = {text}");
        }

        Console.WriteLine();
    }

    /// <summary>集合值只展示前 5 个元素，避免超长小票把控制台刷爆。</summary>
    static string Summarize(IEnumerable items)
    {
        const int take = 5;
        var parts = new List<string>();
        var total = 0;
        foreach (var item in items)
        {
            total++;
            if (parts.Count < take)
                parts.Add(item?.ToString() ?? "(null)");
        }

        return parts.Count == total
            ? string.Join(" | ", parts)
            : $"{string.Join(" | ", parts)} …（共 {total} 项）";
    }

    static async Task<int> PrintOneAsync(PdfFilePrintTarget target, string fileName, PrintDocument document, string outDir, IImageResolver? images = null)
    {
        var issues = new RenderReport();
        var result = await target.PrintAsync(document,
            new PrintSettings(Path.Combine(outDir, fileName)) { Options = OutlineTextOptions, Images = images }, issues, default);
        return Report(Path.Combine(outDir, fileName), result, issues);
    }

    static async Task<int> PrintBatchAsync(PdfFilePrintTarget target, string fileName, IReadOnlyList<PrintDocument> documents, string outDir)
    {
        var issues = new RenderReport();
        var result = await target.PrintBatchAsync(documents,
            new PrintSettings(Path.Combine(outDir, fileName)) { Options = OutlineTextOptions }, issues, default);
        return Report(Path.Combine(outDir, fileName), result, issues);
    }

    /// <summary>
    /// 场景 16：真调起系统打印。控制台 demo 在 CI 上无人值守，不能弹窗也不能真出纸，
    /// 所以先询问，回答 y 才把一单小票交给系统打印管线（不指定打印机：
    /// Windows 走「print」动词弹关联应用的打印框，macOS/Linux 交 CUPS 默认队列）。
    /// </summary>
    static async Task<int> InvokeSystemPrintAsync(SampleTemplates templates)
    {
        Console.Write("  是否真调起系统打印一份小票？(y/N)：");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  [跳过] 未确认，不发起系统打印。");
            return 0;
        }

        var issues = new RenderReport();
        var result = await new SystemPrintTarget().PrintBatchAsync(
            new[] { new PrintDocument(templates.Receipt, MakeOrder(16)) },
            new SystemPrintOptions(), issues, default);

        if (!result.Success)
        {
            Console.WriteLine($"  [失败] {result.Message}");
            if (result.Error is not null)
                Console.WriteLine($"         {result.Error.GetType().Name}: {result.Error.Message}");
            return 1;
        }

        Console.WriteLine($"  [成功] 已交给系统打印管线：{result.PrintedPages} 页（出纸由系统/关联应用完成）");
        if (issues.HasIssues)
            Console.WriteLine($"         渲染告警 {issues.Issues.Count} 条。");
        return 0;
    }

    /// <summary>产物汇报：成功打路径/页数/体积/告警分组，失败打原因与异常。返回失败计数。</summary>
    static int Report(string path, PrintResult result, RenderReport issues)
    {
        if (!result.Success)
        {
            Console.WriteLine($"  [失败] {Path.GetFileName(path)}：{result.Message}");
            if (result.Error is not null)
                Console.WriteLine($"         {result.Error.GetType().Name}: {result.Error.Message}");
            return 1;
        }

        var sizeKb = new FileInfo(path).Length / 1024.0;
        Console.WriteLine($"  [成功] {Path.GetFileName(path)}  {result.PrintedPages} 页  {sizeKb:0.0} KB");
        if (issues.HasIssues)
        {
            Console.WriteLine($"         渲染告警 {issues.Issues.Count} 条：");
            foreach (var g in issues.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key))
                Console.WriteLine($"           {g.Key} ×{g.Count()}（如 {g.First().ElementId ?? "-"}）");
        }

        return 0;
    }

    static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"════ {title}");
    }
}

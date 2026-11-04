using System.Collections.Generic;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Samples.Avalonia;

/// <summary>
/// 模板库行业扩容（partial，宿主主文件见 TemplateLibraryDialog.cs）：
/// 财税金融 / 教育答题 / 医疗健康 / 餐饮零售 / 物流仓储 / 人事行政 / 工业制造，首批 17 个。
/// 全部程序化构造、自带 DesignData（缩略图与预览出真实观感），并刻意使用新形状元素（shape）
/// 做票样边框/密码区/签名区，让扩容同时成为形状组件的示例集。
/// </summary>
internal static partial class TemplateLibraryDialog
{
    /// <summary>行业模板清单（顺序即"全部"页的展示顺序）。</summary>
    static IEnumerable<Entry> BuildIndustryEntries()
    {
        // 财税金融
        yield return new Entry("增值税发票（票样）", "财税金融", BuildInvoice,
            "240×140 票样：购销双方、明细表格、价税合计与红色密码区边框，形状元素打底。");
        yield return new Entry("收款收据（190×100）", "财税金融", BuildReceipt,
            "收款收据：今收到/交来、金额大写行与款项明细，适合两联复写纸打样。");
        yield return new Entry("报价单（A4）", "财税金融", BuildQuotation,
            "A4 报价单：客户信息、明细表格、条款说明与签章区。");
        yield return new Entry("对账单（A4）", "财税金融", BuildStatement,
            "A4 对账单：期初余额、流水明细（借/贷/余额）与期末汇总。");

        // 教育答题
        yield return new Entry("客观题答题卡（A4）", "教育答题", BuildAnswerSheet,
            "A4 答题卡：考号填涂区、条形码、选择题〔A〕〔B〕〔C〕〔D〕阵列与填空题横线。");
        yield return new Entry("试卷头（A4）", "教育答题", BuildExamHeader,
            "试卷首页：装订密封线、得分评卷人表格与考试信息栏。");
        yield return new Entry("课程表（A4）", "教育答题", BuildTimetable,
            "周一至周五课程网格表，节次 × 星期，适合班级张贴。");

        // 医疗健康
        yield return new Entry("处方笺（190×130）", "医疗健康", BuildPrescription,
            "处方笺：Rp 正文区、医师/药师签名栏，红色边框用形状元素绘制。");
        yield return new Entry("化验报告单（A4）", "医疗健康", BuildLabReport,
            "检验报告单：患者信息、项目/结果/单位/参考区间表格与检验审核签署。");

        // 餐饮零售
        yield return new Entry("58mm 购物小票", "餐饮零售", BuildShoppingReceipt,
            "58mm 连续纸小票：门店头、明细、合计与会员二维码。");
        yield return new Entry("58mm 排队小票", "餐饮零售", BuildQueueTicket,
            "排队叫号小票：大号排队号、门店信息与前方等待人数。");
        yield return new Entry("商品吊牌（50×80）", "餐饮零售", BuildHangTag,
            "吊牌：挂绳孔、品名价格、条码与洗涤说明位。");

        // 物流仓储
        yield return new Entry("货运托运单（80mm）", "物流仓储", BuildWaybill,
            "80mm 连续纸托运单：收发货人、货物明细与运费合计。");
        yield return new Entry("库位标签（60×40）", "物流仓储", BuildBinLabel,
            "仓储库位标签：大号库位号 + 条码，适合货架/托盘贴标。");

        // 人事行政
        yield return new Entry("工作证（86×54 横）", "人事行政", BuildWorkBadge,
            "胸卡工牌：照片占位框、姓名/部门/工号与公司抬头。");
        yield return new Entry("会议签到表（A4）", "人事行政", BuildSignInSheet,
            "会议签到表：会议信息头 + 签到网格（姓名/部门/时间/签名）。");

        // 工业制造
        yield return new Entry("设备巡检卡（100×70）", "工业制造", BuildInspectionCard,
            "设备巡检卡：设备编号条码、巡检项目勾选列与巡检人/日期。");

        // 第二批：食品包装 + 复杂面单/表单（见 Catalog2 partial）
        foreach (var entry in BuildMoreEntries())
            yield return entry;

        // 第三批：交通/医疗/校园/政务人事/财务票证（见 Catalog3 partial）
        foreach (var entry in BuildThirdEntries())
            yield return entry;
    }

    // ── 行业数据 ────────────────────────────────────────────────────────────

    /// <summary>行业模板共用演示字段（缩略图/预览用，套用后用户改 DesignData 即可换肤）。</summary>
    static Dictionary<string, string> IndustryData { get; } = new()
    {
        ["company"] = "宏达实业有限公司",
        ["invoiceNo"] = "044031800111",
        ["invoiceCode"] = "3300 2411 8890",
        ["date"] = "2026-11-04",
        ["name"] = "张小明",
        ["phone"] = "138****6688",
        ["dept"] = "生产一部",
        ["staffNo"] = "GH-0326",
        ["addr"] = "上海市浦东新区博云路 2 号",
        ["amount"] = "320.00",
        ["price"] = "128.00",
        ["qty"] = "2",
        ["barcode"] = "6901234567892",
        ["orderNo"] = "ORD-20261104-0068",
    };

    // ── 助手 ────────────────────────────────────────────────────────────────

    static ShapeElement Shape(ShapeKind kind, double x, double y, double w, double h,
        SKColor? stroke = null, SKColor? fill = null, double strokeWidth = 0.3, double corner = 0) =>
        new()
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Shape = new PolygonOptions
            {
                Kind = kind,
                StrokeColor = stroke,
                FillColor = fill,
                StrokeWidthMm = strokeWidth,
                CornerRadiusMm = corner,
            },
        };

    static TableElement Table(double x, double y, double w, double h, double pt,
        (string title, int width, string field, HorizontalAlign align)[] cols,
        List<List<string>>? rows = null)
    {
        var table = new TableElement
        {
            XMm = x,
            YMm = y,
            WidthMm = w,
            HeightMm = h,
            Table = new TableOptions
            {
                FontSizePt = pt,
                Columns = new List<TableColumn>(
                    System.Linq.Enumerable.Select(cols, c => new TableColumn
                    {
                        Title = c.title,
                        WidthMm = c.width,
                        Field = c.field,
                        Align = c.align,
                    })),
                Rows = rows ?? new List<List<string>>(),
            },
        };
        return table;
    }

    static PrintTemplate NewTemplate(double wMm, double hMm) => new()
    {
        Paper = new PaperSpec { WidthMm = wMm, HeightMm = hMm },
        DesignData = new(IndustryData),
    };

    /// <summary>标题下加"某某某：____　某某某：____"两段信息行；bold=true 用于金额合计等强调行。</summary>
    static TextElement InfoLine(string content, double x, double y, double w, double pt = 9, bool bold = false) =>
        Text(content, x, y, w, 6, pt, bold: bold);

    // ── 财税金融 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildInvoice()
    {
        var t = NewTemplate(240, 140);

        Add(t, Shape(ShapeKind.RoundRect, 2, 2, 236, 136, stroke: new SKColor(0x2F, 0x6F, 0xED), strokeWidth: 0.4, corner: 2));
        Add(t, Text("增值税专用发票（票样）", 70, 5, 100, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("发票代码：{{invoiceCode}}", 170, 5, 64, 5, 7.5));
        Add(t, Text("发票号码：{{invoiceNo}}", 170, 11, 64, 5, 7.5));
        Add(t, Text("购买方：{{company}}　纳税人识别号：91310115MA1K35***", 8, 16, 200, 6, 8));
        Add(t, Shape(ShapeKind.Plus, 8, 24, 6, 6, stroke: SKColors.DarkRed, strokeWidth: 0.35));
        Add(t, Text("密码区（机器编码：51 09 22 66 88 03 12 47）", 17, 25, 150, 5, 7));
        Add(t, Table(8, 33, 224, 46, 8,
            new[]
            {
                ("货物或应税劳务、服务名称", 84, "items.item", HorizontalAlign.Left),
                ("规格型号", 30, "items.spec", HorizontalAlign.Left),
                ("单位", 18, "items.unit", HorizontalAlign.Center),
                ("数量", 22, "items.qty", HorizontalAlign.Right),
                ("单价", 30, "items.price", HorizontalAlign.Right),
                ("金额", 40, "items.amount", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "不锈钢托盘 600×400", "SUS304", "只", "4", "45.00", "180.00" },
                new() { "电子称重仪 TCS-150", "150kg/10g", "台", "1", "140.00", "140.00" },
            }));
        Add(t, Text("价税合计（大写）　叁佰贰拾元整　　　（小写）￥320.00", 8, 84, 224, 7, 9, bold: true));
        Add(t, Text("销售方：{{company}}　纳税人识别号：91310115MA1K35***", 8, 96, 200, 6, 8));
        Add(t, Text("收款人：王会计　　复核：李明　　开票人：{{name}}", 8, 104, 200, 6, 8));
        Add(t, Text("销售方（章）", 170, 116, 60, 6, 8));
        Add(t, Text("开票日期：{{date}}", 170, 124, 64, 5, 7.5, align: HorizontalAlign.Right));
        return t;
    }

    static PrintTemplate BuildReceipt()
    {
        var t = NewTemplate(190, 100);
        t.DesignData = new(IndustryData);

        Add(t, Text("收 款 收 据", 55, 4, 80, 9, 15, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("NO. {{orderNo}}", 130, 6, 56, 5, 8, align: HorizontalAlign.Right));
        Add(t, Shape(ShapeKind.RoundRect, 6, 16, 178, 74, stroke: new SKColor(0x2F, 0x6F, 0xED), strokeWidth: 0.35, corner: 1.5));
        Add(t, InfoLine("今收到：{{company}}", 12, 21, 166, 9));
        Add(t, InfoLine("交　来：货款（订单 {{orderNo}}）", 12, 31, 166, 9));
        Add(t, InfoLine("人民币（大写）：叁佰贰拾元整　　（小写）￥{{amount}}", 12, 41, 166, 9));
        Add(t, Table(12, 51, 166, 22, 8,
            new[]
            {
                ("款项内容", 66, "items.item", HorizontalAlign.Left),
                ("数量", 26, "items.qty", HorizontalAlign.Center),
                ("金额（元）", 40, "items.amount", HorizontalAlign.Right),
                ("备注", 34, "items.spec", HorizontalAlign.Left),
            }));
        Add(t, InfoLine("收款单位（盖章有效）", 12, 78, 90, 8));
        Add(t, InfoLine("收款人：王会计　　　经手人：{{name}}", 96, 78, 82, 8));
        return t;
    }

    static PrintTemplate BuildQuotation()
    {
        var t = NewTemplate(210, 297);

        Add(t, Text("产品报价单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("报价单号：{{orderNo}}", 8, 26, 100, 6, 9));
        Add(t, Text("报价日期：{{date}}", 110, 26, 92, 6, 9, align: HorizontalAlign.Right));
        Add(t, Line(8, 34, 194));
        Add(t, Text("致：{{company}}（采购部）", 8, 38, 194, 6, 9.5, bold: true));
        Add(t, InfoLine("联系人：{{name}}　　电话：{{phone}}　　地址：{{addr}}", 8, 46, 194));
        Add(t, Text("一、报价明细", 8, 56, 80, 6, 10, bold: true));
        Add(t, Table(8, 64, 194, 60, 9,
            new[]
            {
                ("序号", 16, "", HorizontalAlign.Center),
                ("品名规格", 70, "items.item", HorizontalAlign.Left),
                ("数量", 22, "items.qty", HorizontalAlign.Center),
                ("单位", 20, "items.unit", HorizontalAlign.Center),
                ("单价（元）", 30, "items.price", HorizontalAlign.Right),
                ("金额（元）", 36, "items.amount", HorizontalAlign.Right),
            }));
        Add(t, Text("二、商务条款", 8, 130, 80, 6, 10, bold: true));
        Add(t, Text("1. 报价有效期 30 天；2. 付款方式：款到发货；3. 含税 13% 增值税；4. 交货周期：款到后 7 个工作日。",
            8, 138, 194, 12, 9));
        Add(t, Text("三、签章区", 8, 154, 80, 6, 10, bold: true));
        Add(t, Shape(ShapeKind.RoundRect, 8, 162, 92, 50, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("供方（盖章）：", 12, 166, 80, 6, 9));
        Add(t, Text("经办人 / 日期：", 12, 202, 80, 6, 9));
        Add(t, Shape(ShapeKind.RoundRect, 110, 162, 92, 50, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("需方（盖章）：", 114, 166, 80, 6, 9));
        Add(t, Text("经办人 / 日期：", 114, 202, 80, 6, 9));
        return t;
    }

    static PrintTemplate BuildStatement()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        Add(t, Text("往来对账单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("客户名称：{{company}}", 8, 27, 120, 6, 9.5));
        Add(t, Text("对账期间：2026-10-01 至 2026-10-31", 8, 35, 120, 6, 9.5));
        Add(t, Text("制单日期：{{date}}", 118, 27, 84, 6, 9.5, align: HorizontalAlign.Right));
        Add(t, Text("期初余额：￥5,820.00", 118, 35, 84, 6, 9.5, align: HorizontalAlign.Right));
        Add(t, Line(8, 44, 194));
        Add(t, Table(8, 48, 194, 92, 9,
            new[]
            {
                ("日期", 26, "items.date", HorizontalAlign.Center),
                ("单据号", 40, "items.no", HorizontalAlign.Left),
                ("摘要", 52, "items.item", HorizontalAlign.Left),
                ("借方（元）", 30, "items.debit", HorizontalAlign.Right),
                ("贷方（元）", 30, "items.credit", HorizontalAlign.Right),
                ("余额（元）", 34, "items.balance", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "10-06", "XS-1021", "销售出库一批", "640.00", "", "6,460.00" },
                new() { "10-12", "SK-0917", "收到货款", "", "2,000.00", "4,460.00" },
                new() { "10-25", "XS-1088", "销售出库一批", "1,360.00", "", "5,820.00" },
            }));
        Add(t, Text("本期合计：借方 ￥2,000.00　贷方 ￥2,000.00　　期末余额：￥5,820.00", 8, 146, 194, 7, 10, bold: true));
        Add(t, InfoLine("以上数据请贵司核对，如有异议请在 5 个工作日内书面回复。", 8, 160, 194));
        Add(t, Text("供方（章）：____________　　需方确认（章）：____________", 8, 176, 194, 6, 9.5));
        Add(t, Text("制单人：{{name}}　　日期：{{date}}", 8, 190, 194, 6, 9, align: HorizontalAlign.Right));
        return t;
    }

    // ── 教育答题 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildAnswerSheet()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        Add(t, Text("客观题答题卡", 55, 8, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Barcode("{{orderNo}}", 150, 6, 52, 14, "orderNo"));
        Add(t, Shape(ShapeKind.RoundRect, 8, 22, 194, 26, stroke: SKColors.Black, strokeWidth: 0.3, corner: 1));
        Add(t, Text("姓名：{{name}}　　班级：生产一部　　考号：[0][5][1][2][3][6]", 14, 26, 180, 6, 10, bold: true));
        Add(t, Text("正确填涂 ■　　缺考标记 □　　保持卡面清洁，禁止折叠", 14, 38, 180, 6, 8.5));
        Add(t, Text("注意事项：1. 用 2B 铅笔填涂；2. 修改用橡皮擦干净；3. 保持条形码完整。", 8, 52, 194, 6, 8.5));
        Add(t, Line(8, 60, 194));

        for (var q = 1; q <= 12; q++)
        {
            var y = 66 + (q - 1) * 7.2;
            Add(t, Text($"{q,2}.", 10, y, 8, 5.5, 9));
            Add(t, Text("〔A〕〔B〕〔C〕〔D〕", 20, y, 60, 5.5, 9));
            if (q >= 7)
            {
                Add(t, Text($"{q,2}.", 110, y, 8, 5.5, 9));
                Add(t, Text("〔A〕〔B〕〔C〕〔D〕", 120, y, 60, 5.5, 9));
            }
        }

        Add(t, Text("二、填空题（请写在横线上）", 8, 156, 100, 6, 10, bold: true));
        for (var i = 0; i < 4; i++)
        {
            var y = 164 + i * 10;
            Add(t, Text($"{i + 1}.", 10, y, 8, 6, 9.5));
            Add(t, Line(20, y + 6, 176));
        }

        Add(t, Text("三、判断题（正确涂 ■，错误涂 □）", 8, 208, 140, 6, 10, bold: true));
        for (var q = 13; q <= 16; q++)
        {
            var y = 216 + (q - 13) * 7.2;
            Add(t, Text($"{q,2}.", 10, y, 8, 5.5, 9));
            Add(t, Text("〔对〕〔错〕", 20, y, 40, 5.5, 9));
        }
        return t;
    }

    static PrintTemplate BuildExamHeader()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        // 左侧装订密封线（虚线 + 竖排提示）
        Add(t, Shape(ShapeKind.RoundRect, 6, 10, 0.25, 260, stroke: SKColors.Gray, strokeWidth: 0.1));
        Add(t, Text("密封线内不得答题　姓名：______　班级：______　考号：______", 14, 130, 90, 5, 7));
        Add(t, Text("2026 学年第一学期期末考试（A 卷）", 40, 12, 150, 9, 14, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("科目：综合业务　　考试时间：120 分钟　　满分：100 分", 40, 24, 150, 6, 10, align: HorizontalAlign.Center));
        Add(t, Table(28, 36, 160, 16, 9,
            new[]
            {
                ("题号", 20, "", HorizontalAlign.Center),
                ("一", 35, "", HorizontalAlign.Center),
                ("二", 35, "", HorizontalAlign.Center),
                ("三", 35, "", HorizontalAlign.Center),
                ("总分", 35, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "得分", "", "", "", "" },
                new() { "评卷人", "", "", "", "" },
            }));
        Add(t, Text("注意事项：1. 答题前填写密封线内信息；2. 答案写在指定区域；3. 保持卷面整洁。",
            28, 58, 160, 10, 9));
        Add(t, Line(28, 72, 160));
        Add(t, Text("一、单项选择题（每题 2 分，共 20 分）", 28, 78, 140, 6, 10, bold: true));
        for (var q = 1; q <= 5; q++)
        {
            var y = 88 + (q - 1) * 8;
            Add(t, Text($"{q}. 本题题干预留位置（套用后替换为真实题干）", 28, y, 120, 6, 9.5));
            Add(t, Text("A. ______　　B. ______　　C. ______　　D. ______", 34, y + 3.2, 150, 5, 9));
        }
        return t;
    }

    static PrintTemplate BuildTimetable()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        Add(t, Text("班级课程表", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("班级：生产一部　　班主任：{{name}}　　学期：2026 春", 8, 27, 194, 6, 9.5, align: HorizontalAlign.Center));
        Add(t, Table(10, 38, 190, 150, 9.5,
            new[]
            {
                ("节次", 20, "", HorizontalAlign.Center),
                ("星期一", 34, "", HorizontalAlign.Center),
                ("星期二", 34, "", HorizontalAlign.Center),
                ("星期三", 34, "", HorizontalAlign.Center),
                ("星期四", 34, "", HorizontalAlign.Center),
                ("星期五", 34, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "1", "数学", "语文", "英语", "物理", "化学" },
                new() { "2", "语文", "数学", "物理", "英语", "数学" },
                new() { "3", "英语", "化学", "数学", "语文", "英语" },
                new() { "4", "体育", "物理", "化学", "数学", "语文" },
                new() { "午休", "午休", "午休", "午休", "午休", "午休" },
                new() { "5", "专业课", "制图", "实训", "专业课", "班会" },
                new() { "6", "实训", "专业课", "制图", "实训", "自习" },
                new() { "7", "自习", "实训", "自习", "文体", "自习" },
            }));
        Add(t, InfoLine("注：实训课请提前 10 分钟到车间集合；自习课保持安静。", 10, 196, 190));
        Add(t, Text("教务处制　　{{date}}", 10, 210, 190, 6, 9, align: HorizontalAlign.Right));
        return t;
    }

    // ── 医疗健康 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildPrescription()
    {
        var t = NewTemplate(190, 130);
        t.DesignData = new(IndustryData);

        Add(t, Shape(ShapeKind.RoundRect, 3, 3, 184, 124, stroke: new SKColor(0xC0, 0x00, 0x00), strokeWidth: 0.45, corner: 1.5));
        Add(t, Text("处方笺", 65, 6, 60, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("编号：{{orderNo}}", 130, 7, 52, 5, 8, align: HorizontalAlign.Right));
        Add(t, InfoLine("姓名：{{name}}　　性别：男　　年龄：34 岁　　科室：内科", 10, 17, 170, 9));
        Add(t, InfoLine("过敏史：无　　　　　　　　　日期：{{date}}", 10, 27, 170, 9));
        Add(t, Line(10, 36, 170));
        Add(t, Text("Rp:", 10, 39, 20, 6, 11, bold: true));
        Add(t, Text("1) 阿莫西林胶囊 0.25g×24 粒×2 盒　用法：口服 每次 0.5g 每日三次", 16, 47, 164, 6, 9));
        Add(t, Text("2) 布洛芬缓释胶囊 0.3g×20 粒×1 盒　用法：口服 每次 0.3g 每日两次", 16, 56, 164, 6, 9));
        Add(t, Line(10, 68, 170));
        Add(t, InfoLine("医师签名：____________　　　审核/调配签名：____________", 10, 74, 170, 9));
        Add(t, InfoLine("核对/发药签名：____________　　　费用：￥{{amount}}", 10, 84, 170, 9));
        Add(t, Text("本处方供样例演示，药品与剂量请勿参照使用。", 10, 100, 170, 5, 7));
        Add(t, Shape(ShapeKind.Diamond, 160, 96, 18, 18, stroke: new SKColor(0xC0, 0x00, 0x00), strokeWidth: 0.3));
        return t;
    }

    static PrintTemplate BuildLabReport()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        Add(t, Text("检验报告单", 55, 10, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("标本号：{{orderNo}}", 130, 8, 72, 6, 9, align: HorizontalAlign.Right));
        Add(t, InfoLine("患者姓名：{{name}}　性别：男　年龄：34　科室：内科　床号：12", 8, 26, 194, 9.5));
        Add(t, InfoLine("标本类型：静脉血　　申请医生：李医生　　采样时间：{{date}} 08:30", 8, 34, 194, 9.5));
        Add(t, Line(8, 44, 194));
        Add(t, Table(8, 48, 194, 100, 9.5,
            new[]
            {
                ("序号", 16, "", HorizontalAlign.Center),
                ("项目", 60, "items.item", HorizontalAlign.Left),
                ("结果", 34, "items.result", HorizontalAlign.Center),
                ("单位", 34, "items.unit", HorizontalAlign.Center),
                ("参考区间", 50, "items.range", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "1", "白细胞计数 WBC", "6.8", "10^9/L", "3.5 - 9.5" },
                new() { "2", "红细胞计数 RBC", "4.9", "10^12/L", "4.3 - 5.8" },
                new() { "3", "血红蛋白 HGB", "148", "g/L", "130 - 175" },
                new() { "4", "血小板计数 PLT", "212", "10^9/L", "125 - 350" },
                new() { "5", "空腹血糖 GLU", "5.3", "mmol/L", "3.9 - 6.1" },
            }));
        Add(t, Text("检验结论：所检项目均在参考区间内。", 8, 152, 194, 6, 10, bold: true));
        Add(t, InfoLine("检验者：王化验　　审核者：赵主任　　报告时间：{{date}} 10:20", 8, 168, 194));
        Add(t, Text("本报告仅对本次标本负责，结果判定请结合临床。", 8, 182, 194, 5, 7.5));
        return t;
    }

    // ── 餐饮零售 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildShoppingReceipt()
    {
        var t = NewTemplate(58, 0);
        t.DesignData = new(IndustryData);

        Add(t, Text("鲜丰生活超市", 4, 3, 50, 6, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("机打小票 · {{date}}", 4, 10, 50, 4.5, 7, align: HorizontalAlign.Center));
        Add(t, Text("单号：{{orderNo}}", 4, 15.5, 50, 4.5, 7));
        Add(t, Line(3, 21, 52));
        Add(t, Text("品名　　　 数量　单价　金额", 4, 23, 50, 4.5, 7.5, bold: true));
        Add(t, Text("阳光玫瑰葡萄 1.2kg 39.8 47.76", 4, 28.5, 50, 4.5, 7.5));
        Add(t, Text("有机牛奶 250ml×12 1 32.0 32.00", 4, 34, 50, 4.5, 7.5));
        Add(t, Text("全麦吐司 400g 2 9.9 19.80", 4, 39.5, 50, 4.5, 7.5));
        Add(t, Line(3, 45.5, 52));
        Add(t, Text("件数：4.2　　合计：￥99.56", 4, 48, 50, 5, 9, bold: true));
        Add(t, Text("会员优惠：-5.00　应付：￥94.56", 4, 54, 50, 5, 8.5));
        Add(t, Text("微信支付：￥94.56", 4, 60, 50, 4.5, 7.5));
        Add(t, Line(3, 66, 52));
        Add(t, Qr("{{orderNo}}", 19, 68, 20, 20, "orderNo"));
        Add(t, Text("扫码开电子发票", 4, 89, 50, 4.5, 7, align: HorizontalAlign.Center));
        Add(t, Text("谢谢惠顾，欢迎再次光临", 4, 94.5, 50, 4.5, 8, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildQueueTicket()
    {
        var t = NewTemplate(58, 0);
        t.DesignData = new(IndustryData);

        Add(t, Text("鲜丰生活超市", 4, 3, 50, 6, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{date}}", 4, 10, 50, 4.5, 7, align: HorizontalAlign.Center));
        Add(t, Text("排队号", 4, 16, 50, 5, 8, align: HorizontalAlign.Center));
        Add(t, Text("A-016", 4, 22, 50, 14, 24, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("前方等待：8 组", 4, 38, 50, 5, 9, align: HorizontalAlign.Center));
        Add(t, Text("请留意叫号屏与语音提示", 4, 44, 50, 4.5, 7.5, align: HorizontalAlign.Center));
        Add(t, Qr("{{orderNo}}", 19, 50, 20, 20, "orderNo"));
        Add(t, Text("扫码查看实时进度", 4, 71, 50, 4.5, 7, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildHangTag()
    {
        var t = NewTemplate(50, 80);
        t.DesignData = new(IndustryData);

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 78, stroke: SKColors.Black, strokeWidth: 0.25, corner: 2));
        Add(t, new EllipseElement { XMm = 21, YMm = 2.5, WidthMm = 8, HeightMm = 8 }); // 挂绳孔
        Add(t, Text("URBAN BASICS", 4, 12, 42, 5, 8, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("纯棉圆领卫衣", 4, 18, 42, 5.5, 10, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("颜色：燕麦色　尺码：L", 4, 26, 42, 4.5, 7.5, align: HorizontalAlign.Center));
        Add(t, Line(5, 33, 40));
        Add(t, Text("零售价", 4, 36, 20, 4.5, 8));
        Add(t, Text("￥{{price}}", 4, 41, 24, 8, 14, bold: true));
        Add(t, Barcode("{{barcode}}", 4, 51, 42, 12, "barcode"));
        Add(t, Text("执行标准：GB/T 22849-2014　等级：一等品", 4, 65, 42, 4, 6));
        Add(t, Text("面料：80%棉 20%聚酯纤维　洗涤：见水洗标", 4, 69.5, 42, 4, 6));
        Add(t, Text("检验员：{{staffNo}}", 4, 74, 42, 4, 6));
        return t;
    }

    // ── 物流仓储 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildWaybill()
    {
        var t = NewTemplate(80, 0);
        t.DesignData = new(IndustryData);

        Add(t, Text("货物托运单", 10, 3, 60, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("单号：{{orderNo}}", 4, 12, 72, 4.5, 7.5));
        Add(t, Line(3, 18, 74));
        Add(t, InfoLine("发货人：{{name}}　{{phone}}", 4, 21, 72, 8.5));
        Add(t, InfoLine("发货地：{{addr}}", 4, 28, 72, 8.5));
        Add(t, InfoLine("收货人：李红　139****2233", 4, 35, 72, 8.5));
        Add(t, InfoLine("收货地：杭州市余杭区文一西路 998 号", 4, 42, 72, 8.5));
        Add(t, Line(3, 49, 74));
        Add(t, Table(4, 52, 72, 34, 7.5,
            new[]
            {
                ("货物名称", 26, "items.item", HorizontalAlign.Left),
                ("包装", 12, "items.spec", HorizontalAlign.Center),
                ("件数", 12, "items.qty", HorizontalAlign.Center),
                ("重量(kg)", 22, "", HorizontalAlign.Right),
            }));
        Add(t, InfoLine("运费：￥{{amount}}　付款方式：到付", 4, 90, 72, 8.5));
        Add(t, InfoLine("托运人签字：______　承运人签字：______", 4, 98, 72, 8.5));
        Add(t, Text("{{date}}", 4, 108, 72, 4.5, 7, align: HorizontalAlign.Right));
        return t;
    }

    static PrintTemplate BuildBinLabel()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(IndustryData);

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: SKColors.Black, strokeWidth: 0.3, corner: 1.5));
        Add(t, Text("库位", 4, 3.5, 14, 5, 8));
        Add(t, Text("A-12-03", 4, 8.5, 52, 10, 17, bold: true));
        Add(t, Barcode("6901234567892", 4, 20, 52, 10, "barcode"));
        Add(t, Text("货架区 A · 第 12 排 · 第 3 层", 4, 31.5, 52, 4.5, 7));
        Add(t, Text("重货区（≤50kg/箱）", 4, 36, 52, 4, 6));
        return t;
    }

    // ── 人事行政 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildWorkBadge()
    {
        var t = NewTemplate(86, 54);
        t.DesignData = new(IndustryData);

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 84, 52, stroke: new SKColor(0x2F, 0x6F, 0xED), strokeWidth: 0.35, corner: 3));
        // 标题条必须内缩到边框内且自带圆角，否则直角盖住卡片圆角、右缘溢出边框（真机截图实锤）。
        Add(t, Shape(ShapeKind.RoundRect, 2, 2, 81, 9, fill: new SKColor(0x2F, 0x6F, 0xED), stroke: null, corner: 2));
        Add(t, Text("{{company}}", 4, 3.5, 77, 6, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Shape(ShapeKind.RoundRect, 5, 15, 22, 28, stroke: SKColors.Gray, strokeWidth: 0.2, corner: 1));
        Add(t, Text("照片", 5, 26, 22, 5, 7, align: HorizontalAlign.Center));
        Add(t, Text("{{name}}", 32, 17, 50, 7, 13, bold: true));
        Add(t, Text("部门：{{dept}}", 32, 26, 50, 5, 8.5));
        Add(t, Text("职务：仓储主管", 32, 32, 50, 5, 8.5));
        Add(t, Text("工号：{{staffNo}}", 32, 38, 50, 5, 8.5));
        Add(t, Barcode("GH0326", 6, 45.5, 30, 6.5, "staffNo"));
        Add(t, Text("补办电话：021-6688****", 40, 46.5, 42, 4.5, 6));
        return t;
    }

    static PrintTemplate BuildSignInSheet()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(IndustryData);

        Add(t, Text("会议签到表", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("会议主题：四季度安全生产例会　　主持人：{{name}}", 8, 28, 194, 9.5));
        Add(t, InfoLine("时间：{{date}} 14:00　　地点：三楼会议室　　记录人：李明", 8, 36, 194, 9.5));
        Add(t, Line(8, 45, 194));
        Add(t, Table(8, 50, 194, 170, 9.5,
            new[]
            {
                ("序号", 16, "", HorizontalAlign.Center),
                ("姓名", 36, "", HorizontalAlign.Left),
                ("部门", 44, "", HorizontalAlign.Left),
                ("签到时间", 40, "", HorizontalAlign.Center),
                ("签名", 58, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "1", "", "", "", "" },
                new() { "2", "", "", "", "" },
                new() { "3", "", "", "", "" },
                new() { "4", "", "", "", "" },
                new() { "5", "", "", "", "" },
                new() { "6", "", "", "", "" },
                new() { "7", "", "", "", "" },
                new() { "8", "", "", "", "" },
            }));
        Add(t, InfoLine("应到：__ 人　实到：__ 人　缺席原因附后。", 8, 228, 194));
        return t;
    }

    // ── 工业制造 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildInspectionCard()
    {
        var t = NewTemplate(100, 70);
        t.DesignData = new(IndustryData);

        Add(t, Text("设备巡检卡", 24, 3, 52, 6.5, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("设备：{{company}} 3 号线", 4, 11, 66, 4.5, 7.5));
        Add(t, Barcode("EQP-L3-026", 62, 2.5, 36, 9, "orderNo"));
        Add(t, Line(3, 17, 94));
        for (var i = 0; i < 5; i++)
        {
            var y = 19.5 + i * 6.4;
            Add(t, Shape(ShapeKind.RoundRect, 4, y, 3.6, 3.6, stroke: SKColors.Black, strokeWidth: 0.25, corner: 0.5));
            Add(t, Text((new[] { "运行声音无异响", "油位在标线内", "皮带无松动破损", "急停按钮有效", "防护罩齐全" })[i], 10.5, y - 0.4, 52, 4.5, 7.5));
            Add(t, Text("□正常 □异常", 64, y - 0.4, 34, 4.5, 7));
        }
        Add(t, Line(3, 52, 94));
        Add(t, InfoLine("巡检人：{{name}}　日期：{{date}}　班次：早班", 4, 54.5, 92, 8));
        Add(t, InfoLine("异常描述：____________________________", 4, 62.5, 92, 8));
        return t;
    }
}

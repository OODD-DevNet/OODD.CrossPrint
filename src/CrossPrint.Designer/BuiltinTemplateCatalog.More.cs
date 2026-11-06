using System.Collections.Generic;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 模板库第二批扩容（partial）：面向"单机软件开箱即用的模板大市场"——
/// 食品外包装/零售标签（含价签、酒标、药标、能效标、称重标、外卖封签等） +
/// 复杂表单（快递面单、发货单、银行回单、工资条、维修工单）。全部程序化构造、自带演示数据。
/// </summary>
public static partial class BuiltinTemplateCatalog
{
    /// <summary>第二批：食品包装 + 复杂面单/表单。</summary>
    static IEnumerable<Entry> BuildMoreEntries()
    {
        // 食品包装 / 零售标签
        yield return new Entry("咖啡豆标签（50×80）", "食品包装", BuildCoffeeBean,
            "精品咖啡豆：风味描述、处理法、烘焙日期与条码，适合挂耳/豆袋。");
        yield return new Entry("茶叶标签（50×80）", "食品包装", BuildTeaLabel,
            "茗茶标签：等级/产地/净含量与冲泡建议。");
        yield return new Entry("红酒酒标（60×90）", "食品包装", BuildWineLabel,
            "红酒正标：酒庄名、年份、产区与酒精度/净含量。");
        yield return new Entry("月饼标签（50×80）", "食品包装", BuildMooncake,
            "月饼品名签：口味、净含量、配料与生产日期提示。");
        yield return new Entry("面包烘焙标签（60×40）", "食品包装", BuildBakery,
            "现烤面包标签：品名、配料与当日日期。");
        yield return new Entry("生鲜称重标签（60×40）", "食品包装", BuildFreshWeigh,
            "生鲜称重：品名/规格/单价/合计 + 二维码溯源 + 条码。");
        yield return new Entry("散装称重标（60×40）", "食品包装", BuildBulkWeigh,
            "散装大米称重签：大字总价 + 重量单价 + 条码。");
        yield return new Entry("咖啡杯贴（50×30）", "食品包装", BuildCupSticker,
            "外带杯贴：杯型/品名/冷热与糖度冰度。");
        yield return new Entry("外卖封签贴（60×40）", "食品包装", BuildDeliverySeal,
            "外卖封签：取餐号、日期与「撕毁无效」提示 + 二维码。");
        yield return new Entry("饮料价签（60×40）", "食品包装", BuildDrinkPrice,
            "货架价签：大字价格 + 品名规格。");
        yield return new Entry("促销爆炸贴（50×30）", "食品包装", BuildPromo,
            "促销贴：大字特价 + 划线原价。");
        yield return new Entry("商品标价签（橙）（100×35）", "食品包装", () => BuildShelfPrice("橙", new SKColor(0xFF, 0x8C, 0x00)),
            "商超货架标价签（橙）：货号/品名/条码 + 黄底价格块。");
        yield return new Entry("商品标价签（绿）（100×35）", "食品包装", () => BuildShelfPrice("绿", new SKColor(0x2E, 0x8B, 0x57)),
            "商超货架标价签（绿）：同款绿色边框版。");

        // 标签卡券（补充）
        yield return new Entry("药品标签（60×40）", "标签卡券", BuildDrugLabel,
            "OTC 药品标签：红头、品名规格、用法用量与价格。");
        yield return new Entry("图书标签（60×40）", "标签卡券", BuildBookLabel,
            "书店/图书馆标签：书名、作者、索书号与条码。");
        yield return new Entry("家电能效标（50×80）", "标签卡券", BuildEnergyLabel,
            "中国能效标识：1-5 级色阶、功率与容量信息。");
        yield return new Entry("珠宝标签（25×30）", "标签卡券", BuildJewelry,
            "珠宝小签：材质/重量/价格 + 条码。");
        yield return new Entry("礼品卡（86×54）", "标签卡券", BuildGiftCard,
            "礼品卡：面值大字、卡号、有效期与条码。");

        // 物流仓储（复杂面单）
        yield return new Entry("顺丰风格快递面单（100×150）", "物流仓储", BuildSfWaybill,
            "三段式面单：头部条码区 + 收派信息格 + 物品栏，含签收二维码。");
        yield return new Entry("京东风格快递面单（100×150）", "物流仓储", BuildJdWaybill,
            "深色头部条面单：运单大字条码 + 分栏收寄信息 + 声明。");
        yield return new Entry("圆通风格快递面单（100×150）", "物流仓储", BuildYtoWaybill,
            "蓝头面单：大头笔/三段码区 + 格状收寄信息。");
        yield return new Entry("外卖订单小票（80mm）", "物流仓储", BuildTakeoutReceipt,
            "外卖订单：配送信息、菜品明细与打包费合计。");

        // 报表单据（复杂表单）
        yield return new Entry("销售发货单（A4）", "报表单据", BuildDeliveryNote,
            "A4 发货单：收货信息、多列明细表与三方签字栏。");
        yield return new Entry("银行电子回单（A4）", "报表单据", BuildBankReceipt,
            "转账回单：回单编号、收付款双方、金额大写与附言。");
        yield return new Entry("工资条（190×90）", "报表单据", BuildPayslip,
            "员工工资条：应发/社保/个税/实发明细表 + 签收。");
        yield return new Entry("维修工单（A4）", "报表单据", BuildRepairOrder,
            "维修工单：客户设备信息、故障描述、维修项目费用表与签字。");
    }

    // ── 第二批共用数据 ──────────────────────────────────────────────────────

    /// <summary>在共用演示字段上追加本模板私有字段。</summary>
    static Dictionary<string, string> Extra(params (string key, string value)[] items)
    {
        var data = new Dictionary<string, string>(IndustryData);
        foreach (var (key, value) in items)
            data[key] = value;
        return data;
    }

    // ── 食品包装 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildCoffeeBean()
    {
        var t = NewTemplate(50, 80);
        t.DesignData = new(Extra(
            ("goods", "手冲咖啡豆"), ("origin", "云南 保山"), ("flavor", "莓果 / 红糖 / 坚果"),
            ("process", "水洗 G1"), ("roast", "中浅烘焙"), ("roastDate", "2026-11-01"),
            ("net", "250g"), ("brand", "云岭庄园")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 78, stroke: new SKColor(0x6B, 0x3F, 0x1D), strokeWidth: 0.3, corner: 2));
        Add(t, Text("云岭庄园", 4, 5, 42, 5, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("SPECIALTY COFFEE", 4, 10.5, 42, 3.5, 6, align: HorizontalAlign.Center));
        Add(t, Line(5, 15.5, 40));
        Add(t, Text("手冲咖啡豆", 4, 17.5, 42, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("云南 保山 · 海拔 1600m", 4, 26.5, 42, 4.5, 7.5, align: HorizontalAlign.Center));
        Add(t, Text("风味：莓果 / 红糖 / 坚果", 4, 32, 42, 4.5, 7.5));
        Add(t, Text("处理法：水洗 G1", 4, 37, 42, 4.5, 7.5));
        Add(t, Text("烘焙度：中浅烘焙", 4, 42, 42, 4.5, 7.5));
        Add(t, Shape(ShapeKind.RoundRect, 4, 49, 42, 10, stroke: new SKColor(0x6B, 0x3F, 0x1D), strokeWidth: 0.2, corner: 1));
        Add(t, Text("建议养豆 5-14 天", 6, 50.5, 38, 3.5, 6.5));
        Add(t, Text("研磨后请在两周内饮用完", 6, 54.5, 38, 3.5, 6.5));
        Add(t, Barcode("{{barcode}}", 6, 61.5, 38, 9, "barcode"));
        Add(t, Text("净含量：{{net}}　　烘焙日期：{{roastDate}}", 4, 72, 42, 3.5, 6));
        return t;
    }

    static PrintTemplate BuildTeaLabel()
    {
        var t = NewTemplate(50, 80);
        t.DesignData = new(Extra(
            ("goods", "明前龙井"), ("grade", "特级"), ("origin", "杭州 狮峰"),
            ("net", "100g"), ("batch", "20260405"), ("brand", "梅家坞")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 78, stroke: new SKColor(0x2E, 0x6B, 0x3F), strokeWidth: 0.3, corner: 2));
        Add(t, Text("梅家坞", 4, 5, 42, 5, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("MEIJIAWU TEA", 4, 10.5, 42, 3.5, 6, align: HorizontalAlign.Center));
        Add(t, Line(5, 15.5, 40));
        Add(t, Text("明前龙井", 4, 18, 42, 9, 15, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("等级：特级", 4, 29, 42, 5, 8.5, align: HorizontalAlign.Center));
        Add(t, Text("产地：杭州 狮峰山", 4, 35, 42, 5, 8.5));
        Add(t, Text("净含量：{{net}}", 4, 41, 42, 5, 8.5));
        Add(t, Text("批次：{{batch}}", 4, 47, 42, 5, 8.5));
        Add(t, Shape(ShapeKind.RoundRect, 4, 54, 42, 14, stroke: new SKColor(0x2E, 0x6B, 0x3F), strokeWidth: 0.2, corner: 1));
        Add(t, Text("冲泡建议：85℃ 水 3g 茶", 6, 56, 38, 4, 6.5));
        Add(t, Text("先水后茶，30 秒出汤", 6, 60.5, 38, 4, 6.5));
        Add(t, Text("避光密封冷藏保存", 6, 65, 38, 4, 6.5));
        Add(t, Barcode("{{barcode}}", 6, 70, 38, 8, "barcode"));
        return t;
    }

    static PrintTemplate BuildWineLabel()
    {
        var t = NewTemplate(60, 90);
        t.DesignData = new(Extra(
            ("goods", "赤霞珠干红"), ("vintage", "2019"), ("region", "宁夏贺兰山东麓"),
            ("alc", "13.5% vol"), ("net", "750ml"), ("brand", "贺兰晴雪"),
            ("winery", "LANSTON WINE")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 88, stroke: new SKColor(0x5C, 0x0A, 0x0A), strokeWidth: 0.35, corner: 1));
        Add(t, Text("{{brand}}", 5, 6, 50, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{winery}}", 5, 13.5, 50, 4, 6.5, align: HorizontalAlign.Center));
        Add(t, Line(8, 19.5, 44));
        Add(t, Text("{{vintage}}", 5, 23, 50, 14, 22, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("CABERNET SAUVIGNON", 5, 39, 50, 4, 6.5, align: HorizontalAlign.Center));
        Add(t, Text("赤霞珠干红葡萄酒", 5, 44, 50, 6, 10, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("EST. 2006", 5, 51, 50, 3.5, 6, align: HorizontalAlign.Center));
        Add(t, Line(8, 56.5, 44));
        Add(t, Text("产区：{{region}}", 5, 60, 50, 5, 8));
        Add(t, Text("酒精度：{{alc}}　　净含量：{{net}}", 5, 66, 50, 5, 8));
        Add(t, Text("原产地灌装 · 详见瓶背标", 5, 72, 50, 4, 6.5));
        Add(t, Barcode("{{barcode}}", 8, 78, 44, 8, "barcode"));
        return t;
    }

    static PrintTemplate BuildMooncake()
    {
        var t = NewTemplate(50, 80);
        t.DesignData = new(Extra(
            ("goods", "广式蛋黄莲蓉"), ("flavor2", "蛋黄莲蓉"), ("net", "125g（2 枚装）"),
            ("made", "2026-09-10"), ("expire", "2026-10-10"), ("brand", "杏花楼"),
            ("ingredients", "小麦粉、莲蓉、咸蛋黄、白砂糖、花生油")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 78, stroke: new SKColor(0xB8, 0x86, 0x0B), strokeWidth: 0.3, corner: 2));
        Add(t, Text("{{brand}}", 4, 5, 42, 5.5, 10, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("中秋礼饼", 4, 11.5, 42, 4, 7, align: HorizontalAlign.Center));
        Add(t, Line(5, 16.5, 40));
        Add(t, Text("{{flavor2}}", 4, 19, 42, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("净含量：{{net}}", 4, 27.5, 42, 4.5, 7.5));
        Add(t, Text("生产日期：{{made}}", 4, 32.5, 42, 4.5, 7.5));
        Add(t, Text("保质期至：{{expire}}", 4, 37.5, 42, 4.5, 7.5));
        Add(t, Shape(ShapeKind.RoundRect, 4, 44, 42, 16, stroke: new SKColor(0xB8, 0x86, 0x0B), strokeWidth: 0.2, corner: 1));
        Add(t, Text("配料：{{ingredients}}", 6, 45.5, 38, 12, 6));
        Add(t, Barcode("{{barcode}}", 6, 62.5, 38, 9, "barcode"));
        Add(t, Text("贮存：阴凉干燥处，开启后尽快食用", 4, 73.5, 42, 3.5, 6));
        return t;
    }

    static PrintTemplate BuildBakery()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "手作全麦吐司"), ("made", "2026-11-04"), ("net", "400g"), ("expire", "3 天")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: new SKColor(0xC8, 0x7C, 0x2B), strokeWidth: 0.3, corner: 1.5));
        Add(t, Text("当日现烤", 3.5, 3, 18, 4.5, 7, bold: true));
        Add(t, Text("{{goods}}", 3.5, 8, 40, 7, 11, bold: true));
        Add(t, Text("净含量：{{net}}　　保质期：{{expire}}", 3.5, 16.5, 53, 4.5, 7));
        Add(t, Text("配料：全麦粉、酵母、黄油、蜂蜜", 3.5, 21.5, 53, 4, 6.5));
        Add(t, Text("生产日期：{{made}}", 3.5, 26, 30, 4.5, 7.5));
        Add(t, Text("￥{{price}}", 38, 24, 20, 10, 14, bold: true, align: HorizontalAlign.Right));
        return t;
    }

    static PrintTemplate BuildFreshWeigh()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "有机小番茄"), ("spec", "500g/盒"), ("weight", "0.512kg"),
            ("unitPrice", "19.80"), ("total", "10.14"), ("shop", "悦客生鲜"), ("when", "2026-10-27 09:32")));

        Add(t, Text("{{shop}}", 2.5, 2, 40, 4.5, 8, bold: true));
        Add(t, Text("{{when}}", 40, 2.5, 18, 4, 6, align: HorizontalAlign.Right));
        Add(t, Line(2.5, 8, 55));
        Add(t, Text("品名：{{goods}}　　规格：{{spec}}", 2.5, 10, 55, 4.5, 7.5));
        Add(t, Text("重量：{{weight}}　　单价：￥{{unitPrice}}/kg", 2.5, 15, 55, 4.5, 7.5));
        Add(t, Text("合计", 2.5, 20.5, 12, 5.5, 8));
        Add(t, Text("￥{{total}}", 13, 19, 26, 8, 14, bold: true));
        Add(t, Qr("{{orderNo}}", 41, 18.5, 16, 16, "orderNo"));
        Add(t, Barcode("{{barcode}}", 2.5, 28.5, 38, 9, "barcode"));
        Add(t, Text("扫码溯源", 42, 35, 15, 3.5, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildBulkWeigh()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "东北五常大米"), ("weight", "2.350kg"), ("unitPrice", "6.80"),
            ("total", "15.98"), ("shelf", "A2-08")));

        Add(t, Text("{{goods}}", 2.5, 2, 42, 5.5, 9.5, bold: true));
        Add(t, Text("散装称重", 46, 2.5, 12, 4, 6.5, align: HorizontalAlign.Right));
        Add(t, Text("重量：{{weight}}　　单价：￥{{unitPrice}}/kg", 2.5, 8.5, 55, 4.5, 7.5));
        Add(t, Text("￥{{total}}", 2.5, 13, 32, 10, 17, bold: true));
        Add(t, Text("货架：{{shelf}}", 38, 14.5, 20, 4.5, 7, align: HorizontalAlign.Right));
        Add(t, Line(2.5, 24.5, 55));
        Add(t, Barcode("{{barcode}}", 2.5, 26.5, 55, 10, "barcode"));
        Add(t, Text("称重时间：{{date}}　　包装前请核对重量与小票", 2.5, 37, 55, 3.5, 6));
        return t;
    }

    static PrintTemplate BuildCupSticker()
    {
        var t = NewTemplate(50, 30);
        t.DesignData = new(Extra(("goods", "燕麦拿铁"), ("cup", "大杯"), ("temp", "少冰"), ("sugar", "五分糖"), ("seq", "L128")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 28, stroke: SKColors.Black, strokeWidth: 0.25, corner: 1.5));
        Add(t, Shape(ShapeKind.RoundRect, 2.5, 2.5, 15, 8, fill: new SKColor(0x8B, 0x5A, 0x2B), stroke: null, corner: 1));
        Add(t, Text("{{cup}}", 3.5, 3.5, 13, 6, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{seq}}", 36, 3, 11, 5, 10, bold: true, align: HorizontalAlign.Right));
        Add(t, Text("{{goods}}", 20, 4.5, 15, 6, 9.5, bold: true));
        Add(t, Text("{{temp}} / {{sugar}}", 3.5, 13, 30, 5, 8));
        Add(t, Text("{{date}} {{time}}", 3.5, 19, 30, 4, 6.5));
        Add(t, Text("门店自提 · {{shop}}", 3.5, 24, 40, 3.5, 6));
        return t;
    }

    static PrintTemplate BuildDeliverySeal()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("seq", "取餐号 A-036"), ("rider", "骑手 王师傅"), ("shop", "巷口烧腊")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: new SKColor(0xE6, 0x7E, 0x22), strokeWidth: 0.35, corner: 2));
        Add(t, Text("外卖封签 · 撕毁无效", 4, 3.5, 52, 5, 8.5, bold: true));
        Add(t, Text("{{shop}}", 4, 9.5, 30, 4.5, 7.5));
        Add(t, Text("{{date}}", 40, 9.5, 16, 4.5, 7, align: HorizontalAlign.Right));
        Add(t, Text("{{seq}}", 4, 15, 28, 10, 15, bold: true));
        Add(t, Qr("{{orderNo}}", 40, 13.5, 17, 17, "orderNo"));
        Add(t, Text("{{rider}}　已消毒封装", 4, 26.5, 34, 4.5, 7));
        Add(t, Text("收餐请核对封签完好", 4, 31.5, 34, 4, 6.5));
        return t;
    }

    static PrintTemplate BuildDrinkPrice()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "白桃味气泡水"), ("spec", "330ml / 罐"), ("bigPrice", "5.50")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: new SKColor(0x2F, 0x6F, 0xED), strokeWidth: 0.3, corner: 1.5));
        Add(t, Shape(ShapeKind.RoundRect, 2, 2, 28, 36, fill: new SKColor(0x2F, 0x6F, 0xED), stroke: null, corner: 1));
        Add(t, Text("促销价", 3, 4, 26, 5, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("￥", 4, 12, 8, 8, 12, bold: true));
        Add(t, Text("{{bigPrice}}", 11, 10, 19, 12, 19, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("元 / 罐", 4, 26, 26, 5, 9, align: HorizontalAlign.Center));
        Add(t, Text("{{goods}}", 32, 5, 26, 6, 9.5, bold: true));
        Add(t, Text("规格：{{spec}}", 32, 12.5, 26, 5, 7.5));
        Add(t, Barcode("{{barcode}}", 32, 19, 26, 10, "barcode"));
        Add(t, Text("{{shop}}", 32, 30.5, 26, 3.5, 6));
        return t;
    }

    static PrintTemplate BuildPromo()
    {
        var t = NewTemplate(50, 30);
        t.DesignData = new(Extra(("bigPrice", "9.90"), ("oldPrice", "原价 15.80"), ("goods", "鲜牛奶 950ml")));

        Add(t, Shape(ShapeKind.Star5, 1, 1, 10, 10, fill: new SKColor(0xE6, 0x00, 0x00), stroke: null));
        Add(t, Text("爆", 1, 3, 10, 7, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("限时特惠", 12, 2.5, 36, 6, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("￥{{bigPrice}}", 6, 9, 38, 12, 19, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{oldPrice}}", 6, 21.5, 38, 5, 8, align: HorizontalAlign.Center));
        Add(t, Text("{{goods}}", 6, 26.5, 38, 3.5, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildShelfPrice(string label, SKColor accent)
    {
        var t = NewTemplate(100, 35);
        t.DesignData = new(Extra(("goods", "香满园大米 5kg"), ("goodsNo", "6901234"), ("unit", "袋"), ("spec", "5kg/袋")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 98, 33, stroke: accent, strokeWidth: 0.35, corner: 1.5));
        Add(t, Text("商品标价签", 3, 2, 22, 4.5, 7.5, bold: true));
        Add(t, Text("货号：{{goodsNo}}", 26, 2, 30, 4.5, 7));
        Add(t, Text("单位：{{unit}}", 84, 2, 14, 4.5, 7, align: HorizontalAlign.Right));
        Add(t, Line(2, 7.5, 96));
        Add(t, Text("{{goods}}", 3, 9, 55, 6, 10, bold: true));
        Add(t, Text("规格：{{spec}}　　产地：{{addr}}", 3, 15.5, 60, 4.5, 7.5));
        Add(t, Barcode("{{barcode}}", 3, 21, 40, 10, "barcode"));
        Add(t, Text("{{date}}", 46, 24, 24, 4, 6.5));
        Add(t, Shape(ShapeKind.RoundRect, 66, 10, 31, 20, fill: new SKColor(0xFF, 0xE1, 0x00), stroke: null, corner: 1.5));
        Add(t, Text("会员价", 68, 11.5, 27, 4, 7, align: HorizontalAlign.Center));
        Add(t, Text("￥{{price}}", 67, 15, 29, 13, 16, bold: true, align: HorizontalAlign.Center));
        return t;
    }

    // ── 标签卡券补充 ────────────────────────────────────────────────────────

    static PrintTemplate BuildDrugLabel()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "布洛芬缓释胶囊"), ("spec", "0.3g × 20 粒"), ("batch", "H20261001"),
            ("usage", "口服，一次 1 粒，一日 2 次"), ("price2", "18.00")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: SKColors.Black, strokeWidth: 0.25, corner: 1));
        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 8, fill: new SKColor(0xC0, 0x00, 0x00), stroke: null, corner: 1));
        Add(t, Text("OTC 非处方药", 3.5, 2, 32, 6, 8.5, bold: true));
        Add(t, Text("国药准字 H10900089", 33, 3, 24, 4, 6, align: HorizontalAlign.Right));
        Add(t, Text("{{goods}}", 3.5, 11, 45, 5.5, 10, bold: true));
        Add(t, Text("规格：{{spec}}　　批号：{{batch}}", 3.5, 17.5, 53, 4.5, 7));
        Add(t, Text("用法用量：{{usage}}", 3.5, 22.5, 53, 4.5, 7));
        Add(t, Text("适应症：缓解轻至中度疼痛及感冒发热。", 3.5, 27, 53, 4, 6.5));
        Add(t, Text("药师指导：{{name}}", 3.5, 31.5, 30, 4, 6.5));
        Add(t, Text("￥{{price2}}", 40, 29.5, 18, 8, 12, bold: true, align: HorizontalAlign.Right));
        return t;
    }

    static PrintTemplate BuildBookLabel()
    {
        var t = NewTemplate(60, 40);
        t.DesignData = new(Extra(("goods", "设计中的设计"), ("author", "原研哉 著"), ("callNo", "TB47/128"),
            ("shelf", "艺术区 3 排"), ("oldPrice", "原价 48.00"), ("price2", "36.00")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 58, 38, stroke: SKColors.Black, strokeWidth: 0.25, corner: 1));
        Add(t, Text("{{goods}}", 3.5, 3, 40, 6, 10, bold: true));
        Add(t, Text("{{author}}", 3.5, 10, 30, 4.5, 7.5));
        Add(t, Text("索书号", 3.5, 16.5, 12, 4, 6.5));
        Add(t, Text("{{callNo}}", 3.5, 21, 24, 6, 10, bold: true));
        Add(t, Text("书架：{{shelf}}", 3.5, 28, 28, 4, 6.5));
        Add(t, Barcode("{{barcode}}", 30, 15, 28, 11, "barcode"));
        Add(t, Text("{{oldPrice}}", 30, 27, 28, 4, 6.5, align: HorizontalAlign.Right));
        Add(t, Text("会员价 ￥{{price2}}", 30, 31, 28, 4.5, 7.5, bold: true, align: HorizontalAlign.Right));
        return t;
    }

    static PrintTemplate BuildEnergyLabel()
    {
        var t = NewTemplate(50, 80);
        t.DesignData = new(Extra(("goods", "房间空气调节器"), ("spec", "KFR-35GW"), ("power", "1080 W"),
            ("consumption", "352 (kW·h)"), ("standard", "GB 21455-2019")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 48, 78, stroke: SKColors.Black, strokeWidth: 0.3, corner: 2));
        Add(t, Text("中国能效标识", 4, 4, 42, 6, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("CHINA ENERGY LABEL", 4, 10.5, 42, 3.5, 6, align: HorizontalAlign.Center));
        Add(t, Text("{{goods}}", 4, 15.5, 42, 4.5, 8));
        Add(t, Text("规格型号：{{spec}}", 4, 20.5, 42, 4, 7));
        var levels = new[] { ("1", new SKColor(0x0B, 0x7A, 0x3B)), ("2", new SKColor(0x7A, 0xB8, 0x1E)),
            ("3", new SKColor(0xF2, 0xC2, 0x00)), ("4", new SKColor(0xE8, 0x7B, 0x1C)), ("5", new SKColor(0xC0, 0x28, 0x1D)) };
        for (var i = 0; i < levels.Length; i++)
        {
            var (name, color) = levels[i];
            var y = 27 + i * 7.5;
            Add(t, Shape(ShapeKind.RoundRect, 6, y, 4.5, 6, fill: color, stroke: null, corner: 0.8));
            Add(t, Text(name, 6, y + 0.8, 4.5, 4.5, 8, bold: true, align: HorizontalAlign.Center));
            if (i < 2)
                Add(t, Text(i == 0 ? "效率高" : "节能", 12, y + 0.8, 24, 4.5, 7));
        }
        Add(t, Text("耗电量（kW·h）", 4, 66, 30, 4, 6.5));
        Add(t, Text("{{consumption}}", 4, 70, 30, 5, 9, bold: true));
        Add(t, Barcode("{{barcode}}", 30, 66, 18, 9, "barcode"));
        Add(t, Text("依据：{{standard}}　输入功率 {{power}}", 4, 76, 42, 3, 5.5));
        return t;
    }

    static PrintTemplate BuildJewelry()
    {
        var t = NewTemplate(25, 30);
        t.DesignData = new(Extra(("goods", "Pt950 钻戒"), ("carat", "0.30ct"), ("weight", "2.86g"),
            ("price2", "6,800"), ("cert", "NGTC-S2611")));

        Add(t, Shape(ShapeKind.RoundRect, 0.8, 0.8, 23.4, 28.4, stroke: SKColors.Black, strokeWidth: 0.25, corner: 1));
        Add(t, Text("{{goods}}", 1.5, 2, 22, 4.5, 7, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("主石：{{carat}}", 1.5, 7.5, 22, 4, 6));
        Add(t, Text("金重：{{weight}}", 1.5, 11.5, 22, 4, 6));
        Add(t, Text("证书号：{{cert}}", 1.5, 15.5, 22, 4, 6));
        Add(t, Text("￥{{price2}}", 1.5, 19.5, 22, 5.5, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Barcode("{{barcode}}", 3, 25.5, 19, 3.2, "barcode"));
        return t;
    }

    static PrintTemplate BuildGiftCard()
    {
        var t = NewTemplate(86, 54);
        t.DesignData = new(Extra(("face", "100"), ("cardNo", "GC-2026-884200"), ("expire", "2028-12-31"),
            ("brand", "宏达优选"), ("slogan", "心意随选 · 通兑全国")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 84, 52, stroke: new SKColor(0xB8, 0x86, 0x0B), strokeWidth: 0.4, corner: 3));
        Add(t, Shape(ShapeKind.RoundRect, 2, 2, 81, 9, fill: new SKColor(0xB8, 0x86, 0x0B), stroke: null, corner: 2));
        Add(t, Text("{{brand}} 礼品卡", 4, 3.5, 77, 6, 9, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("￥{{face}}", 6, 16, 44, 16, 24, bold: true));
        Add(t, Text("{{slogan}}", 6, 34, 50, 5, 8.5));
        Add(t, Text("卡号：{{cardNo}}", 6, 41, 50, 4.5, 7.5));
        Add(t, Text("有效期至：{{expire}}", 6, 46.5, 50, 4.5, 7.5));
        Add(t, Barcode("{{cardNo}}", 50, 36, 32, 10, "cardNo"));
        Add(t, Text("本卡不记名、不挂失、不兑现", 50, 47, 34, 3.5, 6));
        return t;
    }

    // ── 物流仓储：复杂面单 ──────────────────────────────────────────────────

    static PrintTemplate BuildSfWaybill()
    {
        var t = NewTemplate(100, 150);
        t.DesignData = new(Extra(
            ("sender", "张小明 138****6688"), ("senderAddr", "上海市浦东新区博云路 2 号"),
            ("receiver", "李红 139****2233"), ("receiverAddr", "杭州市余杭区文一西路 998 号 3 栋 501"),
            ("goods", "衣物 2 件"), ("no", "SF318 0011 8842"), ("route", "上海浦东 → 杭州余杭"),
            ("bigPen", "388D"), ("three", "998 302 611")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 98, 148, stroke: SKColors.Black, strokeWidth: 0.3, corner: 1));
        Add(t, Text("顺丰速运", 4, 3, 30, 7, 12, bold: true));
        Add(t, Text("{{bigPen}}", 66, 3, 30, 7, 13, bold: true, align: HorizontalAlign.Right));
        Add(t, Barcode("{{no}}", 4, 11, 92, 14, "no"));
        Add(t, Text("{{no}}", 4, 25.5, 92, 4, 6.5, align: HorizontalAlign.Center));
        Add(t, Line(2, 31, 96));
        Add(t, Shape(ShapeKind.RoundRect, 3, 33, 46, 7, fill: new SKColor(0xEE, 0xEE, 0xEE), stroke: null, corner: 0.8));
        Add(t, Text("收件人：{{receiver}}", 5, 34, 43, 5, 7.5, bold: true));
        Add(t, Text("地址：{{receiverAddr}}", 5, 41, 90, 8, 7.5));
        Add(t, Text("电话：139****2233", 5, 50, 60, 4.5, 7.5));
        Add(t, Shape(ShapeKind.RoundRect, 3, 56, 46, 7, fill: new SKColor(0xEE, 0xEE, 0xEE), stroke: null, corner: 0.8));
        Add(t, Text("寄件人：{{sender}}", 5, 57, 43, 5, 7.5, bold: true));
        Add(t, Text("地址：{{senderAddr}}", 5, 64, 90, 8, 7.5));
        Add(t, Text("电话：138****6688", 5, 73, 60, 4.5, 7.5));
        Add(t, Line(2, 79.5, 96));
        Add(t, Text("{{route}}", 5, 82, 60, 5, 8.5, bold: true));
        Add(t, Text("三段码：{{three}}", 62, 82, 34, 5, 7.5, align: HorizontalAlign.Right));
        Add(t, Table(3, 88, 94, 30, 7.5,
            new[]
            {
                ("物品", 34, "", HorizontalAlign.Left),
                ("数量", 14, "", HorizontalAlign.Center),
                ("价值(元)", 18, "", HorizontalAlign.Right),
                ("备注", 28, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "衣物", "2", "380", "" },
                new() { "文件", "1", "20", "" },
            }));
        Add(t, Qr("{{no}}", 70, 122, 26, 26, "no"));
        Add(t, Text("签收人：____________", 4, 126, 40, 4.5, 7.5));
        Add(t, Text("收件时间：{{date}}", 4, 132, 40, 4.5, 7.5));
        Add(t, Text("扫码查件 · 签收确认", 70, 149, 26, 4, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildJdWaybill()
    {
        var t = NewTemplate(100, 150);
        t.DesignData = new(Extra(
            ("sender", "张小明"), ("senderPhone", "138****6688"), ("senderAddr", "上海市浦东新区博云路 2 号"),
            ("receiver", "李红"), ("receiverPhone", "139****2233"), ("receiverAddr", "杭州市余杭区文一西路 998 号"),
            ("no", "JDV012 3456 7801"), ("promise", "211 限时达"), ("site", "余杭站点 302")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 98, 12, fill: new SKColor(0xC0, 0x28, 0x1D), stroke: null, corner: 1));
        Add(t, Text("京东物流 · {{promise}}", 4, 3, 60, 7, 11, bold: true));
        Add(t, Text("{{site}}", 64, 4, 32, 5, 7.5, align: HorizontalAlign.Right));
        Add(t, Barcode("{{no}}", 4, 15, 92, 15, "no"));
        Add(t, Text("{{no}}", 4, 30.5, 92, 4, 7, align: HorizontalAlign.Center));
        Add(t, Shape(ShapeKind.RoundRect, 3, 37, 94, 6.5, fill: new SKColor(0xEE, 0xEE, 0xEE), stroke: null, corner: 0.8));
        Add(t, Text("收件人：{{receiver}}　{{receiverPhone}}", 5, 38, 90, 5, 8, bold: true));
        Add(t, Text("收件地址：{{receiverAddr}}", 5, 45, 90, 7.5, 8));
        Add(t, Shape(ShapeKind.RoundRect, 3, 54.5, 94, 6.5, fill: new SKColor(0xEE, 0xEE, 0xEE), stroke: null, corner: 0.8));
        Add(t, Text("寄件人：{{sender}}　{{senderPhone}}", 5, 55.5, 90, 5, 8, bold: true));
        Add(t, Text("寄件地址：{{senderAddr}}", 5, 62.5, 90, 7.5, 8));
        Add(t, Line(2, 71.5, 96));
        Add(t, Text("货品信息", 5, 74, 30, 5, 8, bold: true));
        Add(t, Table(3, 80, 94, 24, 7.5,
            new[]
            {
                ("货品名称", 40, "", HorizontalAlign.Left),
                ("数量", 16, "", HorizontalAlign.Center),
                ("重量(kg)", 20, "", HorizontalAlign.Right),
                ("体积(m³)", 18, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "图书套装", "2", "1.8", "0.01" },
                new() { "保护气泡膜", "1", "0.1", "0.00" },
            }));
        Add(t, Text("声明：禁止寄递易燃易爆等违禁物品；贵重物品请保价。", 4, 106, 92, 4.5, 6.5));
        Add(t, Text("拒收提示：包装破损请当面拒收并拍照。", 4, 111, 92, 4.5, 6.5));
        Add(t, Line(2, 117, 96));
        Add(t, Text("签收人：____________　签收时间：{{date}}", 4, 120, 70, 5, 7.5));
        Add(t, Qr("{{no}}", 72, 119, 25, 25, "no"));
        Add(t, Text("大客户服务热线 950616", 4, 136, 60, 4, 6));
        Add(t, Text("扫码评价", 72, 145.5, 25, 4, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildYtoWaybill()
    {
        var t = NewTemplate(100, 150);
        t.DesignData = new(Extra(
            ("sender", "张小明 138****6688"), ("senderAddr", "上海市浦东新区博云路 2 号"),
            ("receiver", "李红 139****2233"), ("receiverAddr", "杭州市余杭区文一西路 998 号"),
            ("no", "YT431 0088 9912"), ("bigPen", "021D"), ("three", "302 555 123"),
            ("goods", "日用品 1 箱")));

        Add(t, Shape(ShapeKind.RoundRect, 1, 1, 98, 12, fill: new SKColor(0x00, 0x74, 0xB9), stroke: null, corner: 1));
        Add(t, Text("圆通速递", 4, 3, 40, 7, 12, bold: true));
        Add(t, Text("{{bigPen}}", 64, 3.5, 32, 6, 12, bold: true, align: HorizontalAlign.Right));
        Add(t, Barcode("{{no}}", 4, 15, 92, 14, "no"));
        Add(t, Text("{{no}}　　三段码 {{three}}", 4, 29.5, 92, 4, 6.5, align: HorizontalAlign.Center));
        Add(t, Line(2, 35.5, 96));
        Add(t, Text("收：{{receiver}}", 4, 38, 92, 5.5, 9, bold: true));
        Add(t, Text("{{receiverAddr}}", 4, 44.5, 92, 7.5, 8));
        Add(t, Text("寄：{{sender}}", 4, 53.5, 92, 5.5, 9, bold: true));
        Add(t, Text("{{senderAddr}}", 4, 60, 92, 7.5, 8));
        Add(t, Line(2, 69, 96));
        Add(t, Table(3, 71.5, 94, 26, 7.5,
            new[]
            {
                ("内件品名", 40, "", HorizontalAlign.Left),
                ("数量", 16, "", HorizontalAlign.Center),
                ("价值(元)", 20, "", HorizontalAlign.Right),
                ("保价", 18, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "日用品", "1 箱", "150", "否" },
            }));
        Add(t, Text("付款方式：寄付 □　到付 □　月结 ■", 4, 99.5, 60, 4.5, 7));
        Add(t, Text("月结账号：88420066", 62, 99.5, 34, 4.5, 6.5, align: HorizontalAlign.Right));
        Add(t, Shape(ShapeKind.RoundRect, 3, 106, 46, 18, stroke: SKColors.Gray, strokeWidth: 0.2, corner: 0.8));
        Add(t, Text("收派员签名：", 5, 108, 40, 4, 6.5));
        Add(t, Text("签收时间：", 5, 116, 40, 4, 6.5));
        Add(t, Qr("{{no}}", 70, 106, 27, 27, "no"));
        Add(t, Text("客服 95554　　官网查件", 4, 126, 60, 4, 6.5));
        Add(t, Text("扫码查件", 70, 134.5, 27, 4, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildTakeoutReceipt()
    {
        var t = NewTemplate(80, 0);
        t.DesignData = new(Extra(
            ("shop2", "巷口烧腊（文一路店）"), ("receiver", "李红 139****2233"),
            ("receiverAddr", "文一西路 998 号 3 栋 501"), ("dish1", "烧鹅饭（大份）"), ("dish2", "例汤"),
            ("box", "2"), ("freight", "3.00"), ("total2", "42.00"), ("when", "2026-10-27 12:06")));

        Add(t, Text("{{shop2}}", 4, 3, 72, 6, 10, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("外卖订单 · {{when}}", 4, 10, 72, 4.5, 7, align: HorizontalAlign.Center));
        Add(t, Text("单号：{{orderNo}}", 4, 15.5, 72, 4.5, 7));
        Add(t, Line(3, 21, 74));
        Add(t, InfoLine("收件人：{{receiver}}", 4, 23.5, 72, 8));
        Add(t, InfoLine("地址：{{receiverAddr}}", 4, 30.5, 72, 8));
        Add(t, InfoLine("骑手：{{rider}}　　预计送达 30 分钟", 4, 37.5, 72, 8));
        Add(t, Line(3, 44.5, 74));
        Add(t, InfoLine("{{dish1}}　×1　　￥32.00", 4, 47, 72, 8));
        Add(t, InfoLine("{{dish2}}　×1　　￥5.00", 4, 54, 72, 8));
        Add(t, InfoLine("打包费（{{box}} 份）　　￥2.00", 4, 61, 72, 8));
        Add(t, InfoLine("配送费　　　　　　　　￥3.00", 4, 68, 72, 8));
        Add(t, Line(3, 74.5, 74));
        Add(t, Text("实付：￥{{total2}}", 4, 77, 72, 6, 10, bold: true, align: HorizontalAlign.Right));
        Add(t, Text("在线支付已完成", 4, 84, 72, 4.5, 7, align: HorizontalAlign.Right));
        Add(t, Line(3, 90, 74));
        Add(t, InfoLine("备注：不要香菜，放门口即可。", 4, 92.5, 72, 8));
        Add(t, Text("本小票为消费凭证 · 欢迎再次下单", 4, 103, 72, 4.5, 7, align: HorizontalAlign.Center));
        return t;
    }

    // ── 报表单据：复杂表单 ──────────────────────────────────────────────────

    static PrintTemplate BuildDeliveryNote()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("orderNo2", "FH-20261104-021"), ("customer", "杭州云启贸易有限公司"),
            ("receiver2", "李红 139****2233"), ("receiver2Addr", "文一西路 998 号 3 栋 501"),
            ("terms", "买方自提")));

        Add(t, Text("销售发货单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("发货单号：{{orderNo2}}", 8, 27, 110, 6, 9.5));
        Add(t, Text("发货日期：{{date}}", 128, 27, 74, 6, 9.5, align: HorizontalAlign.Right));
        Add(t, Line(8, 35, 194));
        Add(t, InfoLine("客户名称：{{customer}}　　联系方式：{{phone}}", 8, 39, 194, 9.5));
        Add(t, InfoLine("收货地址：{{receiver2Addr}}　　收货人：{{receiver2}}", 8, 47, 194, 9.5));
        Add(t, InfoLine("交货方式：{{terms}}　　运输方式：公路", 8, 55, 194, 9.5));
        Add(t, Table(8, 64, 194, 110, 9.5,
            new[]
            {
                ("序号", 14, "", HorizontalAlign.Center),
                ("品名规格", 60, "items.item", HorizontalAlign.Left),
                ("单位", 20, "items.unit", HorizontalAlign.Center),
                ("数量", 22, "items.qty", HorizontalAlign.Center),
                ("单价（元）", 30, "items.price", HorizontalAlign.Right),
                ("金额（元）", 48, "items.amount", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "1", "不锈钢托盘 600×400", "只", "4", "45.00", "180.00" },
                new() { "2", "电子称重仪 TCS-150", "台", "1", "140.00", "140.00" },
                new() { "3", "缓冲气泡膜 50m", "卷", "2", "18.00", "36.00" },
            }));
        Add(t, Text("合计（大写）：叁佰伍拾陆元整　　（小写）￥356.00", 8, 180, 194, 7, 10, bold: true));
        Add(t, InfoLine("发货备注：随货附检验报告与质保书各一份。", 8, 192, 194));
        Add(t, Shape(ShapeKind.RoundRect, 8, 202, 92, 40, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("发货方（盖章）：", 12, 206, 80, 5, 8.5));
        Add(t, Text("经办人 / 日期：", 12, 232, 80, 5, 8.5));
        Add(t, Shape(ShapeKind.RoundRect, 110, 202, 92, 40, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("收货方（签章）：", 114, 206, 80, 5, 8.5));
        Add(t, Text("验收人 / 日期：", 114, 232, 80, 5, 8.5));
        return t;
    }

    static PrintTemplate BuildBankReceipt()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("no2", "HBZJ-2026-1104-0099"), ("payer", "宏达实业有限公司"),
            ("payerAcc", "3100 **** 8842"), ("payee", "杭州云启贸易有限公司"), ("payeeAcc", "3210 **** 9917"),
            ("bank", "杭州银行 文一支行"), ("amount2", "35,600.00"), ("big2", "叁万伍仟陆佰元整"),
            ("purpose", "货款（订单 ORD-20261104-0068）"), ("channel", "企业网银")));

        Add(t, Text("银行电子回单", 55, 14, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{bank}}", 55, 27, 100, 5.5, 9, align: HorizontalAlign.Center));
        Add(t, Text("回单编号：{{no2}}", 8, 38, 120, 6, 9.5));
        Add(t, Text("交易日期：{{date}}　渠道：{{channel}}", 8, 46, 194, 6, 9.5));
        Add(t, Line(8, 55, 194));
        Add(t, Table(8, 60, 194, 74, 9.5,
            new[]
            {
                ("项目", 44, "", HorizontalAlign.Left),
                ("内容", 150, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "付款人户名", "宏达实业有限公司" },
                new() { "付款人账号", "3100 **** 8842" },
                new() { "收款人户名", "杭州云启贸易有限公司" },
                new() { "收款人账号", "3210 **** 9917" },
                new() { "币种 / 金额", "人民币 ￥35,600.00" },
                new() { "金额大写", "叁万伍仟陆佰元整" },
                new() { "附言 / 用途", "货款（订单 ORD-20261104-0068）" },
            }));
        Add(t, InfoLine("本回单不作为收款方交易最终入账依据，请以账户实际到账为准。", 8, 140, 194));
        Add(t, InfoLine("回单专用章（电子）：杭州银行 文一支行　　经办：系统自动　　复核：系统自动", 8, 154, 194));
        Add(t, Shape(ShapeKind.RoundRect, 140, 166, 62, 62, stroke: SKColors.Gray, strokeWidth: 0.2, corner: 1));
        Add(t, Qr("{{no2}}", 144, 170, 54, 54, "no2"));
        Add(t, Text("扫码验证回单真伪", 140, 232, 62, 4.5, 7, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildPayslip()
    {
        var t = NewTemplate(190, 90);
        t.DesignData = new(Extra(
            ("staff", "张小明"), ("dept2", "仓储部"), ("period", "2026 年 10 月"), ("job", "仓储主管"),
            ("base", "8,000.00"), ("bonus", "1,200.00"), ("allow", "300.00"),
            ("social", "-960.00"), ("tax", "-168.00"), ("net2", "8,372.00")));

        Add(t, Text("员工工资条", 45, 3, 100, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("姓名：{{staff}}　　部门：{{dept2}}　　职务：{{job}}　　工资月份：{{period}}", 5, 13, 180, 8.5));
        Add(t, Line(5, 21.5, 180));
        Add(t, Table(5, 24, 180, 34, 8,
            new[]
            {
                ("基本工资", 30, "", HorizontalAlign.Right),
                ("绩效奖金", 30, "", HorizontalAlign.Right),
                ("补贴", 24, "", HorizontalAlign.Right),
                ("社保扣款", 30, "", HorizontalAlign.Right),
                ("个税", 26, "", HorizontalAlign.Right),
                ("实发工资", 40, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "8,000.00", "1,200.00", "300.00", "-960.00", "-168.00", "8,372.00" },
            }));
        Add(t, InfoLine("实发（大写）：捌仟叁佰柒拾贰元整", 5, 62, 110, 8.5));
        Add(t, InfoLine("签收：____________", 130, 62, 55, 8.5));
        Add(t, Text("工资信息属个人隐私，请妥善保管；如有疑问请于 3 个工作日内联系人事部。", 5, 74, 180, 4.5, 6.5));
        return t;
    }

    static PrintTemplate BuildRepairOrder()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("no2", "WX-20261104-031"), ("customer", "杭州云启贸易有限公司"),
            ("device", "电子称重仪 TCS-150"), ("sn", "TCS150-2024-0388"),
            ("fault", "称重数值漂移，开机 10 分钟后误差超 ±50g"), ("accessory", "秤盘 ×1、电源适配器 ×1"),
            ("date2", "2026-11-02")));

        Add(t, Text("设备维修工单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("工单号：{{no2}}", 8, 27, 110, 6, 9.5));
        Add(t, Text("受理日期：{{date2}}　　预约取件：{{date}}", 108, 27, 94, 6, 9.5, align: HorizontalAlign.Right));
        Add(t, Line(8, 35, 194));
        Add(t, InfoLine("客户名称：{{customer}}　　联系人：{{name}}　　电话：{{phone}}", 8, 39, 194, 9.5));
        Add(t, InfoLine("设备名称：{{device}}　　序列号：{{sn}}", 8, 47, 194, 9.5));
        Add(t, InfoLine("随附件：{{accessory}}", 8, 55, 194, 9.5));
        Add(t, Shape(ShapeKind.RoundRect, 8, 64, 194, 22, stroke: SKColors.Gray, strokeWidth: 0.2, corner: 1));
        Add(t, Text("故障描述：{{fault}}", 12, 67, 186, 16, 9.5));
        Add(t, Text("维修项目与费用", 8, 90, 80, 6, 10, bold: true));
        Add(t, Table(8, 98, 194, 76, 9.5,
            new[]
            {
                ("序号", 14, "", HorizontalAlign.Center),
                ("维修项目", 74, "", HorizontalAlign.Left),
                ("配件/耗材", 40, "", HorizontalAlign.Left),
                ("工时费（元）", 30, "", HorizontalAlign.Right),
                ("配件费（元）", 36, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "1", "传感器校准", "-", "80.00", "-" },
                new() { "2", "更换称重传感器", "CS-150 型", "60.00", "220.00" },
                new() { "3", "整机清洁保养", "-", "40.00", "-" },
            }));
        Add(t, Text("费用合计（大写）：肆佰元整　　（小写）￥400.00", 8, 180, 194, 7, 10, bold: true));
        Add(t, InfoLine("维修结果：已完成并通过满量程校验，误差 ≤ ±10g，质保 90 天。", 8, 192, 194));
        Add(t, Shape(ShapeKind.RoundRect, 8, 202, 92, 40, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("客户确认（签字）：", 12, 206, 80, 5, 8.5));
        Add(t, Text("取机日期：", 12, 232, 80, 5, 8.5));
        Add(t, Shape(ShapeKind.RoundRect, 110, 202, 92, 40, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, Text("维修工程师（签字）：", 114, 206, 80, 5, 8.5));
        Add(t, Text("质检员（签字）：", 114, 232, 80, 5, 8.5));
        return t;
    }
}

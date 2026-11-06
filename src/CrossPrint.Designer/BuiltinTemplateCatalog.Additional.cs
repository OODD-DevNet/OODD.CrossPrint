using System.Collections.Generic;
using CrossPrint.Core;
using SkiaSharp;

namespace CrossPrint.Designer;

/// <summary>
/// 模板库第三批扩容（partial，对照参考市场截图挑选）：交通/医疗/校园/政务人事/财务票证。
/// 出租车票、门诊收费票据、食堂充值小票、门禁访客单、酒店入住单、酒水单、食品留样记录、
/// 考勤统计表、请假申请单、采购申请单、费用报销单、员工入职登记表、离职证明、收入证明、
/// 学生成绩单、二手车销售发票（票样）。全部程序化构造、自带演示数据。
/// </summary>
public static partial class BuiltinTemplateCatalog
{
    /// <summary>第三批：交通/医疗/校园/政务人事/财务票证。</summary>
    static IEnumerable<Entry> BuildThirdEntries()
    {
        yield return new Entry("出租车票（80mm）", "小票票据", BuildTaxiReceipt,
            "出租车机打票：车牌/上下车时间/里程/金额与监督电话。");
        yield return new Entry("食堂充值小票（58mm）", "小票票据", BuildCanteenTopUp,
            "饭卡充值凭证：卡号/姓名/充值金额与余额。");
        yield return new Entry("门禁访客单（80mm）", "小票票据", BuildVisitorPass,
            "访客登记凭条：访客/被访人/事由与离场时间。");
        yield return new Entry("医院门诊收费票据（190×100）", "医疗健康", BuildClinicInvoice,
            "门诊收费票据票样：收费项目明细表 + 合计大小写 + 收费员。");
        yield return new Entry("酒店入住单（190×140）", "报表单据", BuildHotelCheckIn,
            "前台入住登记：房型房价、押金、入住退房时间与宾客签名。");
        yield return new Entry("酒水单（190×140）", "餐饮零售", BuildBeverageList,
            "餐桌酒水单：品名/规格/单价表格 + 消费合计。");
        yield return new Entry("食品留样记录表（A4）", "食品包装", BuildFoodRetain,
            "食堂食品留样记录：品名/留样量/时间/留样人与审核，食安检查常用。");
        yield return new Entry("考勤月度统计表（A4）", "人事行政", BuildAttendanceSheet,
            "月度考勤：出勤/迟到/早退/请假列统计 + 异常备注。");
        yield return new Entry("请假申请单（190×130）", "人事行政", BuildLeaveForm,
            "请假单：假别/起止时间/事由 + 组长/经理/人事三级审批。");
        yield return new Entry("员工入职登记表（A4）", "人事行政", BuildOnboardingForm,
            "入职登记：个人信息区 + 教育经历表 + 紧急联系人 + 签名确认。");
        yield return new Entry("离职证明（A4）", "人事行政", BuildResignCert,
            "离职证明书：任职期间/岗位/已办结手续说明与公司盖章位。");
        yield return new Entry("收入证明（A4）", "报表单据", BuildIncomeCert,
            "收入证明：月收入/年收入明细说明 + 人事财务双章位。");
        yield return new Entry("采购申请单（A4）", "报表单据", BuildPurchaseRequest,
            "采购申请：需求部门/申购明细表/预算来源 + 三级审批。");
        yield return new Entry("费用报销单（190×140）", "报表单据", BuildExpenseClaim,
            "报销单：费用明细表 + 合计大小写 + 部门/财务/总经理审批栏。");
        yield return new Entry("学生成绩单（A4）", "教育答题", BuildScoreReport,
            "期末成绩单：科目/成绩/班级均分表格 + 班主任寄语。");
        yield return new Entry("二手车销售发票（票样）", "财税金融", BuildUsedCarInvoice,
            "二手车销售统一发票票样：车辆信息区 + 价税合计 + 过户备注。");
    }

    // ── 小票票据 ────────────────────────────────────────────────────────────

    static PrintTemplate BuildTaxiReceipt()
    {
        var t = NewTemplate(80, 0);
        t.DesignData = new(Extra(("goods", "上海强生出租"), ("plate", "沪C·T8842"), ("cert", "3110245"),
            ("up", "2026-11-04 09:12"), ("down", "09:31"), ("mileage", "8.6"), ("fare", "32.00"),
            ("fuel", "1.00"), ("total", "33.00")));

        Add(t, Text("出租车发票", 10, 3, 60, 7, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("TAXI FARE RECEIPT", 10, 11, 60, 4, 6, align: HorizontalAlign.Center));
        Add(t, InfoLine("车号：{{plate}}　证号：{{cert}}", 4, 17, 72, 8));
        Add(t, InfoLine("乘车日期：2026-11-04", 4, 25, 72, 8));
        Add(t, InfoLine("上车：{{up}}", 4, 33, 72, 8));
        Add(t, InfoLine("下车：{{down}}", 4, 41, 72, 8));
        Add(t, InfoLine("里程：{{mileage}} km　单价：2.70 元/km", 4, 49, 72, 8));
        Add(t, InfoLine("候时费：￥0.00　燃油加价：￥{{fuel}}", 4, 57, 72, 8));
        Add(t, Line(3, 64.5, 74));
        Add(t, Text("金额：￥{{total}}", 4, 67, 72, 7, 12, bold: true));
        Add(t, Barcode("6901234567892", 14, 75, 52, 11, "barcode"));
        Add(t, InfoLine("监督电话：021-63232149　服务卡号：0866", 4, 88, 72, 7));
        Add(t, InfoLine("本票为乘车报销凭证，请妥善保存", 4, 96, 72, 7));
        return t;
    }

    static PrintTemplate BuildCanteenTopUp()
    {
        var t = NewTemplate(58, 0);
        t.DesignData = new(Extra(("staff", "张小明"), ("cardNo", "RD-08842"), ("amount2", "200.00"),
            ("balance", "356.80"), ("channel", "微信支付")));

        Add(t, Text("食堂餐卡充值凭证", 4, 3, 50, 6, 10, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{date}} {{time}}", 4, 10.5, 50, 4, 7, align: HorizontalAlign.Center));
        Add(t, Line(3, 16, 52));
        Add(t, InfoLine("姓名：{{staff}}", 4, 18.5, 50, 8));
        Add(t, InfoLine("卡号：{{cardNo}}", 4, 26, 50, 8));
        Add(t, InfoLine("充值金额：￥{{amount2}}", 4, 33.5, 50, 8.5, true));
        Add(t, InfoLine("支付方式：{{channel}}", 4, 42, 50, 8));
        Add(t, Line(3, 49, 52));
        Add(t, InfoLine("充值后余额：￥{{balance}}", 4, 51.5, 50, 8.5));
        Add(t, Qr("{{cardNo}}", 19, 60, 20, 20, "cardNo"));
        Add(t, Text("扫码可查消费明细", 4, 81, 50, 4.5, 7, align: HorizontalAlign.Center));
        Add(t, Text("本凭证不作为报销发票使用", 4, 86.5, 50, 4, 6, align: HorizontalAlign.Center));
        return t;
    }

    static PrintTemplate BuildVisitorPass()
    {
        var t = NewTemplate(80, 0);
        t.DesignData = new(Extra(("staff", "张小明"), ("visitor", "王大伟 330***********1234"),
            ("company2", "杭州云启贸易有限公司"), ("reason", "设备巡检"), ("area", "3 号车间"),
            ("leave", "17:30")));

        Add(t, Text("访客登记凭条", 10, 3, 60, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{date}}", 4, 12, 72, 4.5, 7.5, align: HorizontalAlign.Center));
        Add(t, Line(3, 18, 74));
        Add(t, InfoLine("访客：{{visitor}}", 4, 21, 72, 8.5));
        Add(t, InfoLine("单位：{{company2}}", 4, 29, 72, 8.5));
        Add(t, InfoLine("被访人：{{staff}}　部门：{{dept}}", 4, 37, 72, 8.5));
        Add(t, InfoLine("事由：{{reason}}", 4, 45, 72, 8.5));
        Add(t, InfoLine("区域：{{area}}", 4, 53, 72, 8.5));
        Add(t, InfoLine("入场：09:05　预计离场：{{leave}}", 4, 61, 72, 8.5));
        Add(t, Line(3, 68.5, 74));
        Add(t, Qr("{{orderNo}}", 6, 71, 22, 22, "orderNo"));
        Add(t, InfoLine("凭此条离场", 32, 73, 40, 8.5, true));
        Add(t, InfoLine("请佩戴访客证并在前台注销", 32, 81, 40, 7));
        Add(t, Text("安全生产 · 人人有责", 4, 96, 72, 4, 6.5, align: HorizontalAlign.Center));
        return t;
    }

    // ── 医疗健康 / 餐饮零售 ────────────────────────────────────────────────

    static PrintTemplate BuildClinicInvoice()
    {
        var t = NewTemplate(190, 100);
        t.DesignData = new(Extra(
            ("hospital", "市第一人民医院"), ("no2", "门诊收费 2026110400866"), ("patient", "张小明"),
            ("sex", "男"), ("age2", "34"), ("dept2", "内科门诊"),
            ("total2", "186.50"), ("big2", "壹佰捌拾陆元伍角整"), ("cashier", "收费员 026")));

        Add(t, Text("{{hospital}}", 45, 3, 100, 7, 12, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("门诊收费票据", 45, 11, 100, 6, 11, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("{{no2}}", 110, 18, 74, 5, 7.5, align: HorizontalAlign.Right));
        Add(t, InfoLine("姓名：{{patient}}　性别：{{sex}}　年龄：{{age2}}　科别：{{dept2}}", 8, 25, 174, 8.5));
        Add(t, InfoLine("病历号：0884221　结算方式：医保个人账户", 8, 33.5, 174, 8.5));
        Add(t, Line(8, 42, 174));
        Add(t, Table(8, 45, 174, 26, 8,
            new[]
            {
                ("项目", 52, "", HorizontalAlign.Left),
                ("规格", 30, "", HorizontalAlign.Left),
                ("数量", 20, "", HorizontalAlign.Center),
                ("金额（元）", 34, "", HorizontalAlign.Right),
                ("类别", 38, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "挂号费（副主任医师）", "-", "1", "25.00", "自费" },
                new() { "血常规五分类", "-", "1", "35.00", "医保" },
                new() { "阿莫西林胶囊 0.25g", "0.25g×24", "2", "26.50", "医保" },
                new() { "静脉采血费", "-", "1", "100.00", "医保" },
            }));
        Add(t, Text("合计（大写）：{{big2}}　　（小写）￥{{total2}}", 8, 74, 174, 6, 9, bold: true));
        Add(t, InfoLine("医保统筹支付：￥0.00　个人账户支付：￥186.50　现金支付：￥0.00", 8, 82, 174, 7.5));
        Add(t, InfoLine("收费员：{{cashier}}　　收款单位（章）", 8, 90, 174, 7.5));
        return t;
    }

    static PrintTemplate BuildHotelCheckIn()
    {
        var t = NewTemplate(190, 140);
        t.DesignData = new(Extra(
            ("hotel", "云栖酒店"), ("guest", "张小明"), ("guestId", "330***********0011"),
            ("phone2", "138****6688"), ("room", "8210（高级大床房）"), ("nights", "2"),
            ("in2", "2026-11-04 14:00"), ("out2", "2026-11-06 12:00"), ("deposit", "500.00"),
            ("roomPrice", "388.00"), ("total2", "776.00")));

        Add(t, Shape(ShapeKind.RoundRect, 3, 3, 184, 134, stroke: new SKColor(0x2F, 0x6F, 0xED), strokeWidth: 0.35, corner: 2));
        Add(t, Text("{{hotel}} · 入住登记单", 20, 8, 150, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("CHECK-IN REGISTRATION", 20, 16.5, 150, 4, 6.5, align: HorizontalAlign.Center));
        Add(t, Line(8, 23, 174));
        Add(t, InfoLine("宾客姓名：{{guest}}　　证件号码：{{guestId}}", 10, 27, 170, 8.5));
        Add(t, InfoLine("联系电话：{{phone2}}　　人数：1 人", 10, 35.5, 170, 8.5));
        Add(t, InfoLine("房型房号：{{room}}", 10, 44, 170, 8.5));
        Add(t, InfoLine("入住时间：{{in2}}　　退房时间：{{out2}}　　晚数：{{nights}}", 10, 52.5, 170, 8.5));
        Add(t, InfoLine("房费：￥{{roomPrice}} / 晚　　押金：￥{{deposit}}（现金或预授权）", 10, 61, 170, 8.5));
        Add(t, Table(10, 70, 170, 22, 8,
            new[]
            {
                ("日期", 40, "", HorizontalAlign.Center),
                ("房费（元）", 40, "", HorizontalAlign.Right),
                ("杂费（元）", 40, "", HorizontalAlign.Right),
                ("小计（元）", 50, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "11-04", "388.00", "0.00", "388.00" },
                new() { "11-05", "388.00", "0.00", "388.00" },
            }));
        Add(t, InfoLine("预计消费合计：￥{{total2}}", 10, 95, 170, 8.5, true));
        Add(t, InfoLine("宾客签名：____________　　前台经办：____________", 10, 104, 170, 8.5));
        Add(t, InfoLine("温馨提示：退房时间为 12:00 前；贵重物品请寄存前台保险箱。", 10, 113, 170, 7.5));
        Add(t, Text("房卡 2 张　WiFi：YunQi-8F　密码 884288", 10, 122, 170, 6, 8));
        return t;
    }

    static PrintTemplate BuildBeverageList()
    {
        var t = NewTemplate(190, 140);
        t.DesignData = new(Extra(
            ("shop2", "云栖轩 · 8 号包厢"), ("waiter", "服务员 08"), ("people", "4"), ("no2", "A-082"),
            ("when", "2026-11-04 18:22"), ("total2", "368.00")));

        Add(t, Text("{{shop2}}", 20, 4, 150, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("酒水单 BEVERAGE LIST", 20, 13, 150, 5, 7.5, align: HorizontalAlign.Center));
        Add(t, InfoLine("单号：{{no2}}　　日期：{{when}}　　人数：{{people}}　　服务员：{{waiter}}", 10, 21, 170, 8.5));
        Add(t, Line(8, 30, 174));
        Add(t, Table(10, 34, 170, 66, 9,
            new[]
            {
                ("品名", 56, "", HorizontalAlign.Left),
                ("规格", 34, "", HorizontalAlign.Center),
                ("数量", 20, "", HorizontalAlign.Center),
                ("单价（元）", 28, "", HorizontalAlign.Right),
                ("金额（元）", 32, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "乌龙茶（壶）", "500ml", "2", "28.00", "56.00" },
                new() { "鲜榨橙汁", "300ml", "2", "22.00", "44.00" },
                new() { "精酿啤酒", "330ml", "4", "25.00", "100.00" },
                new() { "气泡水", "330ml", "2", "18.00", "36.00" },
                new() { "红酒（长城干红）", "750ml", "1", "132.00", "132.00" },
            }));
        Add(t, Text("消费合计：￥{{total2}}", 10, 104, 90, 7, 11, bold: true));
        Add(t, InfoLine("签字确认：____________", 118, 104, 62, 8.5));
        Add(t, InfoLine("加单请呼叫服务员；外带酒水收取开瓶费。", 10, 116, 170, 7.5));
        return t;
    }

    // ── 食品包装 / 人事行政 ────────────────────────────────────────────────

    static PrintTemplate BuildFoodRetain()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(
            ("canteen", "宏达实业员工食堂"), ("week", "第 45 周（11-02 至 11-06）"), ("manager", "赵主管"),
            ("retainHours", "48 小时"), ("temp", "0-8℃")));

        Add(t, Text("食品留样记录表", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("单位食堂：{{canteen}}　　留样冷藏温度：{{temp}}　　留样时长：{{retainHours}}", 8, 28, 194, 9.5));
        Add(t, InfoLine("记录周期：{{week}}　　依据：《餐饮服务食品安全操作规范》", 8, 36, 194, 9.5));
        Add(t, Line(8, 45, 194));
        Add(t, Table(8, 50, 194, 190, 9,
            new[]
            {
                ("日期", 24, "", HorizontalAlign.Center),
                ("餐次", 20, "", HorizontalAlign.Center),
                ("留样品名", 56, "", HorizontalAlign.Left),
                ("留样量(g)", 24, "", HorizontalAlign.Center),
                ("留样时间", 30, "", HorizontalAlign.Center),
                ("留样人", 40, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "11-02", "午餐", "红烧肉 / 清炒时蔬 / 米饭", "125", "11:40", "王厨师" },
                new() { "11-02", "晚餐", "清蒸鱼 / 番茄蛋汤", "125", "17:40", "王厨师" },
                new() { "11-03", "午餐", "宫保鸡丁 / 紫菜蛋汤", "125", "11:40", "李厨师" },
                new() { "11-03", "晚餐", "回锅肉 / 炒青菜", "125", "17:40", "李厨师" },
                new() { "11-04", "午餐", "糖醋排骨 / 冬瓜汤", "125", "11:40", "王厨师" },
                new() { "11-04", "晚餐", "青椒肉丝 / 蛋炒饭", "125", "17:40", "李厨师" },
            }));
        Add(t, InfoLine("审核（食品安全员）：{{manager}}　　每周汇总归档，保存不少于 2 周。", 8, 248, 194));
        Add(t, InfoLine("留样柜编号：LY-02　　异常情况记录：无", 8, 258, 194));
        return t;
    }

    static PrintTemplate BuildAttendanceSheet()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("dept2", "仓储部"), ("period2", "2026 年 10 月"), ("leader", "赵主管"), ("hr", "人事 李明")));

        Add(t, Text("月度考勤统计表", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("部门：{{dept2}}　　考勤月份：{{period2}}　　制表：{{hr}}", 8, 28, 194, 9.5));
        Add(t, Line(8, 37, 194));
        Add(t, Table(8, 42, 194, 150, 9,
            new[]
            {
                ("姓名", 32, "", HorizontalAlign.Center),
                ("应出勤(天)", 24, "", HorizontalAlign.Center),
                ("实出勤", 22, "", HorizontalAlign.Center),
                ("迟到(次)", 22, "", HorizontalAlign.Center),
                ("早退(次)", 22, "", HorizontalAlign.Center),
                ("请假(天)", 22, "", HorizontalAlign.Center),
                ("加班(h)", 22, "", HorizontalAlign.Center),
                ("备注", 28, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "张小明", "21", "21", "0", "0", "0", "6", "" },
                new() { "李红", "21", "20", "1", "0", "1", "2", "事假 1 天" },
                new() { "王大伟", "21", "21", "0", "1", "0", "10", "" },
                new() { "陈静", "21", "19", "0", "0", "2", "0", "病假 2 天" },
                new() { "刘强", "21", "21", "2", "0", "0", "14", "" },
                new() { "赵敏", "21", "21", "0", "0", "0", "4", "" },
            }));
        Add(t, InfoLine("部门负责人审核：{{leader}}　　人事复核：{{hr}}　　日期：{{date}}", 8, 200, 194));
        Add(t, InfoLine("说明：迟到 3 次以上按制度处理；加班时数与当月工资单一并核对。", 8, 212, 194));
        return t;
    }

    static PrintTemplate BuildLeaveForm()
    {
        var t = NewTemplate(190, 130);
        t.DesignData = new(Extra(("staff", "张小明"), ("dept2", "仓储部"), ("kind", "事假"),
            ("from", "11 月 10 日"), ("to", "11 月 11 日"), ("days", "2"), ("reason2", "家中老人体检，需陪同前往")));

        Add(t, Text("请假申请单", 45, 6, 100, 8, 14, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("NO. {{orderNo}}", 130, 8, 52, 5, 8, align: HorizontalAlign.Right));
        Add(t, Shape(ShapeKind.RoundRect, 5, 17, 180, 70, stroke: SKColors.Black, strokeWidth: 0.3, corner: 1));
        Add(t, InfoLine("申请人：{{staff}}　　部门：{{dept2}}　　日期：{{date}}", 11, 22, 168, 9));
        Add(t, InfoLine("请假类型：{{kind}}　　共 {{days}} 天", 11, 31.5, 168, 9));
        Add(t, InfoLine("起止时间：{{from}} 至 {{to}}", 11, 41, 168, 9));
        Add(t, InfoLine("请假事由：{{reason2}}", 11, 50.5, 168, 9));
        Add(t, InfoLine("工作交接：已交接给 王大伟，期间电话保持畅通。", 11, 60, 168, 9));
        Add(t, Line(8, 90, 174));
        Add(t, InfoLine("组长意见：□同意 □不同意　　签字：__________", 8, 93.5, 174, 8.5));
        Add(t, InfoLine("部门经理：□同意 □不同意　　签字：__________", 8, 102.5, 174, 8.5));
        Add(t, InfoLine("人事备案：□已登记　　签字：__________　　销假日期：______", 8, 111.5, 174, 8.5));
        return t;
    }

    static PrintTemplate BuildOnboardingForm()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(
            ("staff", "张小明"), ("gender", "男"), ("birth", "1992-06"), ("native", "浙江杭州"),
            ("phone2", "138****6688"), ("idcard", "330***********0011"), ("school", "浙江工业大学"),
            ("major", "机械工程"), ("degree", "本科"), ("entryDate", "2026-11-04"),
            ("job2", "仓储主管"), ("contact", "张父 139****0001")));

        Add(t, Text("员工入职登记表", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("应聘部门：{{dept}}　　岗位：{{job2}}　　入职日期：{{entryDate}}", 8, 28, 194, 9.5));
        Add(t, Line(8, 37, 194));
        Add(t, Text("一、基本信息", 8, 42, 80, 6, 10.5, bold: true));
        Add(t, Table(8, 50, 194, 42, 9.5,
            new[]
            {
                ("姓名", 32, "", HorizontalAlign.Center),
                ("性别", 24, "", HorizontalAlign.Center),
                ("出生年月", 34, "", HorizontalAlign.Center),
                ("籍贯", 34, "", HorizontalAlign.Center),
                ("联系电话", 70, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "张小明", "男", "1992-06", "浙江杭州", "138****6688" },
                new() { "身份证号", "330***********0011", "婚姻状况", "已婚", "紧急联系人" },
                new() { "现住址", "上海市浦东新区博云路 2 号", "", "", "张父 139****0001" },
            }));
        Add(t, Text("二、教育经历（由最高学历填起）", 8, 96, 120, 6, 10.5, bold: true));
        Add(t, Table(8, 104, 194, 32, 9.5,
            new[]
            {
                ("起止年月", 50, "", HorizontalAlign.Center),
                ("学校", 60, "", HorizontalAlign.Center),
                ("专业", 44, "", HorizontalAlign.Center),
                ("学历", 40, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "2011.09 - 2015.06", "浙江工业大学", "机械工程", "本科" },
                new() { "", "", "", "" },
            }));
        Add(t, Text("三、工作经历", 8, 140, 100, 6, 10.5, bold: true));
        Add(t, Table(8, 148, 194, 32, 9.5,
            new[]
            {
                ("起止年月", 50, "", HorizontalAlign.Center),
                ("工作单位", 70, "", HorizontalAlign.Center),
                ("职位", 44, "", HorizontalAlign.Center),
                ("离职原因", 30, "", HorizontalAlign.Center),
            },
            rows: new List<List<string>>
            {
                new() { "2021.03 - 2026.10", "上海某供应链公司", "仓储组长", "个人发展" },
                new() { "", "", "", "" },
            }));
        Add(t, Text("四、声明与签名", 8, 184, 100, 6, 10.5, bold: true));
        Add(t, Text("本人承诺以上信息真实有效，如有隐瞒愿承担相应责任并接受公司处理。", 8, 192, 194, 8, 9.5));
        Add(t, InfoLine("员工签名：____________　　人事确认：____________　　日期：{{date}}", 8, 206, 194, 9.5));
        Add(t, InfoLine("入职引导人：王大伟　　工牌/门禁/邮箱：已开通 □", 8, 220, 194));
        return t;
    }

    static PrintTemplate BuildResignCert()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(
            ("staff", "张小明"), ("idcard", "330***********0011"), ("entryDate", "2021-03-15"),
            ("leaveDate", "2026-10-31"), ("job2", "仓储主管"), ("dept2", "仓储部")));

        Add(t, Text("离 职 证 明", 55, 40, 100, 12, 18, bold: true, align: HorizontalAlign.Center));
        Add(t, Line(30, 58, 150));
        Add(t, Text("兹证明 张小明（身份证号：330***********0011）自 2021 年 3 月 15 日入职我单位，"
            + "曾任仓储部仓储主管一职，双方已于 2026 年 10 月 31 日依法解除劳动合同关系，"
            + "相关手续与工作交接均已办结。",
            30, 74, 150, 44, 12, align: HorizontalAlign.Left));
        Add(t, Text("该员工在职期间无不良记录，特此证明。", 30, 124, 150, 10, 12));
        Add(t, InfoLine("特此证明。", 30, 148, 150, 12));
        Add(t, InfoLine("单位名称（盖章）：宏达实业有限公司", 90, 190, 110, 10, true));
        Add(t, InfoLine("日期：2026 年 11 月 04 日", 90, 212, 110, 10, true));
        Add(t, Text("备注：本证明一式两份，员工与单位各执一份；用于办理社保转移与再就业手续。",
            30, 250, 150, 12, 9));
        return t;
    }

    static PrintTemplate BuildIncomeCert()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(
            ("staff", "张小明"), ("idcard", "330***********0011"), ("job2", "仓储主管"),
            ("entryDate", "2021-03-15"), ("monthly", "9,500"), ("yearly", "133,000"), ("purpose2", "银行贷款")));

        Add(t, Text("收 入 证 明", 55, 40, 100, 12, 18, bold: true, align: HorizontalAlign.Center));
        Add(t, Line(30, 58, 150));
        Add(t, Text("兹证明 张小明（身份证号：330***********0011）为本单位正式员工，"
            + "于 2021 年 3 月 15 日入职，现任仓储部仓储主管一职。",
            30, 74, 150, 32, 12));
        Add(t, InfoLine("月均收入：人民币 9,500 元（税前）", 30, 112, 150, 12, true));
        Add(t, InfoLine("年收入：人民币 133,000 元（税前，含年终奖）", 30, 126, 150, 12, true));
        Add(t, Text("本证明仅用于该员工办理银行贷款用途，我单位对本证明内容的真实性负责。",
            30, 146, 150, 30, 12));
        Add(t, InfoLine("单位名称（盖章）：宏达实业有限公司", 90, 190, 110, 10, true));
        Add(t, InfoLine("人事部（签字）：__________　财务部（签字）：__________", 90, 212, 110, 10));
        Add(t, InfoLine("日期：2026 年 11 月 04 日", 90, 228, 110, 10, true));
        return t;
    }

    // ── 报表单据 / 教育答题 / 财税金融 ─────────────────────────────────────

    static PrintTemplate BuildPurchaseRequest()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("dept2", "仓储部"), ("applicant", "张小明"), ("project", "Q4 产能扩充"),
            ("budget", "仓储耗材专项 5 万元"), ("needed", "2026-11-20")));

        Add(t, Text("采购申请单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("NO. {{orderNo}}", 130, 14, 72, 5, 8, align: HorizontalAlign.Right));
        Add(t, InfoLine("申请部门：{{dept2}}　　申请人：{{applicant}}　　申请日期：{{date}}", 8, 27, 194, 9.5));
        Add(t, InfoLine("关联项目：{{project}}　　预算来源：{{budget}}　　需求日期：{{needed}}", 8, 35, 194, 9.5));
        Add(t, Line(8, 44, 194));
        Add(t, Table(8, 48, 194, 80, 9.5,
            new[]
            {
                ("序号", 14, "", HorizontalAlign.Center),
                ("物品名称", 60, "", HorizontalAlign.Left),
                ("规格型号", 36, "", HorizontalAlign.Left),
                ("数量", 20, "", HorizontalAlign.Center),
                ("预算单价（元）", 30, "", HorizontalAlign.Right),
                ("小计（元）", 34, "", HorizontalAlign.Right),
            },
            rows: new List<List<string>>
            {
                new() { "1", "不锈钢托盘", "600×400", "40", "45.00", "1,800.00" },
                new() { "2", "电子称重仪", "TCS-150", "4", "140.00", "560.00" },
                new() { "3", "手持扫码枪", "HS-02", "6", "320.00", "1,920.00" },
                new() { "4", "缓冲气泡膜", "50m/卷", "30", "18.00", "540.00" },
            }));
        Add(t, Text("合计金额（大写）：肆仟捌佰贰拾元整　　（小写）￥4,820.00", 8, 134, 194, 7, 10, bold: true));
        Add(t, InfoLine("采购原因：旺季备货，现有托盘与称重设备不足，扫码枪老旧影响出入库效率。", 8, 146, 194));
        Add(t, Shape(ShapeKind.RoundRect, 8, 158, 194, 40, stroke: SKColors.Gray, strokeWidth: 0.25, corner: 2));
        Add(t, InfoLine("部门负责人：□同意 □不同意　签字：__________", 14, 163, 180, 8.5));
        Add(t, InfoLine("采购部核价：□已核价　签字：__________", 14, 173, 180, 8.5));
        Add(t, InfoLine("总经理审批：□同意 □不同意　签字：__________", 14, 183, 180, 8.5));
        return t;
    }

    static PrintTemplate BuildExpenseClaim()
    {
        var t = NewTemplate(190, 140);
        t.DesignData = new(Extra(("staff", "张小明"), ("dept2", "仓储部"), ("reason2", "杭州客户巡检出差"),
            ("period2", "11-02 至 11-03"), ("total2", "1,286.00"), ("big2", "壹仟贰佰捌拾陆元整")));

        Add(t, Text("费用报销单", 45, 5, 100, 8, 14, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("报销日期：{{date}}", 122, 7, 62, 5, 8, align: HorizontalAlign.Right));
        Add(t, InfoLine("报销人：{{staff}}　　部门：{{dept2}}　　事由：{{reason2}}", 6, 16, 178, 8.5));
        Add(t, InfoLine("费用期间：{{period2}}　　单据张数：7 张", 6, 25, 178, 8.5));
        Add(t, Line(6, 33.5, 178));
        Add(t, Table(6, 36, 178, 52, 8.5,
            new[]
            {
                ("费用类别", 40, "", HorizontalAlign.Center),
                ("发生日期", 30, "", HorizontalAlign.Center),
                ("单据数", 22, "", HorizontalAlign.Center),
                ("金额（元）", 34, "", HorizontalAlign.Right),
                ("备注", 52, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "高铁票", "11-02/11-03", "2", "296.00", "往返" },
                new() { "住宿费", "11-02", "1", "560.00", "如家商旅" },
                new() { "市内交通", "11-02/11-03", "3", "86.00", "出租车" },
                new() { "出差餐补", "-", "-", "344.00", "2 天 × 172 元" },
            }));
        Add(t, Text("合计（大写）：{{big2}}　　（小写）￥{{total2}}", 6, 92, 178, 6.5, 9.5, bold: true));
        Add(t, InfoLine("部门经理：签字：__________", 6, 102, 80, 8.5));
        Add(t, InfoLine("财务审核：签字：__________", 92, 102, 80, 8.5));
        Add(t, InfoLine("总经理审批：签字：__________", 6, 111.5, 80, 8.5));
        Add(t, InfoLine("出纳付款：签字：__________", 92, 111.5, 80, 8.5));
        Add(t, InfoLine("付款方式：□现金 □转账（账号见工资卡）", 6, 121, 178, 8));
        return t;
    }

    static PrintTemplate BuildScoreReport()
    {
        var t = NewTemplate(210, 297);
        t.DesignData = new(Extra(("staff", "张小明"), ("className", "生产一部"), ("term", "2026 年第一季度培训"),
            ("teacher", "李明"), ("rank", "3 / 26")));

        Add(t, Text("员工培训成绩单", 55, 12, 100, 10, 16, bold: true, align: HorizontalAlign.Center));
        Add(t, InfoLine("姓名：{{staff}}　　班组：{{className}}　　考核周期：{{term}}", 8, 28, 194, 9.5));
        Add(t, Line(8, 37, 194));
        Add(t, Table(8, 42, 194, 110, 9.5,
            new[]
            {
                ("考核科目", 56, "", HorizontalAlign.Left),
                ("满分", 26, "", HorizontalAlign.Center),
                ("成绩", 26, "", HorizontalAlign.Center),
                ("班级均分", 28, "", HorizontalAlign.Center),
                ("等级", 26, "", HorizontalAlign.Center),
                ("备注", 32, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "安全生产规范", "100", "96", "88.2", "优", "" },
                new() { "设备操作规程", "100", "92", "84.6", "良", "" },
                new() { "质量管理体系", "100", "88", "82.1", "良", "" },
                new() { "仓储物流实务", "100", "94", "85.7", "优", "" },
                new() { "应急处置演练", "100", "90", "83.5", "良", "" },
            }));
        Add(t, Text("综合成绩：92.0　　班级排名：{{rank}}　　评定：优秀", 8, 158, 194, 7, 10.5, bold: true));
        Add(t, Shape(ShapeKind.RoundRect, 8, 172, 194, 40, stroke: SKColors.Gray, strokeWidth: 0.2, corner: 1));
        Add(t, Text("班主任寄语：安全意识扎实、实操能力突出，望继续加强质量体系知识的系统学习，"
            + "下一周期目标成长为储备组长。",
            12, 176, 186, 32, 9.5));
        Add(t, InfoLine("培训讲师：{{teacher}}　　人事备案：李明　　日期：{{date}}", 8, 220, 194));
        return t;
    }

    static PrintTemplate BuildUsedCarInvoice()
    {
        var t = NewTemplate(240, 140);
        t.DesignData = new(Extra(
            ("buyer", "李红"), ("buyerId", "330***********2233"), ("seller", "杭州诚运二手车"),
            ("sellerId", "91330106MA2H***"), ("plate2", "浙A·D8821"), ("vin", "LSVAA1234E2118842"),
            ("brand2", "大众 朗逸 1.6L 自动"), ("regDate", "2019-05"), ("odometer", "6.8 万公里"),
            ("price2", "52,800"), ("big2", "伍万贰仟捌佰元整"), ("dealer", "杭州二手车交易市场")));

        Add(t, Shape(ShapeKind.RoundRect, 2, 2, 236, 136, stroke: new SKColor(0x8B, 0x00, 0x00), strokeWidth: 0.4, corner: 2));
        Add(t, Text("二手车销售统一发票（票样）", 50, 5, 140, 8, 13, bold: true, align: HorizontalAlign.Center));
        Add(t, Text("发票代码：3300 2411 8890　发票号码：04403189", 130, 5, 106, 5, 7.5, align: HorizontalAlign.Right));
        Add(t, Shape(ShapeKind.Plus, 8, 15, 6, 6, stroke: SKColors.DarkRed, strokeWidth: 0.35));
        Add(t, Text("机器编码：22 66 88 03 12 47 51 09", 17, 16, 120, 4.5, 7));
        Add(t, Text("买方单位/个人：{{buyer}}　　证件号码：{{buyerId}}", 8, 23, 224, 5.5, 8));
        Add(t, Text("卖方单位/个人：{{seller}}　　证件号码：{{sellerId}}", 8, 30, 224, 5.5, 8));
        Add(t, Table(8, 37, 224, 44, 8,
            new[]
            {
                ("车辆信息", 60, "", HorizontalAlign.Left),
                ("内容", 164, "", HorizontalAlign.Left),
            },
            rows: new List<List<string>>
            {
                new() { "车牌号", "浙A·D8821" },
                new() { "车辆类型 / 品牌型号", "小型轿车 / 大众 朗逸 1.6L 自动" },
                new() { "车架号 / VIN", "LSVAA1234E2118842" },
                new() { "登记日期 / 表显里程", "2019-05 / 6.8 万公里" },
            }));
        Add(t, Text("车价款（大写）：{{big2}}　　（小写）￥{{price2}}", 8, 84, 224, 7, 9.5, bold: true));
        Add(t, Text("交易市场：{{dealer}}　　过户手续费由买方承担；车辆自交付之日起风险转移至买方。",
            8, 93, 224, 10, 8));
        Add(t, InfoLine("买方（签章）：____________　　卖方（签章）：____________", 8, 106, 224, 8));
        Add(t, InfoLine("开票人：王会计　　开票日期：{{date}}　　市场盖章：____________", 8, 118, 224, 8));
        Add(t, Text("本票样仅供打印演示，不作为税收凭证。", 8, 130, 224, 4, 6));
        return t;
    }
}

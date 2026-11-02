// hiprint（vue-plugin-hiprint）模板模块示例，随六个宿主样例一起输出。
// 「文件 → 导入 hiprint 模板…」同时接受：这种整份模块源码、设计器 getJson() 的片段、严格 JSON。
// 字段 title / orderNo / sku 与样例数据 SampleData 对齐，导入后画布上直接能看到绑定结果。
// 最后一个元素带 JS formatter：CrossPrint 内嵌脚本引擎会执行它（渲染时给订单号加前缀），导入无降级告警。
export const json = {
  panels: [{
    index: 0,
    name: "A4 导入示例",
    width: 210,
    height: 297,
    paperHeader: 30,
    paperFooter: 810,
    printElements: [{
      options: {
        left: 20, top: 20, width: 555, height: 32,
        title: "hiprint 导入示例",
        fontSize: 18,
        fontWeight: "700",
        textAlign: "center",
      },
      printElementType: { title: "文本", type: "text" },
    }, {
      options: { left: 20, top: 58, width: 555, height: 10, borderWidth: 0.75 },
      printElementType: { title: "横线", type: "hline" },
    }, {
      options: {
        left: 20, top: 72, width: 555, height: 22,
        title: "标题",
        field: "title",
        fontSize: 11,
      },
      printElementType: { title: "文本", type: "text" },
    }, {
      options: {
        left: 20, top: 98, width: 300, height: 22,
        title: "商品编码",
        field: "sku",
        hideTitle: true,
        fontSize: 10,
        color: "#333333",
      },
      printElementType: { title: "文本", type: "text" },
    }, {
      options: {
        left: 20, top: 130, width: 90, height: 90,
        title: "订单号",
        field: "orderNo",
        textType: "qrcode",
        qrCodeLevel: 3,
      },
      printElementType: { title: "自定义文本", type: "text" },
    }, {
      options: {
        left: 130, top: 165, width: 320, height: 50,
        field: "sku",
        barcodeType: "code128",
      },
      printElementType: { title: "条形码", type: "barcode" },
    }, {
      options: {
        left: 20, top: 240, width: 555, height: 150,
        field: "items",
        fontSize: 9,
        tableBorder: "1px solid #333333",
        tableHeaderBackground: "#F2F2F2",
        tableHeaderFontWeight: "700",
        columns: [[
          { title: "品名", field: "name", width: 300, align: "left" },
          { title: "数量", field: "qty", width: 100, align: "center" },
          {
            title: "金额", field: "amount", width: 155, align: "right",
            tableSummary: "sum", tableSummaryTitle: true,
          },
        ]],
      },
      printElementType: { title: "表格", type: "table" },
    }, {
      options: {
        left: 20, top: 400, width: 300, height: 20,
        title: "订单号",
        field: "orderNo",
        formatter: function(value) {
          return "NO-" + value;
        },
      },
      printElementType: { title: "文本", type: "text" },
    }],
  }],
};

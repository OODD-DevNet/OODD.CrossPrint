<p align="center">
  <img src="assets/oodd-crossprint-logo.png" width="128" alt="OODD CrossPrint Logo" />
</p>

# OODD CrossPrint

A cross-platform .NET print-template component. **Write one template, feed one data object, hit Print** — that's it. Avalonia, WPF, WinForms, Eto.Forms, WinUI, Uno, .NET MAUI — all seven hosts share the exact same rendering engine. What you see on the canvas is exactly what comes out as PDF and exactly what lands on paper. **True WYSIWYG, not two rendering paths pretending to be one.**

Already using hiprint (vue-plugin-hiprint / Vue-Print-Designer)? Perfect — **its exports go straight in**. No re-drawing.

> Templates speak only millimeters and points. There is no pixel here. The engine draws every pixel; host controls just "put the canvas on screen" — they don't interfere.

[中文](README.md) | **English** · [Live WebAssembly demo](https://oodd-devnet.github.io/OODD.CrossPrint/)

---

## First — what the designer and print preview look like

<p align="center">
  <img src="assets/print-design-screenshot.png" width="48%" alt="Designer canvas" />
  <img src="assets/print-preview-screenshot.png" width="48%" alt="Print preview" />
</p>

Left: Avalonia-hosted designer with the "kitchen sink" template (all eight element types, plus formatter/styler scripts wired up). Right: print preview of the same template. Both windows go through the same rendering path — drag an element on the left, the right updates instantly. Not "roughly similar", **pixel-identical**.

Want to try it yourself? Click the **Live WebAssembly demo** link above — runs in your browser, nothing to install.

---

## Quick start — two paths, pick one

### Path A: run a sample first (recommended)

Requires **.NET SDK 8.0 or later**. Open PowerShell, cd into the repo root, pick one:

```powershell
# Avalonia (cross-platform — Windows / Linux / macOS, recommended)
dotnet run --project samples/CrossPrint.Samples.Avalonia

# Windows-only hosts
dotnet run --project samples/CrossPrint.Samples.Wpf
dotnet run --project samples/CrossPrint.Samples.WinForms
dotnet run --project samples/CrossPrint.Samples.Eto
dotnet run --project samples/CrossPrint.Samples.WinUi
dotnet run --project samples/CrossPrint.Samples.Uno
dotnet run --project samples/CrossPrint.Samples.Maui
```

Samples ship with two templates: `kitchen-sink.template.json` (all eight element types) and `hiprint-sample.js`. Open it and click **File → Import hiprint template…** — drop the `.js` in and you'll see hiprint import in action.

### Path B: embed via NuGet into your project

Figure out which UI framework you're using, grab the matching host package. CrossPrint cleanly separates the **engine** (rendering, data binding, scripts, PDF) from the **host adapter** (the glue that puts the canvas into a WPF/Avalonia window) — **core packages don't depend on any specific UI framework**.

| What you're building | Install these |
|---|---|
| Core (all hosts need these) | `CrossPrint.Core` · `CrossPrint.Engine` · `CrossPrint.Designer` · `CrossPrint.Printing` |
| Avalonia | `CrossPrint.Hosts.Avalonia` |
| WPF | `CrossPrint.Hosts.Wpf` |
| WinForms | `CrossPrint.Hosts.WinForms` |
| Eto.Forms | `CrossPrint.Hosts.Eto` |
| WinUI 3 | `CrossPrint.Hosts.WinUi` |
| Uno | `CrossPrint.Hosts.Uno` |
| .NET MAUI | `CrossPrint.Hosts.Maui` |

Concrete example (Avalonia host):

```powershell
dotnet add package CrossPrint.Core
dotnet add package CrossPrint.Engine
dotnet add package CrossPrint.Hosts.Avalonia
```

```csharp
using CrossPrint.Core;
using CrossPrint.Hosts.Avalonia;

// Step 1: load a template (JSON string or hiprint file — both work)
var load = TemplateJson.Load(File.ReadAllText("template.json"),
                             TemplateJsonLoadOptions.Tolerant);

// Step 2: put down a design canvas — users can drag elements
var designer = new DesignerControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001", customer = "Jane Doe" },
};

// Or a read-only preview — show the effect without letting users edit
var preview = new PreviewControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001" },
};
```

Export to PDF:

```csharp
using CrossPrint.Engine;

using var stream = File.Create("invoice.pdf");
var report = PdfExporter.Export(
    designer.Kernel.Template,
    stream,
    data: designer.Data,
    renderer: designer.Renderer);

// report tells you about missing fields, broken images, script errors —
// never crashes the page
if (report.HasIssues) Console.WriteLine(report);
```

Send to the printer:

```csharp
using CrossPrint.Printing;

var document = new PrintDocument(designer.Kernel.Template, designer.Data);
var report = new RenderReport();

// Option 1: print to PDF file
await new PdfFilePrintTarget()
    .PrintBatchAsync([document], new PrintSettings { OutputPath = "invoice.pdf" }, report);

// Option 2: hand off to the OS print dialog
await new SystemPrintTarget()
    .PrintBatchAsync([document], new SystemPrintOptions(), report);
```

---

## Designer guide — the dumbed-down version

All seven hosts look almost identical:

```
┌──────────────────────────────────────────────────────────┐
│ Menu bar: File (Import hiprint / Load JSON / Print / PDF)  │
├──────────────────────────────────────────────────────────┤
│ Toolbar 1: Text Line Rect Ellipse Image QR Barcode Table   │
│ Toolbar 2: Styles | Copy Delete | Bring Forward / Back …   │
│           | Fit 100% | ☐Grid ☐Rulers ☐SafeArea            │
│           | Print ExportPDF Preview                        │
├────────┬───────────────────────────────┬─────────────────┤
│ Paper   │                               │ Property panel  │
│ Toolbox │         Design canvas          │ (form per type) │
│        │    (wheel zoom / middle pan)   │                 │
├────────┴───────────────────────────────┴─────────────────┤
│ ▸ Template JSON (collapsed by default)                    │
├──────────────────────────────────────────────────────────┤
│ Status bar (zoom, selection bounds, bound field)            │
└──────────────────────────────────────────────────────────┘
```

### Adding elements

- **Method 1**: pick a type on the left toolbox, click *Add to canvas* (or double-press Enter).
- **Method 2**: click the toolbar icons — one click per element type.
- **Method 3**: Ctrl+D to duplicate an existing one — fastest, especially for table columns.

### Editing properties

Select an element. The right panel auto-generates a form specific to that type. Text elements get font size/color/alignment/wrap controls; tables get a two-dimensional header editor plus footer aggregation; images get fit mode and corner radius. **Dragging/scaling never tears down the text box you're typing into** — small detail, matters.

### Right-click menu (don't sleep on it)

Duplicate (Ctrl+D), Delete, Bring Forward / Send Backward / Bring to Front / Send to Back, Horizontal Center / Vertical Center, Fit to Page / 100%, Rotate Paper, Custom Paper Size.

### Template JSON panel (the collapsible one at the bottom)

Expands to show the live JSON. Edit and click **Apply JSON** (or Ctrl+Enter). **You can also paste hiprint JSON here** — the `{"panels":[…]}` output from hiprint's `getJson()` — click Apply and CrossPrint auto-detects the format and converts it.

### Shortcuts (forget them, mouse does everything too)

| Key | Action |
|---|---|
| Arrows | 1mm nudge (Shift = 10mm, Ctrl = 0.1mm) |
| Ctrl+D | Duplicate selection |
| Delete | Delete |
| Esc | Cancel drag / clear selection |
| F | Fit to page |
| 0 | 100% zoom |
| Ctrl+wheel | Zoom |
| Middle drag | Pan (useful when zoomed in) |
| Ctrl+Enter (in JSON panel) | Apply edited JSON |

---

## JS scripts (formatter / styler) — what they *can* do, what they *can't*, with real examples

The most-asked question. Short answer:

> **You can run business logic** — for/while/if, array ops, object ops, regex, closures, recursion, built-in `Math`, `Date`, `JSON`, etc. **You cannot touch the outside world** — no filesystem, no network, no DOM, no `console.log`, no C# object access. It's a pure sandbox.

CrossPrint runs these scripts with [Jint](https://github.com/sebastienros/jint) — a pure .NET JavaScript engine. **Semantics are identical to hiprint's formatter/styler** — if you already wrote scripts in hiprint, paste them in and they run.

### Two script types

**formatter** — replaces the displayed text. Takes the bound field's raw value, returns the string you want to show.

**styler** — changes styles. Based on the bound value, returns a style object (can be a JS object or a CSS string). Example: "color the cell red and bold if the amount is over 1000".

### Script signatures (what exactly gets passed in)

**Element-level scripts** (text, barcode, QR):

```javascript
function(value, options, target, templateData) { ... }
```

| Parameter | What it is |
|---|---|
| `value` | The raw value of the bound field. If you bound `order.total`, this is the number. |
| `options` | `{ id: "el_123", type: "text" }` — element's own id and type |
| `target` | Same as `options` (hiprint compatibility) |
| `templateData` | **The entire print data object**. Access `templateData.customer.name`, `templateData.order.items`, anything you need. |

**Table column-level scripts** (each column in a table can have its own formatter/styler):

```javascript
function(value, row, index, options, templateData) { ... }
```

Two extra params — `row` is the full object of the current row (you can access `row.product.name`), `index` is the row number (0-based).

### What JS features are available

✅ **Yes**: variables, if/else, for/while/do, switch, arrays (map/filter/reduce/find/slice/splice — all there), objects, functions, arrow functions, closures, recursion, regex, destructuring, spread, template strings, `Math.*` (floor/ceil/round/random/pow/sqrt/sin/cos…), `Date` (but can't get system time — only construct from passed data), `JSON.parse` / `JSON.stringify`

❌ **No**: `fetch`, `XMLHttpRequest`, `fs`, `require`/`import`, `window`, `document`, `console` (hiprint never had it either), `setTimeout`/`setInterval` (printing is synchronous — no async), `new Date()` without args **won't** give you current time, **no CLR (C#) type instantiation or access** — Jint has CLR interop disabled by default.

### Safety limits (these aren't bugs — they're intentional)

| Limit | Value | Why |
|---|---|---|
| Per-run timeout | **250ms** | A dead loop can't hang printing for 10 seconds |
| Max statements | **100,000** | Prevents `while(true){}` from bypassing the timeout |
| Max recursion depth | **64** | Prevents stack overflow |
| Script cache | **256-entry LRU** | Same source doesn't re-parse every time |

**What happens if you exceed a limit?** Nothing. The script is skipped, the element renders with its original value/default style, and a warning goes into the report. **Rendering never interrupts.** You will never see "script timeout crashed the whole page".

### A pile of real, usable scripts

Enough theory. Here's code you can copy-paste.

---

**Example 1: formatter — currency formatting**

```javascript
function(value) {
    if (value == null || value === '') return '$0.00';
    var n = typeof value === 'number' ? value : parseFloat(value);
    if (isNaN(n)) return String(value);
    return '$' + n.toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}
// input: 1234567.8  → output: "$1,234,567.80"
// input: null       → output: "$0.00"
// input: "hello"    → output: "hello" (parse failed — keep as-is)
```

---

**Example 2: formatter — date formatting (bound value is ISO string)**

```javascript
function(value) {
    if (!value) return '';
    var d = new Date(value);
    if (isNaN(d.getTime())) return String(value);
    var y = d.getFullYear();
    var m = String(d.getMonth() + 1).padStart(2, '0');
    var day = String(d.getDate()).padStart(2, '0');
    return y + '-' + m + '-' + day;
}
// input: "2026-11-02T08:30:00Z"  → output: "2026-11-02"
```

---

**Example 3: formatter — cross-element data**

A text element bound to `item.discount`, but you want to show both original price and discounted price. Original price lives at `templateData.order.originalPrice`:

```javascript
function(discount, options, target, templateData) {
    var original = templateData.order.originalPrice || 0;
    var disc = typeof discount === 'number' ? discount : parseFloat(discount) || 0;
    var final = original * (1 - disc);
    return 'Was $' + original.toFixed(2) + ' → Now $' + final.toFixed(2);
}
// displays: "Was $100.00 → Now $85.00"
```

---

**Example 4: styler — highlight amounts over 1000**

```javascript
function(value) {
    var n = typeof value === 'number' ? value : parseFloat(value);
    if (!isNaN(n) && n > 1000) {
        return { color: '#c0392b', fontWeight: 'bold' };
    }
    return null; // null / undefined / empty object = don't change style
}
```

Same thing as a CSS string — pick whichever you prefer:

```javascript
function(value) {
    var n = typeof value === 'number' ? value : parseFloat(value);
    if (!isNaN(n) && n > 1000) {
        return 'color:#c0392b;font-weight:bold';
    }
    return '';
}
```

---

**Example 5: styler — status badges**

Order status field ('pending' / 'paid' / 'shipped' / 'cancelled'), each gets its own color:

```javascript
function(value) {
    var map = {
        'pending':   { color: '#856404', backgroundColor: '#fff3cd' },
        'paid':      { color: '#0f5132', backgroundColor: '#d1e7dd' },
        'shipped':   { color: '#084298', backgroundColor: '#cfe2ff' },
        'cancelled': { color: '#842029', backgroundColor: '#f8d7da', textDecoration: 'line-through' },
    };
    return map[value] || null;
}
```

---

**Example 6: table column formatter — sequence number**

Add a "No." column to your table without adding a field to the data:

```javascript
function(value, row, index) {
    return String(index + 1);
}
// value is undefined (column has no field bound), row is the row object, index is 0-based
```

---

**Example 7: table column styler — grey out rows from the 3rd one on**

```javascript
function(value, row, index) {
    if (index >= 2) {
        return { color: '#999999' };
    }
    return null;
}
```

---

**Example 8: formatter — EAN-13 barcode self-check**

```javascript
function(value) {
    if (!value || value.length !== 13 || !/^\d+$/.test(value)) return value;
    var sum = 0;
    for (var i = 0; i < 12; i++) {
        sum += (i % 2 === 0 ? 1 : 3) * parseInt(value[i], 10);
    }
    var check = (10 - (sum % 10)) % 10;
    if (check === parseInt(value[12], 10)) return value;
    return value + ' ⚠️';  // mark invalid barcodes
}
```

### Style properties styler can return

Both JS object (camelCase) and CSS string (kebab-case) forms work. Full list:

| JS object key | CSS equivalent | Example |
|---|---|---|
| `color` | `color` | `'#ff0000'` or `'red'` |
| `backgroundColor` | `background-color` | `'#f5f5f5'` |
| `fontSize` | `font-size` | `'12pt'` / `'16px'` / `'4.2mm'` (no unit = px) |
| `fontWeight` | `font-weight` | `'bold'` / `'normal'` / `'700'` (≥700 = bold) |
| `fontStyle` | `font-style` | `'italic'` / `'normal'` |
| `textDecoration` | `text-decoration` | `'underline'` / `'line-through'` / `'none'` (combine: `'underline line-through'`) |
| `letterSpacing` | `letter-spacing` | `'2pt'` |
| `lineHeight` | `line-height` | `'1.5'` (unitless multiplier) |
| `textAlign` | `text-align` | `'left'` / `'center'` / `'right'` / `'justify'` |
| `verticalAlign` | `vertical-align` | `'top'` / `'middle'` / `'bottom'` |
| `padding` | `padding` | `'2mm 4mm'` (shorthand, 1-4 values) |
| `paddingLeft` / `Top` / `Right` / `Bottom` | `padding-*` | `'2mm'` |
| `borderColor` | `border-color` | `'#333'` |
| `borderWidth` | `border-width` | `'0.5mm'` |
| `borderStyle` | `border-style` | `'solid'` / `'dashed'` / `'dotted'` |

Unknown keys are silently ignored — same tolerance as CSS. A typo won't blow anything up.

### Return-value contract

| Return | formatter | styler |
|---|---|---|
| String | Replaces displayed text | Parsed as CSS |
| JS object (styler) | —— | Parsed as style |
| `null` / `undefined` | **Keeps original value** | **No style change** |
| Number / Boolean | `String()` then replaces | —— |
| Exception | **Keeps original + warning in report** | **No style change + warning in report** |

That last row is important. If your script has a syntax error or throws at runtime, CrossPrint **will not** turn the whole page blank. It treats the script as if it never existed, renders with original values, and logs the exact error (like `TypeError: value.toFixed is not a function`) in the render report. You'll see these warnings in the designer's status bar.

---

## hiprint template import — supported features, unsupported features

The short version first: **90% of hiprint templates drop in and work**. Geometry (pt → mm auto-conversion), element types, data binding, formatter/styler scripts, two-dimensional table headers, barcodes, QR codes — all supported.

The remaining 10% is documented below. **Unsupported features are never silently dropped** — the importer produces an issue list telling you exactly what happened ("this element had a watermark; CrossPrint can't draw watermarks"). It will never quietly eat an element you spent an afternoon on.

### Supported hiprint element types and properties

| hiprint element | Becomes in CrossPrint | Notes |
|---|---|---|
| `text` / `longText` | TextElement | pt → mm auto; fontSize → pt; formatter/styler preserved |
| `hline` / `vline` | LineElement | borderWidth / borderStyle / borderColor all supported |
| `rect` / `oval` | RectangleElement / EllipseElement | Uniform border (no per-side styles); backgroundColor fill |
| `image` | ImageElement | **base64 data URLs only**. External URLs need `IImageResolver`, otherwise placeholder |
| `table` / `tableCustom` (legacy) | TableElement | Two-dimensional headers with colspan/rowspan; flat `fields` array; per-column formatter/styler |
| `barcode` | BarcodeElement | 10 symbologies: Code128, EAN-13, EAN-8, UPC-A, UPC-E, ITF, Codabar, Code39, Code93, MSI |
| `qrcode` | QrCodeElement | 4 error-correction levels (L/M/Q/H); caption with alignment + font |
| `html` | RectangleElement (dashed gray placeholder) | **No HTML rendering** — a dashed box marks where your HTML element used to be |

### Supported hiprint panel-level properties

| hiprint property | CrossPrint handling |
|---|---|
| `width` / `height` | Paper size (mm); auto-matches presets (A4 etc.) or marks as Custom |
| `panelPageRule: "none"` | Continuous paper (height 0, follows content) |
| `paperHeader` / `paperFooter` | Printable-area margins (top/bottom) |
| `formatter` / `styler` (element-level) | Stored verbatim; executed by Jint at render time |
| `testData` | Collected as design-time preview data |
| `transform: "rotate(Ndeg)"` | Extracts N as rotation angle |
| `zIndex` | Mapped directly |
| `qrCodeLevel` / `qrcodeLevel` | 0→L, 1→M, 2→Q, 3→H |
| `barcodeType` / `barcodeMode` | 10 symbology mapping |
| `borderRadius` (rect/oval) | Maps to corner radius |
| `textAlign` / `textContentVerticalAlign` | → HorizontalAlign / VerticalAlign |
| `textContentWrap` | nowrap/clip → no wrap; ellipsis → no wrap + ellipsis; else → wrap |
| `contentPadding` / `contentPaddingLeft` … | Four inner padding values |
| `textDecoration` | underline / line-through supported; overline **not** |
| `fit` (image) | contain / cover / fill / none all supported; scale-down → contain |
| `columns` (table) | 2D header expansion; flat `[{title, field, width}]` array also works |
| `fields` (table, legacy) | Single-row column definition |
| `rowspan` / `colspan` | Expanded into grid |
| `tableSummary` / `tableSummaryTitle` / `tableSummaryText` / `tableSummaryNumFormat` / `tableSummaryColspan` | All supported — footer aggregation |
| `checked: false` | Hidden column; ignored on import |
| `tableTextType` | Kept as-is |

### Unsupported hiprint features (warnings are emitted on import)

| hiprint feature | What happens | Severity | Workaround |
|---|---|---|---|
| **Watermark** `watermarkOptions` | Ignored | ⚠️ Warning | Use a background ImageElement (bottom layer) or custom drawing |
| **Page numbers** `paperNumber*` | Ignored | ⚠️ Warning | Not implemented yet — if demand grows we'll add it |
| **Panel print offset** `leftOffset/topOffset` | Ignored | ⚠️ Warning | CrossPrint doesn't support whole-page offset |
| **HTML elements** | Dashed gray placeholder | ⚠️ Warning | Rebuild with Text + Image + Table |
| **Custom plugin elements** | Skipped (element disappears) | ⚠️ Warning | Redraw with built-in elements |
| **Long-text auto-pagination** `longText` | Clipped to element box — no auto page-break | ⚠️ Warning | No auto-pagination yet |
| **Date/boolean formatting** `dataType` / `format` | Printed as raw value | ⚠️ Warning | Use a formatter script |
| **Chinese amount-to-words** `upperCase` | Printed as raw value | ⚠️ Warning | Write a formatter for it |
| **Forced page break** `pageBreak` | Ignored | ⚠️ Warning | No manual pagination yet |
| **Show on specific pages** `showInPage` / `unShowInPage` | Ignored | ⚠️ Warning | —— |
| **Repeat per page** `fixed` | Printed once only | ⚠️ Warning | —— |
| **Barcode fixed module width** `barWidth` | Scaled to element width (still scannable) | ℹ️ Info | —— |
| **External image URLs** (non-data:) | Requires `IImageResolver`; placeholder otherwise | ℹ️ Info | Convert to base64 or implement resolver |
| **Table-level formatter/styler** | Dropped (only column-level kept) | ℹ️ Info | Use column-level scripts |
| **Grouped headers** `{title, columns:[...]}` | Sub-columns flattened; group title lost | ℹ️ Info | Single-row headers don't have a grouping concept |
| **Multi-panel template** | Imports panel 0 only | ℹ️ Info | Use `HiprintImportOptions.PanelIndex` to import each |
| **transform != rotate(Ndeg)** | Ignored | ℹ️ Info | Only `rotate(Ndeg)` supported |
| **Per-side borders** `borderLeft` / `borderTop` … | Single border style applied to all sides | ℹ️ Info | —— |
| **Text overline** `textDecoration: overline` | Ignored | ℹ️ Info | —— |
| **longTextIndent** | Ignored | ℹ️ Info | —— |

### What input formats the importer accepts

Four formats — pick whichever your tool produces:

1. **Strict JSON** — `{"panels":[{"width":210,"height":297,"printElements":[...]}]}`
2. **hiprint `getJson()` output** — `{"json":{"panels":[...]}}` (wrapped in a `json` key)
3. **JS object literals** — `{ panels: [{ width: 210, printElements: [...] }] }` (unquoted keys OK)
4. **Entire `.js` module files** — `export const json = {...}` or `module.exports = {...}` — it'll dig the object out

```csharp
using CrossPrint.Core.Hiprint;

var result = HiprintTemplateImporter.Import(File.ReadAllText("my-hiprint-template.js"));

var template = result.Template;                  // drop into designer / renderer / print pipeline
foreach (var issue in result.Issues)             // iterate all degradations/warnings
    Console.WriteLine(issue);

// result.Panels has all panels from the source — if multi-panel (multi-part form),
// show the user the list and import each with PanelIndex
```

---

## Template format — writing your own from scratch

### Units

**Everything millimeters** (`WidthMm`, `XMm`, `YMm`…). **Font sizes in points** (`SizePt`). **No pixels.** No DPI conversion traps. What you specify is exactly what you get on paper.

### Data binding

In a text element write `{{order.customer.name}}` — at runtime it pulls from your data object. In a table set `table.field: "items"` — each row becomes an item in `data.items`.

Data sources can be:
- Any POCO (plain C# class — properties and fields both work, case-insensitive)
- `Dictionary<string, object?>`
- `System.Text.Json` `JsonElement` / `JsonNode`

**Bind failures don't throw** — the value falls back to empty string and a "field xxx not found" warning goes into the report.

### Minimal template JSON

A4 paper + one text element + one table — the bare minimum:

```json
{
  "paper": { "preset": "A4", "widthMm": 210, "heightMm": 297, "orientation": 0 },
  "margins": { "topMm": 5, "bottomMm": 5, "leftMm": 5, "rightMm": 5 },
  "elements": [
    {
      "id": "title",
      "typeKey": "text",
      "xMm": 10, "yMm": 8, "widthMm": 80, "heightMm": 10,
      "text": { "content": "Invoice No.{{orderNo}}", "fontSizePt": 18, "bold": true }
    },
    {
      "id": "items",
      "typeKey": "table",
      "xMm": 10, "yMm": 25, "widthMm": 190, "heightMm": 80,
      "table": {
        "field": "items",
        "columns": [
          { "title": "#", "formatter": "function(v,r,i){return i+1;}" },
          { "title": "Product", "field": "name" },
          { "title": "Unit Price", "field": "price",
            "formatter": "function(v){return '$'+(+v).toFixed(2);}",
            "styler": "function(v){return +v>1000?'color:#c0392b;font-weight:bold':null;}" },
          { "title": "Qty", "field": "qty" },
          { "title": "Subtotal", "formatter": "function(v,r){return '$'+((+r.price)*(+r.qty)).toFixed(2);}" }
        ]
      }
    }
  ]
}
```

---

## Host support

| Host | Minimum .NET | Platform | Notes |
|---|---|---|---|
| Avalonia 11.3 | .NET 8 | Windows / Linux / macOS + Browser/WASM | **Only host that does WASM** |
| WPF | .NET 8 (Windows) | Windows | —— |
| WinForms | .NET 8 (Windows) | Windows | —— |
| Eto.Forms 2.10 | .NET 8 | Windows (portable to other platforms) | —— |
| WinUI 3 (WindowsAppSDK 1.6) | .NET 8 (Windows) | Windows | —— |
| Uno 5.6 | .NET 8 | Windows | Uno 6.x requires SkiaSharp 3.x + net9 — not tracked |
| .NET MAUI 8 | .NET 8 | Windows | —— |

**Core layer has zero UI-framework dependencies** — so on Linux/macOS you can reference just `CrossPrint.Engine` + `CrossPrint.Printing` and run headless PDF generation as a backend service.

---

## Deploying the WebAssembly demo

The Avalonia Browser sample compiles to **pure static files** (~40MB first load, self-contained .NET WASM runtime + main assembly). Drop it on GitHub Pages, Cloudflare Pages, Vercel — any static host works:

```powershell
dotnet workload install wasm-tools
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# Output: samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

**Sub-path deployments** (e.g. `/OODD.CrossPrint/` on GitHub Pages) — already handled by `<base href="./">` in `index.html`. You don't need to change anything.

The repo ships a GitHub Actions workflow ([`.github/workflows/pages-demo.yml`](.github/workflows/pages-demo.yml)) that auto-publishes on every push to main.

---

## Known limitations (read this before filing an issue)

- **QR quiet zone** drawn at 4 modules. Extremely strict scanners might complain — most phones are fine.
- **No manual pagination** — the template renders as one long page. If it's taller than paper, the bottom is clipped. For multi-page layouts, use multiple templates (one per page).
- **No watermark** — hiprint's `watermarkOptions` can't be drawn. Workaround: use a background ImageElement (bottom layer, semi-transparent).
- **No page numbers** — `paperNumberFormat` and friends aren't recognized. Let us know if you actually need this.
- **WASM sample** has no file dialogs (browser sandbox). Importing hiprint templates goes through the template JSON panel at the bottom.
- **.NET Framework 4.x not supported** — SkiaSharp 2.88.9 can run net461, but Jint 4.x and System.Text.Json 8.0 require net8.0. Supporting old .NET would mean downgrading every dependency and maintaining a fork. .NET 8 minimum.

---

## Documentation

- [`docs/template-schema.md`](docs/template-schema.md) — Complete template JSON field reference + hiprint property mapping table
- [`docs/rendering-spec.md`](docs/rendering-spec.md) — Per-element rendering specification (coordinate calc, kerning, table row height…)
- [`docs/webassembly-demo.md`](docs/webassembly-demo.md) — Step-by-step WASM build & deploy guide
- [`docs/third-party-licenses.md`](docs/third-party-licenses.md) — Third-party dependencies, licenses, and links

## License

Apache License 2.0. No AGPL or commercial-license dependencies.

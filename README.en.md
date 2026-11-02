<p align="center">
  <img src="assets/oodd-crossprint-logo.png" width="128" alt="OODD CrossPrint Logo" />
</p>

# OODD CrossPrint

Cross-platform .NET print-template component: **one template, one rendering kernel**, embeddable into Avalonia / WPF / WinForms / Eto.Forms / WinUI / Uno / .NET MAUI. Design, preview, PDF export and OS printing all share the same SkiaSharp rendering path — what you see is what you get. Imports templates exported from hiprint (vue-plugin-hiprint / Vue-Print-Designer) directly.

> Templates speak only millimeters and points; the engine draws every element, host controls just "put the canvas on screen". What you see in the designer is what gets exported — and what gets printed.

[中文](README.md) | **English** · [Live WebAssembly demo](https://oodd-devnet.github.io/OODD.CrossPrint/)

---

## Designer & Print Preview

<p align="center">
  <img src="assets/print-design-screenshot.png" width="48%" alt="Designer canvas" />
  <img src="assets/print-preview-screenshot.png" width="48%" alt="Print preview" />
</p>

Left: Avalonia-hosted designer (kitchen-sink template with all eight element types + formatter/styler scripts). Right: print preview of the same template — both go through the same rendering path, WYSIWYG. Want to try it yourself? Click the **Live WebAssembly demo** link above; it runs entirely in your browser, nothing to install.

---

## Quick Start

Two paths: **run the samples** first, or **embed via NuGet** into your project.

### Run a sample to see it in action

Requires .NET SDK **8.0+**. Pick any host:

```bash
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

Samples ship with `kitchen-sink.template.json` (covers all eight element types) and `hiprint-sample.js` — open it directly via **File → Import hiprint template…**.

### Embed via NuGet

Each host ships its own NuGet package, plus four core packages. The core layer does **not** depend on any specific UI framework.

| Package | Purpose |
|---|---|
| `CrossPrint.Core` | Template model, units, JSON, hiprint import |
| `CrossPrint.Engine` | SkiaSharp rendering, data binding, scripts, PDF/PNG export |
| `CrossPrint.Designer` | Editor kernel (selection, drag, property-panel generation) |
| `CrossPrint.Printing` | Print targets (PDF file / OS print pipeline) |
| `CrossPrint.Hosts.Avalonia` | Avalonia host adapter |
| `CrossPrint.Hosts.Wpf` | WPF host adapter |
| `CrossPrint.Hosts.WinForms` | WinForms host adapter |
| `CrossPrint.Hosts.Eto` | Eto.Forms host adapter |
| `CrossPrint.Hosts.WinUi` | WinUI 3 host adapter |
| `CrossPrint.Hosts.Uno` | Uno host adapter |
| `CrossPrint.Hosts.Maui` | .NET MAUI host adapter |

Minimal embedding example (Avalonia):

```bash
dotnet add package CrossPrint.Core
dotnet add package CrossPrint.Engine
dotnet add package CrossPrint.Hosts.Avalonia
```

```csharp
using CrossPrint.Core;
using CrossPrint.Hosts.Avalonia;

// Load a template
var load = TemplateJson.Load(File.ReadAllText("template.json"),
                             TemplateJsonLoadOptions.Tolerant);

// Design canvas
var designer = new DesignerControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001", customer = "Jane Doe" },
};

// Read-only preview
var preview = new PreviewControl(load.Template)
{
    Data = new { orderNo = "SO-2026-0001" },
};
```

Export a PDF:

```csharp
using CrossPrint.Engine;

using var stream = File.Create("out.pdf");
var report = PdfExporter.Export(
    designer.Kernel.Template,
    stream,
    data: designer.Data,
    renderer: designer.Renderer);

if (report.HasIssues) Console.WriteLine(report); // missing fields, bad images, script errors — no crash
```

Print:

```csharp
using CrossPrint.Printing;

var document = new PrintDocument(designer.Kernel.Template, designer.Data);
var report = new RenderReport();

// Print to PDF file
await new PdfFilePrintTarget()
    .PrintBatchAsync([document], new PrintSettings { OutputPath = "out.pdf" }, report);

// Hand off to the OS print pipeline
await new SystemPrintTarget()
    .PrintBatchAsync([document], new SystemPrintOptions(), report);
```

---

## Designer Guide

All samples share the same interaction model:

```
┌──────────────────────────────────────────────────────────┐
│ Menu bar (File: Import hiprint / Load template / Print)   │
├──────────────────────────────────────────────────────────┤
│ Toolbar 1: Text Line Rect Ellipse Image QR Barcode Table   │
│ Toolbar 2: Import Styles | Copy Delete | Bring Forward …  │
│           | Fit 100% | ☐Grid ☐Rulers ☐SafeArea           │
│           | Print ExportPDF Preview                       │
├────────┬───────────────────────────────┬─────────────────┤
│ Paper   │                               │ Property panel  │
│ Toolbox │         Design canvas          │ (form per type) │
│        │    (wheel zoom / middle pan)   │                 │
├────────┴───────────────────────────────┴─────────────────┤
│ ▸ Template JSON (collapsed by default)                    │
├──────────────────────────────────────────────────────────┤
│ Status bar                                                │
└──────────────────────────────────────────────────────────┘
```

### Adding & editing elements

- **Toolbox**: pick a type on the left, click *Add to canvas* (or double-press Enter) — the element lands at the page center. Toolbar buttons add instantly.
- **Property panel**: auto-generates a form grouped by *Basic / Position / Appearance / Text / Line / Shape / Image / QR / Barcode / Table / Table Columns / Script*. Dragging/scaling only backfills values, never tears down an input you're typing in.
- **Style reference**: the dropdown next to appearance fields lists named template styles — change it once, everywhere updates.
- **Context menu**: Duplicate (Ctrl+D), Delete, z-order, center, Fit / 100%, rotate paper, custom paper size.

### Paper sizes

Left sidebar: A3 / A4 / A5 / B5 / 80mm / 58mm continuous / 100×150 thermal / Custom. Change is undoable and auto-fits the page.

### Template JSON panel (bottom)

Expands to show the live JSON. Edit it and click *Apply JSON* (Ctrl+Enter) — or paste `getJson()` output from hiprint directly; the importer auto-converts and lists any degradations.

### Shortcuts

| Key | Action |
|---|---|
| Arrows | 1mm nudge (Shift 10mm, Ctrl 0.1mm) |
| Ctrl+D | Duplicate selection |
| Delete | Delete |
| Esc | Cancel drag / clear selection |
| F | Fit to page |
| 0 | 100% zoom |
| Ctrl+wheel | Zoom |
| Middle drag | Pan |
| Ctrl+Enter | Apply template JSON |

---

## hiprint Template Import

Already using hiprint? CrossPrint eats its exports directly:

```csharp
using CrossPrint.Core.Hiprint;

// Accepts strict JSON, getJson() snippets, JS object literals, whole .js module sources
var result = HiprintTemplateImporter.Import(File.ReadAllText("hiprint-template.js"));

var template = result.Template;                  // drop into designer / renderer / print pipeline
foreach (var issue in result.Issues) Console.WriteLine(issue);
```

- **Geometry**: pt → mm auto-converted.
- **Elements**: text, lines, rect/ellipse, image (data URL), QR (error level / caption), 10 barcode symbologies, tables (colspan/rowspan headers, hidden columns, row numbers, footer aggregation).
- **Scripts**: `formatter` / `styler` pass through untouched; executed at render time inside a Jint sandbox.
- **Degradation log**: watermarks, page numbers, HTML, cross-page text, inline barcodes — features that have no equivalent in CrossPrint — are imported as the closest static result and listed in `Issues`. Nothing is silently dropped.

---

## Template Format & Data Binding

### Units

Pages and geometry are always **millimeters** (`WidthMm`, `Xmm`…). Font sizes are always **points** (`SizePt`). No pixel-conversion traps.

### Data binding

Text fields support `{{path}}` placeholders:

```json
{ "text": "{{order.customer.name}}, dear customer" }
{ "table": { "columns": [{ "field": "price" }] } }
```

Tables auto-expand rows from a data path. Data sources can be:

- `IDictionary<string, object?>` / `IDictionary`
- Any POCO (properties/fields, case-insensitive)
- `System.Text.Json` `JsonElement` / `JsonNode`

Bind failures **never throw**. The value falls back to an empty string and a warning is logged in `RenderReport`.

### Scripts (formatter / styler)

hiprint-compatible. Both text elements and table columns support:

```json
{
  "typeKey": "table",
  "table": {
    "columns": [
      {
        "field": "price",
        "title": "Price",
        "formatter": "function(value){ return '$' + (value || 0).toFixed(2); }",
        "styler": "function(value){ return value > 1000 ? { color:'#c0392b', fontWeight:'bold' } : ''; }"
      }
    ]
  }
}
```

- **formatter** returns a replacement string; `null` / `undefined` keeps the original.
- **styler** accepts JS objects (camelCase) or CSS strings (kebab-case).
- **Safety**: Jint sandbox, no CLR access. 250ms per-run timeout, 100k statements, 64-level recursion cap. **Rendering never interrupts**.
- **Designer-visible**: scripts run at design time; you see the effect on the canvas.

Full field tables and examples in [`docs/template-schema.md`](docs/template-schema.md).

---

## Host Support

| Host | Minimum Framework | Platform |
|---|---|---|
| Avalonia 11.3 | .NET 8 | Windows / Linux / macOS + Browser/WASM |
| WPF | .NET 8 (Windows) | Windows |
| WinForms | .NET 8 (Windows) | Windows |
| Eto.Forms 2.10 | .NET 8 | Windows (portable — Linux/macOS need self-publish) |
| WinUI 3 (WindowsAppSDK 1.6) | .NET 8 (Windows) | Windows |
| Uno 5.6 (Skia Desktop) | .NET 8 | Windows |
| .NET MAUI 8 | .NET 8 | Windows |

The core layer (CrossPrint.Core / Engine / Designer / Printing) has **zero UI-framework dependencies** and builds/tests on Windows, Linux and macOS.

---

## Known Limitations

- **QR quiet zone** drawn at 4 modules; some scanners use stricter/looser tolerances.
- **hiprint import** does not execute import-time JavaScript (watermarks, page numbers, HTML, cross-page text have no equivalent); multi-panel templates import one panel at a time.
- **Undo/redo** — every change produces a revertable command; hosts can wire up their own stack.
- **Uno 6.x** requires SkiaSharp 3.x and net9; we follow SkiaSharp 2.88.9 and don't track Uno 6 yet.
- **WASM sample** has no file dialogs (browser sandbox); hiprint import goes through the template JSON panel.

---

## Deploying the WebAssembly Demo

The Avalonia Browser sample compiles to pure static files (~40MB first-load, self-contained .NET WASM runtime + main assembly). Drop it on GitHub Pages, Cloudflare Pages, Vercel or any static host:

```bash
dotnet workload install wasm-tools
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# Output: samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

Sub-path deployments (e.g. `/OODD.CrossPrint/` on GitHub Pages) are handled by `<base href="./">` in `index.html`. The repo ships a GitHub Actions workflow ([`.github/workflows/pages-demo.yml`](.github/workflows/pages-demo.yml)) that auto-publishes on every push to main.

---

## Documentation

- [`docs/template-schema.md`](docs/template-schema.md) — Complete template JSON field reference + hiprint import mapping
- [`docs/rendering-spec.md`](docs/rendering-spec.md) — Per-element rendering specification
- [`docs/webassembly-demo.md`](docs/webassembly-demo.md) — WASM demo build & deploy guide
- [`docs/third-party-licenses.md`](docs/third-party-licenses.md) — Third-party dependencies and licenses
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — Contributing guide

## License

Apache License 2.0. No AGPL or commercial-license dependencies.

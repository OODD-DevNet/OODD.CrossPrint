# CrossPrint

Cross-platform .NET print-template component: one template, one rendering kernel, embeddable into Avalonia / WPF / WinForms / Eto.Forms / WinUI / Uno / .NET MAUI. Design, preview, PDF export and printing all share the same SkiaSharp rendering path. Imports templates exported from hiprint designers (vue-plugin-hiprint / Vue-Print-Designer) directly.

> Templates only speak millimeters and points; the engine draws every element, host controls just "put the canvas on screen". What you see in the designer is what gets exported — and what gets printed.

[中文](README.md) | **English**

## Features

- **Eight element types**: text, line, rectangle, ellipse, image, QR code, barcode (10 symbologies), table (multi-row headers / row numbers / footer aggregation).
- **WYSIWYG**: designer, print preview and PDF export all go through the same `TemplateRenderer` — no "looks right in the designer, prints differently" surprises.
- **Data binding**: `{{order.customer.name}}` path placeholders; tables expand rows from a bound path automatically. POCOs, dictionaries and `JsonElement` all work as data sources. Binding failures degrade to empty string + warning, never break the page.
- **formatter / styler scripts** (hiprint-compatible semantics): embedded Jint sandbox executes JavaScript to rewrite text and styles; 250 ms per-run timeout plus statement/recursion caps, and every failure degrades to "no script" — rendering never stops.
- **Style library**: named styles inside the template (font / color / alignment / borders…). Elements reference a style by name; change it once, applies everywhere.
- **hiprint import**: accepts `getJson()` output, JS module sources and strict JSON; features that cannot be expressed equivalently are logged item by item (stable `Code` + source path), never silently dropped.
- **Pluggable print targets**: `PdfFilePrintTarget` (print to a PDF file) and `SystemPrintTarget` (hand off to the OS print pipeline: Windows opens the associated app's print dialog; macOS / Linux go through CUPS).
- **Trim / AOT friendly**: template JSON uses System.Text.Json source generators; zero IL warnings from Core.
- **Units are semantics**: page and geometry in millimeters (`WidthMm` / `Xmm`…), fonts in points (`SizePt`). No pixel-conversion traps.

## Online demo (WebAssembly)

The Avalonia sample compiles to WebAssembly and runs in the browser (same canvas code, same SkiaSharp rendering path). The output is pure static files, deployable to GitHub Pages or any static host:

```
dotnet workload install wasm-tools
dotnet publish samples/CrossPrint.Samples.Avalonia.Browser -c Release
# Output: samples/CrossPrint.Samples.Avalonia.Browser/bin/Release/net8.0-browser/publish/wwwroot
```

See [docs/webassembly-demo.md](docs/webassembly-demo.md) for deployment details (sub-path hosting, MIME/compression, embedded fonts). The repository ships a GitHub Actions workflow that publishes to Pages on every push to main.

## Support matrix

| Host | Adapter | Sample | Verification |
|---|---|---|---|
| Avalonia 11.3 (desktop) | `CrossPrint.Hosts.Avalonia` | `samples/CrossPrint.Samples.Avalonia` | Build + headless render smoke (96/144 DPI pixel assertions); interaction by manual review |
| Avalonia 11.3 (Browser / WASM) | same adapter (`net8.0-browser`) | `samples/CrossPrint.Samples.Avalonia.Browser` | Build + manual browser smoke (rendering/Chinese verified; interaction by manual review) |
| WPF (net8.0-windows) | `CrossPrint.Hosts.Wpf` | `samples/CrossPrint.Samples.Wpf` | Build + headless render smoke; interaction by manual review |
| WinForms (net8.0-windows) | `CrossPrint.Hosts.WinForms` | `samples/CrossPrint.Samples.WinForms` | Build + headless render smoke; interaction by manual review |
| Eto.Forms 2.10 | `CrossPrint.Hosts.Eto` | `samples/CrossPrint.Samples.Eto` | Build (Windows); runtime smoke by manual review |
| WinUI 3 (WindowsAppSDK 1.6) | `CrossPrint.Hosts.WinUi` | `samples/CrossPrint.Samples.WinUi` | Build; runtime smoke by manual review |
| Uno 5.6 (Skia desktop single-project, net8.0-desktop) | `CrossPrint.Hosts.Uno` | `samples/CrossPrint.Samples.Uno` | Build (`SKSwapChainPanel`, SkiaSharp 2.88.9); runtime smoke by manual review |
| .NET MAUI 8 (Windows) | `CrossPrint.Hosts.Maui` | `samples/CrossPrint.Samples.Maui` | Build; runtime smoke by manual review |

The kernel layers (`CrossPrint.Core` / `CrossPrint.Engine` / `CrossPrint.Designer` / `CrossPrint.Printing`) depend on no UI framework and build/test on Windows/Linux/macOS.

## Repository layout

```
src/
  CrossPrint.Core       Models, mm/pt units, JSON (source-generated), hiprint import, font/image resolver abstractions
  CrossPrint.Engine     Renderers, data binding, formatter/styler script execution, PNG/PDF/batch export
  CrossPrint.Designer   Editor kernel, hit testing, property channel, overlay drawing (no UI dependency)
  CrossPrint.Printing   Print target abstraction, PDF file target, system print target, batch printing
  CrossPrint.Hosts.*    Thin per-host adapters (canvas bridge + input forwarding, ~300–400 lines each)
samples/                Seven runnable samples (same toolbox/paper panel/property panel/context menu/scripts/style library/hiprint import/preview/printing) + Avalonia browser build (CrossPrint.Samples.Avalonia.Browser, see "Online demo")
tests/                  Five test suites: Core / Engine / Designer / Printing / Avalonia
docs/                   Requirements, architecture, development, task plan, template schema, rendering spec, WASM deployment, licenses
```

## Getting started

Requires .NET SDK **8.0.420** (pinned in `global.json`).

```bash
dotnet build OODD.CrossPrint.sln
dotnet test OODD.CrossPrint.sln
```

On Linux/macOS build/test the kernel layers only:

```bash
dotnet build OODD.CrossPrint.Portable.slnf
dotnet test OODD.CrossPrint.Portable.slnf
```

Run the samples (Windows):

```bash
dotnet run --project samples/CrossPrint.Samples.Avalonia
dotnet run --project samples/CrossPrint.Samples.Wpf
dotnet run --project samples/CrossPrint.Samples.WinForms
dotnet run --project samples/CrossPrint.Samples.Eto
dotnet run --project samples/CrossPrint.Samples.WinUi
dotnet run --project samples/CrossPrint.Samples.Uno
dotnet run --project samples/CrossPrint.Samples.Maui
```

The Uno sample targets `net8.0-desktop` (Uno 5.6 Skia desktop single-project, `Uno.Sdk/5.6.54`) and needs no extra workloads; Uno 6.7 requires SkiaSharp 3.119 and net9, which conflicts with this repo's SkiaSharp 2.88.9 pin, so it is not tracked yet.

Samples ship with `kitchen-sink.template.json` (copied to the output directory at build time) covering all eight element types, plus `hiprint-sample.js` (a hiprint module source example) for "File → Import hiprint template…".

## Designer guide (identical across samples)

### Layout

```
┌──────────────────────────────────────────────────────────────┐
│ Menu bar (File: import hiprint / complex template / styles / print) │
├──────────────────────────────────────────────────────────────┤
│ Toolbar 1: Text Line Rect Ellipse Image QR Barcode Table      │
│ Toolbar 2: Import Styles | Duplicate Delete | Up Down Top Bottom │
│            | Fit 100% | □Grid □Rulers □Safe area              │
│            | Print Export PDF Preview                         │
├─────────┬─────────────────────────────────┬──────────────────┤
│ Paper   │                                 │ Property panel    │
│ Toolbox │        Design canvas            │ (form generated   │
│ (resiz.)│   (wheel zoom / pan)            │  per selection)   │
│         │                                 │   (resizable)     │
├─────────┴─────────────────────────────────┴──────────────────┤
│ ▸ Template JSON (collapsed by default; draggable when open)    │
├──────────────────────────────────────────────────────────────┤
│ Status bar                                                    │
└──────────────────────────────────────────────────────────────┘
```

Side panels and the property panel are resizable via splitters; the bottom "Template JSON" strip is collapsed by default — click "▸ Template JSON" to expand (and drag its height), leaving vertical space to the canvas.

### Adding & editing elements

- **Toolbox**: pick a type on the left, click "Add to canvas" (or double-click / press Enter) — the element lands at page center; toolbar buttons add with one click.
- **Property panel**: selecting an element generates a form grouped into "Basic / Position & size / Appearance / Text / Line / Shape / Image / QR / Barcode / Table / Table columns / Script". Edits commit straight back into the template and repaint; while the same selection stays active, dragging/zooming only refreshes values and never tears down a control you are typing in.
- **Script fields**: text/table elements expose multi-line `formatter` / `styler` boxes; semantics below.
- **Style reference**: the dropdown next to appearance fields lists the template's named styles, managed via "File → Style library…".
- **Context menu**: duplicate (Ctrl+D), delete, four z-order operations, horizontal/vertical centering, fit page / 100%, rotate paper, custom paper. Right-clicking hits an element selects it; hitting blank space clears the selection.

### Paper

Left paper panel: A3 / A4 / A5 / B5 / 80mm & 58mm continuous receipt / 100×150 label / custom; toggle portrait/landscape (continuous paper is fixed portrait). Every change goes through a `PaperCommand` (revertible) and auto-fits the page.

### Template JSON panel

When expanded, the bottom strip shows the live template JSON: edit the text and click "Apply JSON" (Ctrl+Enter on most hosts) to replace the template; "Reload" discards edits. Pasting a hiprint designer's `getJson` (`{"panels":[…]}`) also works — it is routed through the importer automatically, with degradation issues listed. Canvas/property edits sync into the textbox automatically and are never overwritten while you have unapplied edits; a broken JSON only reports an error and leaves the current template untouched.

### Preview / export / print

- **Preview**: a read-only `PreviewControl` dialog sharing the same rendering path.
- **Export PDF**: binds sample data and writes via `PdfExporter`.
- **Print (system)**: renders a temporary PDF and hands it to the OS print pipeline (`SystemPrintTarget`) — on Windows it uses the ShellExecute "print" verb so the associated app (WPS / Adobe / Edge…) opens its print dialog to pick printer and copies; on macOS / Linux it goes to the default CUPS queue. Double-fire protection: a second job cannot start until the first one returns.

### Keyboard

| Key | Action |
|---|---|
| Arrow keys | nudge 1 mm (Shift 10 mm, Ctrl 0.1 mm) |
| Ctrl+D | duplicate selection |
| Delete | delete selection |
| Esc | cancel drag / clear selection |
| F | fit page |
| 0 | zoom 100% |
| Ctrl+wheel | zoom |
| Middle-drag | pan canvas |
| Ctrl+Enter | apply template JSON (inside the textbox) |

## Embedding into your app

Host controls share identical APIs: `DesignerControl` (design canvas) and `PreviewControl` (read-only preview). Avalonia example:

```csharp
using CrossPrint.Core;
using CrossPrint.Engine;
using CrossPrint.Hosts.Avalonia;

var load = TemplateJson.Load(File.ReadAllText("kitchen-sink.template.json"),
                             TemplateJsonLoadOptions.Tolerant);
var designer = new DesignerControl(load.Template)
{
    Data = new { title = "Sample title", orderNo = "SO-2026-0001", sku = "SKU-42" },
};
// designer.Kernel is the editor kernel: selection, dragging, property commits all live there
```

Export goes through the same renderer as the canvas:

```csharp
using var stream = File.Create("out.pdf");
var report = PdfExporter.Export(
    designer.Kernel.Template,
    stream,
    data: designer.Data,
    fontResolver: designer.FontResolver,
    images: designer.Images,
    renderer: designer.Renderer);

if (report.HasIssues)
    Console.WriteLine(report); // broken images, missing fields, script errors — the page never fails wholesale
```

`PngExporter` (single-page PNG) and `BatchPdfExporter` (many templates/records merged into one multi-page PDF) are documented in `docs/03-development.md`.

### Printing

The single entry point is `IPrintTarget.PrintBatchAsync(documents, settings, report)` with pluggable targets:

```csharp
using CrossPrint.Printing;

var document = new PrintDocument(designer.Kernel.Template, designer.Data);
var report = new RenderReport();

// 1) Print to a PDF file (output path, font/image resolvers, render options are configurable)
var result = await new PdfFilePrintTarget()
    .PrintBatchAsync([document], new PrintSettings { OutputPath = "out.pdf" }, report);

// 2) Hand off to the OS print pipeline: Windows opens the associated app's print dialog
//    (printer & copies selectable); macOS / Linux go to the default CUPS queue. Actual
//    paper output is the OS's job; here you only wait for the "delivered" result.
var result2 = await new SystemPrintTarget()
    .PrintBatchAsync([document], new SystemPrintOptions(), report);

Console.WriteLine(result.Success ? $"{result.PrintedPages} page(s)" : result.Message);
```

The third `PrintDocument` argument is the copy count (`Copies`); `PrintResult.Success` means the job was delivered/written — paper jams and out-of-paper are the printer's and OS's domain.

## Template format & data binding

- Page and element geometry in millimeters (`WidthMm` / `Xmm` …), fonts in points (`SizePt`).
- Templates are JSON; polymorphic element payloads are serialized by System.Text.Json source generators (no reflection, trimmable).
- Field tables and examples: [`docs/template-schema.md`](docs/template-schema.md); rendering details: [`docs/rendering-spec.md`](docs/rendering-spec.md).
- Text supports `{{path}}` placeholders: `{{order.customer.name}}`, `{{items[0].sku}}`; tables expand data rows automatically from a bound path.
- Data sources: `IDictionary<string, object?>`, `IDictionary`, objects implementing `IDictionaryProvider`, arbitrary POCOs (properties/fields, case-insensitive), or `System.Text.Json` `JsonElement` / `JsonNode` (JSON property names are case-sensitive; numbers keep their raw text).
- Binding failures never throw: the value becomes an empty string and a `BindingMissingField` issue is recorded in `RenderReport`.

## Scripts (formatter / styler)

hiprint's `formatter` (rewrite content) and `styler` (rewrite style) semantics are first-class here: supported at element level and table-column level, stored directly in the template JSON (modeled in `CrossPrint.Core`, preserved by the importer) and executed in a Jint sandbox during rendering.

```json
{
  "typeKey": "table",
  "table": {
    "columns": [
      {
        "field": "price",
        "title": "Unit price",
        "formatter": "function(value, row, index){ return '¥' + (value || 0).toFixed(2); }",
        "styler": "function(value, row, index){ return value > 1000 ? { color:'#c0392b', fontWeight:'bold' } : ''; }"
      }
    ]
  }
}
```

Call signatures (aligned with hiprint):

| Position | Element level | Table column level |
|---|---|---|
| 1 | `value` — the main bound field's raw value | `value` — the cell's raw value |
| 2 | `options` — `{ id, type }` | `row` — the current row object |
| 3 | `options` (same, positional alignment) | `index` — row number |
| 4 | `options` (same) | `options` — `{ id, type }` |
| 5 | `templateData` — the whole template data | `templateData` — the whole template data |

- **formatter** returns a string that replaces the displayed text; `null` / `undefined` keeps the original.
- **styler** returns a style: either a JS object (camelCase, e.g. `{ color:'#c0392b', fontWeight:'bold' }`) or a CSS string (kebab-case, e.g. `'color:#c0392b;font-weight:bold'`). Supported keys: `color`, `backgroundColor`, `fontSize`, `fontWeight`, `fontStyle`, `textDecoration`, `letterSpacing`, `lineHeight`, `textAlign`, `verticalAlign`, `padding` (four sides), `borderColor`, `borderWidth`, `borderStyle`; unknown keys are silently ignored, CSS-style.
- **Sandbox & limits**: Jint exposes no CLR access by default — scripts cannot touch files or the network; 250 ms timeout, 100k statement cap and 64-frame recursion cap per run.
- **Degradation discipline**: parse failures, exceptions and timeouts all degrade to "no script" (original value, original style) with a `ScriptIssue` warning in `RenderReport`; rendering never stops.
- **Performance**: sources are precompiled globally (AST cache of 256, LRU-evicted); script-free templates pay zero cost (no executor is created).
- **Visible in the designer**: scripts also run at design time (`value` falls back to the element's test data when unresolved), so effects show directly on the canvas.

## hiprint template import

`CrossPrint.Core.Hiprint.HiprintTemplateImporter` maps content exported from hiprint designers into a `PrintTemplate`:

```csharp
using CrossPrint.Core.Hiprint;

// strict JSON, getJson() fragments, JS object literals, whole .js module sources — all accepted
var result = HiprintTemplateImporter.Import(File.ReadAllText("hiprint-template.js"));

var template = result.Template;   // straight into the designer / renderer / PDF / print pipeline
foreach (var issue in result.Issues)
    Console.WriteLine(issue);     // features without an equivalent are logged, never silently dropped
```

- **Input**: JS objects with single quotes/unquoted keys/trailing commas/comments, `export const json = {...}` modules, `getJson()` fragments; function bodies are not executed.
- **Geometry**: element `left/top/width/height` are pt, uniformly converted to mm; panel sizes are mm; `panelPageRule: "none"` imports as continuous paper.
- **Elements**: text (field placeholders, title prefixes, wrap strategy, padding, letter spacing, justified alignment), horizontal/vertical lines, rectangles/ellipses, images (data URLs), QR codes (error correction, caption), ten barcode symbologies, tables (2D headers with colspan/rowspan, hidden columns, row numbers, footer aggregation, row-set fields). Legacy `tableCustom` imports identically to `table`; `columns` as object maps or single flat column objects are accepted; missing column definitions keep the table frame with one placeholder column instead of dropping the table.
- **Scripts**: element `formatter` / `styler` and their table-column counterparts are preserved verbatim for the render-time script executor (see above); anything non-static is logged in `Issues`.
- **Test data**: `field + testData` pairs are collected into the template's `designData`, used by the designer and print preview to show sample content when no host data is present (barcodes/QR codes don't bind to empty strings); real printing and export ignore it.
- **Multi-panel**: a hiprint template may contain multiple panels (multi-part forms); `HiprintImportOptions.PanelIndex` selects which one to import; `result.Panels` reports each panel's size and element count for host UIs.
- **Degradation log**: watermarks, page numbers, HTML, cross-page long text, in-cell codes and similar unrepresentable features import as the closest static result, with a stable `Code` and source path in `Issues`. `Severity=Error` means that element was skipped; everything else imports normally.

The field-by-field mapping and limitation list is in [`docs/template-schema.md` §8](docs/template-schema.md#8-hiprint-模板导入); rendering semantics (rotation, per-side borders, ellipsis, merged headers, etc.) are pixel-verified by `HiprintSemanticsRendererTests`.

## Trim / AOT

- Template JSON uses source-generated `JsonTypeInfo`; Core produces no IL2026.
- Data binding falls back to reflection for POCOs (suppressed in place in Engine, with the rationale documented). If trimmed, binding degrades to "missing-field warning + empty string" instead of crashing; for deterministic Trim/AOT behavior, use `IDictionary<string, object?>` or implement `IDictionaryProvider` as the data source and keep your model types rooted via
  `[DynamicallyAccessedMembers]` / a TrimmerRootDescriptor.
- In the `samples/CrossPrint.Samples.Avalonia` `PublishTrimmed` smoke test, this repo's assemblies emit zero IL warnings; remaining `Avalonia.DesignerSupport` IL2026/IL2104 come from Avalonia itself (design-time BSON remoting) and are unrelated to this component.

## Known limitations (v0.2)

- QR quiet zone is drawn at 4 modules; scanners with stricter/looser rules may differ.
- hiprint import does not execute JavaScript at import time: `formatter`/`styler` are preserved for the render-time sandbox; watermarks, page numbers, HTML, cross-page long text and in-cell codes have no equivalent. Multi-panel templates import one panel at a time.
- No alignment guides/snapping or multi-select batch property editing; no undo/redo stack yet (every change produces a revertible command — hosts can wire their own stack).
- `SystemPrintTarget` depends on the OS file association: on Windows, if the associated app is WPS or another app with its own print queue, the output path is that app's decision; for unattended server printing use `PdfFilePrintTarget` and deliver the file yourself.
- The Uno adapter baseline is Uno 5.6 (`net8.0-desktop`, package TFM `net8.0-desktop1.0`); Uno 6.x needs SkiaSharp 3.x and is not provided yet.
- The WASM sample has no file dialogs (browser sandbox); hiprint import goes through pasting into the Template JSON panel.
- Canvas interaction (dragging, zooming, shortcuts, print dialogs) is covered by build and pixel-level smoke tests only; feel and real-device printing need manual review.

## Documentation

- [`docs/01-requirements.md`](docs/01-requirements.md) — requirements & acceptance criteria
- [`docs/02-architecture.md`](docs/02-architecture.md) — layering, coordinate contracts, per-host bridges
- [`docs/03-development.md`](docs/03-development.md) — dev environment, commands, debugging & trimming
- [`docs/04-task-plan.md`](docs/04-task-plan.md) — milestones, delivery log, device review checklist
- [`docs/template-schema.md`](docs/template-schema.md) — template JSON field tables & hiprint import mapping
- [`docs/rendering-spec.md`](docs/rendering-spec.md) — per-element rendering spec
- [`docs/webassembly-demo.md`](docs/webassembly-demo.md) — Avalonia WASM demo build & GitHub Pages deployment
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — the four architecture iron rules & commit requirements

## License

Apache License 2.0. Third-party dependencies and their licenses are listed in [`docs/third-party-licenses.md`](docs/third-party-licenses.md) (CI verifies the list against actual packages). AGPL and commercially licensed dependencies (including QuestPDF) are explicitly excluded.

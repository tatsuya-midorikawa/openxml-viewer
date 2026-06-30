# OpenXML Viewer

A Visual Studio Code extension that lets you **preview Office Open XML files (`.xlsx` / `.docx` / `.pptx`) directly inside Visual Studio Code**.
Just double-click a file to quickly inspect its contents—without launching Excel, Word, or PowerPoint.

> [!NOTE]
> This extension is intended for **viewing (read-only)**. It does not provide editing or saving capabilities.

---

## Table of Contents

- [OpenXML Viewer](#openxml-viewer)
  - [Table of Contents](#table-of-contents)
  - [Key Features](#key-features)
  - [Supported File Formats](#supported-file-formats)
  - [Requirements](#requirements)
  - [Installation](#installation)
    - [From the Visual Studio Code Marketplace](#from-the-visual-studio-code-marketplace)
    - [From a VSIX File](#from-a-vsix-file)
  - [Usage](#usage)
  - [Commands](#commands)
  - [Settings](#settings)
  - [Limitations](#limitations)
  - [Roadmap](#roadmap)
  - [Contributing](#contributing)
  - [Sponsors](#sponsors)
  - [License](#license)

---

## Key Features

- 📊 **View Excel workbooks (`.xlsx`)**
  - Display cell contents in a table, sheet by sheet
  - Switch between multiple sheets with tabs
  - Resize column widths and row heights (drag borders, double-click to auto-fit, reset per sheet); adjustments persist across reopen
- 📝 **View Word documents (`.docx`)**
  - Display body structure such as paragraphs, headings, and tables
- 📑 **View PowerPoint presentations (`.pptx`)**
  - Display text, shapes, and images for each slide
  - Jump from the slide list (thumbnails)
- 🔍 **In-viewer text search**
  - Run a full-text search over the displayed content and highlight matches
  - Jump to the previous/next match (moving across sheets and slides)
- 🖱️ **Seamless operation**
  - Launch the preview simply by opening a file in the Explorer (custom editor)
  - No need to launch external applications
- 🔒 **Safe viewing**
  - Read-only, so accidental edits cannot corrupt the file
  - The Webview's Content Security Policy (CSP) restricts loading external resources

---

## Supported File Formats

| Type | Extension | Format | Status |
| --- | --- | --- | --- |
| Excel workbook | `.xlsx` | SpreadsheetML | ✅ View |
| Word document | `.docx` | WordprocessingML | ✅ View |
| PowerPoint | `.pptx` | PresentationML | ✅ View |

> [!IMPORTANT]
> The target is **Office Open XML (OOXML)** files.
> Legacy binary formats (`.xls` / `.doc` / `.ppt`) and macro-enabled formats (such as `.xlsm`) are not supported.

---

## Requirements

- Visual Studio Code `1.90.0` or later
- No additional runtime or external application (such as Microsoft Office) is **required**

---

## Installation

### From the Visual Studio Code Marketplace

1. Open the **Extensions** view from the VS Code sidebar (`Ctrl+Shift+X` / `⌘+Shift+X`)
2. Search for `OpenXML Viewer`
3. Click **Install**

### From a VSIX File

```bash
code --install-extension openxml-viewer-<version>.vsix
```

Alternatively, open the Command Palette (`Ctrl+Shift+P` / `⌘+Shift+P`), run
**"Extensions: Install from VSIX..."**, and select the `.vsix` file.

---

## Usage

1. Open a `.xlsx` / `.docx` / `.pptx` file in the VS Code Explorer.
2. This extension's custom editor launches automatically and shows a preview of the contents.
3. If you want to open the file in the default text editor or another editor, right-click the file and switch the way it opens via
   **"Open With..."**.

> [!TIP]
> If you want to change the default way a file opens, you can set the default editor per extension via
> **"Open With..." → "Configure default editor for '*.xlsx'..."**.

Type a keyword into the search bar at the top of the preview to run a full-text search over the displayed document.
Press `Enter` / `Shift+Enter` to move to the next / previous match, and `Esc` to clear the search.
For spreadsheets and presentations, the viewer automatically switches to the matching sheet / slide.

---

## Commands

These are available from the Command Palette (`Ctrl+Shift+P` / `⌘+Shift+P`).

| Command | Command ID | Description |
| --- | --- | --- |
| OpenXML Viewer: Open Preview | `openxml-viewer.openPreview` | Open the active file in the preview |
| OpenXML Viewer: Reload | `openxml-viewer.reload` | Reload the current preview |

---

## Settings

You can configure these in `settings.json` (user settings / workspace settings).

| Setting Key | Type | Default | Description |
| --- | --- | --- | --- |
| `openxmlViewer.spreadsheet.maxRows` | `number` | `1000` | Maximum number of rows rendered at once in a spreadsheet |
| `openxmlViewer.spreadsheet.showGridlines` | `boolean` | `true` | Whether to show cell gridlines |
| `openxmlViewer.document.showImages` | `boolean` | `true` | Whether to show images in Word documents |
| `openxmlViewer.presentation.thumbnails` | `boolean` | `true` | Whether to show the slide thumbnail list |
| `openxmlViewer.theme` | `string` | `"auto"` | Preview color scheme (`auto` / `light` / `dark`) |

Example configuration:

```jsonc
{
  "openxmlViewer.spreadsheet.maxRows": 5000,
  "openxmlViewer.theme": "dark"
}
```

---

## Limitations

> [!WARNING]
> This extension is intended to provide a **simple preview**. It does not guarantee rendering that is fully compatible with Excel, Word, or PowerPoint.
> The layout, formatting, colors, and so on may differ from how the file appears in the original Office application.

- Editing, saving, and printing are not supported (view-only).
- Complex layouts (advanced shapes, SmartArt, charts, conditional formatting, macros, and so on) may be simplified or hidden.
- Password-protected or encrypted files cannot be opened.
- Formulas display their cached result values (no recalculation is performed).
- Legacy binary formats (`.xls` / `.doc` / `.ppt`) are not supported.

---

## Roadmap

- [x] `.xlsx` viewer (cells, multiple sheets)
- [x] `.docx` viewer (body, tables, images)
- [x] `.pptx` viewer (slides, thumbnails)
- [x] In-viewer text search (match highlighting, previous/next navigation)
- [x] Resizable spreadsheet column widths and row heights (drag, auto-fit, persistence)
- [ ] Text copy support
- [ ] Enhanced chart and shape rendering
- [ ] Export (PDF / HTML)

---

## Contributing

Bug reports, feature requests, and pull requests are welcome.
Feel free to reach out via [Issues](https://github.com/tatsuya-midorikawa/openxml-viewer/issues).

For specifications, design, and build/debug instructions, see [DEVELOPMENT.md](./DEVELOPMENT.md). For publishing to the Marketplace, see [PUBLISHING.md](./PUBLISHING.md).

---

## Sponsors

If you would like to support the development of this project, contributions via [GitHub Sponsors](https://github.com/sponsors/tatsuya-midorikawa) are welcome.
Your support will be used to improve features and maintain the project over time.

---

## License

This project is released under the [MIT License](./LICENSE).

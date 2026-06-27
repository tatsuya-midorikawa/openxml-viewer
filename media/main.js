// OpenXML Viewer — Webview レンダラー (フレームワーク非依存のバニラ JS)。
// 拡張機能本体から postMessage で受け取った解析結果を DOM へ描画する。
// テキストはすべて textContent 経由で挿入し、HTML インジェクションを避ける。
(function () {
  "use strict";

  const app = document.getElementById("app");

  /** 0 始まりの列番号を Excel 風の列名 (A, B, ... Z, AA ...) へ変換する。 */
  function colLetter(n) {
    let s = "";
    n += 1;
    while (n > 0) {
      const r = (n - 1) % 26;
      s = String.fromCharCode(65 + r) + s;
      n = Math.floor((n - 1) / 26);
    }
    return s;
  }

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function px(value) {
    return `${Math.max(1, Math.round(value * 100) / 100)}px`;
  }

  // -------------------------------------------------------------------------
  // スプレッドシート
  // -------------------------------------------------------------------------
  const ROW_HEADER_WIDTH = 48;
  const COLUMN_HEADER_HEIGHT = 24;
  const DEFAULT_COL_WIDTH = 8.43;
  const DEFAULT_ROW_HEIGHT = 15;
  const CELL_TEXT_PADDING = 6;
  const TRAILING_BLANK_COLUMNS = 20;

  function columnWidthToPx(width) {
    return Math.floor(width * 7 + 5);
  }

  function rowHeightToPx(height) {
    return height * 96 / 72;
  }

  function columnWidth(sheet, col) {
    const columns = sheet.columns || [];
    const column = columns.find((c) => c.min <= col && col <= c.max);
    return columnWidthToPx(column ? column.width : (sheet.defaultColWidth || DEFAULT_COL_WIDTH));
  }

  function rowHeight(sheet, rowNumber, rowByIndex) {
    const row = rowByIndex[rowNumber];
    return rowHeightToPx(row && row.height > 0 ? row.height : (sheet.defaultRowHeight || DEFAULT_ROW_HEIGHT));
  }

  function buildSheetMetrics(sheet, maxCol, maxRow, rowByIndex) {
    const colWidths = [];
    const colOffsets = [0];
    for (let c = 0; c <= maxCol; c++) {
      const width = columnWidth(sheet, c);
      colWidths.push(width);
      colOffsets.push(colOffsets[c] + width);
    }

    const rowHeights = [];
    const rowOffsets = [0];
    for (let r = 1; r <= maxRow; r++) {
      const height = rowHeight(sheet, r, rowByIndex);
      rowHeights.push(height);
      rowOffsets.push(rowOffsets[r - 1] + height);
    }

    return { colWidths, colOffsets, rowHeights, rowOffsets };
  }

  function hasCellText(cell) {
    return cell && cell.text !== undefined && cell.text !== null && String(cell.text) !== "";
  }

  function applyRunStyle(node, run) {
    if (run.bold) node.style.fontWeight = "700";
    if (run.italic) node.style.fontStyle = "italic";
    const decorations = [];
    if (run.underline) decorations.push("underline");
    if (run.strike) decorations.push("line-through");
    if (decorations.length > 0) node.style.textDecorationLine = decorations.join(" ");
    if (run.fontSize > 0) node.style.fontSize = `${run.fontSize}pt`;
    if (run.fontName) node.style.fontFamily = run.fontName;
    if (run.color) node.style.color = run.color;
  }

  function appendCellText(container, cell) {
    const runs = cell.runs || [];
    if (runs.length === 0) {
      container.textContent = cell.text || "";
      return;
    }

    runs.forEach((run) => {
      if (!run || run.text === undefined || run.text === null) return;
      const span = el("span", "text-run", String(run.text));
      applyRunStyle(span, run);
      container.appendChild(span);
    });
  }

  function spillWidth(metrics, occupiedCols, col, maxCol) {
    let stopCol = maxCol + 1;
    for (const occupiedCol of occupiedCols) {
      if (occupiedCol > col) {
        stopCol = occupiedCol;
        break;
      }
    }
    return Math.max(1, metrics.colOffsets[stopCol] - metrics.colOffsets[col] - CELL_TEXT_PADDING);
  }

  function renderSpreadsheet(data) {
    clear(app);
    app.className = "spreadsheet";

    const sheets = data.sheets || [];
    if (sheets.length === 0) {
      app.appendChild(el("div", "empty", "表示できるシートがありません。"));
      return;
    }

    const tabs = el("div", "tabs");
    const body = el("div", "sheet-body");
    app.appendChild(body);
    app.appendChild(tabs);

    function showSheet(index) {
      Array.from(tabs.children).forEach((t, i) =>
        t.classList.toggle("active", i === index)
      );
      clear(body);
      body.appendChild(buildSheetTable(sheets[index]));
    }

    sheets.forEach((sheet, i) => {
      const tab = el("button", "tab", sheet.name);
      tab.addEventListener("click", () => showSheet(i));
      tabs.appendChild(tab);
    });

    showSheet(0);
  }

  function buildSheetTable(sheet) {
    const rows = sheet.rows || [];
    const images = sheet.images || [];
    const contentMaxCol = Math.max(sheet.maxCol || 0, ...images.map((image) => Math.max(image.col || 0, image.toCol || 0)));
    const maxCol = Math.min(16383, contentMaxCol + TRAILING_BLANK_COLUMNS);
    const rowByIndex = {};
    rows.forEach((row) => {
      rowByIndex[row.index] = row;
    });

    const maxDataRow = rows.reduce((max, row) => Math.max(max, row.index || 0), 0);
    const maxImageRow = images.reduce((max, image) => Math.max(max, (image.row || 0) + 1, (image.toRow || 0) + 1), 0);
    const maxRow = Math.max(maxDataRow, maxImageRow, 1);
    const limit = Math.min(maxRow, 2000);
    const metrics = buildSheetMetrics(sheet, maxCol, limit, rowByIndex);
    const sheetWidth = ROW_HEADER_WIDTH + metrics.colOffsets[metrics.colOffsets.length - 1];
    const sheetHeight = COLUMN_HEADER_HEIGHT + metrics.rowOffsets[metrics.rowOffsets.length - 1];

    const table = el("table", "grid");
    table.classList.toggle("hide-gridlines", sheet.showGridLines === false);
    table.style.width = px(sheetWidth);
    const colgroup = el("colgroup");
    const rowHeaderCol = el("col");
    rowHeaderCol.style.width = px(ROW_HEADER_WIDTH);
    colgroup.appendChild(rowHeaderCol);
    metrics.colWidths.forEach((width) => {
      const col = el("col");
      col.style.width = px(width);
      colgroup.appendChild(col);
    });
    table.appendChild(colgroup);

    const thead = el("thead");
    const headRow = el("tr");
    headRow.style.height = px(COLUMN_HEADER_HEIGHT);
    headRow.appendChild(el("th", "corner", ""));
    for (let c = 0; c <= maxCol; c++) {
      headRow.appendChild(el("th", "col-head", colLetter(c)));
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = el("tbody");
    for (let rowNumber = 1; rowNumber <= limit; rowNumber++) {
      const row = rowByIndex[rowNumber] || { index: rowNumber, cells: [] };
      const tr = el("tr");
      tr.style.height = px(metrics.rowHeights[rowNumber - 1]);
      tr.appendChild(el("th", "row-head", String(row.index)));
      const cellByCol = {};
      const occupiedCols = [];
      (row.cells || []).forEach((cell) => {
        if (hasCellText(cell)) {
          cellByCol[cell.col] = cell;
          occupiedCols.push(cell.col);
        }
      });
      occupiedCols.sort((a, b) => a - b);
      for (let c = 0; c <= maxCol; c++) {
        const td = el("td", "cell");
        td.dataset.row = String(row.index);
        td.dataset.col = String(c);
        const cell = cellByCol[c];
        if (cell !== undefined) {
          const text = el("span", "cell-text");
          text.style.width = px(spillWidth(metrics, occupiedCols, c, maxCol));
          appendCellText(text, cell);
          td.title = String(cell.text);
          td.classList.add("has-value");
          td.appendChild(text);
        }
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);

    const wrap = el("div", "table-wrap");
    const canvas = el("div", "sheet-canvas");
    canvas.style.width = px(sheetWidth);
    canvas.style.height = px(sheetHeight);
    canvas.appendChild(table);
    canvas.appendChild(buildImageLayer(images, metrics, limit));
    wrap.appendChild(canvas);
    if (maxRow > limit) {
      wrap.appendChild(el("div", "truncation", `先頭 ${limit} 行のみ表示しています (全 ${maxRow} 行)。`));
    }
    return wrap;
  }

  function buildImageLayer(images, metrics, renderedRows) {
    const layer = el("div", "sheet-images");
    images.forEach((image) => {
      if (image.row >= renderedRows || image.col < 0 || image.col >= metrics.colOffsets.length) return;
      const img = el("img", "sheet-image");
      img.alt = image.altText || "";
      img.src = `data:${image.contentType};base64,${image.data}`;
      img.style.left = px(ROW_HEADER_WIDTH + metrics.colOffsets[image.col] + (image.colOffset || 0));
      img.style.top = px(COLUMN_HEADER_HEIGHT + metrics.rowOffsets[image.row] + (image.rowOffset || 0));
      if (image.width > 0) img.style.width = px(image.width);
      if (image.height > 0) img.style.height = px(image.height);
      layer.appendChild(img);
    });
    return layer;
  }

  // -------------------------------------------------------------------------
  // 文書 (Word)
  // -------------------------------------------------------------------------
  function renderDocument(data) {
    clear(app);
    app.className = "document";

    const blocks = data.blocks || [];
    if (blocks.length === 0) {
      app.appendChild(el("div", "empty", "表示できる内容がありません。"));
      return;
    }

    const page = el("div", "page");
    blocks.forEach((block) => {
      if (block.kind === "heading") {
        const level = Math.min(Math.max(block.level || 1, 1), 6);
        page.appendChild(el("h" + level, "heading", block.text));
      } else if (block.kind === "table") {
        page.appendChild(buildDocTable(block.rows || []));
      } else {
        const p = el("p", "para", block.text);
        if (!block.text) p.classList.add("empty-para");
        page.appendChild(p);
      }
    });
    app.appendChild(page);
  }

  function buildDocTable(rows) {
    const table = el("table", "doc-table");
    rows.forEach((cells) => {
      const tr = el("tr");
      cells.forEach((text) => {
        const td = el("td");
        text.split("\n").forEach((line, i) => {
          if (i > 0) td.appendChild(document.createElement("br"));
          td.appendChild(document.createTextNode(line));
        });
        tr.appendChild(td);
      });
      table.appendChild(tr);
    });
    return table;
  }

  // -------------------------------------------------------------------------
  // プレゼンテーション (PowerPoint)
  // -------------------------------------------------------------------------
  function renderPresentation(data) {
    clear(app);
    app.className = "presentation";

    const slides = data.slides || [];
    if (slides.length === 0) {
      app.appendChild(el("div", "empty", "表示できるスライドがありません。"));
      return;
    }

    const list = el("aside", "slide-list");
    const stage = el("div", "slide-stage");
    app.appendChild(list);
    app.appendChild(stage);

    function showSlide(index) {
      Array.from(list.children).forEach((t, i) =>
        t.classList.toggle("active", i === index)
      );
      clear(stage);
      stage.appendChild(buildSlide(slides[index]));
    }

    slides.forEach((slide, i) => {
      const item = el("button", "slide-thumb");
      item.appendChild(el("span", "slide-no", String(slide.index)));
      item.appendChild(el("span", "slide-title", slide.title || "(無題)"));
      item.addEventListener("click", () => showSlide(i));
      list.appendChild(item);
    });

    showSlide(0);
  }

  function buildSlide(slide) {
    const card = el("div", "slide-card");
    if (slide.title) card.appendChild(el("h2", "slide-heading", slide.title));
    (slide.texts || []).forEach((text) => {
      card.appendChild(el("p", "slide-text", text));
    });
    return card;
  }

  // -------------------------------------------------------------------------
  // ディスパッチ
  // -------------------------------------------------------------------------
  function renderError(data) {
    clear(app);
    app.className = "error";
    app.appendChild(el("div", "error-title", "ファイルを解析できませんでした"));
    app.appendChild(el("div", "error-detail", data.message || ""));
  }

  function render(payload) {
    switch (payload.kind) {
      case "spreadsheet":
        renderSpreadsheet(payload);
        break;
      case "document":
        renderDocument(payload);
        break;
      case "presentation":
        renderPresentation(payload);
        break;
      default:
        renderError(payload);
        break;
    }
  }

  window.addEventListener("message", (event) => {
    const message = event.data;
    if (message && message.type === "render") {
      render(message.payload);
    }
  });
})();

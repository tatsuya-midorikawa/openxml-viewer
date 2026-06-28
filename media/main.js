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
  const PPT_EMU_PER_INCH = 914400;
  const PPT_EXPORT_DPI = 81;
  const PPT_EMU_PER_PIXEL = PPT_EMU_PER_INCH / PPT_EXPORT_DPI;

  function columnWidthToPx(width) {
    return Math.floor(width * 7 + 5);
  }

  function rowHeightToPx(height) {
    return height * 96 / 72;
  }

  function emuToSlidePx(value) {
    return value / PPT_EMU_PER_PIXEL;
  }

  function pointToSlidePx(value) {
    return value * PPT_EXPORT_DPI / 72;
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
          if (cell.fillColor) td.style.backgroundColor = cell.fillColor;
          const text = el("span", "cell-text");
          const align = cell.align || "left";
          text.style.textAlign = align;
          if (cell.wrap) {
            text.classList.add("wrap");
            text.style.width = px(Math.max(1, metrics.colWidths[c] - 2 * CELL_TEXT_PADDING));
          } else if (align === "left") {
            text.style.width = px(spillWidth(metrics, occupiedCols, c, maxCol));
          } else {
            text.style.width = px(Math.max(1, metrics.colWidths[c] - 2 * CELL_TEXT_PADDING));
          }
          if (cell.valign === "top") {
            text.style.top = "3px";
            text.style.transform = "none";
          } else if (cell.valign === "bottom") {
            text.style.top = "auto";
            text.style.bottom = "3px";
            text.style.transform = "none";
          }
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
        const h = el("h" + level, "heading");
        if (block.align) h.style.textAlign = block.align;
        appendDocRuns(h, block);
        page.appendChild(h);
      } else if (block.kind === "table") {
        page.appendChild(buildDocTable(block.rows || []));
      } else {
        const p = el("p", "para");
        if (block.align) p.style.textAlign = block.align;
        appendDocRuns(p, block);
        if (!block.text) p.classList.add("empty-para");
        page.appendChild(p);
      }
    });
    app.appendChild(page);
  }

  // 文書段落の run を装飾つきで描画する (run 内の改行は <br> へ展開)。
  function appendDocRuns(node, block) {
    const runs = block.runs || [];
    if (runs.length === 0) {
      node.textContent = block.text || "";
      return;
    }
    runs.forEach((run) => {
      if (!run || run.text === undefined || run.text === null) return;
      String(run.text).split("\n").forEach((line, i) => {
        if (i > 0) node.appendChild(document.createElement("br"));
        if (line !== "") {
          const span = el("span", "text-run", line);
          applyRunStyle(span, run);
          node.appendChild(span);
        }
      });
    });
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
    const slideWidth = slide.width || 12192000;
    const slideHeight = slide.height || 6858000;
    card.style.aspectRatio = `${slideWidth} / ${slideHeight}`;
    if (slide.backgroundColor) card.style.backgroundColor = slide.backgroundColor;

    (slide.shapes || []).forEach((shape) => {
      const node = el("div", "slide-shape");
      applySlideShapeStyle(node, shape, slideWidth);
      placeSlideItem(node, shape, slideWidth, slideHeight);
      card.appendChild(node);
    });

    (slide.tables || []).forEach((table) => {
      const node = buildSlideTable(table, slideWidth);
      placeSlideItem(node, table, slideWidth, slideHeight);
      card.appendChild(node);
    });

    (slide.images || []).forEach((image) => {
      const img = el("img", "slide-image");
      img.alt = image.altText || "";
      img.src = `data:${image.contentType};base64,${image.data}`;
      placeSlideItem(img, image, slideWidth, slideHeight);
      card.appendChild(img);
    });

    const textBoxes = slide.textBoxes || [];
    if (textBoxes.length > 0) {
      textBoxes.forEach((box) => {
        const node = el("div", "slide-textbox");
        applySlideShapeStyle(node, box, slideWidth);
        applyTextBoxVAlign(node, box);
        appendSlideParagraphs(node, box, slideWidth);
        placeSlideItem(node, box, slideWidth, slideHeight);
        card.appendChild(node);
      });
    } else {
      if (slide.title) card.appendChild(el("h2", "slide-heading", slide.title));
      (slide.texts || []).forEach((text) => {
        card.appendChild(el("p", "slide-text", text));
      });
    }
    return card;
  }

  function applySlideShapeStyle(node, item, slideWidth) {
    const shapeType = item.shapeType || "rect";
    const t = shapeType.toLowerCase();
    node.dataset.shapeType = shapeType;
    const isCallout = t.includes("callout");
    if (isCallout && (item.adj1 || item.adj2)) {
      node.classList.add("slide-shape-callout");
      node.style.overflow = "visible";
      node.appendChild(buildCalloutSvg(item));
      return;
    }
    node.classList.toggle("slide-shape-ellipse", t.includes("ellipse") && !isCallout);
    if (item.fillColor) node.style.backgroundColor = item.fillColor;
    if (item.lineColor) {
      node.style.borderColor = item.lineColor;
      node.style.borderStyle = "solid";
      node.style.borderWidth = `${pointToSlidePx(item.lineWidth || 1) / emuToSlidePx(slideWidth) * 100}cqw`;
    }
  }

  // wedgeEllipseCallout を ECMA-376 の幾何 (楕円 + しっぽ) に従って SVG で描画する。
  function buildCalloutSvg(item) {
    const ns = "http://www.w3.org/2000/svg";
    const w = item.width || 1;
    const h = item.height || 1;
    const vbH = 100 * h / w; // viewBox を縦横同一スケールに保つ
    const cx = 50;
    const cy = vbH / 2;
    const rx = 50;
    const ry = vbH / 2;
    const tipX = cx + (item.adj1 || 0) * 100;
    const tipY = cy + (item.adj2 || 0) * vbH;
    const pang = Math.atan2(item.adj2 || 0, item.adj1 || 0);
    const half = 11 * Math.PI / 180; // 吹き出し基部の半角 (660000/60000 度)
    const b1x = cx + rx * Math.cos(pang - half);
    const b1y = cy + ry * Math.sin(pang - half);
    const b2x = cx + rx * Math.cos(pang + half);
    const b2y = cy + ry * Math.sin(pang + half);
    const d = `M ${b1x} ${b1y} L ${tipX} ${tipY} L ${b2x} ${b2y} A ${rx} ${ry} 0 1 1 ${b1x} ${b1y} Z`;
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("class", "slide-shape-svg");
    svg.setAttribute("viewBox", `0 0 100 ${vbH}`);
    svg.setAttribute("preserveAspectRatio", "none");
    const path = document.createElementNS(ns, "path");
    path.setAttribute("d", d);
    path.setAttribute("fill", item.fillColor || "transparent");
    if (item.lineColor) {
      path.setAttribute("stroke", item.lineColor);
      path.setAttribute("stroke-width", String((item.lineWidth > 0 ? item.lineWidth : 1) * 1270000 / w));
      path.setAttribute("stroke-linejoin", "round");
    }
    svg.appendChild(path);
    return svg;
  }

  function buildSlideTable(table, slideWidth) {
    const wrap = el("div", "slide-table-wrap");
    const htmlTable = el("table", "slide-table");
    const columnWidths = table.columnWidths || [];
    const colSum = columnWidths.reduce((sum, w) => sum + (w || 0), 0);
    const colgroup = el("colgroup");
    columnWidths.forEach((width) => {
      const col = el("col");
      if (colSum > 0) col.style.width = `${(width || 0) / colSum * 100}%`;
      colgroup.appendChild(col);
    });
    htmlTable.appendChild(colgroup);

    const rowHeights = table.rowHeights || [];
    const rowSum = rowHeights.reduce((sum, h) => sum + (h || 0), 0);
    (table.rows || []).forEach((row, rowIndex) => {
      const tr = el("tr");
      const rowHeight = rowHeights[rowIndex] || 0;
      if (rowHeight > 0 && rowSum > 0) tr.style.height = `${rowHeight / rowSum * 100}%`;
      (row || []).forEach((cell) => {
        const td = el("td", "slide-table-cell");
        if (cell.fillColor) td.style.backgroundColor = cell.fillColor;
        td.style.textAlign = cell.textAlign || "left";
        td.style.verticalAlign = cell.verticalAlign === "center" ? "middle" : (cell.verticalAlign === "bottom" ? "bottom" : "top");
        appendRuns(td, cell.runs || [], cell.text || "", slideWidth);
        tr.appendChild(td);
      });
      htmlTable.appendChild(tr);
    });
    wrap.appendChild(htmlTable);
    return wrap;
  }

  function applyTextBoxVAlign(node, box) {
    const vertical = box.verticalAlign || "top";
    node.style.justifyContent = vertical === "center" ? "center" : (vertical === "bottom" ? "flex-end" : "flex-start");
  }

  // テキストボックスの段落を箇条書き・インデント・整列・行間つきで描画する。
  function appendSlideParagraphs(node, box, slideWidth) {
    (box.paragraphs || []).forEach((para) => {
      const p = el("div", "slide-para");
      p.style.textAlign = para.align || "left";
      if (para.lineSpace > 0) p.style.lineHeight = String(para.lineSpace);
      if (para.marginLeft > 0) p.style.paddingLeft = `${para.marginLeft / slideWidth * 100}cqw`;
      if (para.indent) p.style.textIndent = `${para.indent / slideWidth * 100}cqw`;
      if (para.bullet) {
        const bullet = el("span", "slide-bullet", `${para.bullet}\u00A0`);
        if (para.bulletColor) bullet.style.color = para.bulletColor;
        p.appendChild(bullet);
      }
      const runs = para.runs || [];
      if (runs.length === 0) {
        p.appendChild(el("span", "text-run", "\u00A0"));
      } else {
        runs.forEach((run) => {
          if (!run || run.text === undefined || run.text === null) return;
          if (run.text === "\n") {
            p.appendChild(el("br"));
          } else {
            const span = el("span", "text-run", String(run.text));
            applySlideRunStyle(span, run, slideWidth);
            p.appendChild(span);
          }
        });
      }
      node.appendChild(p);
    });
  }

  function appendRuns(node, runs, fallbackText, slideWidth) {
    if (runs.length === 0) {
      node.textContent = fallbackText;
      return;
    }
    const p = el("p", "slide-text");
    runs.forEach((run) => appendRunOrBreak(node, p, run, slideWidth));
    if (p.childNodes.length > 0) node.appendChild(p);
  }

  function appendRunOrBreak(parent, paragraph, run, slideWidth) {
    if (!run || run.text === undefined || run.text === null) return;
    if (run.text === "\n") {
      parent.appendChild(paragraph.cloneNode(true));
      clear(paragraph);
    } else {
      const span = el("span", "text-run", String(run.text));
      applySlideRunStyle(span, run, slideWidth);
      paragraph.appendChild(span);
    }
  }

  function placeSlideItem(node, item, slideWidth, slideHeight) {
    node.style.left = `${(item.x || 0) / slideWidth * 100}%`;
    node.style.top = `${(item.y || 0) / slideHeight * 100}%`;
    node.style.width = `${(item.width || 0) / slideWidth * 100}%`;
    node.style.height = `${(item.height || 0) / slideHeight * 100}%`;
  }

  function applySlideRunStyle(node, run, slideWidth) {
    applyRunStyle(node, run);
    if (run.fontSize > 0) node.style.fontSize = `${pointToSlidePx(run.fontSize) / emuToSlidePx(slideWidth) * 100}cqw`;
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

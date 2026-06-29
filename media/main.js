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
  // 検索
  // -------------------------------------------------------------------------
  // 表示モデルから {id, text, navigate} のインデックスを作り、Webview 内で完結して検索する。
  // id は座標から決定的に算出し、各レンダラーが付与する data-search-id と一致させる
  // (スプレッドシート/プレゼンはアクティブな面のみ DOM 化されるため、連番ではなく座標で対応付ける)。

  function normalizeSearchText(value) {
    return String(value === undefined || value === null ? "" : value).toLowerCase();
  }

  function searchSelector(id) {
    return `[data-search-id="${id}"]`;
  }

  function scrollNodeIntoView(node) {
    if (!node) return;
    try {
      node.scrollIntoView({ block: "center", inline: "nearest" });
    } catch (_) {
      node.scrollIntoView();
    }
  }

  // run 配列を検索用のプレーン文字列へ連結する。
  function runsText(runs) {
    return (runs || [])
      .map((run) => (run && run.text !== undefined && run.text !== null ? String(run.text) : ""))
      .join("");
  }

  const search = {
    entries: [],
    matches: [],
    active: -1,
    query: "",
    input: null,
    count: null,
    prev: null,
    next: null,

    // 新しい描画のたびにインデックスを差し替え、状態と装飾をリセットする。
    reset(entries) {
      this.entries = entries || [];
      this.matches = [];
      this.active = -1;
      this.query = "";
      if (this.input) this.input.value = "";
      this.clearHighlights();
      this.updateCount();
    },

    run(query) {
      this.query = query || "";
      const needle = normalizeSearchText(this.query);
      this.clearHighlights();
      if (needle === "") {
        this.matches = [];
        this.active = -1;
        this.updateCount();
        return;
      }
      this.matches = this.entries.filter((entry) => normalizeSearchText(entry.text).indexOf(needle) >= 0);
      this.active = this.matches.length > 0 ? 0 : -1;
      this.updateCount();
      if (this.active >= 0) this.activate(this.active);
    },

    step(delta) {
      if (this.matches.length === 0) return;
      this.active = (this.active + delta + this.matches.length) % this.matches.length;
      this.activate(this.active);
      this.updateCount();
    },

    // 対象の面へ切り替え (必要時)、装飾を貼り直し、該当ノードを表示位置へスクロールする。
    activate(index) {
      const match = this.matches[index];
      if (!match) return;
      if (match.navigate) match.navigate();
      this.applyHighlights();
      scrollNodeIntoView(app.querySelector(searchSelector(match.id)));
    },

    // 現在の DOM 内に存在する一致ノードへ装飾を付ける (面切替後の再描画にも追従)。
    applyHighlights() {
      this.clearHighlights();
      this.matches.forEach((match, i) => {
        const nodes = app.querySelectorAll(searchSelector(match.id));
        for (const node of nodes) {
          node.classList.add("search-match");
          if (i === this.active) node.classList.add("search-active");
        }
      });
    },

    clearHighlights() {
      const nodes = app.querySelectorAll(".search-match, .search-active");
      for (const node of nodes) node.classList.remove("search-match", "search-active");
    },

    updateCount() {
      if (this.count) {
        this.count.textContent = this.query === ""
          ? ""
          : `${this.matches.length === 0 ? 0 : this.active + 1} / ${this.matches.length}`;
      }
      const empty = this.matches.length === 0;
      if (this.prev) this.prev.disabled = empty;
      if (this.next) this.next.disabled = empty;
    },

    // 検索バーが表示されているときに入力欄へフォーカスし、既存の文字列を全選択する。
    focus() {
      if (!this.input || !this.input.isConnected) return false;
      this.input.focus();
      this.input.select();
      return true;
    },
  };

  // 検索バー (入力欄 + 件数 + 前後ボタン) を生成する。各レンダラーが app 直下へ追加する。
  function makeSearchBar() {
    const bar = el("div", "search-bar");
    const input = el("input", "search-input");
    input.type = "search";
    input.placeholder = "検索…";
    input.setAttribute("aria-label", "検索");
    const count = el("span", "search-count");
    const prev = el("button", "search-button", "\u2039");
    prev.type = "button";
    prev.title = "前の一致 (Shift+Enter)";
    const next = el("button", "search-button", "\u203A");
    next.type = "button";
    next.title = "次の一致 (Enter)";

    bar.appendChild(input);
    bar.appendChild(count);
    bar.appendChild(prev);
    bar.appendChild(next);

    input.addEventListener("input", () => search.run(input.value));
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        search.step(event.shiftKey ? -1 : 1);
      } else if (event.key === "Escape") {
        event.preventDefault();
        input.value = "";
        search.run("");
      }
    });
    prev.addEventListener("click", () => {
      search.step(-1);
      input.focus();
    });
    next.addEventListener("click", () => {
      search.step(1);
      input.focus();
    });

    search.input = input;
    search.count = count;
    search.prev = prev;
    search.next = next;
    return bar;
  }

  // 表示モデルから検索インデックスを構築する (id は DOM の data-search-id と一致させる)。
  function buildSpreadsheetIndex(data, showSheet) {
    const entries = [];
    (data.sheets || []).forEach((sheet, sheetIndex) => {
      (sheet.rows || []).forEach((row) => {
        if (!row || row.index > SHEET_ROW_LIMIT) return;
        (row.cells || []).forEach((cell) => {
          if (!hasCellText(cell)) return;
          entries.push({
            id: `sheet${sheetIndex}-r${row.index}-c${cell.col}`,
            text: String(cell.text),
            navigate: () => showSheet(sheetIndex),
          });
        });
      });
    });
    return entries;
  }

  function buildDocumentIndex(data) {
    const entries = [];
    (data.blocks || []).forEach((block, blockIndex) => {
      if (block.kind === "image") return;
      if (block.kind === "table") {
        (block.cells || []).forEach((row, r) => {
          (row || []).forEach((cell, c) => {
            const text = cell.text || runsText(cell.runs);
            if (text.trim() === "") return;
            entries.push({ id: `doc-block${blockIndex}-r${r}-c${c}`, text });
          });
        });
        return;
      }
      const text = block.text || runsText(block.runs);
      if (text.trim() === "") return;
      entries.push({ id: `doc-block${blockIndex}`, text });
    });
    return entries;
  }

  function buildPresentationIndex(data, showSlide) {
    const entries = [];
    (data.slides || []).forEach((slide, slideIndex) => {
      const navigate = () => showSlide(slideIndex);
      const textBoxes = slide.textBoxes || [];
      if (textBoxes.length > 0) {
        textBoxes.forEach((box, boxIndex) => {
          (box.paragraphs || []).forEach((para, paraIndex) => {
            const text = runsText(para.runs);
            if (text.trim() === "") return;
            entries.push({ id: `slide${slideIndex}-tb${boxIndex}-p${paraIndex}`, text, navigate });
          });
        });
      } else {
        if (slide.title) {
          entries.push({ id: `slide${slideIndex}-title`, text: String(slide.title), navigate });
        }
        (slide.texts || []).forEach((text, textIndex) => {
          if (String(text).trim() === "") return;
          entries.push({ id: `slide${slideIndex}-text${textIndex}`, text: String(text), navigate });
        });
      }
      (slide.tables || []).forEach((table, tableIndex) => {
        (table.rows || []).forEach((row, r) => {
          (row || []).forEach((cell, c) => {
            const text = cell.text || runsText(cell.runs);
            if (text.trim() === "") return;
            entries.push({ id: `slide${slideIndex}-tbl${tableIndex}-r${r}-c${c}`, text, navigate });
          });
        });
      });
    });
    return entries;
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
  const SHEET_ROW_LIMIT = 2000;
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
      search.reset([]);
      app.appendChild(el("div", "empty", "表示できるシートがありません。"));
      return;
    }

    const tabs = el("div", "tabs");
    const body = el("div", "sheet-body");

    let activeSheet = -1;
    function showSheet(index) {
      if (index === activeSheet) return;
      activeSheet = index;
      Array.from(tabs.children).forEach((t, i) =>
        t.classList.toggle("active", i === index)
      );
      clear(body);
      body.appendChild(buildSheetTable(sheets[index], index));
      search.applyHighlights();
    }

    sheets.forEach((sheet, i) => {
      const tab = el("button", "tab", sheet.name);
      tab.addEventListener("click", () => showSheet(i));
      tabs.appendChild(tab);
    });

    app.appendChild(makeSearchBar());
    app.appendChild(body);
    app.appendChild(tabs);

    search.reset(buildSpreadsheetIndex(data, showSheet));
    showSheet(0);
  }

  function buildSheetTable(sheet, sheetIndex) {
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
    const limit = Math.min(maxRow, SHEET_ROW_LIMIT);
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
          td.dataset.searchId = `sheet${sheetIndex}-r${row.index}-c${c}`;
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
      search.reset([]);
      app.appendChild(el("div", "empty", "表示できる内容がありません。"));
      return;
    }

    app.appendChild(makeSearchBar());
    const page = el("div", "page");
    blocks.forEach((block, blockIndex) => {
      if (block.kind === "heading") {
        const level = Math.min(Math.max(block.level || 1, 1), 6);
        const h = el("h" + level, "heading");
        h.dataset.searchId = `doc-block${blockIndex}`;
        if (block.align) h.style.textAlign = block.align;
        appendDocRuns(h, block);
        page.appendChild(h);
      } else if (block.kind === "table") {
        page.appendChild(buildDocTable(block, blockIndex));
      } else if (block.kind === "image") {
        const img = el("img", "doc-image");
        img.src = `data:${block.contentType};base64,${block.imageData}`;
        if (block.imageWidth > 0) img.style.width = px(block.imageWidth);
        if (block.imageHeight > 0) img.style.height = px(block.imageHeight);
        page.appendChild(img);
      } else {
        const p = el("p", "para");
        p.dataset.searchId = `doc-block${blockIndex}`;
        if (block.align) p.style.textAlign = block.align;
        if (block.bullet) {
          p.classList.add("list-item");
          p.style.marginLeft = px(24 * ((block.listLevel || 0) + 1));
          p.appendChild(el("span", "list-marker", block.bullet + " "));
        }
        appendDocRuns(p, block);
        if (!block.text && !block.bullet) p.classList.add("empty-para");
        page.appendChild(p);
      }
    });
    app.appendChild(page);
    search.reset(buildDocumentIndex(data));
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

  function buildDocTable(block, blockIndex) {
    const table = el("table", "doc-table");
    if (block.hasBorders) table.classList.add("bordered");
    const colOwner = {};
    (block.cells || []).forEach((row, r) => {
      const tr = el("tr");
      let col = 0;
      (row || []).forEach((cell, c) => {
        const span = Math.max(1, cell.gridSpan || 1);
        if (cell.vMergeContinue && colOwner[col]) {
          colOwner[col].rowSpan = (colOwner[col].rowSpan || 1) + 1;
          col += span;
          return;
        }
        const td = el("td");
        td.dataset.searchId = `doc-block${blockIndex}-r${r}-c${c}`;
        if (span > 1) td.colSpan = span;
        appendDocRuns(td, cell);
        tr.appendChild(td);
        colOwner[col] = td;
        col += span;
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
      search.reset([]);
      app.appendChild(el("div", "empty", "表示できるスライドがありません。"));
      return;
    }

    const layout = el("div", "slide-layout");
    const list = el("aside", "slide-list");
    const stage = el("div", "slide-stage");
    layout.appendChild(list);
    layout.appendChild(stage);

    let activeSlide = -1;
    function showSlide(index) {
      if (index === activeSlide) return;
      activeSlide = index;
      Array.from(list.children).forEach((t, i) =>
        t.classList.toggle("active", i === index)
      );
      clear(stage);
      stage.appendChild(buildSlide(slides[index], index));
      search.applyHighlights();
    }

    slides.forEach((slide, i) => {
      const item = el("button", "slide-thumb");
      item.appendChild(el("span", "slide-no", String(slide.index)));
      item.appendChild(el("span", "slide-title", slide.title || "(無題)"));
      item.addEventListener("click", () => showSlide(i));
      list.appendChild(item);
    });

    app.appendChild(makeSearchBar());
    app.appendChild(layout);

    search.reset(buildPresentationIndex(data, showSlide));
    showSlide(0);
  }

  function buildSlide(slide, slideIndex) {
    const card = el("div", "slide-card");
    const slideWidth = slide.width || 12192000;
    const slideHeight = slide.height || 6858000;
    card.style.aspectRatio = `${slideWidth} / ${slideHeight}`;
    applyFillStyle(card, slide.backgroundColor);

    (slide.shapes || []).forEach((shape) => {
      const node = el("div", "slide-shape");
      applySlideShapeStyle(node, shape, slideWidth);
      placeSlideItem(node, shape, slideWidth, slideHeight);
      card.appendChild(node);
    });

    (slide.tables || []).forEach((table, tableIndex) => {
      const node = buildSlideTable(table, slideWidth, slideIndex, tableIndex);
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
      textBoxes.forEach((box, boxIndex) => {
        const node = el("div", "slide-textbox");
        applySlideShapeStyle(node, box, slideWidth);
        applyTextBoxVAlign(node, box);
        appendSlideParagraphs(node, box, slideWidth, slideIndex, boxIndex);
        placeSlideItem(node, box, slideWidth, slideHeight);
        card.appendChild(node);
      });
    } else {
      if (slide.title) {
        const heading = el("h2", "slide-heading", slide.title);
        heading.dataset.searchId = `slide${slideIndex}-title`;
        card.appendChild(heading);
      }
      (slide.texts || []).forEach((text, textIndex) => {
        const p = el("p", "slide-text", text);
        p.dataset.searchId = `slide${slideIndex}-text${textIndex}`;
        card.appendChild(p);
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
    applyFillStyle(node, item.fillColor);
    if (item.lineColor) {
      node.style.borderColor = item.lineColor;
      node.style.borderStyle = "solid";
      node.style.borderWidth = `${pointToSlidePx(item.lineWidth || 1) / emuToSlidePx(slideWidth) * 100}cqw`;
    }
  }

  // 塗り値 (色 / グラデーション / 画像 url) を適切な CSS プロパティへ割り当てる。
  function applyFillStyle(node, value) {
    if (!value) return;
    if (value[0] === "#") node.style.backgroundColor = value;
    else node.style.background = value;
  }

  // SVG の fill 属性用に塗り値を解決する (グラデーション文字列からは最初の色を抽出)。
  function svgFill(value) {
    if (!value) return "transparent";
    if (value[0] === "#") return value;
    const m = value.match(/#[0-9a-fA-F]{6}/);
    return m ? m[0] : "#cccccc";
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
    path.setAttribute("fill", svgFill(item.fillColor));
    if (item.lineColor) {
      path.setAttribute("stroke", item.lineColor);
      path.setAttribute("stroke-width", String((item.lineWidth > 0 ? item.lineWidth : 1) * 1270000 / w));
      path.setAttribute("stroke-linejoin", "round");
    }
    svg.appendChild(path);
    return svg;
  }

  function buildSlideTable(table, slideWidth, slideIndex, tableIndex) {
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
      (row || []).forEach((cell, cellIndex) => {
        const td = el("td", "slide-table-cell");
        td.dataset.searchId = `slide${slideIndex}-tbl${tableIndex}-r${rowIndex}-c${cellIndex}`;
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
  function appendSlideParagraphs(node, box, slideWidth, slideIndex, boxIndex) {
    (box.paragraphs || []).forEach((para, paraIndex) => {
      const p = el("div", "slide-para");
      p.dataset.searchId = `slide${slideIndex}-tb${boxIndex}-p${paraIndex}`;
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
    search.reset([]);
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

  // Ctrl+F / Cmd+F で検索バーの入力欄へフォーカスする (検索バーが表示されている場合のみ)。
  window.addEventListener("keydown", (event) => {
    const isFindKey = event.code === "KeyF" || event.key === "f" || event.key === "F";
    if (isFindKey && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) {
      if (search.focus()) event.preventDefault();
    }
  });

  window.addEventListener("message", (event) => {
    const message = event.data;
    if (message && message.type === "render") {
      render(message.payload);
    }
  });
})();

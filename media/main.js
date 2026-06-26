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

  // -------------------------------------------------------------------------
  // スプレッドシート
  // -------------------------------------------------------------------------
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
    app.appendChild(tabs);
    app.appendChild(body);

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
    const maxCol = sheet.maxCol || 0;
    const table = el("table", "grid");

    const thead = el("thead");
    const headRow = el("tr");
    headRow.appendChild(el("th", "corner", ""));
    for (let c = 0; c <= maxCol; c++) {
      headRow.appendChild(el("th", "col-head", colLetter(c)));
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = el("tbody");
    const rows = sheet.rows || [];
    const limit = Math.min(rows.length, 2000);
    for (let r = 0; r < limit; r++) {
      const row = rows[r];
      const tr = el("tr");
      tr.appendChild(el("th", "row-head", String(row.index)));
      const cellByCol = {};
      (row.cells || []).forEach((cell) => {
        cellByCol[cell.col] = cell.text;
      });
      for (let c = 0; c <= maxCol; c++) {
        tr.appendChild(el("td", "cell", cellByCol[c] !== undefined ? cellByCol[c] : ""));
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);

    const wrap = el("div", "table-wrap");
    wrap.appendChild(table);
    if (rows.length > limit) {
      wrap.appendChild(el("div", "truncation", `先頭 ${limit} 行のみ表示しています (全 ${rows.length} 行)。`));
    }
    return wrap;
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

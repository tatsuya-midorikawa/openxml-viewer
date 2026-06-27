/// SpreadsheetML (.xlsx) パーサー。
/// ECMA-376 Part 1 §18 (SpreadsheetML) を参照。
module OpenXmlViewer.Parsers.Spreadsheet

open OpenXmlViewer.Core
open OpenXmlViewer.Model

/// 数字のみの文字列を整数へ変換する (失敗時は None)。
let private tryParseInt (s: string) : int option =
    let t = s.Trim()
    let mutable acc = 0
    let mutable ok = t.Length > 0
    for ch in t do
        if ch >= '0' && ch <= '9' then acc <- acc * 10 + (int ch - int '0')
        else ok <- false
    if ok then Some acc else None

/// 小数を含む数値文字列を float へ変換する (失敗時は None)。
let private tryParseFloat (s: string) : float option =
    let t = s.Trim()
    let mutable i = 0
    let mutable sign = 1.0
    if t.Length > 0 && t.[0] = '-' then
        sign <- -1.0
        i <- 1
    elif t.Length > 0 && t.[0] = '+' then
        i <- 1

    let mutable value = 0.0
    let mutable hasDigit = false
    while i < t.Length && t.[i] >= '0' && t.[i] <= '9' do
        value <- value * 10.0 + float (int t.[i] - int '0')
        hasDigit <- true
        i <- i + 1

    if i < t.Length && t.[i] = '.' then
        i <- i + 1
        let mutable scale = 0.1
        while i < t.Length && t.[i] >= '0' && t.[i] <= '9' do
            value <- value + scale * float (int t.[i] - int '0')
            scale <- scale / 10.0
            hasDigit <- true
            i <- i + 1

    if hasDigit && i = t.Length then Some(sign * value) else None

let private attrInt name el = Xml.attrLocal name el |> Option.bind tryParseInt
let private attrFloat name el = Xml.attrLocal name el |> Option.bind tryParseFloat

/// セル参照 (例 "AB12") の列部分を 0 始まりの列番号へ変換する。
let private colFromRef (cellRef: string) : int =
    let mutable col = 0
    let mutable i = 0
    let mutable go = true
    while go && i < cellRef.Length do
        let c = cellRef.[i]
        if c >= 'A' && c <= 'Z' then
            col <- col * 26 + (int c - int 'A' + 1)
            i <- i + 1
        elif c >= 'a' && c <= 'z' then
            col <- col * 26 + (int c - int 'a' + 1)
            i <- i + 1
        else
            go <- false
    col - 1

let private emuToPx (emu: float) : float = emu / 9525.0

let private columnWidthToPx (width: float) : float = floor (width * 7.0 + 5.0)

let private rowHeightToPx (height: float) : float = height * 96.0 / 72.0

let private defaultColWidth = 8.43
let private defaultRowHeight = 15.0

let private columnWidthPx (defaultWidth: float) (columns: Column[]) (col: int) : float =
    columns
    |> Array.tryFind (fun c -> c.min <= col && col <= c.max)
    |> Option.map (fun c -> c.width)
    |> Option.defaultValue defaultWidth
    |> columnWidthToPx

let private rowHeightPx (defaultHeight: float) (rows: Row[]) (row: int) : float =
    rows
    |> Array.tryFind (fun r -> r.index = row + 1)
    |> Option.map (fun r -> r.height)
    |> Option.filter (fun h -> h > 0.0)
    |> Option.defaultValue defaultHeight
    |> rowHeightToPx

/// <si> / <is> 要素のテキストを取り出す (phonetic <rPh> は除外)。
let private richText (el: Xml.XmlElement) : string =
    el
    |> Xml.elementChildren
    |> List.choose (fun e ->
        match Xml.localName e.Name with
        | "t" -> Some(Xml.innerText e)
        | "r" -> Some(Xml.childrenByLocal "t" e |> List.map Xml.innerText |> String.concat "")
        | _ -> None)
    |> String.concat ""

/// 共有文字列テーブル (xl/sharedStrings.xml) を読み込む。
let private parseSharedStrings (archive: Zip.ZipArchive) : string[] =
    match Zip.tryReadBytes archive "xl/sharedStrings.xml" with
    | None -> [||]
    | Some bytes ->
        Xml.parseBytes bytes
        |> Xml.childrenByLocal "si"
        |> List.map richText
        |> Array.ofList

/// 1 つのセルを解析する。
let private parseCell (shared: string[]) (c: Xml.XmlElement) : Cell =
    let col = Xml.attrLocal "r" c |> Option.map colFromRef |> Option.defaultValue 0
    let cellType = Xml.attrLocal "t" c |> Option.defaultValue "n"
    let text =
        match cellType with
        | "s" ->
            match Xml.tryChildByLocal "v" c |> Option.bind (Xml.innerText >> tryParseInt) with
            | Some i when i >= 0 && i < shared.Length -> shared.[i]
            | _ -> ""
        | "inlineStr" ->
            match Xml.tryChildByLocal "is" c with
            | Some is -> richText is
            | None -> ""
        | _ ->
            match Xml.tryChildByLocal "v" c with
            | Some v -> Xml.innerText v
            | None -> ""
    { col = col; text = text }

/// ワークシートの既定寸法を解析する。
let private parseSheetDefaults (root: Xml.XmlElement) : float * float =
    match Xml.tryChildByLocal "sheetFormatPr" root with
    | Some sheetFormat ->
        let colWidth = attrFloat "defaultColWidth" sheetFormat |> Option.defaultValue defaultColWidth
        let rowHeight = attrFloat "defaultRowHeight" sheetFormat |> Option.defaultValue defaultRowHeight
        colWidth, rowHeight
    | None -> defaultColWidth, defaultRowHeight

/// <cols> の列幅定義を解析する。
let private parseColumns (root: Xml.XmlElement) : Column[] =
    match Xml.tryChildByLocal "cols" root with
    | None -> [||]
    | Some cols ->
        Xml.childrenByLocal "col" cols
        |> List.choose (fun col ->
            match attrFloat "width" col with
            | Some width when width > 0.0 ->
                let minCol = attrInt "min" col |> Option.defaultValue 1
                let maxCol = attrInt "max" col |> Option.defaultValue minCol
                Some { min = max 0 (minCol - 1); max = max 0 (maxCol - 1); width = width }
            | _ -> None)
        |> Array.ofList

/// ワークシート XML の行配列を解析する。
let private parseRows (shared: string[]) (root: Xml.XmlElement) : Row[] =
    match Xml.tryChildByLocal "sheetData" root with
    | None -> [||]
    | Some sheetData ->
        Xml.childrenByLocal "row" sheetData
        |> List.mapi (fun i row ->
            let idx = attrInt "r" row |> Option.defaultValue (i + 1)
            let height = attrFloat "ht" row |> Option.defaultValue 0.0
            let cells =
                Xml.childrenByLocal "c" row
                |> List.map (parseCell shared)
                |> Array.ofList
            { index = idx; height = height; cells = cells })
        |> Array.ofList

type private Marker =
    { Col: int
      Row: int
      ColOffset: float
      RowOffset: float }

let private markerInt name marker =
    Xml.tryChildByLocal name marker
    |> Option.map Xml.innerText
    |> Option.bind tryParseInt
    |> Option.defaultValue 0

let private markerEmu name marker =
    Xml.tryChildByLocal name marker
    |> Option.map Xml.innerText
    |> Option.bind tryParseFloat
    |> Option.map emuToPx
    |> Option.defaultValue 0.0

let private parseMarker name anchor : Marker option =
    Xml.tryChildByLocal name anchor
    |> Option.map (fun marker ->
        { Col = markerInt "col" marker
          Row = markerInt "row" marker
          ColOffset = markerEmu "colOff" marker
          RowOffset = markerEmu "rowOff" marker })

let private contentType (path: string) : string option =
    let lower = path.ToLower()
    if lower.EndsWith ".png" then Some "image/png"
    elif lower.EndsWith ".jpg" || lower.EndsWith ".jpeg" then Some "image/jpeg"
    elif lower.EndsWith ".gif" then Some "image/gif"
    elif lower.EndsWith ".bmp" then Some "image/bmp"
    elif lower.EndsWith ".webp" then Some "image/webp"
    elif lower.EndsWith ".svg" || lower.EndsWith ".svgz" then Some "image/svg+xml"
    else None

let private imageData (archive: Zip.ZipArchive) (path: string) : (string * string) option =
    match contentType path, Zip.tryReadBytes archive path with
    | Some mime, Some bytes -> Some(mime, System.Convert.ToBase64String bytes)
    | _ -> None

let private altText (anchor: Xml.XmlElement) : string =
    Xml.descendantsByLocal "cNvPr" anchor
    |> Seq.tryPick (fun p ->
        Xml.attrLocal "descr" p
        |> Option.orElseWith (fun () -> Xml.attrLocal "name" p))
    |> Option.defaultValue ""

let private imageSize defaultColumnWidth defaultRowHeight columns rows fromMarker toMarker ext =
    match ext with
    | Some extEl ->
        let width = attrFloat "cx" extEl |> Option.map emuToPx |> Option.defaultValue 0.0
        let height = attrFloat "cy" extEl |> Option.map emuToPx |> Option.defaultValue 0.0
        width, height
    | None ->
        match toMarker with
        | Some marker when marker.Col >= fromMarker.Col && marker.Row >= fromMarker.Row ->
            let mutable width = marker.ColOffset - fromMarker.ColOffset
            for c in fromMarker.Col .. marker.Col - 1 do
                width <- width + columnWidthPx defaultColumnWidth columns c

            let mutable height = marker.RowOffset - fromMarker.RowOffset
            for r in fromMarker.Row .. marker.Row - 1 do
                height <- height + rowHeightPx defaultRowHeight rows r

            max 1.0 width, max 1.0 height
        | _ -> 0.0, 0.0

let private parseImage defaultColumnWidth defaultRowHeight (columns: Column[]) (rows: Row[]) (archive: Zip.ZipArchive) (drawingPath: string) (rels: Map<string, Opc.Relationship>) (anchor: Xml.XmlElement) : SheetImage option =
    let rid = Xml.descendantsByLocal "blip" anchor |> Seq.tryPick (Xml.attrLocal "embed")
    let fromMarker = parseMarker "from" anchor
    match rid, fromMarker with
    | Some rid, Some fromMarker ->
        match Map.tryFind rid rels with
        | Some rel when rel.TargetMode <> "External" ->
            let target = Opc.resolveTarget drawingPath rel.Target
            match imageData archive target with
            | Some(mime, data) ->
                let toMarker = parseMarker "to" anchor
                let ext = Xml.tryChildByLocal "ext" anchor
                let width, height = imageSize defaultColumnWidth defaultRowHeight columns rows fromMarker toMarker ext
                let toCol = toMarker |> Option.map (fun m -> m.Col) |> Option.defaultValue fromMarker.Col
                let toRow = toMarker |> Option.map (fun m -> m.Row) |> Option.defaultValue fromMarker.Row
                Some
                    { col = fromMarker.Col
                      row = fromMarker.Row
                      colOffset = fromMarker.ColOffset
                      rowOffset = fromMarker.RowOffset
                      toCol = toCol
                      toRow = toRow
                      width = width
                      height = height
                      altText = altText anchor
                      contentType = mime
                      data = data }
            | None -> None
        | _ -> None
    | _ -> None

let private parseImages defaultColumnWidth defaultRowHeight (columns: Column[]) (rows: Row[]) (archive: Zip.ZipArchive) (sheetPath: string) (root: Xml.XmlElement) : SheetImage[] =
    let sheetRels = Opc.loadRels archive sheetPath
    let drawingIds = Xml.descendantsByLocal "drawing" root |> Seq.choose (Xml.attrLocal "id")

    drawingIds
    |> Seq.choose (fun rid -> Map.tryFind rid sheetRels)
    |> Seq.filter (fun rel -> rel.TargetMode <> "External")
    |> Seq.collect (fun rel ->
        let drawingPath = Opc.resolveTarget sheetPath rel.Target
        match Zip.tryReadBytes archive drawingPath with
        | None -> Seq.empty<SheetImage>
        | Some drawingBytes ->
            let drawing = Xml.parseBytes drawingBytes
            let rels = Opc.loadRels archive drawingPath
            Seq.append (Xml.descendantsByLocal "twoCellAnchor" drawing) (Xml.descendantsByLocal "oneCellAnchor" drawing)
            |> Seq.choose (parseImage defaultColumnWidth defaultRowHeight columns rows archive drawingPath rels))
    |> Array.ofSeq

/// .xlsx バイト列を解析する。
let parse (data: byte[]) : SpreadsheetData =
    let archive = Zip.read data
    let shared = parseSharedStrings archive
    let rels = Opc.loadRels archive "xl/workbook.xml"

    match Zip.tryReadBytes archive "xl/workbook.xml" with
    | None -> { kind = "spreadsheet"; sheets = [||] }
    | Some wbBytes ->
        let wb = Xml.parseBytes wbBytes
        let sheets =
            match Xml.tryChildByLocal "sheets" wb with
            | None -> [||]
            | Some sheetsEl ->
                Xml.childrenByLocal "sheet" sheetsEl
                |> List.choose (fun sheetEl ->
                    let name = Xml.attrLocal "name" sheetEl |> Option.defaultValue "Sheet"
                    let target =
                        Xml.attrLocal "id" sheetEl
                        |> Option.bind (fun rid -> Map.tryFind rid rels)
                        |> Option.map (fun r -> Opc.resolveTarget "xl/workbook.xml" r.Target)
                    match target with
                    | Some sheetPath ->
                        match Zip.tryReadBytes archive sheetPath with
                        | None -> None
                        | Some sheetBytes ->
                        let sheetRoot = Xml.parseBytes sheetBytes
                        let defaultColumnWidth, defaultRowHeight = parseSheetDefaults sheetRoot
                        let columns = parseColumns sheetRoot
                        let rows = parseRows shared sheetRoot
                        let images = parseImages defaultColumnWidth defaultRowHeight columns rows archive sheetPath sheetRoot
                        let maxCol =
                            let cellCols = rows |> Array.collect (fun r -> r.cells |> Array.map (fun c -> c.col))
                            let imageCols = images |> Array.collect (fun i -> [| i.col; i.toCol |])
                            Array.append cellCols imageCols
                            |> fun cols -> if cols.Length = 0 then 0 else Array.max cols
                        Some
                            { name = name
                              rows = rows
                              columns = columns
                              images = images
                              maxCol = maxCol
                              defaultColWidth = defaultColumnWidth
                              defaultRowHeight = defaultRowHeight }
                    | None -> None)
                |> Array.ofList
        { kind = "spreadsheet"; sheets = sheets }

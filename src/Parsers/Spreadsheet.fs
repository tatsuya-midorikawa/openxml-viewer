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

type private FontStyle =
    { Bold: bool
      Italic: bool
      Underline: bool
      Strike: bool
      FontSize: float
      FontName: string
      Color: string }

type private RawTextRun =
    { Text: string
      Bold: bool option
      Italic: bool option
      Underline: bool option
      Strike: bool option
      FontSize: float option
      FontName: string option
      Color: string option }

type private RichTextValue = { Text: string; Runs: RawTextRun[] }

let private defaultFont =
    { Bold = false
      Italic = false
      Underline = false
      Strike = false
      FontSize = 11.0
      FontName = ""
      Color = "" }

let private normalizeColor (value: string) : string =
    let hex = if value.Length = 8 then value.Substring 2 else value
    if hex.Length = 6 then "#" + hex else ""

let private parseThemeColors (archive: Zip.ZipArchive) : Map<int, string> =
    match Zip.tryReadBytes archive "xl/theme/theme1.xml" with
    | None -> Map.empty
    | Some bytes ->
        let theme = Xml.parseBytes bytes
        let names = [| "lt1"; "dk1"; "lt2"; "dk2"; "accent1"; "accent2"; "accent3"; "accent4"; "accent5"; "accent6"; "hlink"; "folHlink" |]
        names
        |> Array.mapi (fun i name ->
            Xml.descendantsByLocal name theme
            |> Seq.tryHead
            |> Option.bind (fun color ->
                Xml.tryChildByLocal "srgbClr" color
                |> Option.bind (Xml.attrLocal "val")
                |> Option.orElseWith (fun () -> Xml.tryChildByLocal "sysClr" color |> Option.bind (Xml.attrLocal "lastClr"))
                |> Option.map normalizeColor)
            |> Option.map (fun color -> i, color))
        |> Array.choose id
        |> Map.ofArray

let private indexedColor (index: int) : string =
    match index with
    | 0 -> "#000000"
    | 1 -> "#FFFFFF"
    | 2 -> "#FF0000"
    | 3 -> "#00FF00"
    | 4 -> "#0000FF"
    | 5 -> "#FFFF00"
    | 6 -> "#FF00FF"
    | 7 -> "#00FFFF"
    | 8 -> "#000000"
    | 9 -> "#FFFFFF"
    | 10 -> "#FF0000"
    | 11 -> "#00FF00"
    | 12 -> "#0000FF"
    | 13 -> "#FFFF00"
    | 14 -> "#FF00FF"
    | 15 -> "#00FFFF"
    | _ -> ""

let private parseColor (themeColors: Map<int, string>) (el: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "color" el
    |> Option.bind (fun color ->
        Xml.attrLocal "rgb" color
        |> Option.map normalizeColor
        |> Option.orElseWith (fun () ->
            Xml.attrLocal "theme" color
            |> Option.bind tryParseInt
            |> Option.bind (fun theme -> Map.tryFind theme themeColors))
        |> Option.orElseWith (fun () ->
            Xml.attrLocal "indexed" color
            |> Option.bind tryParseInt
            |> Option.map indexedColor))
    |> Option.filter (fun color -> color <> "")

let private parseFont (themeColors: Map<int, string>) (font: Xml.XmlElement) : FontStyle =
    { Bold = Xml.tryChildByLocal "b" font |> Option.isSome
      Italic = Xml.tryChildByLocal "i" font |> Option.isSome
      Underline = Xml.tryChildByLocal "u" font |> Option.isSome
      Strike = Xml.tryChildByLocal "strike" font |> Option.isSome
      FontSize = Xml.tryChildByLocal "sz" font |> Option.bind (attrFloat "val") |> Option.defaultValue defaultFont.FontSize
      FontName = Xml.tryChildByLocal "name" font |> Option.bind (Xml.attrLocal "val") |> Option.defaultValue defaultFont.FontName
      Color = parseColor themeColors font |> Option.defaultValue defaultFont.Color }

let private parseStyleFonts (archive: Zip.ZipArchive) : FontStyle[] =
    match Zip.tryReadBytes archive "xl/styles.xml" with
    | None -> [| defaultFont |]
    | Some bytes ->
        let styles = Xml.parseBytes bytes
        let themeColors = parseThemeColors archive
        let fonts =
            match Xml.tryChildByLocal "fonts" styles with
            | Some fonts -> Xml.childrenByLocal "font" fonts |> List.map (parseFont themeColors) |> Array.ofList
            | None -> [| defaultFont |]

        match Xml.tryChildByLocal "cellXfs" styles with
        | Some cellXfs ->
            Xml.childrenByLocal "xf" cellXfs
            |> List.map (fun xf ->
                let fontId = attrInt "fontId" xf |> Option.defaultValue 0
                if fontId >= 0 && fontId < fonts.Length then fonts.[fontId] else defaultFont)
            |> Array.ofList
        | None -> fonts

let private rawTextRun text =
    { Text = text
      Bold = None
      Italic = None
      Underline = None
      Strike = None
      FontSize = None
      FontName = None
      Color = None }

let private parseRunProperties (themeColors: Map<int, string>) (rPr: Xml.XmlElement option) : RawTextRun -> RawTextRun =
    match rPr with
    | None -> id
    | Some rPr ->
        fun run ->
            { run with
                Bold = if Xml.tryChildByLocal "b" rPr |> Option.isSome then Some true else run.Bold
                Italic = if Xml.tryChildByLocal "i" rPr |> Option.isSome then Some true else run.Italic
                Underline = if Xml.tryChildByLocal "u" rPr |> Option.isSome then Some true else run.Underline
                Strike = if Xml.tryChildByLocal "strike" rPr |> Option.isSome then Some true else run.Strike
                FontSize = Xml.tryChildByLocal "sz" rPr |> Option.bind (attrFloat "val") |> Option.orElse run.FontSize
                FontName = Xml.tryChildByLocal "rFont" rPr |> Option.bind (Xml.attrLocal "val") |> Option.orElse run.FontName
                Color = parseColor themeColors rPr |> Option.orElse run.Color }

let private materializeRun (font: FontStyle) (run: RawTextRun) : TextRun =
    { text = run.Text
      bold = defaultArg run.Bold font.Bold
      italic = defaultArg run.Italic font.Italic
      underline = defaultArg run.Underline font.Underline
      strike = defaultArg run.Strike font.Strike
      fontSize = defaultArg run.FontSize font.FontSize
      fontName = defaultArg run.FontName font.FontName
      color = defaultArg run.Color font.Color }

let private richText (themeColors: Map<int, string>) (el: Xml.XmlElement) : RichTextValue =
    let runs =
        el
        |> Xml.elementChildren
        |> List.choose (fun e ->
            match Xml.localName e.Name with
            | "t" -> Some(rawTextRun (Xml.innerText e))
            | "r" ->
                let text = Xml.childrenByLocal "t" e |> List.map Xml.innerText |> String.concat ""
                Some(parseRunProperties themeColors (Xml.tryChildByLocal "rPr" e) (rawTextRun text))
            | _ -> None)
        |> Array.ofList

    { Text = runs |> Array.map (fun r -> r.Text) |> String.concat ""
      Runs = runs }

/// 共有文字列テーブル (xl/sharedStrings.xml) を読み込む。
let private parseSharedStrings (archive: Zip.ZipArchive) (themeColors: Map<int, string>) : RichTextValue[] =
    match Zip.tryReadBytes archive "xl/sharedStrings.xml" with
    | None -> [||]
    | Some bytes ->
        Xml.parseBytes bytes
        |> Xml.childrenByLocal "si"
        |> List.map (richText themeColors)
        |> Array.ofList

/// 1 つのセルを解析する。
let private parseCell (shared: RichTextValue[]) (styleFonts: FontStyle[]) (themeColors: Map<int, string>) (c: Xml.XmlElement) : Cell =
    let col = Xml.attrLocal "r" c |> Option.map colFromRef |> Option.defaultValue 0
    let cellType = Xml.attrLocal "t" c |> Option.defaultValue "n"
    let font =
        Xml.attrLocal "s" c
        |> Option.bind tryParseInt
        |> Option.bind (fun i -> if i >= 0 && i < styleFonts.Length then Some styleFonts.[i] else None)
        |> Option.defaultValue defaultFont
    let value =
        match cellType with
        | "s" ->
            match Xml.tryChildByLocal "v" c |> Option.bind (Xml.innerText >> tryParseInt) with
            | Some i when i >= 0 && i < shared.Length -> shared.[i]
            | _ -> { Text = ""; Runs = [||] }
        | "inlineStr" ->
            match Xml.tryChildByLocal "is" c with
            | Some is -> richText themeColors is
            | None -> { Text = ""; Runs = [||] }
        | _ ->
            match Xml.tryChildByLocal "v" c with
            | Some v -> { Text = Xml.innerText v; Runs = [||] }
            | None -> { Text = ""; Runs = [||] }
    let runs =
        if value.Runs.Length = 0 && value.Text <> "" then [| materializeRun font (rawTextRun value.Text) |]
        else value.Runs |> Array.map (materializeRun font)
    { col = col; text = value.Text; runs = runs }

/// ワークシートの既定寸法を解析する。
let private parseSheetDefaults (root: Xml.XmlElement) : float * float =
    match Xml.tryChildByLocal "sheetFormatPr" root with
    | Some sheetFormat ->
        let colWidth = attrFloat "defaultColWidth" sheetFormat |> Option.defaultValue defaultColWidth
        let rowHeight = attrFloat "defaultRowHeight" sheetFormat |> Option.defaultValue defaultRowHeight
        colWidth, rowHeight
    | None -> defaultColWidth, defaultRowHeight

/// シートビューのグリッド線表示設定を解析する。未指定の場合は Excel の既定どおり表示する。
let private parseShowGridLines (root: Xml.XmlElement) : bool =
    Xml.tryChildByLocal "sheetViews" root
    |> Option.bind (Xml.tryChildByLocal "sheetView")
    |> Option.bind (Xml.attrLocal "showGridLines")
    |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false")
    |> Option.defaultValue true

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
let private parseRows (shared: RichTextValue[]) (styleFonts: FontStyle[]) (themeColors: Map<int, string>) (root: Xml.XmlElement) : Row[] =
    match Xml.tryChildByLocal "sheetData" root with
    | None -> [||]
    | Some sheetData ->
        Xml.childrenByLocal "row" sheetData
        |> List.mapi (fun i row ->
            let idx = attrInt "r" row |> Option.defaultValue (i + 1)
            let height = attrFloat "ht" row |> Option.defaultValue 0.0
            let cells =
                Xml.childrenByLocal "c" row
                |> List.map (parseCell shared styleFonts themeColors)
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
    let themeColors = parseThemeColors archive
    let styleFonts = parseStyleFonts archive
    let shared = parseSharedStrings archive themeColors
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
                        let showGridLines = parseShowGridLines sheetRoot
                        let columns = parseColumns sheetRoot
                        let rows = parseRows shared styleFonts themeColors sheetRoot
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
                              showGridLines = showGridLines
                              defaultColWidth = defaultColumnWidth
                              defaultRowHeight = defaultRowHeight }
                    | None -> None)
                |> Array.ofList
        { kind = "spreadsheet"; sheets = sheets }

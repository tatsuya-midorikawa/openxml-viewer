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

let private rgbOfHex (color: string) : (int * int * int) option =
    if color.Length = 7 && color.[0] = '#' then
        Some(System.Convert.ToInt32(color.Substring(1, 2), 16), System.Convert.ToInt32(color.Substring(3, 2), 16), System.Convert.ToInt32(color.Substring(5, 2), 16))
    else
        None

/// テーマ色の tint 属性 (-1..1) を近似適用する。負で暗く、正で明るくする。
let private applyTint (tint: float) (color: string) : string =
    if tint = 0.0 then color
    else
        match rgbOfHex color with
        | None -> color
        | Some(r, g, b) ->
            let f channel =
                let v = if tint < 0.0 then float channel * (1.0 + tint) else float channel + (255.0 - float channel) * tint
                max 0 (min 255 (int (round v)))
            sprintf "#%02X%02X%02X" (f r) (f g) (f b)

/// 色要素 (color/fgColor 等) の rgb/theme/indexed/tint を解決する。
let private colorFromElement (themeColors: Map<int, string>) (color: Xml.XmlElement) : string option =
    Xml.attrLocal "rgb" color
    |> Option.map normalizeColor
    |> Option.orElseWith (fun () ->
        Xml.attrLocal "theme" color
        |> Option.bind tryParseInt
        |> Option.bind (fun theme -> Map.tryFind theme themeColors)
        |> Option.map (fun baseColor ->
            let tint = attrFloat "tint" color |> Option.defaultValue 0.0
            applyTint tint baseColor))
    |> Option.orElseWith (fun () ->
        Xml.attrLocal "indexed" color
        |> Option.bind tryParseInt
        |> Option.map indexedColor)
    |> Option.filter (fun color -> color <> "")

let private parseColor (themeColors: Map<int, string>) (el: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "color" el |> Option.bind (colorFromElement themeColors)

// ---------------------------------------------------------------------------
// 数値書式 (ECMA-376 §18.8.30 numFmt)
// ---------------------------------------------------------------------------

/// 組み込み書式 ID (0..49) を書式コードへ対応付ける。
let private builtinNumFmt (id: int) : string =
    match id with
    | 1 -> "0"
    | 2 -> "0.00"
    | 3 -> "#,##0"
    | 4 -> "#,##0.00"
    | 9 -> "0%"
    | 10 -> "0.00%"
    | 11 -> "0.00E+00"
    | 12 -> "# ?/?"
    | 13 -> "# ??/??"
    | 14 -> "m/d/yyyy"
    | 15 -> "d-mmm-yy"
    | 16 -> "d-mmm"
    | 17 -> "mmm-yy"
    | 18 -> "h:mm AM/PM"
    | 19 -> "h:mm:ss AM/PM"
    | 20 -> "h:mm"
    | 21 -> "h:mm:ss"
    | 22 -> "m/d/yyyy h:mm"
    | 37 -> "#,##0;(#,##0)"
    | 38 -> "#,##0;[Red](#,##0)"
    | 39 -> "#,##0.00;(#,##0.00)"
    | 40 -> "#,##0.00;[Red](#,##0.00)"
    | 45 -> "mm:ss"
    | 46 -> "[h]:mm:ss"
    | 47 -> "mm:ss.0"
    | 48 -> "##0.0E+0"
    | 49 -> "@"
    | _ -> ""

let private excelEpoch = System.DateTime(1899, 12, 30)
let private monthsShort = [| "Jan"; "Feb"; "Mar"; "Apr"; "May"; "Jun"; "Jul"; "Aug"; "Sep"; "Oct"; "Nov"; "Dec" |]
let private monthsLong = [| "January"; "February"; "March"; "April"; "May"; "June"; "July"; "August"; "September"; "October"; "November"; "December" |]
let private daysShort = [| "Sun"; "Mon"; "Tue"; "Wed"; "Thu"; "Fri"; "Sat" |]
let private daysLong = [| "Sunday"; "Monday"; "Tuesday"; "Wednesday"; "Thursday"; "Friday"; "Saturday" |]

let private padZero (width: int) (n: int) : string =
    let s = string n
    if s.Length >= width then s else System.String('0', width - s.Length) + s

/// 書式コードから引用符・エスケープ・角括弧を除去する (日付判定用)。
let private stripFormatLiterals (code: string) : string =
    let sb = System.Text.StringBuilder()
    let mutable i = 0
    let n = code.Length
    while i < n do
        let c = code.[i]
        if c = '"' then
            let e = code.IndexOf('"', i + 1)
            i <- (if e < 0 then n else e + 1)
        elif c = '\\' && i + 1 < n then i <- i + 2
        elif c = '[' then
            let e = code.IndexOf(']', i)
            i <- (if e < 0 then n else e + 1)
        else
            sb.Append c |> ignore
            i <- i + 1
    sb.ToString()

/// 書式コードが日付・時刻書式かどうかを判定する。
let private isDateFormat (code: string) : bool =
    let cleaned = (stripFormatLiterals code).ToLower()
    if cleaned.Contains "e+" || cleaned.Contains "e-" then false
    else cleaned |> Seq.exists (fun c -> c = 'y' || c = 'd' || c = 'h' || c = 's' || c = 'm')

/// 日付・時刻書式コードに従って日時を整形する。
let private formatDate (code: string) (dt: System.DateTime) : string =
    let n = code.Length
    let hour12 =
        let low = code.ToLower()
        low.Contains "am/pm" || low.Contains "a/p"
    let sb = System.Text.StringBuilder()
    let mutable i = 0
    let mutable prevWasHour = false
    while i < n do
        let c = code.[i]
        let cl = System.Char.ToLower c
        if c = '"' then
            let e = code.IndexOf('"', i + 1)
            let endq = if e < 0 then n else e
            sb.Append(code.Substring(i + 1, endq - i - 1)) |> ignore
            i <- (if e < 0 then n else e + 1)
        elif c = '\\' && i + 1 < n then
            sb.Append(code.[i + 1]) |> ignore
            i <- i + 2
        elif c = '[' then
            let e = code.IndexOf(']', i)
            i <- (if e < 0 then n else e + 1)
        elif cl = 'y' then
            let mutable j = i
            while j < n && System.Char.ToLower code.[j] = 'y' do j <- j + 1
            sb.Append(if j - i >= 4 then string dt.Year else padZero 2 (dt.Year % 100)) |> ignore
            prevWasHour <- false
            i <- j
        elif cl = 'd' then
            let mutable j = i
            while j < n && System.Char.ToLower code.[j] = 'd' do j <- j + 1
            let v =
                match j - i with
                | 1 -> string dt.Day
                | 2 -> padZero 2 dt.Day
                | 3 -> daysShort.[int dt.DayOfWeek]
                | _ -> daysLong.[int dt.DayOfWeek]
            sb.Append v |> ignore
            prevWasHour <- false
            i <- j
        elif cl = 'h' then
            let mutable j = i
            while j < n && System.Char.ToLower code.[j] = 'h' do j <- j + 1
            let h = if hour12 then (let hh = dt.Hour % 12 in if hh = 0 then 12 else hh) else dt.Hour
            sb.Append(if j - i >= 2 then padZero 2 h else string h) |> ignore
            prevWasHour <- true
            i <- j
        elif cl = 's' then
            let mutable j = i
            while j < n && System.Char.ToLower code.[j] = 's' do j <- j + 1
            sb.Append(if j - i >= 2 then padZero 2 dt.Second else string dt.Second) |> ignore
            prevWasHour <- false
            i <- j
        elif cl = 'm' then
            let mutable j = i
            while j < n && System.Char.ToLower code.[j] = 'm' do j <- j + 1
            let mutable k = j
            while k < n && not (System.Char.IsLetter code.[k]) do k <- k + 1
            let nextIsSec = k < n && System.Char.ToLower code.[k] = 's'
            if prevWasHour || nextIsSec then
                sb.Append(if j - i >= 2 then padZero 2 dt.Minute else string dt.Minute) |> ignore
            else
                let v =
                    match j - i with
                    | 1 -> string dt.Month
                    | 2 -> padZero 2 dt.Month
                    | 3 -> monthsShort.[dt.Month - 1]
                    | _ -> monthsLong.[dt.Month - 1]
                sb.Append v |> ignore
            prevWasHour <- false
            i <- j
        elif hour12 && (cl = 'a') && i + 4 < n + 1 && (code.Substring(i, min 5 (n - i))).ToUpper().StartsWith "AM/PM" then
            sb.Append(if dt.Hour < 12 then "AM" else "PM") |> ignore
            i <- i + 5
        else
            sb.Append c |> ignore
            i <- i + 1
    sb.ToString()

/// 書式コードの小数部桁数を数える。
let private decimalsOf (code: string) : int =
    let dot = code.IndexOf '.'
    if dot < 0 then 0
    else
        let mutable cnt = 0
        let mutable i = dot + 1
        while i < code.Length && (code.[i] = '0' || code.[i] = '#') do
            cnt <- cnt + 1
            i <- i + 1
        cnt

/// 整数部に 3 桁区切りを挿入する。
let private groupThousands (intPart: string) : string =
    let n = intPart.Length
    if n <= 3 then intPart
    else
        let sb = System.Text.StringBuilder()
        for k in 0 .. n - 1 do
            if k > 0 && (n - k) % 3 = 0 then sb.Append ',' |> ignore
            sb.Append(intPart.[k]) |> ignore
        sb.ToString()

/// 数値書式コードに従って数値を整形する。
let private formatNumber (code: string) (value: float) : string =
    let section = (code.Split(';')).[0]
    let isPercent = section.Contains "%"
    let v0 = if isPercent then value * 100.0 else value
    let decimals = decimalsOf section
    let useThousands = section.Contains ","
    let neg = v0 < 0.0
    let av = abs v0
    let rounded = System.Math.Round(av, decimals)
    let intVal = floor rounded
    let intStr =
        let s = sprintf "%.0f" intVal
        if useThousands then groupThousands s else s
    let frac =
        if decimals > 0 then
            let scaled = System.Math.Round((rounded - intVal) * (10.0 ** float decimals))
            "." + padZero decimals (int scaled)
        else ""
    let body = intStr + frac + (if isPercent then "%" else "")
    if neg then "-" + body else body

/// セルの数値書式を表示用文字列へ適用する。書式が無ければ素の値を返す。
let private applyNumberFormat (code: string) (raw: string) : string =
    if code = "" || code = "General" || code = "@" then raw
    else
        match tryParseFloat raw with
        | None -> raw
        | Some value ->
            if isDateFormat code then formatDate code (excelEpoch.AddDays value)
            else formatNumber code value

/// 塗りつぶし (fill) の前景色を求める。patternType=none は空。
let private fillColorOf (themeColors: Map<int, string>) (fill: Xml.XmlElement) : string =
    match Xml.tryChildByLocal "patternFill" fill with
    | Some pf ->
        let pt = Xml.attrLocal "patternType" pf |> Option.defaultValue "none"
        if pt = "none" then ""
        else Xml.tryChildByLocal "fgColor" pf |> Option.bind (colorFromElement themeColors) |> Option.defaultValue ""
    | None -> ""


let private parseFont (themeColors: Map<int, string>) (font: Xml.XmlElement) : FontStyle =
    { Bold = Xml.tryChildByLocal "b" font |> Option.isSome
      Italic = Xml.tryChildByLocal "i" font |> Option.isSome
      Underline = Xml.tryChildByLocal "u" font |> Option.isSome
      Strike = Xml.tryChildByLocal "strike" font |> Option.isSome
      FontSize = Xml.tryChildByLocal "sz" font |> Option.bind (attrFloat "val") |> Option.defaultValue defaultFont.FontSize
      FontName = Xml.tryChildByLocal "name" font |> Option.bind (Xml.attrLocal "val") |> Option.defaultValue defaultFont.FontName
      Color = parseColor themeColors font |> Option.defaultValue defaultFont.Color }

type private CellStyle =
    { Font: FontStyle
      NumFmtCode: string
      FillColor: string
      Align: string
      VAlign: string
      Wrap: bool }

let private defaultCellStyle =
    { Font = defaultFont
      NumFmtCode = ""
      FillColor = ""
      Align = ""
      VAlign = ""
      Wrap = false }

let private parseStyles (archive: Zip.ZipArchive) : CellStyle[] =
    match Zip.tryReadBytes archive "xl/styles.xml" with
    | None -> [| defaultCellStyle |]
    | Some bytes ->
        let styles = Xml.parseBytes bytes
        let themeColors = parseThemeColors archive
        let customFmts =
            match Xml.tryChildByLocal "numFmts" styles with
            | Some el ->
                Xml.childrenByLocal "numFmt" el
                |> List.choose (fun f ->
                    match attrInt "numFmtId" f, Xml.attrLocal "formatCode" f with
                    | Some id, Some code -> Some(id, code)
                    | _ -> None)
                |> Map.ofList
            | None -> Map.empty
        let numFmtCode id =
            match Map.tryFind id customFmts with
            | Some c -> c
            | None -> builtinNumFmt id
        let fonts =
            match Xml.tryChildByLocal "fonts" styles with
            | Some fonts -> Xml.childrenByLocal "font" fonts |> List.map (parseFont themeColors) |> Array.ofList
            | None -> [| defaultFont |]
        let fills =
            match Xml.tryChildByLocal "fills" styles with
            | Some el -> Xml.childrenByLocal "fill" el |> List.map (fillColorOf themeColors) |> Array.ofList
            | None -> [||]
        match Xml.tryChildByLocal "cellXfs" styles with
        | Some cellXfs ->
            Xml.childrenByLocal "xf" cellXfs
            |> List.map (fun xf ->
                let font =
                    match attrInt "fontId" xf with
                    | Some i when i >= 0 && i < fonts.Length -> fonts.[i]
                    | _ -> defaultFont
                let numCode = attrInt "numFmtId" xf |> Option.map numFmtCode |> Option.defaultValue ""
                let fillColor =
                    match attrInt "fillId" xf with
                    | Some i when i >= 2 && i < fills.Length -> fills.[i]
                    | _ -> ""
                let alignEl = Xml.tryChildByLocal "alignment" xf
                let align = alignEl |> Option.bind (Xml.attrLocal "horizontal") |> Option.defaultValue ""
                let valign = alignEl |> Option.bind (Xml.attrLocal "vertical") |> Option.defaultValue ""
                let wrap = alignEl |> Option.bind (Xml.attrLocal "wrapText") |> Option.map (fun v -> v = "1" || v.ToLower() = "true") |> Option.defaultValue false
                { Font = font
                  NumFmtCode = numCode
                  FillColor = fillColor
                  Align = align
                  VAlign = valign
                  Wrap = wrap })
            |> Array.ofList
        | None -> [| defaultCellStyle |]

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
let private parseCell (shared: RichTextValue[]) (styles: CellStyle[]) (themeColors: Map<int, string>) (c: Xml.XmlElement) : Cell =
    let col = Xml.attrLocal "r" c |> Option.map colFromRef |> Option.defaultValue 0
    let cellType = Xml.attrLocal "t" c |> Option.defaultValue "n"
    let style =
        Xml.attrLocal "s" c
        |> Option.bind tryParseInt
        |> Option.bind (fun i -> if i >= 0 && i < styles.Length then Some styles.[i] else None)
        |> Option.defaultValue defaultCellStyle
    let vText () = Xml.tryChildByLocal "v" c |> Option.map Xml.innerText |> Option.defaultValue ""
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
        | "b" -> { Text = (if vText () = "1" then "TRUE" else "FALSE"); Runs = [||] }
        | "e" | "str" -> { Text = vText (); Runs = [||] }
        | _ ->
            let raw = vText ()
            { Text = applyNumberFormat style.NumFmtCode raw; Runs = [||] }
    let runs =
        if value.Runs.Length = 0 && value.Text <> "" then [| materializeRun style.Font (rawTextRun value.Text) |]
        else value.Runs |> Array.map (materializeRun style.Font)
    let defaultAlign =
        match cellType with
        | "s" | "inlineStr" | "str" -> "left"
        | "b" | "e" -> "center"
        | _ -> "right"
    let align = if style.Align <> "" && style.Align <> "general" then style.Align else defaultAlign
    { col = col
      text = value.Text
      runs = runs
      fillColor = style.FillColor
      align = align
      valign = style.VAlign
      wrap = style.Wrap }

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
let private parseRows (shared: RichTextValue[]) (styles: CellStyle[]) (themeColors: Map<int, string>) (root: Xml.XmlElement) : Row[] =
    match Xml.tryChildByLocal "sheetData" root with
    | None -> [||]
    | Some sheetData ->
        Xml.childrenByLocal "row" sheetData
        |> List.mapi (fun i row ->
            let idx = attrInt "r" row |> Option.defaultValue (i + 1)
            let height = attrFloat "ht" row |> Option.defaultValue 0.0
            let cells =
                Xml.childrenByLocal "c" row
                |> List.map (parseCell shared styles themeColors)
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
    let styles = parseStyles archive
    let shared = parseSharedStrings archive themeColors
    let workbookPath = Opc.officeDocumentPath archive |> Option.defaultValue "xl/workbook.xml"
    let rels = Opc.loadRels archive workbookPath

    match Zip.tryReadBytes archive workbookPath with
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
                        |> Option.map (fun r -> Opc.resolveTarget workbookPath r.Target)
                    match target with
                    | Some sheetPath ->
                        match Zip.tryReadBytes archive sheetPath with
                        | None -> None
                        | Some sheetBytes ->
                        let sheetRoot = Xml.parseBytes sheetBytes
                        let defaultColumnWidth, defaultRowHeight = parseSheetDefaults sheetRoot
                        let showGridLines = parseShowGridLines sheetRoot
                        let columns = parseColumns sheetRoot
                        let rows = parseRows shared styles themeColors sheetRoot
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

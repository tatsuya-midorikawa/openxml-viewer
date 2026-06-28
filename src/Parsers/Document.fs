/// WordprocessingML (.docx) パーサー。
/// ECMA-376 Part 1 §17 (WordprocessingML) を参照。
module OpenXmlViewer.Parsers.Document

open System.Text
open System.Collections.Generic
open OpenXmlViewer.Core
open OpenXmlViewer.Model

let private tryParseInt (s: string) : int option =
    let mutable acc = 0
    let mutable ok = s.Length > 0
    for ch in s do
        if ch >= '0' && ch <= '9' then acc <- acc * 10 + (int ch - int '0')
        else ok <- false
    if ok then Some acc else None

/// 段落配下のテキストを連結する (<w:tab> はタブ、<w:br>/<w:cr> は改行)。
let private paragraphText (p: Xml.XmlElement) : string =
    let sb = StringBuilder()
    let rec walk (el: Xml.XmlElement) =
        for child in Xml.elementChildren el do
            match Xml.localName child.Name with
            | "t" -> sb.Append(Xml.innerText child) |> ignore
            | "tab" -> sb.Append('\t') |> ignore
            | "br"
            | "cr" -> sb.Append('\n') |> ignore
            | _ -> walk child
    walk p
    sb.ToString()

/// 16 進または "auto" の色値を CSS 色へ変換する。
let private normalizeColor (v: string) : string =
    if v = "" || v.ToLower() = "auto" then ""
    elif v.Length = 6 then "#" + v
    else ""

/// 真偽プロパティ (<w:b>, <w:i> 等) を val 属性も考慮して判定する。
let private boolProp (rPr: Xml.XmlElement) (name: string) : bool =
    match Xml.tryChildByLocal name rPr with
    | None -> false
    | Some el ->
        match Xml.attrLocal "val" el with
        | Some v -> not (v = "0" || v.ToLower() = "false" || v.ToLower() = "off")
        | None -> true

/// run のテキストを連結する。
let private runText (r: Xml.XmlElement) : string =
    let sb = StringBuilder()
    for child in Xml.elementChildren r do
        match Xml.localName child.Name with
        | "t" -> sb.Append(Xml.innerText child) |> ignore
        | "tab" -> sb.Append('\t') |> ignore
        | "br"
        | "cr" -> sb.Append('\n') |> ignore
        | _ -> ()
    sb.ToString()

/// run プロパティ (<w:rPr>) を解釈して 1 つの run を構成する。
let private parseRun (isLink: bool) (r: Xml.XmlElement) : TextRun =
    let rPr = Xml.tryChildByLocal "rPr" r
    let underline =
        match rPr |> Option.bind (Xml.tryChildByLocal "u") with
        | Some el -> (Xml.attrLocal "val" el |> Option.defaultValue "single") <> "none"
        | None -> false
    let size =
        rPr
        |> Option.bind (Xml.tryChildByLocal "sz")
        |> Option.bind (Xml.attrLocal "val")
        |> Option.bind tryParseInt
        |> Option.map (fun v -> float v / 2.0)
        |> Option.defaultValue 0.0
    let font =
        rPr
        |> Option.bind (Xml.tryChildByLocal "rFonts")
        |> Option.bind (fun f -> Xml.attrLocal "ascii" f |> Option.orElseWith (fun () -> Xml.attrLocal "hAnsi" f))
        |> Option.defaultValue ""
    let color =
        rPr
        |> Option.bind (Xml.tryChildByLocal "color")
        |> Option.bind (Xml.attrLocal "val")
        |> Option.map normalizeColor
        |> Option.defaultValue ""
    { text = runText r
      bold = rPr |> Option.map (fun p -> boolProp p "b") |> Option.defaultValue false
      italic = rPr |> Option.map (fun p -> boolProp p "i") |> Option.defaultValue false
      underline = underline || isLink
      strike = rPr |> Option.map (fun p -> boolProp p "strike") |> Option.defaultValue false
      fontSize = size
      fontName = font
      color = if color <> "" then color elif isLink then "#0563C1" else "" }

/// 段落の run 一覧を取得する (ハイパーリンク内の run も含む)。
let private paragraphRuns (p: Xml.XmlElement) : TextRun[] =
    Xml.elementChildren p
    |> List.collect (fun child ->
        match Xml.localName child.Name with
        | "r" -> [ parseRun false child ]
        | "hyperlink" -> Xml.childrenByLocal "r" child |> List.map (parseRun true)
        | _ -> [])
    |> List.filter (fun r -> r.text <> "")
    |> Array.ofList

/// 段落の配置 (<w:jc>) を取得する。
let private paragraphAlign (p: Xml.XmlElement) : string =
    Xml.tryChildByLocal "pPr" p
    |> Option.bind (Xml.tryChildByLocal "jc")
    |> Option.bind (Xml.attrLocal "val")
    |> Option.map (function
        | "center" -> "center"
        | "right"
        | "end" -> "right"
        | "both"
        | "distribute" -> "justify"
        | _ -> "left")
    |> Option.defaultValue "left"

/// 段落スタイル名 (<w:pStyle w:val="...">) を取得する。
let private paragraphStyle (p: Xml.XmlElement) : string =
    Xml.tryChildByLocal "pPr" p
    |> Option.bind (Xml.tryChildByLocal "pStyle")
    |> Option.bind (Xml.attrLocal "val")
    |> Option.defaultValue ""

/// スタイル名から見出しレベルを推定する (見出しでなければ 0)。
let private headingLevel (style: string) : int =
    let s = style.ToLower()
    if s.StartsWith "heading" then
        tryParseInt (s.Substring 7) |> Option.defaultValue 1
    elif s = "title" then
        1
    else
        0

/// すべての省略時値を持つブロック。
let private emptyBlock =
    { kind = "paragraph"
      style = ""
      level = 0
      text = ""
      runs = [||]
      align = "left"
      bullet = ""
      listLevel = 0
      cells = [||]
      hasBorders = false
      imageData = ""
      contentType = ""
      imageWidth = 0.0
      imageHeight = 0.0 }

// ---------------------------------------------------------------------------
// 番号付け (numbering.xml)
// ---------------------------------------------------------------------------
type private NumLevel = { Format: string; LvlText: string }

/// numbering.xml を numId -> (ilvl -> NumLevel) へ解析する。
let private parseNumbering (archive: Zip.ZipArchive) (docPath: string) (rels: Map<string, Opc.Relationship>) : Map<int, Map<int, NumLevel>> =
    let numberingPath =
        rels
        |> Map.toSeq
        |> Seq.tryPick (fun (_, rel) -> if rel.Type.EndsWith "/numbering" then Some(Opc.resolveTarget docPath rel.Target) else None)
        |> Option.defaultValue "word/numbering.xml"
    match Zip.tryReadBytes archive numberingPath with
    | None -> Map.empty
    | Some bytes ->
        let root = Xml.parseBytes bytes
        let abstracts =
            Xml.childrenByLocal "abstractNum" root
            |> List.choose (fun an ->
                match Xml.attrLocal "abstractNumId" an |> Option.bind tryParseInt with
                | None -> None
                | Some aid ->
                    let levels =
                        Xml.childrenByLocal "lvl" an
                        |> List.choose (fun lvl ->
                            match Xml.attrLocal "ilvl" lvl |> Option.bind tryParseInt with
                            | None -> None
                            | Some il ->
                                let fmt = Xml.tryChildByLocal "numFmt" lvl |> Option.bind (Xml.attrLocal "val") |> Option.defaultValue "bullet"
                                let txt = Xml.tryChildByLocal "lvlText" lvl |> Option.bind (Xml.attrLocal "val") |> Option.defaultValue ""
                                Some(il, { Format = fmt; LvlText = txt }))
                        |> Map.ofList
                    Some(aid, levels))
            |> Map.ofList
        Xml.childrenByLocal "num" root
        |> List.choose (fun n ->
            match Xml.attrLocal "numId" n |> Option.bind tryParseInt with
            | None -> None
            | Some nid ->
                Xml.tryChildByLocal "abstractNumId" n
                |> Option.bind (Xml.attrLocal "val")
                |> Option.bind tryParseInt
                |> Option.bind (fun aid -> Map.tryFind aid abstracts)
                |> Option.map (fun levels -> nid, levels))
        |> Map.ofList

let private toLetter (n: int) (upper: bool) : string =
    let mutable v = n
    let mutable s = ""
    while v > 0 do
        let r = (v - 1) % 26
        s <- string (char ((if upper then int 'A' else int 'a') + r)) + s
        v <- (v - 1) / 26
    if s = "" then string n else s

let private numText (fmt: string) (count: int) : string =
    match fmt with
    | "lowerLetter" -> toLetter count false
    | "upperLetter" -> toLetter count true
    | _ -> string count

/// 段落の番号付けマーカーとレベルを計算する (状態を持つカウンタを更新)。
let private listMarker (numbering: Map<int, Map<int, NumLevel>>) (counters: Dictionary<int, int[]>) (p: Xml.XmlElement) : string * int =
    let numPr = Xml.tryChildByLocal "pPr" p |> Option.bind (Xml.tryChildByLocal "numPr")
    match numPr with
    | None -> "", 0
    | Some numPr ->
        let numId = Xml.tryChildByLocal "numId" numPr |> Option.bind (Xml.attrLocal "val") |> Option.bind tryParseInt
        let ilvl = Xml.tryChildByLocal "ilvl" numPr |> Option.bind (Xml.attrLocal "val") |> Option.bind tryParseInt |> Option.defaultValue 0
        match numId with
        | Some nid when nid > 0 ->
            match Map.tryFind nid numbering |> Option.bind (Map.tryFind ilvl) with
            | Some lvl when lvl.Format = "bullet" ->
                let t = lvl.LvlText
                (if t.Length = 1 && int t.[0] >= 32 && int t.[0] <= 126 then t else "•"), ilvl
            | Some lvl ->
                let arr =
                    match counters.TryGetValue nid with
                    | true, a -> a
                    | _ ->
                        let a = Array.zeroCreate 9
                        counters.[nid] <- a
                        a
                arr.[ilvl] <- arr.[ilvl] + 1
                for k in ilvl + 1 .. 8 do
                    arr.[k] <- 0
                let mutable s = lvl.LvlText
                for k in 0 .. ilvl do
                    let token = "%" + string (k + 1)
                    if s.Contains token then s <- s.Replace(token, numText lvl.Format arr.[k])
                (if s = "" then numText lvl.Format arr.[ilvl] + "." else s), ilvl
            | None -> "•", ilvl
        | _ -> "", 0

// ---------------------------------------------------------------------------
// 画像 (w:drawing)
// ---------------------------------------------------------------------------
let private contentType (path: string) : string option =
    let lower = path.ToLower()
    if lower.EndsWith ".png" then Some "image/png"
    elif lower.EndsWith ".jpg" || lower.EndsWith ".jpeg" then Some "image/jpeg"
    elif lower.EndsWith ".gif" then Some "image/gif"
    elif lower.EndsWith ".bmp" then Some "image/bmp"
    elif lower.EndsWith ".webp" then Some "image/webp"
    elif lower.EndsWith ".svg" || lower.EndsWith ".svgz" then Some "image/svg+xml"
    else None

let private attrFloat name el = Xml.attrLocal name el |> Option.bind (fun v -> tryParseInt v |> Option.map float)

/// w:drawing を画像ブロックへ変換する。
let private parseDrawing (ct: Opc.ContentTypes) (archive: Zip.ZipArchive) (docPath: string) (rels: Map<string, Opc.Relationship>) (drawing: Xml.XmlElement) : Block option =
    let rid = Xml.descendantsByLocal "blip" drawing |> Seq.tryPick (Xml.attrLocal "embed")
    match rid |> Option.bind (fun id -> Map.tryFind id rels) with
    | Some rel when rel.TargetMode <> "External" ->
        let target = Opc.resolveTarget docPath rel.Target
        let mime =
            Opc.contentTypeOf ct target
            |> Option.filter (fun t -> t.StartsWith "image/")
            |> Option.orElseWith (fun () -> contentType target)
        match mime, Zip.tryReadBytes archive target with
        | Some mime, Some bytes ->
            let ext = Xml.descendantsByLocal "extent" drawing |> Seq.tryHead
            let w = ext |> Option.bind (attrFloat "cx") |> Option.defaultValue 0.0
            let h = ext |> Option.bind (attrFloat "cy") |> Option.defaultValue 0.0
            Some
                { emptyBlock with
                    kind = "image"
                    contentType = mime
                    imageData = System.Convert.ToBase64String bytes
                    imageWidth = w / 9525.0
                    imageHeight = h / 9525.0 }
        | _ -> None
    | _ -> None

/// 段落を (画像 + 段落/見出し) ブロック列へ変換する。
let private parseParagraphBlocks (archive: Zip.ZipArchive) (ct: Opc.ContentTypes) (docPath: string) (rels: Map<string, Opc.Relationship>) (numbering: Map<int, Map<int, NumLevel>>) (counters: Dictionary<int, int[]>) (p: Xml.XmlElement) : Block list =
    let images = Xml.descendantsByLocal "drawing" p |> Seq.choose (parseDrawing ct archive docPath rels) |> Seq.toList
    let style = paragraphStyle p
    let level = headingLevel style
    let runs = paragraphRuns p
    let bullet, listLevel = if level > 0 then "", 0 else listMarker numbering counters p
    let textBlock =
        if runs.Length = 0 then
            if images.IsEmpty then [ { emptyBlock with text = paragraphText p } ] else []
        else
            [ { emptyBlock with
                  kind = (if level > 0 then "heading" else "paragraph")
                  style = style
                  level = level
                  text = paragraphText p
                  runs = runs
                  align = paragraphAlign p
                  bullet = bullet
                  listLevel = listLevel } ]
    images @ textBlock

/// 表セル (<w:tc>) を解析する。
let private parseDocCell (tc: Xml.XmlElement) : DocCell =
    let tcPr = Xml.tryChildByLocal "tcPr" tc
    let gridSpan = tcPr |> Option.bind (Xml.tryChildByLocal "gridSpan") |> Option.bind (Xml.attrLocal "val") |> Option.bind tryParseInt |> Option.defaultValue 1
    let vMergeContinue =
        match tcPr |> Option.bind (Xml.tryChildByLocal "vMerge") with
        | Some el -> (Xml.attrLocal "val" el |> Option.defaultValue "continue") <> "restart"
        | None -> false
    let paras = Xml.childrenByLocal "p" tc
    let breakRun = { text = "\n"; bold = false; italic = false; underline = false; strike = false; fontSize = 0.0; fontName = ""; color = "" }
    let runs =
        paras
        |> List.mapi (fun i p ->
            let prs = paragraphRuns p |> Array.toList
            if i > 0 then breakRun :: prs else prs)
        |> List.collect id
        |> Array.ofList
    { text = paras |> List.map paragraphText |> String.concat "\n"
      runs = runs
      gridSpan = gridSpan
      vMergeContinue = vMergeContinue }

/// 表をブロックへ変換する。
let private parseTable (tbl: Xml.XmlElement) : Block =
    let cells =
        Xml.childrenByLocal "tr" tbl
        |> List.map (fun tr -> Xml.childrenByLocal "tc" tr |> List.map parseDocCell |> Array.ofList)
        |> Array.ofList
    let hasBorders =
        match Xml.tryChildByLocal "tblPr" tbl |> Option.bind (Xml.tryChildByLocal "tblBorders") with
        | Some borders ->
            [ "top"; "left"; "bottom"; "right"; "insideH"; "insideV" ]
            |> List.exists (fun side ->
                match Xml.tryChildByLocal side borders |> Option.bind (Xml.attrLocal "val") with
                | Some v -> v <> "none" && v <> "nil"
                | None -> false)
        | None -> true
    { emptyBlock with
        kind = "table"
        cells = cells
        hasBorders = hasBorders }

/// .docx バイト列を解析する。
let parse (data: byte[]) : DocumentData =
    let archive = Zip.read data
    let documentPath = Opc.officeDocumentPath archive |> Option.defaultValue "word/document.xml"
    let docRels = Opc.loadRels archive documentPath
    let contentTypes = Opc.loadContentTypes archive
    let numbering = parseNumbering archive documentPath docRels
    match Zip.tryReadBytes archive documentPath with
    | None -> { kind = "document"; blocks = [||] }
    | Some bytes ->
        match Xml.tryChildByLocal "body" (Xml.parseBytes bytes) with
        | None -> { kind = "document"; blocks = [||] }
        | Some body ->
            let counters = Dictionary<int, int[]>()
            let blocks =
                Xml.elementChildren body
                |> List.collect (fun el ->
                    match Xml.localName el.Name with
                    | "p" -> parseParagraphBlocks archive contentTypes documentPath docRels numbering counters el
                    | "tbl" -> [ parseTable el ]
                    | _ -> [])
                |> Array.ofList
            { kind = "document"; blocks = blocks }

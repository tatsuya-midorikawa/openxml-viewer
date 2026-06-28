/// WordprocessingML (.docx) パーサー。
/// ECMA-376 Part 1 §17 (WordprocessingML) を参照。
module OpenXmlViewer.Parsers.Document

open System.Text
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

/// 段落をブロックへ変換する。
let private parseParagraph (p: Xml.XmlElement) : Block =
    let style = paragraphStyle p
    let level = headingLevel style
    { kind = (if level > 0 then "heading" else "paragraph")
      style = style
      level = level
      text = paragraphText p
      runs = paragraphRuns p
      align = paragraphAlign p
      rows = [||] }

/// 表をブロックへ変換する。
let private parseTable (tbl: Xml.XmlElement) : Block =
    let rows =
        Xml.childrenByLocal "tr" tbl
        |> List.map (fun tr ->
            Xml.childrenByLocal "tc" tr
            |> List.map (fun tc -> Xml.childrenByLocal "p" tc |> List.map paragraphText |> String.concat "\n")
            |> Array.ofList)
        |> Array.ofList
    { kind = "table"
      style = ""
      level = 0
      text = ""
      runs = [||]
      align = "left"
      rows = rows }

/// .docx バイト列を解析する。
let parse (data: byte[]) : DocumentData =
    let archive = Zip.read data
    let documentPath = Opc.officeDocumentPath archive |> Option.defaultValue "word/document.xml"
    match Zip.tryReadBytes archive documentPath with
    | None -> { kind = "document"; blocks = [||] }
    | Some bytes ->
        match Xml.tryChildByLocal "body" (Xml.parseBytes bytes) with
        | None -> { kind = "document"; blocks = [||] }
        | Some body ->
            let blocks =
                Xml.elementChildren body
                |> List.choose (fun el ->
                    match Xml.localName el.Name with
                    | "p" -> Some(parseParagraph el)
                    | "tbl" -> Some(parseTable el)
                    | _ -> None)
                |> Array.ofList
            { kind = "document"; blocks = blocks }

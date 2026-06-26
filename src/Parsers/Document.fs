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
      rows = rows }

/// .docx バイト列を解析する。
let parse (data: byte[]) : DocumentData =
    let archive = Zip.read data
    match Zip.tryReadBytes archive "word/document.xml" with
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

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

/// ワークシート XML の行配列を解析する。
let private parseRows (shared: string[]) (root: Xml.XmlElement) : Row[] =
    match Xml.tryChildByLocal "sheetData" root with
    | None -> [||]
    | Some sheetData ->
        Xml.childrenByLocal "row" sheetData
        |> List.map (fun row ->
            let idx = Xml.attrLocal "r" row |> Option.bind tryParseInt |> Option.defaultValue 0
            let cells =
                Xml.childrenByLocal "c" row
                |> List.map (parseCell shared)
                |> Array.ofList
            { index = idx; cells = cells })
        |> Array.ofList

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
                    match target |> Option.bind (Zip.tryReadBytes archive) with
                    | Some sheetBytes ->
                        let rows = parseRows shared (Xml.parseBytes sheetBytes)
                        let maxCol =
                            rows
                            |> Array.collect (fun r -> r.cells |> Array.map (fun c -> c.col))
                            |> fun cols -> if cols.Length = 0 then 0 else Array.max cols
                        Some { name = name; rows = rows; maxCol = maxCol }
                    | None -> None)
                |> Array.ofList
        { kind = "spreadsheet"; sheets = sheets }

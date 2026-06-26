/// 最小限の XML パーサーをフルスクラッチで実装するモジュール。
/// OOXML の各パート (要素・属性・テキスト) を木構造へ変換する。
/// 名前空間プレフィックスは修飾名のまま保持し、局所名で照合できるヘルパーを提供する。
module OpenXmlViewer.Core.Xml

open System.Text
open System.Collections.Generic
open OpenXmlViewer.Core

/// XML ノード。要素またはテキスト。
type XmlNode =
    | Element of XmlElement
    | Text of string

/// XML 要素。
and XmlElement =
    { Name: string
      Attributes: (string * string) list
      Children: XmlNode list }

// ---------------------------------------------------------------------------
// 解析
// ---------------------------------------------------------------------------

let private parseIntRadix (str: string) (radix: int) : int =
    let mutable v = 0
    for ch in str do
        let digit =
            if ch >= '0' && ch <= '9' then int ch - int '0'
            elif ch >= 'a' && ch <= 'f' then int ch - int 'a' + 10
            elif ch >= 'A' && ch <= 'F' then int ch - int 'A' + 10
            else 0
        v <- v * radix + digit
    v

/// XML 文字列を解析してルート要素を返す。
let parse (input: string) : XmlElement =
    let s = input
    let n = s.Length
    let mutable pos = 0

    let peek () = if pos < n then s.[pos] else '\000'
    let isWs c = c = ' ' || c = '\t' || c = '\r' || c = '\n'
    let skipWs () =
        while pos < n && isWs s.[pos] do
            pos <- pos + 1

    let decodeEntities (t: string) : string =
        if t.IndexOf('&') < 0 then
            t
        else
            let sb = StringBuilder(t.Length)
            let mutable i = 0
            let m = t.Length
            while i < m do
                let c = t.[i]
                if c = '&' then
                    let semi = t.IndexOf(';', i)
                    if semi < 0 then
                        sb.Append(c) |> ignore
                        i <- i + 1
                    else
                        let ent = t.Substring(i + 1, semi - i - 1)
                        (match ent with
                         | "amp" -> sb.Append('&') |> ignore
                         | "lt" -> sb.Append('<') |> ignore
                         | "gt" -> sb.Append('>') |> ignore
                         | "quot" -> sb.Append('"') |> ignore
                         | "apos" -> sb.Append('\'') |> ignore
                         | _ when ent.Length > 1 && ent.[0] = '#' ->
                             let code =
                                 if ent.[1] = 'x' || ent.[1] = 'X' then parseIntRadix (ent.Substring 2) 16
                                 else parseIntRadix (ent.Substring 1) 10
                             if code > 0xFFFF then
                                 let cp = code - 0x10000
                                 sb.Append(char (0xD800 ||| (cp >>> 10))) |> ignore
                                 sb.Append(char (0xDC00 ||| (cp &&& 0x3FF))) |> ignore
                             else
                                 sb.Append(char code) |> ignore
                         | _ -> sb.Append('&').Append(ent).Append(';') |> ignore)
                        i <- semi + 1
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            sb.ToString()

    let parseName () =
        let startp = pos
        while pos < n && (let c = s.[pos] in not (isWs c) && c <> '>' && c <> '/' && c <> '=') do
            pos <- pos + 1
        s.Substring(startp, pos - startp)

    let parseAttributes () =
        let attrs = ResizeArray<string * string>()
        let mutable go = true
        while go do
            skipWs ()
            let c = peek ()
            if c = '>' || c = '/' || c = '\000' then
                go <- false
            else
                let aname = parseName ()
                skipWs ()
                if peek () = '=' then
                    pos <- pos + 1
                    skipWs ()
                    let quote = peek ()
                    if quote = '"' || quote = '\'' then
                        pos <- pos + 1
                        let vstart = pos
                        while pos < n && s.[pos] <> quote do
                            pos <- pos + 1
                        let raw = s.Substring(vstart, pos - vstart)
                        pos <- pos + 1
                        attrs.Add(aname, decodeEntities raw)
                    else
                        attrs.Add(aname, "")
                else
                    attrs.Add(aname, "")
        List.ofSeq attrs

    // 開始タグの '<' 位置にいる前提で要素を解析する。
    let rec parseElement () : XmlElement =
        pos <- pos + 1 // '<'
        let name = parseName ()
        let attrs = parseAttributes ()
        if peek () = '/' then
            pos <- pos + 2 // "/>"
            { Name = name; Attributes = attrs; Children = [] }
        else
            pos <- pos + 1 // '>'
            let children = parseChildren ()
            { Name = name; Attributes = attrs; Children = children }

    and parseChildren () : XmlNode list =
        let children = ResizeArray<XmlNode>()
        let mutable go = true
        while go do
            if pos >= n then
                go <- false
            elif s.[pos] = '<' then
                if pos + 1 < n && s.[pos + 1] = '/' then
                    let close = s.IndexOf('>', pos)
                    pos <- (if close < 0 then n else close + 1)
                    go <- false
                elif pos + 3 < n && s.[pos + 1] = '!' && s.[pos + 2] = '-' && s.[pos + 3] = '-' then
                    let cl = s.IndexOf("-->", pos)
                    pos <- (if cl < 0 then n else cl + 3)
                elif pos + 8 < n && s.Substring(pos, 9) = "<![CDATA[" then
                    let cl = s.IndexOf("]]>", pos)
                    let textEnd = if cl < 0 then n else cl
                    children.Add(Text(s.Substring(pos + 9, textEnd - (pos + 9))))
                    pos <- (if cl < 0 then n else cl + 3)
                elif pos + 1 < n && s.[pos + 1] = '?' then
                    let cl = s.IndexOf("?>", pos)
                    pos <- (if cl < 0 then n else cl + 2)
                else
                    children.Add(Element(parseElement ()))
            else
                let startp = pos
                while pos < n && s.[pos] <> '<' do
                    pos <- pos + 1
                children.Add(Text(decodeEntities (s.Substring(startp, pos - startp))))
        List.ofSeq children

    // プロローグ (XML 宣言・コメント・DOCTYPE) を読み飛ばす。
    let mutable go = true
    while go do
        skipWs ()
        if pos + 1 < n && s.[pos] = '<' && s.[pos + 1] = '?' then
            let close = s.IndexOf("?>", pos)
            pos <- (if close < 0 then n else close + 2)
        elif pos + 3 < n && s.[pos] = '<' && s.[pos + 1] = '!' && s.[pos + 2] = '-' && s.[pos + 3] = '-' then
            let close = s.IndexOf("-->", pos)
            pos <- (if close < 0 then n else close + 3)
        elif pos + 1 < n && s.[pos] = '<' && s.[pos + 1] = '!' then
            let close = s.IndexOf('>', pos)
            pos <- (if close < 0 then n else close + 1)
        else
            go <- false

    if pos >= n || s.[pos] <> '<' then
        failwith "Xml: ルート要素が見つかりません"
    parseElement ()

/// バイト列 (UTF-8) を解析する。
let parseBytes (bytes: byte[]) : XmlElement = parse (Text.decodeUtf8 bytes)

// ---------------------------------------------------------------------------
// 木探索ヘルパー
// ---------------------------------------------------------------------------

/// 修飾名から局所名 (プレフィックスを除いた部分) を取り出す。
let localName (qname: string) : string =
    let i = qname.IndexOf(':')
    if i < 0 then qname else qname.Substring(i + 1)

/// 完全一致で属性値を取得する。
let attr (name: string) (el: XmlElement) : string option =
    el.Attributes |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

/// 局所名一致で属性値を取得する。
let attrLocal (name: string) (el: XmlElement) : string option =
    el.Attributes |> List.tryPick (fun (k, v) -> if localName k = name then Some v else None)

/// 要素の子のうち要素ノードだけを返す。
let elementChildren (el: XmlElement) : XmlElement list =
    el.Children
    |> List.choose (function
        | Element e -> Some e
        | _ -> None)

/// 局所名一致の直接の子要素を返す。
let childrenByLocal (name: string) (el: XmlElement) : XmlElement list =
    elementChildren el |> List.filter (fun e -> localName e.Name = name)

/// 局所名一致の最初の子要素を返す。
let tryChildByLocal (name: string) (el: XmlElement) : XmlElement option =
    elementChildren el |> List.tryFind (fun e -> localName e.Name = name)

/// 要素配下のテキストを連結する。
let rec innerText (el: XmlElement) : string =
    el.Children
    |> List.map (function
        | Text t -> t
        | Element e -> innerText e)
    |> String.concat ""

/// 局所名一致の子孫要素を再帰的に列挙する。
let rec descendantsByLocal (name: string) (el: XmlElement) : XmlElement seq =
    seq {
        for child in elementChildren el do
            if localName child.Name = name then
                yield child
            yield! descendantsByLocal name child
    }

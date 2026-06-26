/// 文字エンコーディング関連のユーティリティ。外部ライブラリに依存せず
/// UTF-8 のデコードをフルスクラッチで行う (Fable / .NET 双方で動作させるため)。
module OpenXmlViewer.Core.Text

open System.Text

/// UTF-8 バイト列を文字列へデコードする。先頭の BOM は取り除く。
let decodeUtf8 (bytes: byte[]) : string =
    let n = bytes.Length
    // BOM (EF BB BF) をスキップ
    let start =
        if n >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy then 3 else 0

    let sb = StringBuilder(n)
    let mutable i = start
    while i < n do
        let b0 = int bytes.[i]
        if b0 < 0x80 then
            sb.Append(char b0) |> ignore
            i <- i + 1
        elif b0 < 0xE0 && i + 1 < n then
            let b1 = int bytes.[i + 1] &&& 0x3F
            sb.Append(char (((b0 &&& 0x1F) <<< 6) ||| b1)) |> ignore
            i <- i + 2
        elif b0 < 0xF0 && i + 2 < n then
            let b1 = int bytes.[i + 1] &&& 0x3F
            let b2 = int bytes.[i + 2] &&& 0x3F
            sb.Append(char (((b0 &&& 0x0F) <<< 12) ||| (b1 <<< 6) ||| b2)) |> ignore
            i <- i + 3
        elif i + 3 < n then
            let b1 = int bytes.[i + 1] &&& 0x3F
            let b2 = int bytes.[i + 2] &&& 0x3F
            let b3 = int bytes.[i + 3] &&& 0x3F
            let cp = (((b0 &&& 0x07) <<< 18) ||| (b1 <<< 12) ||| (b2 <<< 6) ||| b3) - 0x10000
            sb.Append(char (0xD800 ||| (cp >>> 10))) |> ignore
            sb.Append(char (0xDC00 ||| (cp &&& 0x3FF))) |> ignore
            i <- i + 4
        else
            // 不完全なシーケンスは置換文字にする
            sb.Append('\uFFFD') |> ignore
            i <- i + 1
    sb.ToString()

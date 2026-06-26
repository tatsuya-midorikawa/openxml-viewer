/// ZIP アーカイブの読み取りをフルスクラッチで実装するモジュール。
/// 中央ディレクトリを解析し、各パートを必要に応じて DEFLATE 展開する。
module OpenXmlViewer.Core.Zip

open System.Collections.Generic
open OpenXmlViewer.Core

// ---------------------------------------------------------------------------
// リトルエンディアン読み取り
// ---------------------------------------------------------------------------

let inline private u16 (d: byte[]) (o: int) = int d.[o] ||| (int d.[o + 1] <<< 8)

let inline private u32 (d: byte[]) (o: int) =
    int d.[o] ||| (int d.[o + 1] <<< 8) ||| (int d.[o + 2] <<< 16) ||| (int d.[o + 3] <<< 24)

// ---------------------------------------------------------------------------
// モデル
// ---------------------------------------------------------------------------

/// 中央ディレクトリ上の 1 エントリ。
type ZipEntry =
    { Name: string
      Method: int
      CompressedSize: int
      LocalHeaderOffset: int }

/// 読み込んだ ZIP アーカイブ。生バイトとエントリ表を保持する。
type ZipArchive =
    { Data: byte[]
      Entries: IReadOnlyDictionary<string, ZipEntry> }

// ---------------------------------------------------------------------------
// 解析
// ---------------------------------------------------------------------------

let private eocdSignature = 0x06054b50

/// 末尾から End Of Central Directory レコードを探す。
let private findEocd (d: byte[]) : int =
    let limit = max 0 (d.Length - 22 - 65535)
    let mutable i = d.Length - 22
    let mutable found = -1
    while found < 0 && i >= limit do
        if u32 d i = eocdSignature then found <- i else i <- i - 1
    if found < 0 then failwith "Zip: End Of Central Directory が見つかりません"
    found

/// ZIP アーカイブを読み込み、エントリ表を構築する。
let read (data: byte[]) : ZipArchive =
    let eocd = findEocd data
    let count = u16 data (eocd + 10)
    let cdOffset = u32 data (eocd + 16)

    let entries = Dictionary<string, ZipEntry>()
    let mutable p = cdOffset
    for _ in 1 .. count do
        // p の中央ディレクトリヘッダ署名は 0x02014b50
        let method = u16 data (p + 10)
        let compSize = u32 data (p + 20)
        let nameLen = u16 data (p + 28)
        let extraLen = u16 data (p + 30)
        let commentLen = u16 data (p + 32)
        let localOffset = u32 data (p + 42)
        let name = Text.decodeUtf8 (Array.sub data (p + 46) nameLen)
        entries.[name] <-
            { Name = name
              Method = method
              CompressedSize = compSize
              LocalHeaderOffset = localOffset }
        p <- p + 46 + nameLen + extraLen + commentLen

    { Data = data; Entries = entries }

/// エントリの中身を展開して取り出す。
let readEntry (archive: ZipArchive) (entry: ZipEntry) : byte[] =
    let d = archive.Data
    let lo = entry.LocalHeaderOffset
    // ローカルファイルヘッダのファイル名長・拡張領域長からデータ開始位置を算出
    let nameLen = u16 d (lo + 26)
    let extraLen = u16 d (lo + 28)
    let dataStart = lo + 30 + nameLen + extraLen
    let raw = Array.sub d dataStart entry.CompressedSize
    match entry.Method with
    | 0 -> raw
    | 8 -> Inflate.inflate raw
    | m -> failwithf "Zip: 未対応の圧縮方式です (method=%d)" m

/// 名前を指定してパートのバイト列を取得する。
let tryReadBytes (archive: ZipArchive) (name: string) : byte[] option =
    match archive.Entries.TryGetValue name with
    | true, entry -> Some(readEntry archive entry)
    | _ -> None

/// 名前を指定してパートを UTF-8 文字列として取得する。
let tryReadText (archive: ZipArchive) (name: string) : string option =
    tryReadBytes archive name |> Option.map Text.decodeUtf8

/// アーカイブ内の全エントリ名。
let names (archive: ZipArchive) : string seq = archive.Entries.Keys

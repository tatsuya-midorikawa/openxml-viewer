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
let private zip64EocdSignature = 0x06064b50
let private zip64LocatorSignature = 0x07064b50
let private placeholder32 = 0xFFFFFFFF

/// 末尾から End Of Central Directory レコードを探す。
/// 通常は末尾 22 バイトに存在するが、ZIP コメントや末尾の余分なバイトを考慮し、
/// 見つかるまでバッファ全体を後方走査する。
let private findEocd (d: byte[]) : int =
    let mutable i = d.Length - 22
    let mutable found = -1
    while found < 0 && i >= 0 do
        if i + 4 <= d.Length && u32 d i = eocdSignature then found <- i else i <- i - 1
    if found < 0 then failwith "Zip: End Of Central Directory が見つかりません"
    found

/// 中央ディレクトリエントリの ZIP64 拡張フィールド (tag 0x0001) を走査し、
/// プレースホルダ (0xFFFFFFFF) で置き換えられた圧縮サイズ・ローカルヘッダオフセットを取り出す。
let private resolveZip64Entry
    (d: byte[]) (extraStart: int) (extraLen: int)
    (uncompPh: bool) (compPh: bool) (offPh: bool) : int option * int option =
    let stop = extraStart + extraLen
    let mutable pos = extraStart
    let mutable comp = None
    let mutable off = None
    while pos + 4 <= stop do
        let tag = u16 d pos
        let size = u16 d (pos + 2)
        if tag = 0x0001 then
            let mutable f = pos + 4
            if uncompPh then f <- f + 8
            if compPh && f + 8 <= stop then comp <- Some(u32 d f); f <- f + 8
            if offPh && f + 8 <= stop then off <- Some(u32 d f)
            pos <- stop
        else
            pos <- pos + 4 + size
    comp, off

/// 先頭が複合ファイル (OLE2/CFB) 署名 D0 CF 11 E0 A1 B1 1A E1 かどうか。
/// パスワード保護や秘密度ラベル (RMS/IRM) で暗号化された Office ファイルはこの形式になる。
let private isCompoundFile (d: byte[]) : bool =
    d.Length >= 8 && d.[0] = 0xD0uy && d.[1] = 0xCFuy && d.[2] = 0x11uy && d.[3] = 0xE0uy
    && d.[4] = 0xA1uy && d.[5] = 0xB1uy && d.[6] = 0x1Auy && d.[7] = 0xE1uy

/// ZIP アーカイブを読み込み、エントリ表を構築する。
let read (data: byte[]) : ZipArchive =
    if isCompoundFile data then
        failwith "このファイルは暗号化または保護されています (パスワード保護または秘密度ラベル)。復号できないため表示できません。"
    let eocd = findEocd data
    let mutable count = u16 data (eocd + 10)
    let mutable cdOffset = u32 data (eocd + 16)

    // ZIP64: 件数/オフセットがプレースホルダの場合は ZIP64 EOCD レコードを参照する。
    if count = 0xFFFF || cdOffset = placeholder32 then
        let locator = eocd - 20
        if locator >= 0 && u32 data locator = zip64LocatorSignature then
            let z = u32 data (locator + 8)
            if z >= 0 && z + 4 <= data.Length && u32 data z = zip64EocdSignature then
                count <- u32 data (z + 32)
                cdOffset <- u32 data (z + 48)

    let entries = Dictionary<string, ZipEntry>()
    let mutable p = cdOffset
    for _ in 1 .. count do
        // p の中央ディレクトリヘッダ署名は 0x02014b50
        let method = u16 data (p + 10)
        let uncompSize = u32 data (p + 24)
        let compSize = u32 data (p + 20)
        let nameLen = u16 data (p + 28)
        let extraLen = u16 data (p + 30)
        let commentLen = u16 data (p + 32)
        let localOffset = u32 data (p + 42)
        let name = Text.decodeUtf8 (Array.sub data (p + 46) nameLen)
        let comp64, off64 =
            if compSize = placeholder32 || localOffset = placeholder32 then
                resolveZip64Entry data (p + 46 + nameLen) extraLen
                    (uncompSize = placeholder32) (compSize = placeholder32) (localOffset = placeholder32)
            else None, None
        entries.[name] <-
            { Name = name
              Method = method
              CompressedSize = defaultArg comp64 compSize
              LocalHeaderOffset = defaultArg off64 localOffset }
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

/// DEFLATE (RFC 1951) 解凍をフルスクラッチで実装するモジュール。
/// ZIP の格納方式 8 (deflate) を展開するために使用する。外部ライブラリには依存しない。
module OpenXmlViewer.Core.Inflate

open System.Collections.Generic

// ---------------------------------------------------------------------------
// 静的テーブル (RFC 1951 §3.2.5)
// ---------------------------------------------------------------------------

/// 長さコード 257..285 の基準長
let private lengthBase =
    [| 3; 4; 5; 6; 7; 8; 9; 10; 11; 13; 15; 17; 19; 23; 27; 31; 35; 43; 51; 59; 67; 83; 99; 115; 131; 163; 195; 227; 258 |]

/// 長さコード 257..285 の追加ビット数
let private lengthExtra =
    [| 0; 0; 0; 0; 0; 0; 0; 0; 1; 1; 1; 1; 2; 2; 2; 2; 3; 3; 3; 3; 4; 4; 4; 4; 5; 5; 5; 5; 0 |]

/// 距離コード 0..29 の基準距離
let private distBase =
    [| 1; 2; 3; 4; 5; 7; 9; 13; 17; 25; 33; 49; 65; 97; 129; 193; 257; 385; 513; 769; 1025; 1537; 2049; 3073; 4097; 6145; 8193; 12289; 16385; 24577 |]

/// 距離コード 0..29 の追加ビット数
let private distExtra =
    [| 0; 0; 0; 0; 1; 1; 2; 2; 3; 3; 4; 4; 5; 5; 6; 6; 7; 7; 8; 8; 9; 9; 10; 10; 11; 11; 12; 12; 13; 13 |]

/// 動的ハフマンのコード長並び順 (RFC 1951 §3.2.7)
let private codeLengthOrder =
    [| 16; 17; 18; 0; 8; 7; 9; 6; 10; 5; 11; 4; 12; 3; 13; 2; 14; 1; 15 |]

// ---------------------------------------------------------------------------
// ハフマン木
// ---------------------------------------------------------------------------

/// 正準ハフマン木。counts.[len] = その長さのコード数、symbols = 長さ順に並べたシンボル。
type private Huffman = { Counts: int[]; Symbols: int[] }

/// コード長配列から正準ハフマン木を構築する。
let private buildHuffman (lengths: int[]) (n: int) : Huffman =
    let counts = Array.zeroCreate 16
    for i in 0 .. n - 1 do
        counts.[lengths.[i]] <- counts.[lengths.[i]] + 1
    counts.[0] <- 0

    let offsets = Array.zeroCreate 16
    let mutable sum = 0
    for len in 1 .. 15 do
        offsets.[len] <- sum
        sum <- sum + counts.[len]

    let symbols = Array.zeroCreate (max sum 1)
    for i in 0 .. n - 1 do
        let len = lengths.[i]
        if len <> 0 then
            symbols.[offsets.[len]] <- i
            offsets.[len] <- offsets.[len] + 1

    { Counts = counts; Symbols = symbols }

// ---------------------------------------------------------------------------
// ビットリーダー (LSB ファースト)
// ---------------------------------------------------------------------------

type private BitReader(data: byte[]) =
    let mutable bytePos = 0
    let mutable bitBuf = 0
    let mutable bitCnt = 0

    /// 次の 1 ビットを取得する。
    member _.GetBit() : int =
        if bitCnt = 0 then
            bitBuf <- int data.[bytePos]
            bytePos <- bytePos + 1
            bitCnt <- 8
        let bit = bitBuf &&& 1
        bitBuf <- bitBuf >>> 1
        bitCnt <- bitCnt - 1
        bit

    /// 次の n ビットを LSB ファーストで取得する。
    member this.GetBits(n: int) : int =
        let mutable v = 0
        for i in 0 .. n - 1 do
            v <- v ||| (this.GetBit() <<< i)
        v

    /// バイト境界へ整列する (非圧縮ブロック用)。
    member _.AlignToByte() = bitCnt <- 0

    /// バイトを直接 1 つ読み取る (整列後に使用)。
    member _.ReadByteDirect() : int =
        let b = int data.[bytePos]
        bytePos <- bytePos + 1
        b

// ---------------------------------------------------------------------------
// ハフマン復号
// ---------------------------------------------------------------------------

/// ビット列を 1 ビットずつ読みながらハフマンシンボルを復号する。
let private decode (br: BitReader) (h: Huffman) : int =
    let mutable code = 0
    let mutable first = 0
    let mutable index = 0
    let mutable len = 1
    let mutable result = System.Int32.MinValue
    while result = System.Int32.MinValue do
        code <- code ||| br.GetBit()
        let count = h.Counts.[len]
        if code - first < count then
            result <- h.Symbols.[index + code - first]
        else
            index <- index + count
            first <- (first + count) <<< 1
            code <- code <<< 1
            len <- len + 1
            if len > 15 then failwith "Inflate: invalid Huffman code"
    result

// ---------------------------------------------------------------------------
// 固定ハフマン木 (RFC 1951 §3.2.6)
// ---------------------------------------------------------------------------

let private fixedLiteral =
    let lengths = Array.zeroCreate 288
    for i in 0 .. 287 do
        lengths.[i] <-
            if i < 144 then 8
            elif i < 256 then 9
            elif i < 280 then 7
            else 8
    buildHuffman lengths 288

let private fixedDistance =
    let lengths = Array.create 30 5
    buildHuffman lengths 30

// ---------------------------------------------------------------------------
// ブロック展開
// ---------------------------------------------------------------------------

/// 圧縮ブロックを 1 つ展開して out へ書き出す。
let private inflateBlock (br: BitReader) (out: List<byte>) (lit: Huffman) (dist: Huffman) =
    let mutable endOfBlock = false
    while not endOfBlock do
        let sym = decode br lit
        if sym = 256 then
            endOfBlock <- true
        elif sym < 256 then
            out.Add(byte sym)
        else
            let li = sym - 257
            let length = lengthBase.[li] + br.GetBits(lengthExtra.[li])
            let dsym = decode br dist
            let distance = distBase.[dsym] + br.GetBits(distExtra.[dsym])
            let start = out.Count - distance
            for k in 0 .. length - 1 do
                out.Add(out.[start + k])

/// 動的ハフマン木 (リテラル/距離) を読み取る。
let private readDynamicTables (br: BitReader) : Huffman * Huffman =
    let hlit = br.GetBits(5) + 257
    let hdist = br.GetBits(5) + 1
    let hclen = br.GetBits(4) + 4

    let clLengths = Array.zeroCreate 19
    for i in 0 .. hclen - 1 do
        clLengths.[codeLengthOrder.[i]] <- br.GetBits(3)
    let clHuff = buildHuffman clLengths 19

    let total = hlit + hdist
    let lengths = Array.zeroCreate total
    let mutable i = 0
    while i < total do
        let sym = decode br clHuff
        if sym < 16 then
            lengths.[i] <- sym
            i <- i + 1
        elif sym = 16 then
            let repeat = br.GetBits(2) + 3
            let prev = lengths.[i - 1]
            for _ in 1 .. repeat do
                lengths.[i] <- prev
                i <- i + 1
        elif sym = 17 then
            let repeat = br.GetBits(3) + 3
            for _ in 1 .. repeat do
                lengths.[i] <- 0
                i <- i + 1
        else
            let repeat = br.GetBits(7) + 11
            for _ in 1 .. repeat do
                lengths.[i] <- 0
                i <- i + 1

    let litHuff = buildHuffman (Array.sub lengths 0 hlit) hlit
    let distHuff = buildHuffman (Array.sub lengths hlit hdist) hdist
    litHuff, distHuff

// ---------------------------------------------------------------------------
// 公開 API
// ---------------------------------------------------------------------------

/// raw DEFLATE ストリームを展開する。
let inflate (data: byte[]) : byte[] =
    let br = BitReader(data)
    let out = List<byte>()
    let mutable finalBlock = false
    while not finalBlock do
        if br.GetBit() = 1 then finalBlock <- true
        match br.GetBits(2) with
        | 0 ->
            // 非圧縮ブロック
            br.AlignToByte()
            let len = br.ReadByteDirect() ||| (br.ReadByteDirect() <<< 8)
            br.ReadByteDirect() |> ignore
            br.ReadByteDirect() |> ignore
            for _ in 1 .. len do
                out.Add(byte (br.ReadByteDirect()))
        | 1 -> inflateBlock br out fixedLiteral fixedDistance
        | 2 ->
            let lit, dist = readDynamicTables br
            inflateBlock br out lit dist
        | _ -> failwith "Inflate: invalid block type"
    out.ToArray()

/// PresentationML (.pptx) パーサー。
/// ECMA-376 Part 1 §19 (PresentationML)、テキストは DrawingML §21 を参照。
module OpenXmlViewer.Parsers.Presentation

open OpenXmlViewer.Core
open OpenXmlViewer.Model

let private tryParseInt (s: string) : int option =
    let t = s.Trim()
    let mutable acc = 0
    let mutable ok = t.Length > 0
    for ch in t do
        if ch >= '0' && ch <= '9' then acc <- acc * 10 + (int ch - int '0')
        else ok <- false
    if ok then Some acc else None

let private tryParseFloat (s: string) : float option = tryParseInt s |> Option.map float
let private attrFloat name el = Xml.attrLocal name el |> Option.bind tryParseFloat

/// 符号付き整数 (調整値などで負値を取りうる) を解釈する。
let private tryParseIntSigned (s: string) : int option =
    let t = s.Trim()
    if t.StartsWith "-" then tryParseInt (t.Substring 1) |> Option.map (fun v -> -v)
    else tryParseInt t

let private defaultSlideWidth = 12192000.0
let private defaultSlideHeight = 6858000.0
let private defaultTextSize = 18.0

/// r:id 属性 (プレフィックス付きの id) を取得する。非修飾の id とは区別する。
let private relId (el: Xml.XmlElement) : string option =
    el.Attributes |> List.tryPick (fun (k, v) -> if k.EndsWith ":id" then Some v else None)

let private normalizeColor (value: string) : string =
    let hex = if value.Length = 8 then value.Substring 2 else value
    if hex.Length = 6 then "#" + hex else ""

let private rgbOfHex (color: string) : (int * int * int) option =
    if color.Length = 7 && color.[0] = '#' then
        let parsePair i =
            System.Convert.ToInt32(color.Substring(i, 2), 16)
        Some(parsePair 1, parsePair 3, parsePair 5)
    else
        None

/// schemeClr/srgbClr の輝度・濃淡変換 (lumMod/lumOff/shade/tint) をまとめて適用する。
let private applyColorMods (color: string) (lumMod: float option) (lumOff: float option) (shade: float option) (tint: float option) : string =
    match rgbOfHex color with
    | None -> color
    | Some(r, g, b) ->
        let mutable rf = float r
        let mutable gf = float g
        let mutable bf = float b
        match lumMod with Some m -> rf <- rf * m; gf <- gf * m; bf <- bf * m | None -> ()
        match lumOff with Some o -> rf <- rf + 255.0 * o; gf <- gf + 255.0 * o; bf <- bf + 255.0 * o | None -> ()
        match shade with Some s -> rf <- rf * s; gf <- gf * s; bf <- bf * s | None -> ()
        match tint with Some t -> rf <- rf * t + 255.0 * (1.0 - t); gf <- gf * t + 255.0 * (1.0 - t); bf <- bf * t + 255.0 * (1.0 - t) | None -> ()
        let clamp v = max 0 (min 255 (int (round v)))
        sprintf "#%02X%02X%02X" (clamp rf) (clamp gf) (clamp bf)

let private themeKey (scheme: string) : string =
    match scheme with
    | "tx1" -> "dk1"
    | "bg1" -> "lt1"
    | "tx2" -> "dk2"
    | "bg2" -> "lt2"
    | other -> other

let private parseThemeColors (archive: Zip.ZipArchive) : Map<string, string> =
    match Zip.tryReadBytes archive "ppt/theme/theme1.xml" with
    | None -> Map.empty
    | Some bytes ->
        let theme = Xml.parseBytes bytes
        [ "dk1"; "lt1"; "dk2"; "lt2"; "accent1"; "accent2"; "accent3"; "accent4"; "accent5"; "accent6"; "hlink"; "folHlink" ]
        |> List.choose (fun name ->
            Xml.descendantsByLocal name theme
            |> Seq.tryHead
            |> Option.bind (fun color ->
                Xml.tryChildByLocal "srgbClr" color
                |> Option.bind (Xml.attrLocal "val")
                |> Option.orElseWith (fun () -> Xml.tryChildByLocal "sysClr" color |> Option.bind (Xml.attrLocal "lastClr"))
                |> Option.map normalizeColor)
            |> Option.map (fun color -> name, color))
        |> Map.ofList

let private colorMods (clr: Xml.XmlElement) =
    let m name = Xml.tryChildByLocal name clr |> Option.bind (attrFloat "val") |> Option.map (fun v -> v / 100000.0)
    m "lumMod", m "lumOff", m "shade", m "tint"

let private childColor themeColors (fill: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "srgbClr" fill
    |> Option.bind (fun c ->
        Xml.attrLocal "val" c
        |> Option.map normalizeColor
        |> Option.map (fun baseColor ->
            let lm, lo, sh, ti = colorMods c
            applyColorMods baseColor lm lo sh ti))
    |> Option.orElseWith (fun () ->
        Xml.tryChildByLocal "schemeClr" fill
        |> Option.bind (fun schemeClr ->
            Xml.attrLocal "val" schemeClr
            |> Option.bind (fun key -> Map.tryFind (themeKey key) themeColors)
            |> Option.map (fun baseColor ->
                let lm, lo, sh, ti = colorMods schemeClr
                applyColorMods baseColor lm lo sh ti)))
    |> Option.orElseWith (fun () ->
        Xml.tryChildByLocal "sysClr" fill
        |> Option.bind (Xml.attrLocal "lastClr")
        |> Option.map normalizeColor)

let private solidFillColor themeColors (el: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "solidFill" el |> Option.bind (childColor themeColors)

let private lineStyle themeColors (el: Xml.XmlElement) : string * float =
    match Xml.tryChildByLocal "ln" el with
    | None -> "", 0.0
    | Some ln ->
        let color = solidFillColor themeColors ln |> Option.defaultValue ""
        let width = attrFloat "w" ln |> Option.map (fun w -> w / 12700.0) |> Option.defaultValue 1.0
        color, width

let private styleFillColor themeColors (sp: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "style" sp
    |> Option.bind (Xml.tryChildByLocal "fillRef")
    |> Option.bind (childColor themeColors)

let private styleLineColor themeColors (sp: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "style" sp
    |> Option.bind (Xml.tryChildByLocal "lnRef")
    |> Option.bind (childColor themeColors)

let private styleFontColor themeColors (sp: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "style" sp
    |> Option.bind (Xml.tryChildByLocal "fontRef")
    |> Option.bind (childColor themeColors)

let private geometryType (sp: Xml.XmlElement) : string =
    Xml.descendantsByLocal "prstGeom" sp
    |> Seq.tryHead
    |> Option.bind (Xml.attrLocal "prst")
    |> Option.defaultValue "rect"

/// prstGeom の調整値 (avLst の a:gd) を比率 (1.0 = 100%) で取り出す。
let private adjustValue (sp: Xml.XmlElement) (name: string) (def: float) : float =
    Xml.descendantsByLocal "gd" sp
    |> Seq.tryPick (fun gd ->
        match Xml.attrLocal "name" gd, Xml.attrLocal "fmla" gd with
        | Some n, Some fmla when n = name && fmla.StartsWith "val " -> tryParseIntSigned (fmla.Substring 4) |> Option.map float
        | _ -> None)
    |> Option.defaultValue def
    |> fun v -> v / 100000.0

/// 吹き出しのしっぽ位置 (中心からの比率)。geometry が吹き出しでなければ 0,0。
let private calloutAdjust (sp: Xml.XmlElement) : float * float =
    let geom = geometryType sp
    if geom.Contains "Callout" && (geom.Contains "wedge" || geom.Contains "Wedge") then
        adjustValue sp "adj1" -20833.0, adjustValue sp "adj2" 62500.0
    else
        0.0, 0.0

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

/// [Content_Types].xml を優先して画像 MIME を解決する (拡張子はフォールバック)。
let private imageDataCt (ct: Opc.ContentTypes) (archive: Zip.ZipArchive) (path: string) : (string * string) option =
    let mime =
        Opc.contentTypeOf ct path
        |> Option.filter (fun t -> t.StartsWith "image/")
        |> Option.orElseWith (fun () -> contentType path)
    match mime, Zip.tryReadBytes archive path with
    | Some m, Some bytes -> Some(m, System.Convert.ToBase64String bytes)
    | _ -> None

/// グラデーション塗り (gradFill) を CSS linear-gradient へ変換する。
let private gradientCss (themeColors: Map<string, string>) (gf: Xml.XmlElement) : string option =
    let stops =
        Xml.descendantsByLocal "gs" gf
        |> Seq.choose (fun gs ->
            match childColor themeColors gs with
            | Some c -> Some((attrFloat "pos" gs |> Option.defaultValue 0.0) / 1000.0, c)
            | None -> None)
        |> Seq.toList
    match stops with
    | [] -> None
    | [ (_, c) ] -> Some c
    | _ ->
        let angle =
            Xml.tryChildByLocal "lin" gf
            |> Option.bind (attrFloat "ang")
            |> Option.map (fun a -> a / 60000.0)
            |> Option.defaultValue 0.0
        let cssAngle = angle + 90.0
        let stopStr = stops |> List.map (fun (p, c) -> sprintf "%s %g%%" c p) |> String.concat ", "
        Some(sprintf "linear-gradient(%gdeg, %s)" cssAngle stopStr)

/// solidFill / gradFill / blipFill を CSS の塗り値 (色 / グラデーション / 画像 url) へ解決する。
let private resolveFill (themeColors: Map<string, string>) (archive: Zip.ZipArchive) (basePath: string) (rels: Map<string, Opc.Relationship>) (el: Xml.XmlElement) : string option =
    match solidFillColor themeColors el with
    | Some c -> Some c
    | None ->
        match Xml.tryChildByLocal "gradFill" el |> Option.bind (gradientCss themeColors) with
        | Some g -> Some g
        | None ->
            Xml.tryChildByLocal "blipFill" el
            |> Option.bind (fun bf -> Xml.descendantsByLocal "blip" bf |> Seq.tryPick (Xml.attrLocal "embed"))
            |> Option.bind (fun id -> Map.tryFind id rels)
            |> Option.filter (fun rel -> rel.TargetMode <> "External")
            |> Option.bind (fun rel ->
                let target = Opc.resolveTarget basePath rel.Target
                imageData archive target |> Option.map (fun (mime, data) -> sprintf "url(\"data:%s;base64,%s\") center / cover no-repeat" mime data))

let private altText (el: Xml.XmlElement) : string =
    Xml.descendantsByLocal "cNvPr" el
    |> Seq.tryPick (fun p -> Xml.attrLocal "descr" p |> Option.orElseWith (fun () -> Xml.attrLocal "name" p))
    |> Option.defaultValue ""

let private transformOpt (el: Xml.XmlElement) : (float * float * float * float) option =
    match Xml.descendantsByLocal "xfrm" el |> Seq.tryHead with
    | None -> None
    | Some xfrm ->
        let off = Xml.tryChildByLocal "off" xfrm
        let ext = Xml.tryChildByLocal "ext" xfrm
        let x = off |> Option.bind (attrFloat "x") |> Option.defaultValue 0.0
        let y = off |> Option.bind (attrFloat "y") |> Option.defaultValue 0.0
        let width = ext |> Option.bind (attrFloat "cx") |> Option.defaultValue 0.0
        let height = ext |> Option.bind (attrFloat "cy") |> Option.defaultValue 0.0
        Some(x, y, width, height)

let private transform (el: Xml.XmlElement) : float * float * float * float =
    transformOpt el |> Option.defaultValue (0.0, 0.0, 0.0, 0.0)

let private placeholder (sp: Xml.XmlElement) : Xml.XmlElement option =
    Xml.descendantsByLocal "ph" sp |> Seq.tryHead

let private placeholderType (sp: Xml.XmlElement) : string =
    placeholder sp |> Option.bind (Xml.attrLocal "type") |> Option.defaultValue "body"

let private placeholderIdx (sp: Xml.XmlElement) : string =
    placeholder sp |> Option.bind (Xml.attrLocal "idx") |> Option.defaultValue ""

let private samePlaceholder (source: Xml.XmlElement) (candidate: Xml.XmlElement) : bool =
    match placeholder source, placeholder candidate with
    | Some _, Some _ ->
        let sourceIdx = placeholderIdx source
        let candidateIdx = placeholderIdx candidate
        if sourceIdx <> "" && candidateIdx <> "" then sourceIdx = candidateIdx
        else placeholderType source = placeholderType candidate
    | _ -> false

/// <p:txBody> の各段落 <a:p> を 1 行のテキストとして取り出す。
let private shapeParagraphs (txBody: Xml.XmlElement) : string list =
    Xml.childrenByLocal "p" txBody
    |> List.map (fun ap -> Xml.descendantsByLocal "t" ap |> Seq.map Xml.innerText |> String.concat "")

/// プレースホルダー種別がタイトルの図形か判定する。
let private isTitleShape (sp: Xml.XmlElement) : bool =
    Xml.descendantsByLocal "ph" sp
    |> Seq.exists (fun ph ->
        match Xml.attrLocal "type" ph with
        | Some t -> t = "title" || t = "ctrTitle"
        | None -> false)

let private defaultRun : TextRun = { text = ""; bold = false; italic = false; underline = false; strike = false; fontSize = defaultTextSize; fontName = ""; color = "" }

let private fontName (rPr: Xml.XmlElement) : string =
    Xml.tryChildByLocal "latin" rPr
    |> Option.bind (Xml.attrLocal "typeface")
    |> Option.orElseWith (fun () -> Xml.tryChildByLocal "ea" rPr |> Option.bind (Xml.attrLocal "typeface"))
    |> Option.filter (fun name -> not (name.StartsWith "+"))
    |> Option.defaultValue ""

let private runStyle (themeColors: Map<string, string>) (baseRun: TextRun) (text: string) (rPr: Xml.XmlElement option) : TextRun =
    match rPr with
    | None -> { baseRun with text = text }
    | Some rPr ->
        let isLink = Xml.tryChildByLocal "hlinkClick" rPr |> Option.isSome
        let explicitColor = solidFillColor themeColors rPr
        { text = text
          bold = Xml.attrLocal "b" rPr |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false") |> Option.defaultValue baseRun.bold
          italic = Xml.attrLocal "i" rPr |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false") |> Option.defaultValue baseRun.italic
          underline = (Xml.attrLocal "u" rPr |> Option.map (fun v -> v <> "none") |> Option.defaultValue baseRun.underline) || isLink
          strike = Xml.attrLocal "strike" rPr |> Option.map (fun v -> v <> "noStrike") |> Option.defaultValue baseRun.strike
          fontSize = attrFloat "sz" rPr |> Option.map (fun v -> v / 100.0) |> Option.defaultValue baseRun.fontSize
          fontName = let name = fontName rPr in if name = "" then baseRun.fontName else name
          color =
            match explicitColor with
            | Some c -> c
            | None -> if isLink then Map.tryFind "hlink" themeColors |> Option.defaultValue baseRun.color else baseRun.color }

let private paragraphRuns (themeColors: Map<string, string>) (baseRun: TextRun) (p: Xml.XmlElement) : TextRun[] =
    p
    |> Xml.elementChildren
    |> List.choose (fun child ->
        match Xml.localName child.Name with
        | "r" -> Some(runStyle themeColors baseRun (Xml.childrenByLocal "t" child |> List.map Xml.innerText |> String.concat "") (Xml.tryChildByLocal "rPr" child))
        | "br" -> Some(runStyle themeColors baseRun "\n" (Xml.tryChildByLocal "rPr" child))
        | _ -> None)
    |> Array.ofList

let private styleDefaultRun (themeColors: Map<string, string>) (name: string) (master: Xml.XmlElement option) : TextRun =
    master
    |> Option.bind (fun master -> Xml.descendantsByLocal name master |> Seq.tryHead)
    |> Option.bind (fun style -> Xml.descendantsByLocal "defRPr" style |> Seq.tryHead)
    |> Option.map (fun rPr -> runStyle themeColors defaultRun "" (Some rPr))
    |> Option.defaultValue defaultRun

let private defaultRunForShape (themeColors: Map<string, string>) (master: Xml.XmlElement option) (sp: Xml.XmlElement) : TextRun =
    if isTitleShape sp then styleDefaultRun themeColors "titleStyle" master
    else styleDefaultRun themeColors "bodyStyle" master

let private textAlign (txBody: Xml.XmlElement) : string =
    Xml.descendantsByLocal "pPr" txBody
    |> Seq.tryPick (Xml.attrLocal "algn")
    |> Option.map (function
        | "ctr" -> "center"
        | "r" -> "right"
        | _ -> "left")
    |> Option.defaultValue "left"

let private isSymbolFont (font: string) : bool =
    let f = font.ToLower()
    f.Contains "wingding" || f.Contains "webding" || f = "symbol" || f.Contains "marlett"

/// 箇条書き記号を表示用に変換する (記号フォント/私用領域はビュレットに置換)。
let private displayBullet (ch: string) (font: string) : string =
    if ch = "" then ""
    else
        let code = int ch.[0]
        if isSymbolFont font || (code >= 0xF000 && code <= 0xF0FF) then "•"
        else ch

/// マスターの bodyStyle/titleStyle から指定レベルの lvlNpPr を取得する。
let private masterLevelPPr (master: Xml.XmlElement option) (styleName: string) (level: int) : Xml.XmlElement option =
    master
    |> Option.bind (fun m -> Xml.descendantsByLocal styleName m |> Seq.tryHead)
    |> Option.bind (fun style -> Xml.childrenByLocal (sprintf "lvl%dpPr" (level + 1)) style |> List.tryHead)

/// 図形の bodyPr の anchor (縦位置) を取り出す。
let private bodyPrAnchorOf (sp: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "txBody" sp
    |> Option.bind (Xml.tryChildByLocal "bodyPr")
    |> Option.bind (Xml.attrLocal "anchor")

/// 1 段落 (a:p) を解析する。styleName は箇条書き/インデント継承元 (None なら継承なし)。
let private buildParagraph (themeColors: Map<string, string>) (master: Xml.XmlElement option) (styleName: string option) (baseRun: TextRun) (p: Xml.XmlElement) : SlideParagraph =
    let pPr = Xml.tryChildByLocal "pPr" p
    let level = pPr |> Option.bind (Xml.attrLocal "lvl") |> Option.bind tryParseInt |> Option.defaultValue 0
    let masterPPr = styleName |> Option.bind (fun s -> masterLevelPPr master s level)
    let attrFrom name = pPr |> Option.bind (Xml.attrLocal name) |> Option.orElseWith (fun () -> masterPPr |> Option.bind (Xml.attrLocal name))
    let align =
        attrFrom "algn"
        |> Option.map (function
            | "ctr" -> "center"
            | "r" -> "right"
            | "just" -> "justify"
            | _ -> "left")
        |> Option.defaultValue "left"
    let marginLeft = attrFrom "marL" |> Option.bind tryParseFloat |> Option.defaultValue 0.0
    let indent = attrFrom "indent" |> Option.bind tryParseIntSigned |> Option.map float |> Option.defaultValue 0.0
    let lnSpcOf el =
        el
        |> Option.bind (Xml.tryChildByLocal "lnSpc")
        |> Option.bind (Xml.tryChildByLocal "spcPct")
        |> Option.bind (attrFloat "val")
        |> Option.map (fun v -> v / 100000.0)
    let lineSpace = lnSpcOf pPr |> Option.orElseWith (fun () -> lnSpcOf masterPPr) |> Option.defaultValue 0.0
    let runs = paragraphRuns themeColors baseRun p
    let bullet, bulletColor =
        match styleName, (pPr |> Option.bind (Xml.tryChildByLocal "buNone")) with
        | Some "bodyStyle", None when runs.Length > 0 ->
            let buSource =
                match pPr |> Option.bind (Xml.tryChildByLocal "buChar") with
                | Some _ -> pPr
                | None -> masterPPr
            let ch = buSource |> Option.bind (Xml.tryChildByLocal "buChar") |> Option.bind (Xml.attrLocal "char") |> Option.defaultValue ""
            let font = buSource |> Option.bind (Xml.tryChildByLocal "buFont") |> Option.bind (Xml.attrLocal "typeface") |> Option.defaultValue ""
            let clr = buSource |> Option.bind (Xml.tryChildByLocal "buClr") |> Option.bind (childColor themeColors) |> Option.defaultValue ""
            displayBullet ch font, clr
        | _ -> "", ""
    { runs = runs
      align = align
      level = level
      bullet = bullet
      bulletColor = bulletColor
      marginLeft = marginLeft
      indent = indent
      lineSpace = lineSpace }

let private parseTextBox (themeColors: Map<string, string>) (archive: Zip.ZipArchive) (basePath: string) (rels: Map<string, Opc.Relationship>) (master: Xml.XmlElement option) (baseRun: TextRun) (fallbackTransform: Xml.XmlElement -> (float * float * float * float) option) (fallbackAnchor: Xml.XmlElement -> string option) (sp: Xml.XmlElement) : SlideTextBox option =
    match Xml.tryChildByLocal "txBody" sp with
    | None -> None
    | Some txBody ->
        let paraTexts = shapeParagraphs txBody
        if paraTexts |> List.forall (fun s -> s = "") then
            None
        else
            let baseRun =
                match styleFontColor themeColors sp with
                | Some c when c <> "" -> { baseRun with color = c }
                | _ -> baseRun
            let x, y, width, height = transformOpt sp |> Option.orElseWith (fun () -> fallbackTransform sp) |> Option.defaultValue (0.0, 0.0, 0.0, 0.0)
            let styleName =
                match placeholder sp with
                | None -> None
                | Some _ -> if isTitleShape sp then Some "titleStyle" else Some "bodyStyle"
            let paragraphs = Xml.childrenByLocal "p" txBody |> List.map (buildParagraph themeColors master styleName baseRun) |> Array.ofList
            let spPr = Xml.tryChildByLocal "spPr" sp
            let fillColor = spPr |> Option.bind (resolveFill themeColors archive basePath rels) |> Option.orElseWith (fun () -> styleFillColor themeColors sp) |> Option.defaultValue ""
            let directLineColor, lineWidth = spPr |> Option.map (lineStyle themeColors) |> Option.defaultValue ("", 0.0)
            let lineColor = if directLineColor = "" then styleLineColor themeColors sp |> Option.defaultValue "" else directLineColor
            let anchor =
                bodyPrAnchorOf sp
                |> Option.orElseWith (fun () -> fallbackAnchor sp)
                |> Option.map (function
                    | "ctr" -> "center"
                    | "b" -> "bottom"
                    | _ -> "top")
                |> Option.defaultValue "top"
            let adj1, adj2 = calloutAdjust sp
            Some
                { x = x
                  y = y
                  width = width
                  height = height
                  text = paraTexts |> List.filter (fun s -> s <> "") |> String.concat "\n"
                  paragraphs = paragraphs
                  fillColor = fillColor
                  lineColor = lineColor
                  lineWidth = lineWidth
                  shapeType = geometryType sp
                  verticalAlign = anchor
                  adj1 = adj1
                  adj2 = adj2 }

let private parseShape (themeColors: Map<string, string>) (archive: Zip.ZipArchive) (basePath: string) (rels: Map<string, Opc.Relationship>) (fallbackTransform: Xml.XmlElement -> (float * float * float * float) option) (sp: Xml.XmlElement) : SlideShape option =
    let spPr = Xml.tryChildByLocal "spPr" sp
    let fillColor = spPr |> Option.bind (resolveFill themeColors archive basePath rels) |> Option.orElseWith (fun () -> styleFillColor themeColors sp) |> Option.defaultValue ""
    let directLineColor, lineWidth = spPr |> Option.map (lineStyle themeColors) |> Option.defaultValue ("", 0.0)
    let lineColor = if directLineColor = "" then styleLineColor themeColors sp |> Option.defaultValue "" else directLineColor
    let hasText =
        match Xml.tryChildByLocal "txBody" sp with
        | Some txBody -> shapeParagraphs txBody |> List.exists (fun s -> s <> "")
        | None -> false
    if hasText || (fillColor = "" && lineColor = "") then
        None
    else
        let x, y, width, height = transformOpt sp |> Option.orElseWith (fun () -> fallbackTransform sp) |> Option.defaultValue (0.0, 0.0, 0.0, 0.0)
        let adj1, adj2 = calloutAdjust sp
        Some
            { x = x
              y = y
              width = width
              height = height
              fillColor = fillColor
              lineColor = lineColor
              lineWidth = lineWidth
              shapeType = geometryType sp
              adj1 = adj1
              adj2 = adj2 }

let private parseImage (ct: Opc.ContentTypes) (archive: Zip.ZipArchive) (slidePath: string) (rels: Map<string, Opc.Relationship>) (pic: Xml.XmlElement) : SlideImage option =
    let rid = Xml.descendantsByLocal "blip" pic |> Seq.tryPick (Xml.attrLocal "embed")
    match rid |> Option.bind (fun id -> Map.tryFind id rels) with
    | Some rel when rel.TargetMode <> "External" ->
        let target = Opc.resolveTarget slidePath rel.Target
        match imageDataCt ct archive target with
        | Some(mime, data) ->
            let x, y, width, height = transform pic
            Some
                { x = x
                  y = y
                  width = width
                  height = height
                  altText = altText pic
                  contentType = mime
                  data = data }
        | None -> None
    | _ -> None

let private tableCell (themeColors: Map<string, string>) (tc: Xml.XmlElement) : SlideTableCell =
    let txBody = Xml.tryChildByLocal "txBody" tc
    let runs =
        match txBody with
        | Some txBody -> Xml.childrenByLocal "p" txBody |> List.collect (paragraphRuns themeColors defaultRun >> Array.toList) |> Array.ofList
        | None -> [||]
    let text = runs |> Array.map (fun run -> run.text) |> String.concat ""
    let tcPr = Xml.tryChildByLocal "tcPr" tc
    { text = text
      runs = runs
      fillColor = tcPr |> Option.bind (solidFillColor themeColors) |> Option.defaultValue ""
      textAlign = txBody |> Option.map textAlign |> Option.defaultValue "left"
      verticalAlign = tcPr |> Option.bind (Xml.attrLocal "anchor") |> Option.map (function | "ctr" -> "center" | "b" -> "bottom" | _ -> "top") |> Option.defaultValue "top" }

let private parseTable (themeColors: Map<string, string>) (frame: Xml.XmlElement) : SlideTable option =
    match Xml.descendantsByLocal "tbl" frame |> Seq.tryHead with
    | None -> None
    | Some tbl ->
        let x, y, width, height = transform frame
        let columnWidths =
            Xml.descendantsByLocal "tblGrid" tbl
            |> Seq.tryHead
            |> Option.map (fun grid -> Xml.childrenByLocal "gridCol" grid |> List.map (fun col -> attrFloat "w" col |> Option.defaultValue 0.0) |> Array.ofList)
            |> Option.defaultValue [||]
        let rows =
            Xml.childrenByLocal "tr" tbl
            |> List.map (fun tr -> Xml.childrenByLocal "tc" tr |> List.map (tableCell themeColors) |> Array.ofList)
            |> Array.ofList
        let rowHeights = Xml.childrenByLocal "tr" tbl |> List.map (fun tr -> attrFloat "h" tr |> Option.defaultValue 0.0) |> Array.ofList
        Some
            { x = x
              y = y
              width = width
              height = height
              rows = rows
              columnWidths = columnWidths
              rowHeights = rowHeights }

let private parseSlideSize (pres: Xml.XmlElement) : float * float =
    match Xml.tryChildByLocal "sldSz" pres with
    | Some sldSz ->
        let width = attrFloat "cx" sldSz |> Option.defaultValue defaultSlideWidth
        let height = attrFloat "cy" sldSz |> Option.defaultValue defaultSlideHeight
        width, height
    | None -> defaultSlideWidth, defaultSlideHeight

let private parseBackground (themeColors: Map<string, string>) (archive: Zip.ZipArchive) (basePath: string) (rels: Map<string, Opc.Relationship>) (root: Xml.XmlElement) : string =
    Xml.descendantsByLocal "bgPr" root
    |> Seq.tryHead
    |> Option.bind (resolveFill themeColors archive basePath rels)
    |> Option.defaultValue "#FFFFFF"

let private readXmlPart (archive: Zip.ZipArchive) (path: string) : Xml.XmlElement option =
    Zip.tryReadBytes archive path |> Option.map Xml.parseBytes

let private showMasterShapes (el: Xml.XmlElement option) : bool =
    el
    |> Option.bind (Xml.attrLocal "showMasterSp")
    |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false")
    |> Option.defaultValue true

let private loadLayoutAndMaster (archive: Zip.ZipArchive) (slidePath: string) : Xml.XmlElement option * Xml.XmlElement option =
    let slideRels = Opc.loadRels archive slidePath
    let layoutPath =
        slideRels
        |> Map.toSeq
        |> Seq.tryPick (fun (_, rel) ->
            if rel.Type.EndsWith "/slideLayout" && rel.TargetMode <> "External" then Some(Opc.resolveTarget slidePath rel.Target)
            else None)
    match layoutPath with
    | None -> None, None
    | Some layoutPath ->
        let layout = readXmlPart archive layoutPath
        let masterPath =
            Opc.loadRels archive layoutPath
            |> Map.toSeq
            |> Seq.tryPick (fun (_, rel) ->
                if rel.Type.EndsWith "/slideMaster" && rel.TargetMode <> "External" then Some(Opc.resolveTarget layoutPath rel.Target)
                else None)
        layout, (masterPath |> Option.bind (readXmlPart archive))

let private placeholderShapes (roots: Xml.XmlElement option list) : Xml.XmlElement[] =
    roots
    |> List.choose id
    |> List.collect (fun root -> Xml.descendantsByLocal "sp" root |> Seq.filter (placeholder >> Option.isSome) |> Seq.toList)
    |> Array.ofList

let private inheritedDecorations (themeColors: Map<string, string>) (archive: Zip.ZipArchive) (roots: Xml.XmlElement option list) : SlideShape[] =
    roots
    |> List.choose id
    |> List.collect (fun root -> Xml.descendantsByLocal "sp" root |> Seq.filter (placeholder >> Option.isNone) |> Seq.toList)
    |> List.choose (parseShape themeColors archive "" Map.empty (fun _ -> None))
    |> Array.ofList

/// 1 枚のスライドを解析する。
let private parseSlide (archive: Zip.ZipArchive) (ct: Opc.ContentTypes) (themeColors: Map<string, string>) (slidePath: string) (slideWidth: float) (slideHeight: float) (index: int) (root: Xml.XmlElement) : Slide =
    let rels = Opc.loadRels archive slidePath
    let layout, master = loadLayoutAndMaster archive slidePath
    let includeMaster = showMasterShapes (Some root) && showMasterShapes layout
    let inheritanceRoots = if includeMaster then [ layout; master ] else [ layout ]
    let inheritedPlaceholders = placeholderShapes inheritanceRoots
    let fallbackTransform sp =
        inheritedPlaceholders
        |> Array.tryPick (fun candidate -> if samePlaceholder sp candidate then transformOpt candidate else None)
    let fallbackAnchor sp =
        inheritedPlaceholders
        |> Array.tryPick (fun candidate -> if samePlaceholder sp candidate then bodyPrAnchorOf candidate else None)
    let shapeElements = Xml.descendantsByLocal "sp" root |> Array.ofSeq
    let parseTextBoxFor sp = parseTextBox themeColors archive slidePath rels master (defaultRunForShape themeColors master sp) fallbackTransform fallbackAnchor sp

    // p:grpSp のグループ座標変換を考慮して各要素を再帰収集する。
    let textBoxAcc = System.Collections.Generic.List<SlideTextBox>()
    let shapeAcc = System.Collections.Generic.List<SlideShape>()
    let imageAcc = System.Collections.Generic.List<SlideImage>()
    let tableAcc = System.Collections.Generic.List<SlideTable>()
    let applyBox (f: float * float * float * float -> float * float * float * float) (b: SlideTextBox) =
        let x, y, w, h = f (b.x, b.y, b.width, b.height)
        { b with x = x; y = y; width = w; height = h }
    let applyShapeXf (f: float * float * float * float -> float * float * float * float) (s: SlideShape) =
        let x, y, w, h = f (s.x, s.y, s.width, s.height)
        { s with x = x; y = y; width = w; height = h }
    let applyImageXf (f: float * float * float * float -> float * float * float * float) (im: SlideImage) =
        let x, y, w, h = f (im.x, im.y, im.width, im.height)
        { im with x = x; y = y; width = w; height = h }
    let applyTableXf (f: float * float * float * float -> float * float * float * float) (t: SlideTable) =
        let x, y, w, h = f (t.x, t.y, t.width, t.height)
        { t with x = x; y = y; width = w; height = h }
    let groupMap (parent: float * float * float * float -> float * float * float * float) (grpSp: Xml.XmlElement) =
        match Xml.tryChildByLocal "grpSpPr" grpSp |> Option.bind (Xml.tryChildByLocal "xfrm") with
        | None -> parent
        | Some xfrm ->
            let coord child name = Xml.tryChildByLocal child xfrm |> Option.bind (attrFloat name)
            let ox = coord "off" "x" |> Option.defaultValue 0.0
            let oy = coord "off" "y" |> Option.defaultValue 0.0
            let ecx = coord "ext" "cx" |> Option.defaultValue 0.0
            let ecy = coord "ext" "cy" |> Option.defaultValue 0.0
            let cox = coord "chOff" "x" |> Option.defaultValue 0.0
            let coy = coord "chOff" "y" |> Option.defaultValue 0.0
            let ccx = coord "chExt" "cx" |> Option.defaultValue ecx
            let ccy = coord "chExt" "cy" |> Option.defaultValue ecy
            let sx = if ccx = 0.0 then 1.0 else ecx / ccx
            let sy = if ccy = 0.0 then 1.0 else ecy / ccy
            let local (x, y, w, h) = ox + (x - cox) * sx, oy + (y - coy) * sy, w * sx, h * sy
            local >> parent
    let rec walk (toAbs: float * float * float * float -> float * float * float * float) (container: Xml.XmlElement) =
        for child in Xml.elementChildren container do
            match Xml.localName child.Name with
            | "sp" ->
                match parseTextBoxFor child with
                | Some tb -> textBoxAcc.Add(applyBox toAbs tb)
                | None ->
                    match parseShape themeColors archive slidePath rels fallbackTransform child with
                    | Some s -> shapeAcc.Add(applyShapeXf toAbs s)
                    | None -> ()
            | "pic" ->
                match parseImage ct archive slidePath rels child with
                | Some im -> imageAcc.Add(applyImageXf toAbs im)
                | None -> ()
            | "graphicFrame" ->
                match parseTable themeColors child with
                | Some t -> tableAcc.Add(applyTableXf toAbs t)
                | None -> ()
            | "grpSp" -> walk (groupMap toAbs child) child
            | _ -> ()
    match Xml.descendantsByLocal "spTree" root |> Seq.tryHead with
    | Some spTree -> walk id spTree
    | None -> ()

    let textBoxes = textBoxAcc.ToArray()
    let shapes = Array.append (inheritedDecorations themeColors archive inheritanceRoots) (shapeAcc.ToArray())
    let images = imageAcc.ToArray()
    let tables = tableAcc.ToArray()
    let backgroundColor = parseBackground themeColors archive slidePath rels root
    let title =
        shapeElements
        |> Array.tryPick (fun sp ->
            if isTitleShape sp then parseTextBoxFor sp |> Option.map (fun box -> box.text.Replace("\n", " "))
            else None)
        |> Option.orElseWith (fun () -> textBoxes |> Array.tryHead |> Option.map (fun box -> box.text.Replace("\n", " ")))
        |> Option.defaultValue ""
    let texts = textBoxes |> Array.collect (fun box -> box.text.Split('\n'))
    { index = index; title = title; texts = texts; textBoxes = textBoxes; shapes = shapes; tables = tables; images = images; backgroundColor = backgroundColor; width = slideWidth; height = slideHeight }

/// .pptx バイト列を解析する。
let parse (data: byte[]) : PresentationData =
    let archive = Zip.read data
    let themeColors = parseThemeColors archive
    let contentTypes = Opc.loadContentTypes archive
    let presentationPath = Opc.officeDocumentPath archive |> Option.defaultValue "ppt/presentation.xml"
    let rels = Opc.loadRels archive presentationPath

    match Zip.tryReadBytes archive presentationPath with
    | None -> { kind = "presentation"; slides = [||] }
    | Some bytes ->
        let pres = Xml.parseBytes bytes
        let slideWidth, slideHeight = parseSlideSize pres
        let slideRefs =
            match Xml.tryChildByLocal "sldIdLst" pres with
            | None -> []
            | Some lst -> Xml.childrenByLocal "sldId" lst |> List.choose relId

        let slides =
            slideRefs
            |> List.mapi (fun i rid -> i, rid)
            |> List.choose (fun (i, rid) ->
                Map.tryFind rid rels
                |> Option.map (fun r -> Opc.resolveTarget presentationPath r.Target)
                |> Option.bind (fun slidePath ->
                    Zip.tryReadBytes archive slidePath
                    |> Option.map (fun sb -> parseSlide archive contentTypes themeColors slidePath slideWidth slideHeight (i + 1) (Xml.parseBytes sb))))
            |> Array.ofList

        { kind = "presentation"; slides = slides }

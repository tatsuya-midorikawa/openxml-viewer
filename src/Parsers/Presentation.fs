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

let private applyLum (color: string) (lumMod: float option) (lumOff: float option) : string =
    match rgbOfHex color with
    | None -> color
    | Some(r, g, b) ->
        let m = defaultArg lumMod 1.0
        let o = defaultArg lumOff 0.0
        let clamp channel =
            let v = int (round (float channel * m + 255.0 * o))
            max 0 (min 255 v)
        sprintf "#%02X%02X%02X" (clamp r) (clamp g) (clamp b)

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

let private childColor themeColors (fill: Xml.XmlElement) : string option =
    Xml.tryChildByLocal "srgbClr" fill
    |> Option.bind (Xml.attrLocal "val")
    |> Option.map normalizeColor
    |> Option.orElseWith (fun () ->
        Xml.tryChildByLocal "schemeClr" fill
        |> Option.bind (fun schemeClr ->
            Xml.attrLocal "val" schemeClr
            |> Option.bind (fun key -> Map.tryFind (themeKey key) themeColors)
            |> Option.map (fun baseColor ->
                let lumMod = Xml.tryChildByLocal "lumMod" schemeClr |> Option.bind (attrFloat "val") |> Option.map (fun v -> v / 100000.0)
                let lumOff = Xml.tryChildByLocal "lumOff" schemeClr |> Option.bind (attrFloat "val") |> Option.map (fun v -> v / 100000.0)
                applyLum baseColor lumMod lumOff)))
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

let private geometryType (sp: Xml.XmlElement) : string =
    Xml.descendantsByLocal "prstGeom" sp
    |> Seq.tryHead
    |> Option.bind (Xml.attrLocal "prst")
    |> Option.defaultValue "rect"

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
        { text = text
          bold = Xml.attrLocal "b" rPr |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false") |> Option.defaultValue baseRun.bold
          italic = Xml.attrLocal "i" rPr |> Option.map (fun v -> v <> "0" && v.ToLower() <> "false") |> Option.defaultValue baseRun.italic
          underline = Xml.attrLocal "u" rPr |> Option.map (fun v -> v <> "none") |> Option.defaultValue baseRun.underline
          strike = Xml.attrLocal "strike" rPr |> Option.map (fun v -> v <> "noStrike") |> Option.defaultValue baseRun.strike
          fontSize = attrFloat "sz" rPr |> Option.map (fun v -> v / 100.0) |> Option.defaultValue baseRun.fontSize
          fontName = let name = fontName rPr in if name = "" then baseRun.fontName else name
          color = solidFillColor themeColors rPr |> Option.defaultValue baseRun.color }

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

let private verticalAlign (txBody: Xml.XmlElement) : string =
    Xml.tryChildByLocal "bodyPr" txBody
    |> Option.bind (Xml.attrLocal "anchor")
    |> Option.map (function
        | "ctr" -> "center"
        | "b" -> "bottom"
        | _ -> "top")
    |> Option.defaultValue "top"

let private parseTextBox (themeColors: Map<string, string>) (baseRun: TextRun) (fallbackTransform: Xml.XmlElement -> (float * float * float * float) option) (sp: Xml.XmlElement) : SlideTextBox option =
    match Xml.tryChildByLocal "txBody" sp with
    | None -> None
    | Some txBody ->
        let paras = shapeParagraphs txBody |> List.filter (fun s -> s <> "")
        if List.isEmpty paras then
            None
        else
            let x, y, width, height = transformOpt sp |> Option.orElseWith (fun () -> fallbackTransform sp) |> Option.defaultValue (0.0, 0.0, 0.0, 0.0)
            let paragraphRunLists = Xml.childrenByLocal "p" txBody |> List.map (paragraphRuns themeColors baseRun >> Array.toList)
            let runs =
                paragraphRunLists
                |> List.mapi (fun i runs ->
                    if i = 0 then runs else runStyle themeColors baseRun "\n" None :: runs)
                |> List.collect id
                |> Array.ofList
            let spPr = Xml.tryChildByLocal "spPr" sp
            let fillColor = spPr |> Option.bind (solidFillColor themeColors) |> Option.orElseWith (fun () -> styleFillColor themeColors sp) |> Option.defaultValue ""
            let directLineColor, lineWidth = spPr |> Option.map (lineStyle themeColors) |> Option.defaultValue ("", 0.0)
            let lineColor = if directLineColor = "" then styleLineColor themeColors sp |> Option.defaultValue "" else directLineColor
            Some
                { x = x
                  y = y
                  width = width
                  height = height
                  text = String.concat "\n" paras
                  paragraphs = Array.ofList paras
                  runs = runs
                  fillColor = fillColor
                  lineColor = lineColor
                  lineWidth = lineWidth
                  shapeType = geometryType sp
                  textAlign = textAlign txBody
                  verticalAlign = verticalAlign txBody }

let private parseShape (themeColors: Map<string, string>) (fallbackTransform: Xml.XmlElement -> (float * float * float * float) option) (sp: Xml.XmlElement) : SlideShape option =
    let spPr = Xml.tryChildByLocal "spPr" sp
    let fillColor = spPr |> Option.bind (solidFillColor themeColors) |> Option.orElseWith (fun () -> styleFillColor themeColors sp) |> Option.defaultValue ""
    let directLineColor, lineWidth = spPr |> Option.map (lineStyle themeColors) |> Option.defaultValue ("", 0.0)
    let lineColor = if directLineColor = "" then styleLineColor themeColors sp |> Option.defaultValue "" else directLineColor
    if fillColor = "" && lineColor = "" then
        None
    else
        let x, y, width, height = transformOpt sp |> Option.orElseWith (fun () -> fallbackTransform sp) |> Option.defaultValue (0.0, 0.0, 0.0, 0.0)
        Some
            { x = x
              y = y
              width = width
              height = height
              fillColor = fillColor
              lineColor = lineColor
              lineWidth = lineWidth
              shapeType = geometryType sp }

let private parseImage (archive: Zip.ZipArchive) (slidePath: string) (rels: Map<string, Opc.Relationship>) (pic: Xml.XmlElement) : SlideImage option =
    let rid = Xml.descendantsByLocal "blip" pic |> Seq.tryPick (Xml.attrLocal "embed")
    match rid |> Option.bind (fun id -> Map.tryFind id rels) with
    | Some rel when rel.TargetMode <> "External" ->
        let target = Opc.resolveTarget slidePath rel.Target
        match imageData archive target with
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

let private parseBackground (themeColors: Map<string, string>) (root: Xml.XmlElement) : string =
    Xml.descendantsByLocal "bgPr" root
    |> Seq.tryHead
    |> Option.bind (solidFillColor themeColors)
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

let private inheritedDecorations (themeColors: Map<string, string>) (roots: Xml.XmlElement option list) : SlideShape[] =
    roots
    |> List.choose id
    |> List.collect (fun root -> Xml.descendantsByLocal "sp" root |> Seq.filter (placeholder >> Option.isNone) |> Seq.toList)
    |> List.choose (parseShape themeColors (fun _ -> None))
    |> Array.ofList

/// 1 枚のスライドを解析する。
let private parseSlide (archive: Zip.ZipArchive) (themeColors: Map<string, string>) (slidePath: string) (slideWidth: float) (slideHeight: float) (index: int) (root: Xml.XmlElement) : Slide =
    let rels = Opc.loadRels archive slidePath
    let layout, master = loadLayoutAndMaster archive slidePath
    let includeMaster = showMasterShapes (Some root) && showMasterShapes layout
    let inheritanceRoots = if includeMaster then [ layout; master ] else [ layout ]
    let inheritedPlaceholders = placeholderShapes inheritanceRoots
    let fallbackTransform sp =
        inheritedPlaceholders
        |> Array.tryPick (fun candidate -> if samePlaceholder sp candidate then transformOpt candidate else None)
    let shapeElements = Xml.descendantsByLocal "sp" root |> Array.ofSeq
    let parseTextBoxFor sp = parseTextBox themeColors (defaultRunForShape themeColors master sp) fallbackTransform sp
    let textBoxes = shapeElements |> Array.choose parseTextBoxFor
    let shapes = Array.append (inheritedDecorations themeColors inheritanceRoots) (shapeElements |> Array.choose (parseShape themeColors fallbackTransform))
    let images = Xml.descendantsByLocal "pic" root |> Seq.choose (parseImage archive slidePath rels) |> Array.ofSeq
    let tables = Xml.descendantsByLocal "graphicFrame" root |> Seq.choose (parseTable themeColors) |> Array.ofSeq
    let backgroundColor = parseBackground themeColors root
    let title =
        shapeElements
        |> Array.tryPick (fun sp ->
            if isTitleShape sp then parseTextBoxFor sp |> Option.map (fun box -> box.text.Replace("\n", " "))
            else None)
        |> Option.orElseWith (fun () -> textBoxes |> Array.tryHead |> Option.map (fun box -> box.text.Replace("\n", " ")))
        |> Option.defaultValue ""
    let texts = textBoxes |> Array.collect (fun box -> box.paragraphs)
    { index = index; title = title; texts = texts; textBoxes = textBoxes; shapes = shapes; tables = tables; images = images; backgroundColor = backgroundColor; width = slideWidth; height = slideHeight }

/// .pptx バイト列を解析する。
let parse (data: byte[]) : PresentationData =
    let archive = Zip.read data
    let themeColors = parseThemeColors archive
    let rels = Opc.loadRels archive "ppt/presentation.xml"

    match Zip.tryReadBytes archive "ppt/presentation.xml" with
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
                |> Option.map (fun r -> Opc.resolveTarget "ppt/presentation.xml" r.Target)
                |> Option.bind (fun slidePath ->
                    Zip.tryReadBytes archive slidePath
                    |> Option.map (fun sb -> parseSlide archive themeColors slidePath slideWidth slideHeight (i + 1) (Xml.parseBytes sb))))
            |> Array.ofList

        { kind = "presentation"; slides = slides }

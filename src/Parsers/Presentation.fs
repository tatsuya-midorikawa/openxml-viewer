/// PresentationML (.pptx) パーサー。
/// ECMA-376 Part 1 §19 (PresentationML)、テキストは DrawingML §21 を参照。
module OpenXmlViewer.Parsers.Presentation

open OpenXmlViewer.Core
open OpenXmlViewer.Model

/// r:id 属性 (プレフィックス付きの id) を取得する。非修飾の id とは区別する。
let private relId (el: Xml.XmlElement) : string option =
    el.Attributes |> List.tryPick (fun (k, v) -> if k.EndsWith ":id" then Some v else None)

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

/// 1 枚のスライドを解析する。
let private parseSlide (index: int) (root: Xml.XmlElement) : Slide =
    let mutable title = ""
    let texts = ResizeArray<string>()
    for sp in Xml.descendantsByLocal "sp" root do
        match Xml.tryChildByLocal "txBody" sp with
        | Some txBody ->
            let paras = shapeParagraphs txBody |> List.filter (fun s -> s <> "")
            if isTitleShape sp && title = "" then
                title <- String.concat " " paras
            else
                texts.AddRange paras
        | None -> ()
    { index = index
      title = title
      texts = Array.ofSeq texts }

/// .pptx バイト列を解析する。
let parse (data: byte[]) : PresentationData =
    let archive = Zip.read data
    let rels = Opc.loadRels archive "ppt/presentation.xml"

    match Zip.tryReadBytes archive "ppt/presentation.xml" with
    | None -> { kind = "presentation"; slides = [||] }
    | Some bytes ->
        let pres = Xml.parseBytes bytes
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
                |> Option.bind (Zip.tryReadBytes archive)
                |> Option.map (fun sb -> parseSlide (i + 1) (Xml.parseBytes sb)))
            |> Array.ofList

        { kind = "presentation"; slides = slides }

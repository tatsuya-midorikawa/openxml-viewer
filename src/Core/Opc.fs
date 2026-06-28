/// Open Packaging Conventions (ECMA-376 Part 2) の最小実装。
/// リレーションシップ (_rels/*.rels) の解決を担う。
module OpenXmlViewer.Core.Opc

open System.Collections.Generic
open OpenXmlViewer.Core

/// OPC リレーションシップ。
type Relationship =
    { Id: string
      Type: string
      Target: string
      TargetMode: string }

/// パスのディレクトリ部分を返す。
let private dirOf (path: string) =
    let i = path.LastIndexOf('/')
    if i < 0 then "" else path.Substring(0, i)

/// あるパートに対応する .rels パスを求める。
/// 例: "xl/workbook.xml" -> "xl/_rels/workbook.xml.rels"、"" -> "_rels/.rels"
let relsPathFor (partPath: string) : string =
    let dir = dirOf partPath
    let file =
        let i = partPath.LastIndexOf('/')
        if i < 0 then partPath else partPath.Substring(i + 1)
    if dir = "" then "_rels/" + file + ".rels"
    else dir + "/_rels/" + file + ".rels"

/// リレーションシップの Target をパッケージ内の絶対パートパスへ解決する。
let resolveTarget (sourcePart: string) (target: string) : string =
    if target.StartsWith "/" then
        target.Substring 1
    else
        let baseDir = dirOf sourcePart
        let combined = if baseDir = "" then target else baseDir + "/" + target
        let stack = ResizeArray<string>()
        for p in combined.Split('/') do
            if p = "" || p = "." then ()
            elif p = ".." then
                if stack.Count > 0 then stack.RemoveAt(stack.Count - 1)
            else
                stack.Add p
        String.concat "/" stack

/// Relationships ルート要素から一覧を取り出す。
let parseRels (root: Xml.XmlElement) : Relationship list =
    Xml.childrenByLocal "Relationship" root
    |> List.map (fun e ->
        { Id = Xml.attrLocal "Id" e |> Option.defaultValue ""
          Type = Xml.attrLocal "Type" e |> Option.defaultValue ""
          Target = Xml.attrLocal "Target" e |> Option.defaultValue ""
          TargetMode = Xml.attrLocal "TargetMode" e |> Option.defaultValue "Internal" })

/// 指定パートのリレーションシップを Id -> Relationship の辞書として読み込む。
let loadRels (archive: Zip.ZipArchive) (partPath: string) : Map<string, Relationship> =
    match Zip.tryReadBytes archive (relsPathFor partPath) with
    | Some bytes ->
        parseRels (Xml.parseBytes bytes)
        |> List.map (fun r -> r.Id, r)
        |> Map.ofList
    | None -> Map.empty

/// パッケージ ルートリレーションシップ (/_rels/.rels) から、指定タイプのターゲットパートを解決する。
let partByRelType (archive: Zip.ZipArchive) (relTypeSuffix: string) : string option =
    loadRels archive ""
    |> Map.toSeq
    |> Seq.tryPick (fun (_, rel) ->
        if rel.Type.EndsWith relTypeSuffix && rel.TargetMode <> "External" then Some(resolveTarget "" rel.Target)
        else None)

/// メイン文書パート (officeDocument リレーションシップのターゲット) を解決する。
let officeDocumentPath (archive: Zip.ZipArchive) : string option =
    partByRelType archive "/officeDocument"

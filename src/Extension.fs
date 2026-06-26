/// 拡張機能のエントリーポイント。
/// 各形式のカスタムエディター (読み取り専用) を登録し、Webview へ解析結果を送る。
module OpenXmlViewer.Extension

open Fable.Core
open Fable.Core.JsInterop
open OpenXmlViewer
open OpenXmlViewer.Parsers

/// 指定バイト列を種別ごとに解析し、Webview 用ペイロードへ変換する。
let private parsePayload (kind: string) (bytes: byte[]) : obj =
    try
        match kind with
        | "spreadsheet" -> box (Spreadsheet.parse bytes)
        | "document" -> box (Document.parse bytes)
        | _ -> box (Presentation.parse bytes)
    with ex ->
        box (createObj [ "kind" ==> "error"; "message" ==> ex.Message ])

/// Webview に表示する HTML を構築する。スクリプト/スタイルは asWebviewUri 経由で読み込む。
let private buildHtml (webview: Vscode.Webview) (nonce: string) (scriptUri: string) (styleUri: string) : string =
    let csp =
        sprintf
            "default-src 'none'; img-src %s data:; style-src %s; script-src 'nonce-%s';"
            webview.cspSource
            webview.cspSource
            nonce

    sprintf
        """<!DOCTYPE html>
<html lang="ja">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy" content="%s">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <link href="%s" rel="stylesheet">
  <title>OpenXML Viewer</title>
</head>
<body>
  <div id="app" class="loading">読み込み中…</div>
  <script nonce="%s" src="%s"></script>
</body>
</html>"""
        csp
        styleUri
        nonce
        scriptUri

/// カスタムエディターを解決し、Webview をセットアップして解析結果を送信する。
let private render (extensionUri: Vscode.Uri) (kind: string) (document: Vscode.CustomDocument) (panel: Vscode.WebviewPanel) =
    let webview = panel.webview
    webview.options <- createObj [ "enableScripts" ==> true; "localResourceRoots" ==> [| extensionUri |] ]

    let nonce = Vscode.nonce ()
    let scriptUri = Vscode.uriToString (webview.asWebviewUri (Vscode.joinPath extensionUri [ "media"; "main.js" ]))
    let styleUri = Vscode.uriToString (webview.asWebviewUri (Vscode.joinPath extensionUri [ "media"; "style.css" ]))
    webview.html <- buildHtml webview nonce scriptUri styleUri

    Vscode.readFile document.uri
    |> Vscode.thenDo (fun (bytes: byte[]) ->
        let payload = parsePayload kind bytes
        webview.postMessage (createObj [ "type" ==> "render"; "payload" ==> payload ]) |> ignore)

/// 拡張機能の有効化。
let activate (context: Vscode.ExtensionContext) : unit =
    let extensionUri = context.extensionUri

    let registerViewer (viewType: string) (kind: string) =
        let provider =
            { new Vscode.CustomReadonlyEditorProvider with
                member _.openCustomDocument(uri, _openContext, _token) = Vscode.makeDocument uri
                member _.resolveCustomEditor(document, panel, _token) = render extensionUri kind document panel }
        context.subscriptions.Add(Vscode.registerCustomEditorProvider viewType provider true)

    registerViewer "openxml-viewer.spreadsheet" "spreadsheet"
    registerViewer "openxml-viewer.document" "document"
    registerViewer "openxml-viewer.presentation" "presentation"

    context.subscriptions.Add(
        Vscode.registerCommand "openxml-viewer.openPreview" (fun _ ->
            match Vscode.activeUri () with
            | Some uri ->
                let lower = (Vscode.uriToString uri).ToLower()
                let viewType =
                    if lower.EndsWith ".xlsx" then "openxml-viewer.spreadsheet"
                    elif lower.EndsWith ".docx" then "openxml-viewer.document"
                    elif lower.EndsWith ".pptx" then "openxml-viewer.presentation"
                    else ""
                if viewType <> "" then
                    Vscode.executeCommandWith "vscode.openWith" (box uri) (box viewType)
            | None -> ())
    )

    context.subscriptions.Add(
        Vscode.registerCommand "openxml-viewer.reload" (fun _ ->
            Vscode.executeCommand "workbench.action.webview.reloadWebviewAction")
    )

/// 拡張機能の無効化。
let deactivate () : unit = ()

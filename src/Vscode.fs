/// VS Code 拡張 API への最小限のバインディング。
/// 外部のバインディングパッケージに依存せず、Fable.Core のインターオップで必要な部分だけを定義する。
module OpenXmlViewer.Vscode

open Fable.Core
open Fable.Core.JsInterop

// ---------------------------------------------------------------------------
// 型 (必要な部分のみ)
// ---------------------------------------------------------------------------

type Uri =
    interface
    end

type Webview =
    abstract html: string with get, set
    abstract options: obj with get, set
    abstract cspSource: string
    abstract asWebviewUri: Uri -> Uri
    abstract postMessage: obj -> JS.Promise<bool>
    abstract onDidReceiveMessage: (obj -> unit) -> obj

type WebviewPanel =
    abstract webview: Webview
    abstract title: string with get, set

type CustomDocument =
    abstract uri: Uri

/// VS Code の Memento (キー値ストア)。列幅・行高の表示設定を保存する。
type Memento =
    abstract get: string -> obj
    abstract update: string * obj -> JS.Promise<unit>

type ExtensionContext =
    abstract subscriptions: ResizeArray<obj>
    abstract extensionUri: Uri
    abstract workspaceState: Memento

/// CustomReadonlyEditorProvider のうち本拡張で実装するメンバー。
type CustomReadonlyEditorProvider =
    abstract openCustomDocument: Uri * obj * obj -> CustomDocument
    abstract resolveCustomEditor: CustomDocument * WebviewPanel * obj -> unit

// ---------------------------------------------------------------------------
// API インポート
// ---------------------------------------------------------------------------

[<Import("window", "vscode")>]
let private window: obj = jsNative

[<Import("commands", "vscode")>]
let private commands: obj = jsNative

[<Import("workspace", "vscode")>]
let private workspace: obj = jsNative

[<Import("Uri", "vscode")>]
let private uriApi: obj = jsNative

// ---------------------------------------------------------------------------
// ヘルパー
// ---------------------------------------------------------------------------

[<Emit("$0 == null")>]
let private isNullish (x: obj) : bool = jsNative

[<Emit("$0.toString()")>]
let uriToString (uri: Uri) : string = jsNative

[<Emit("$0.joinPath($1, ...$2)")>]
let private joinPathRaw (uriApi: obj) (baseUri: Uri) (parts: string[]) : Uri = jsNative

/// 拡張機能内のリソース Uri を組み立てる。
let joinPath (baseUri: Uri) (parts: string list) : Uri = joinPathRaw uriApi baseUri (Array.ofList parts)

/// ワークスペースのファイルシステム経由でファイルを読み込む。
[<Emit("$0.fs.readFile($1).then(function (bytes) {\n  function normalize(value) { return Array.from(value instanceof ArrayBuffer ? new Uint8Array(value) : value); }\n  function hasEocd(value) {\n    for (var i = value.length - 22; i >= Math.max(0, value.length - 22 - 65535); i--) {\n      if (value[i] === 0x50 && value[i + 1] === 0x4b && value[i + 2] === 0x05 && value[i + 3] === 0x06) return true;\n    }\n    return false;\n  }\n  var normalized = normalize(bytes);\n  if (hasEocd(normalized) || !$1 || $1.scheme !== 'file') return normalized;\n  try { return normalize(require('fs').readFileSync($1.fsPath)); } catch (_) { return normalized; }\n})")>]
let private readFileRaw (workspace: obj) (uri: Uri) : JS.Promise<byte[]> = jsNative

let readFile (uri: Uri) : JS.Promise<byte[]> = readFileRaw workspace uri

[<Emit("$1.then($0)")>]
let thenDo (callback: 'T -> unit) (promise: JS.Promise<'T>) : unit = jsNative

[<Emit("$0.executeCommand($1)")>]
let private exec0 (commands: obj) (command: string) : unit = jsNative

[<Emit("$0.executeCommand($1, $2, $3)")>]
let private exec2 (commands: obj) (command: string) (a: obj) (b: obj) : unit = jsNative

/// 引数なしのコマンドを実行する。
let executeCommand (command: string) : unit = exec0 commands command

/// 2 引数のコマンドを実行する。
let executeCommandWith (command: string) (a: obj) (b: obj) : unit = exec2 commands command a b

/// 現在アクティブなテキストエディタの Uri を取得する。
let activeUri () : Uri option =
    let editor = window?activeTextEditor
    if isNullish editor then None else Some(editor?document?uri)

/// カスタムエディタプロバイダーを登録する。
let registerCustomEditorProvider (viewType: string) (provider: CustomReadonlyEditorProvider) (retain: bool) : obj =
    let options =
        createObj [ "webviewOptions" ==> createObj [ "retainContextWhenHidden" ==> retain ] ]
    window?registerCustomEditorProvider (viewType, box provider, options)

/// コマンドを登録する。
let registerCommand (commandId: string) (handler: obj -> unit) : obj =
    commands?registerCommand (commandId, handler)

/// CustomDocument を生成する。
let makeDocument (uri: Uri) : CustomDocument =
    createObj [ "uri" ==> uri; "dispose" ==> (fun () -> ()) ] |> unbox

/// セキュリティ用の nonce (一度きりのトークン) を生成する。
let nonce () : string =
    let chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
    System.String(Array.init 32 (fun _ -> chars.[int (JS.Math.random () * float chars.Length)]))

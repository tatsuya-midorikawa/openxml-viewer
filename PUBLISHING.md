# 公開ガイド (PUBLISHING)

OpenXML Viewer を [Visual Studio Marketplace](https://marketplace.visualstudio.com/vscode) に公開・更新する手順をまとめます。
ビルド／デバッグ手順は [DEVELOPMENT.md](./DEVELOPMENT.md) を参照してください。

---

## 目次

- [前提環境](#前提環境)
- [全体の流れ](#全体の流れ)
- [1. パブリッシャーを作成する](#1-パブリッシャーを作成する)
- [2. Personal Access Token (PAT) を発行する](#2-personal-access-token-pat-を発行する)
- [3. vsce でログインする](#3-vsce-でログインする)
- [4. 公開前チェックリスト](#4-公開前チェックリスト)
- [5. パッケージング（.vsix の生成と確認）](#5-パッケージングvsix-の生成と確認)
- [6. 公開する](#6-公開する)
- [7. バージョンを上げて更新する](#7-バージョンを上げて更新する)
- [8. Open VSX への公開（任意）](#8-open-vsx-への公開任意)
- [9. GitHub Actions での自動公開（任意）](#9-github-actions-での自動公開任意)
- [トラブルシューティング](#トラブルシューティング)

---

## 前提環境

| ツール | バージョン | 用途 |
| --- | --- | --- |
| [Node.js](https://nodejs.org/) | `18` 以降 | `@vscode/vsce` の実行 |
| [.NET SDK](https://dotnet.microsoft.com/) | `10.0` 以降 | F# / Fable のビルド |
| [@vscode/vsce](https://github.com/microsoft/vscode-vsce) | `3.0` 以降 | パッケージング・公開（`devDependencies` に同梱） |

> [!NOTE]
> `vsce` はリポジトリの `devDependencies` に含まれているため、`npm install` 後は `npx vsce` または npm スクリプト経由で利用できます。グローバルインストール（`npm i -g @vscode/vsce`）は必須ではありません。

---

## 全体の流れ

```
publisher 作成 ──▶ PAT 発行 ──▶ vsce login ──▶ チェックリスト確認
      │                                              │
      └────────────────── 初回のみ ──────────────────┘
                                                     ▼
                          npm run package（.vsix 確認） ──▶ npm run publish
```

---

## 1. パブリッシャーを作成する

Marketplace への公開には **パブリッシャー（publisher）** が必要です。`package.json` の `publisher` は `tmidorikawa` を指定済みです。

1. [Azure DevOps](https://dev.azure.com/) のアカウントを用意する（Microsoft アカウントでサインイン）。
2. [Marketplace 管理ページ](https://marketplace.visualstudio.com/manage) を開く。
3. **Create publisher** から、ID を `tmidorikawa` として作成する（`package.json` の `publisher` と一致させる）。

> [!IMPORTANT]
> パブリッシャー ID は後から変更できません。`package.json` の `publisher` と完全に一致している必要があります。

---

## 2. Personal Access Token (PAT) を発行する

`vsce` の認証には Azure DevOps の PAT を使用します。

1. [https://dev.azure.com/](https://dev.azure.com/) → 右上のユーザー設定 → **Personal access tokens**。
2. **New Token** を作成する。
   - **Organization**: `All accessible organizations` を選択。
   - **Expiration**: 任意（最大 1 年）。
   - **Scopes**: `Custom defined` → **Marketplace** の **Manage** を有効化。
3. 生成されたトークン文字列を安全に控える（再表示不可）。

> [!WARNING]
> PAT はパスワード相当の秘密情報です。リポジトリやチャットに貼り付けないでください。漏えい時は Azure DevOps で即時失効させてください。

---

## 3. vsce でログインする

```bash
npx vsce login tmidorikawa
```

プロンプトに PAT を入力するとログインできます。CI 環境では `VSCE_PAT` 環境変数で渡す方法（[手順 9](#9-github-actions-での自動公開任意)）を推奨します。

---

## 4. 公開前チェックリスト

| 項目 | 状態 | 確認方法 |
| --- | --- | --- |
| `name` / `displayName` / `description` | ✅ | [package.json](./package.json) |
| `publisher` がパブリッシャー ID と一致 | ✅ | `tmidorikawa` |
| `version`（SemVer） | ✅ | 公開のたびに更新（[手順 7](#7-バージョンを上げて更新する)） |
| `repository` / `bugs` / `homepage` | ✅ | [package.json](./package.json) |
| `icon`（128×128 以上 PNG） | ✅ | [media/icon.png](./media/icon.png) |
| `categories` / `keywords` | ✅ | 検索性向上 |
| `engines.vscode` | ✅ | `^1.90.0` |
| `README.md` | ✅ | Marketplace 説明文として表示 |
| `CHANGELOG.md` | ✅ | Marketplace の更新履歴タブに表示 |
| `LICENSE` | ✅ | MIT |
| `.vscodeignore` | ✅ | ソース・仕様書を除外 |

> [!TIP]
> 公開前にバージョンへ合わせて [CHANGELOG.md](./CHANGELOG.md) を更新してください。Marketplace のリスティングに反映されます。

---

## 5. パッケージング（.vsix の生成と確認）

公開前に必ずローカルで `.vsix` を生成し、同梱内容を確認します。

```bash
# Fable → esbuild ビルド後に .vsix を生成
npm run package

# 同梱されるファイル一覧を確認（公開はしない）
npx vsce ls
```

生成された `openxml-viewer-<version>.vsix` をローカルへインストールして動作確認します。

```bash
code --install-extension openxml-viewer-<version>.vsix
```

> [!IMPORTANT]
> `dist/` と `media/`（`icon.png` 含む）のみが含まれ、`src/`・`build/`・`ECMA-376/`・`node_modules/` が除外されていることを `npx vsce ls` で確認してください。

---

## 6. 公開する

```bash
# ビルドして Marketplace へ公開
npm run publish
```

`npm run publish` は `npm run build` の後に `vsce publish` を実行します。CI など PAT を直接渡す場合:

```bash
npx vsce publish -p <PAT>
```

公開後は [Marketplace 管理ページ](https://marketplace.visualstudio.com/manage) または拡張機能の公開 URL で反映を確認します。

```
https://marketplace.visualstudio.com/items?itemName=tmidorikawa.openxml-viewer
```

> [!NOTE]
> 公開直後はインデックス反映に数分かかる場合があります。

---

## 7. バージョンを上げて更新する

`vsce` はバージョンを自動インクリメントして公開できます。

```bash
npx vsce publish patch   # 0.0.1 → 0.0.2
npx vsce publish minor   # 0.0.1 → 0.1.0
npx vsce publish major   # 0.0.1 → 1.0.0
```

手動運用の場合は次の順で更新します。

1. [CHANGELOG.md](./CHANGELOG.md) に変更点を追記。
2. `package.json` の `version` を SemVer で更新。
3. `npm run publish` を実行。
4. Git タグ（例: `v0.0.2`）を打って push。

---

## 8. Open VSX への公開（任意）

VS Code 派生エディター（VSCodium / Cursor 等）にも配布する場合は [Open VSX Registry](https://open-vsx.org/) への公開を推奨します。

```bash
npm i -g ovsx
ovsx publish openxml-viewer-<version>.vsix -p <OPENVSX_TOKEN>
```

---

## 9. GitHub Actions での自動公開（任意）

タグ push をトリガーに自動公開する例。PAT はリポジトリの **Secrets**（`VSCE_PAT`）に登録します。

```yaml
name: publish
on:
  push:
    tags: ['v*']
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: '20' }
      - run: dotnet tool restore
      - run: npm ci
      - run: npx vsce publish -p ${{ secrets.VSCE_PAT }}
```

---

## トラブルシューティング

| 症状 | 対処 |
| --- | --- |
| `Missing publisher name` | `package.json` の `publisher` を設定し、パブリッシャー ID と一致させる。 |
| `401 Unauthorized` | PAT のスコープ（Marketplace: Manage）と有効期限、対象 Organization を確認。 |
| `Make sure to edit the README` | プレースホルダーのままの README を実内容へ更新する。 |
| `.vsix` にソースが含まれる | [.vscodeignore](./.vscodeignore) の除外設定を確認。 |
| `icon ... must be ... png` | アイコンは PNG（128×128 以上）。[media/icon.png](./media/icon.png) を指定。 |
| バージョン重複で公開不可 | 同一バージョンは再公開不可。`version` を上げて再実行。 |

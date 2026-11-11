# ClaudeCost for Windows

Claude Code の利用料金の推計を、Windows の通知領域（タスクバー右下）に表示するアプリです。集計の方法と単価表は [Mac 版](https://github.com/Orihara-Don/ClaudeCost)と同じです。

- 金額は Claude Code のローカルログ（`%USERPROFILE%\.claude\projects`）のトークン数 × 公開単価で計算した**推計値**です。実際の請求額とはずれることがあります。
- 集計するのは、そのPCで動いた Claude Code の利用分だけです。WSL 内で動かした分も集計します（[WSL](#wsl)）。
- 外部とは通信しません。
- Mac 版にあるウィジェットはありません。

## 必要なもの

- Windows 10 / 11（x64）
- .NET のインストールは不要です。

## インストール

1. [Releases](https://github.com/Orihara-Don/ClaudeCost-for-Windows/releases/latest) から `ClaudeCost.exe` をダウンロードします。
2. 好きな場所（例: `%LOCALAPPDATA%\Programs\ClaudeCost`）に置いて開きます。

更新するときは、通知領域のアイコンを右クリックして「終了」してから、新しい exe で置き換えて開きます。集計結果と設定はそのまま引き継がれます。

## 自分でビルドする場合

1. [.NET 10 SDK](https://dotnet.microsoft.com/download) を入れます。

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

2. ソースを取得して、フォルダに移動します。

   ```powershell
   git clone https://github.com/Orihara-Don/ClaudeCost-for-Windows.git
   cd ClaudeCost-for-Windows
   ```

3. exe を作ります。.NET を入れていないPCでも動く、1ファイルの exe ができます。

   ```powershell
   dotnet publish ClaudeCost.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\win-x64
   ```

4. `publish\win-x64\ClaudeCost.exe` を好きな場所（例: `%LOCALAPPDATA%\Programs\ClaudeCost`）に置いて開きます。

Mac でも `brew install dotnet` で SDK を入れれば、同じコマンドで Windows 用の exe を作れます。

テストは `dotnet test ClaudeCost.Core.Tests` で実行できます。

### リリースの作り方

`v1.2.3` の形のタグを push すると、GitHub Actions（`.github/workflows/release.yml`）がテストと exe のビルドをして、リリースに `ClaudeCost.exe` を添付します。exe のバージョンはタグから付きます。

```powershell
git tag v1.2.3
git push origin v1.2.3
```

## 使い方

- 通知領域のアイコンにマウスを乗せると、今日と今月の金額が出ます。
- アイコンをクリックすると、内訳のパネルが開きます。グラフの棒にマウスを乗せると、その日の金額とトークン数が出ます。
- 右クリックすると、更新、設定、終了のメニューが出ます。
- アイコンが「^」の中に隠れている場合は、タスクバーへドラッグするか、Windows の「設定」>「個人用設定」>「タスクバー」>「その他のシステム トレイ アイコン」でオンにしてください。
- 月の予算を超えそうなときは、アイコンが赤くなります。

署名していない exe のため、ほかの人から受け取った exe を初めて開くと、Windows が警告（SmartScreen）を出すことがあります。その場合は「詳細情報」>「実行」で開けます。

## 設定で確認してほしいこと

- **リージョナル料金（×1.1）:** 既定でオンです。Bedrock の global エンドポイントを使っている場合や、Bedrock を使っていない場合はオフにしてください。
- **単価:** Anthropic の公開価格（`ClaudeCost.Core/prices.json`）を exe に入れています。設定の「prices.json を開く」から上書きできます。
- **USD/JPY レート:** 手入力です。自動では更新しません。

## WSL

WSL 内で動かした Claude Code のログ（`~/.claude/projects` と `~/.config/claude/projects`）も、`\\wsl.localhost\<ディストリビューション>\home\*\` 経由で読んで合算します。

- 対象は起動中のディストリビューションだけです。停止中のものにアクセスすると起動してしまうため読みません（停止中はログも増えません）。前に読んだ分は残ります。
- WSL 側はファイルの変更通知が届かないため、1分ごとに確認します。
- パネルの「環境別」に Windows と WSL ごとの金額が出ます。WSL のプロジェクト名には「（WSL）」が付きます。
- 設定の「WSL 内の Claude Code のログも集計する」でオフにできます。

## データの場所

| ファイル | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\ClaudeCost\state.json` | 日別のトークン数、読み取り位置 |
| `%LOCALAPPDATA%\ClaudeCost\settings.json` | 設定 |
| `%LOCALAPPDATA%\ClaudeCost\prices.json` | 単価の上書き（作成した場合のみ） |
| `%LOCALAPPDATA%\ClaudeCost\error.log` | 予期しないエラーの記録（起きた場合のみ） |

会話の本文は保存しません。アンインストールするときは、設定で「Windows へのサインイン時に起動」をオフにしてからアプリを終了し、exe と上記のフォルダを削除してください。

## ライセンス

[MIT License](LICENSE)

Anthropic の公式アプリではありません。Issue と Pull Request は受け付けていません。

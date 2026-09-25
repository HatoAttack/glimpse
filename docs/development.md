# 開発者向けメモ

## 必要なもの

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```
dotnet run --project src/ImageViewer.App        # 起動
dotnet run --project tests/ImageViewer.Tests    # テスト
```

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/ImageViewer.Core` | UI に依存しない中核（画像読み込み・対応形式・コマンドの仕組み・編集処理） |
| `src/ImageViewer.App` | WinForms の UI（ツールバー・一覧・1枚表示・メニュー・ダイアログ・配色） |
| `tests/ImageViewer.Tests` | 動作テスト |

.NET 8 + WinForms + ImageSharp。

アイコンの元は `docs/images/glimpse-icon.svg`（40px 以上）と、線を太くした `docs/images/glimpse-icon-small.svg`（16〜32px。タスクバーなど）。
どちらも 512px の PNG にしてから縮小し、1 つの `src/ImageViewer.App/Glimpse.ico` にまとめる（exe とウィンドウのアイコン）。

## コマンドの仕組み

編集機能はすべて `IImageCommand`（`src/ImageViewer.Core/Commands`）として実装し、`MainForm.RegisterCommands` で登録する。
☰ メニュー・右クリックメニュー・ショートカット・アドレスバーのコマンド検索は登録済みコマンドから自動で組み立てられ、
選択枚数に応じて実行可否（例: 連結は2枚以上、切り抜きは1枚）が切り替わる。

```csharp
public sealed class CopyPathsCommand : ImageCommandBase
{
    public override string Id => "file.copyPaths";
    public override string Name => "パスをコピー";
    public override string Category => "ファイル";          // メニューのグループ
    public override string? DefaultShortcut => "Ctrl+Shift+C";
    protected override int MinSelection => 1;              // 選択枚数の条件
    public override Task ExecuteAsync(CommandContext context) { ... }
}
```

## 設定・保存データ

`%LOCALAPPDATA%\ima-ge-viewer\` に保存する（名前を Glimpse に変える前からの場所のまま）。どれも小さく上限があり、壊れていたら初期値で動く（起動できなくならない）。

| ファイル | 内容 |
|---|---|
| `settings.json` | 設定（ホームフォルダ・サムネイルの大きさ・配色・サイドバーと詳細パネルの表示・フォルダジャンプ・リサイズと連結で前回使った設定） |
| `orders\*.json` | 手動の並び順（フォルダごと、最大 1000 件） |
| `folder-index.bin` | フォルダジャンプの索引（フォルダ名と親の番号だけ。最大 30 万件、ユーザーフォルダで数十 KB〜数 MB） |
| `visits.json` | 開いたフォルダの記録（最大 500 件） |

書き込みは一時ファイルに書いてから差し替えるので、途中で落ちても壊れたファイルは残らない。

## 軽さの工夫

- サムネイルは「表示中 → 下に 1 画面 → 上に 1 画面」の分だけ要求し、スクロールで外れた要求は捨てる
- まずエクスプローラーと同じシェルのサムネイル（thumbcache）を使い、取れないときだけ自前で縮小読み込み
- メモリ上のサムネイルは合計 128MB を上限に古いものから破棄（1500 枚のフォルダを端までスクロールしてもプロセス全体で約 150MB）
- 縮小デコードが効かない形式（PNG / WEBP 等）は同時に 2 枚までしか展開しない

## リリースの手順

GitHub でリリースを公開すると、GitHub Actions（`.github/workflows/release.yml`）がテストしてから
`Glimpse.exe`（単一ファイル・インストール不要）を作り、そのリリースに添付する（数分かかる）。

1. main に変更を入れる
2. リリースを作る（タグ `v0.1.4` のように。説明文も書く）
   - `gh release create v0.1.4 --target main --title v0.1.4 --notes "…"`、または GitHub の Releases 画面から
3. Actions の「リリース」が終わると、リリースに `Glimpse.exe` とチェックサム `Glimpse.exe.sha256`（アプリの自動更新が使う）が付く。テストが通らなければ付かない（Actions で理由を確認）
   - 名前を変える前のバージョン（v0.1.5 まで）の自動更新のために、同じ中身の `ImageViewer.exe`・`ImageViewer.exe.sha256` も付く

バージョン番号はタグから付く（`v0.1.4` → 0.1.4）。手元で作るときは次のとおり（出力は `publish` フォルダ）:

```
dotnet publish src/ImageViewer.App/ImageViewer.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=0.1.4 -o publish
```

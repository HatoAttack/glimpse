# 開発者向けメモ

## 必要なもの

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（10.0.100 以上の 10.0 の SDK）

使う SDK は `global.json` で決めている（`"version": "10.0.100"`、`"rollForward": "latestFeature"`）。
10.0 の SDK のうち、入っている最新のもの（機能バンド・パッチは新しくてよい）を使い、.NET 11 など別のメジャーバージョンの SDK は使わない。
10.0 の SDK が入っていないと、`dotnet` のコマンドは「A compatible .NET SDK was not found」と、`global.json` が求める版を表示して止まる。そのときは上のリンクから 10.0 の SDK を入れる。
入っている SDK は `dotnet --list-sdks`、実際に選ばれる SDK は `dotnet --version` で確かめられる。GitHub Actions も同じ `global.json` で SDK を選ぶ。

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

.NET 10 + WinForms + SkiaSharp（画像の読み書きと編集）。

アイコンの元は `docs/images/glimpse-icon.svg`（40px 以上）と、線を太くした `docs/images/glimpse-icon-small.svg`（16〜32px。タスクバーなど）。
どちらも 512px の PNG にしてから縮小し、1 つの `src/ImageViewer.App/Glimpse.ico` にまとめる（exe とウィンドウのアイコン）。

## コマンドの仕組み

編集機能はすべて `IImageCommand`（`src/ImageViewer.Core/Commands`）として実装し、`MainForm.RegisterCommands` で登録する。
☰ メニュー・右クリックメニュー・ショートカット・アドレスバーのコマンド検索は登録済みコマンドから自動で組み立てられ（右クリックメニューは項目を絞り、回転はサブメニューにまとめる。`MainForm.BuildContextMenu`）、
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
| `settings.json` | 設定（ホームフォルダ・サムネイルの大きさ・配色・サイドバーと詳細パネルの表示・フォルダジャンプ・リサイズ・連結・まとめて補正で前回使った設定・保存の画質・前回の補正） |
| `orders\*.json` | 手動の並び順（フォルダごと、最大 1000 件） |
| `folder-index.bin` | フォルダジャンプの索引（フォルダ名と親の番号だけ。最大 30 万件、ユーザーフォルダで数十 KB〜数 MB） |
| `visits.json` | 開いたフォルダの記録（最大 500 件） |

書き込みは一時ファイルに書いてから差し替えるので、途中で落ちても壊れたファイルは残らない。

## 軽さの工夫

- サムネイルは「表示中 → 下に 1 画面 → 上に 1 画面」の分だけ要求し、スクロールで外れた要求は捨てる
- まずエクスプローラーと同じシェルのサムネイル（thumbcache）を使い、取れないときだけ自前で縮小読み込み
- メモリ上のサムネイルは合計 128MB を上限に古いものから破棄（1500 枚のフォルダを端までスクロールしてもプロセス全体で約 150MB）
- 縮小デコードが効かない形式（PNG / WEBP 等）は同時に 2 枚までしか展開しない

## PR の検証

すべての PR と main への push で、GitHub Actions（`.github/workflows/ci.yml`、ワークフロー名「検証」）が Release ビルドと既存テストを実行する。
チェック名は `build-and-test`。main のルールセットで必須のチェックに指定しているので、通らない PR はマージできない（ジョブ名を変えると必須チェックと合わなくなるので変えない）。

失敗したときは PR の「Checks」から Actions のログを開く。ビルドエラーは「ビルド」の手順に、テストの失敗は「テスト」の手順に `NG` で始まる行として出る。
手元で同じことをするには次のとおり:

```
dotnet restore ImageViewer.sln --locked-mode
dotnet build ImageViewer.sln -c Release --no-restore
dotnet run --project tests/ImageViewer.Tests -c Release --no-build
```

## 依存パッケージの固定と更新

NuGet のパッケージは、プロジェクトに具体的な版を書いたうえで、各プロジェクトの `packages.lock.json` に推移的な依存も含めた版とハッシュを記録して固定している
（`Directory.Build.props` の `RestorePackagesWithLockFile`）。ロックファイルには、リリースの exe（`-r win-x64` の自己完結ビルド）で使う win-x64 用の分も入っている。

- GitHub Actions では locked mode で復元する。プロジェクトの定義とロックファイルが合わないと、「依存の復元」の手順が `NU1004` で失敗する
- 手元ではロックファイルを書き換えてよい通常の復元になる。プロジェクトの依存を変えたら、書き換わったロックファイルも一緒にコミットする
- 単一ファイルの解析器（`Microsoft.NET.ILLink.Tasks`）も版を固定している。SDK に任せると、SDK の版に合わせて publish のときだけ足され、ロックファイルと合わなくなるため（`Directory.Build.props` に理由を書いている）

### 更新のしかた

更新は Dependabot（`.github/dependabot.yml`）が週に 1 回、NuGet のパッケージと GitHub Actions について PR を作る。
NuGet の更新 PR はロックファイルも書き換えている。手動で更新するときは次のとおり:

1. `.csproj` か `Directory.Build.props` の `Version` を書き換える
2. ロックファイルを作り直す:
   ```
   dotnet restore ImageViewer.sln --force-evaluate
   ```
3. CI と同じ確認をする（locked mode で復元できること、ビルドとテスト）:
   ```
   dotnet restore ImageViewer.sln --locked-mode
   dotnet build ImageViewer.sln -c Release --no-restore
   dotnet run --project tests/ImageViewer.Tests -c Release --no-build
   ```
4. リリースと同じ自己完結の exe が作れることを確かめる（「リリースの手順」のコマンド）。PR では「リリース」のワークフローが同じことを試しに行う
5. `.csproj` とロックファイルをいっしょにコミットする

どの更新 PR も「検証」の `build-and-test` が通ってからマージする。SkiaSharp・MetadataExtractor など画像の読み書きに関わる更新は、[manual-test.md](manual-test.md) で表示と保存も確かめる。

## 手動確認

画面・エクスプローラーとのやり取り・更新など、自動テストで確かめられないことは [manual-test.md](manual-test.md) の表で確かめる。
.NET の更新や画像ライブラリの置き換えなど、アプリ全体に関わる変更では、変更の前と後の版で同じ表を実施して比べる。

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

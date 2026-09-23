# 画像ビューア（仮称）

サムネイル一覧で画像を眺めながら、選択した画像（複数可）にメニューやショートカットキーから
リサイズ・切り抜き・連結などの編集を行う Windows 用画像ビューア。

[image-sizechange](https://github.com/HatoAttack/image-sizechange) の後継として開発中。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/ImageViewer.Core` | UI に依存しない中核（画像読み込み・対応形式・コマンドの仕組み・編集処理） |
| `src/ImageViewer.App` | WinForms の UI（一覧・1枚表示・メニュー・ダイアログ） |
| `tests/ImageViewer.Tests` | 動作テスト（`dotnet run --project tests/ImageViewer.Tests`） |

.NET 8 + WinForms + ImageSharp。

## コマンドの仕組み

編集機能はすべて `IImageCommand`（`src/ImageViewer.Core/Commands`）として実装し、`MainForm.RegisterCommands` で登録する。
メインメニュー・右クリックメニュー・ショートカットは登録済みコマンドから自動で組み立てられ、
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

## 対応形式

| 区分 | 形式 |
|---|---|
| どの PC でも読める（ImageSharp） | JPG / PNG / GIF / WEBP / BMP / TIFF / TGA / ICO / CUR / PBM 系 / QOI |
| Windows の拡張機能があれば読める（WIC） | HEIC / HEIF / AVIF / JPEG XL / 各社 RAW / JPEG XR / DDS など |

WIC の対応形式は起動時にその PC のデコーダを列挙して決まる（メニューの「ヘルプ → 対応形式」で確認できる）。
HEIC は「HEIF 画像拡張機能」＋「HEVC ビデオ拡張機能」、AVIF は「AV1 Video Extension」、RAW は「Raw Image Extension」が必要。

## 軽さの工夫

- サムネイルは「表示中 → 下に 1 画面 → 上に 1 画面」の分だけ要求し、スクロールで外れた要求は捨てる
- まずエクスプローラーと同じシェルのサムネイル（thumbcache）を使い、取れないときだけ自前で縮小読み込み
- メモリ上のサムネイルは合計 128MB を上限に古いものから破棄（1500 枚のフォルダを端までスクロールしてもプロセス全体で約 150MB）
- 縮小デコードが効かない形式（PNG / WEBP 等）は同時に 2 枚までしか展開しない

## 現状と予定

- [x] フォルダを開く（Ctrl+O・フォルダのドロップ・起動引数）
- [x] コマンドの仕組み（メニュー / 右クリック / ショートカット、複数選択）
- [x] 読み込み層 `ImageLoader`（ImageSharp の縮小デコード + WIC 経由で HEIC / AVIF / RAW / JPEG XL をベストエフォート対応、回転補正・sRGB 変換）
- [x] サムネイル生成 `ThumbnailService`（シェルのサムネイル → 縮小デコード、表示中優先の待ち行列、上限付きメモリキャッシュ）
- [x] サムネイルグリッド（見えている分＋前後 1 画面だけ生成、クリック / Ctrl / Shift / キー操作 / ドラッグ範囲選択で複数選択）
- [x] チェック（¥ で付け外し、Shift+¥ で選択中にチェック、Ctrl+¥ でチェックした画像を選択）
- [ ] 手動の並べ替え（ドラッグ、並び順の保存）
- [ ] 連番リネーム（プレビュー付き、元に戻す）
- [ ] 1枚表示（画面サイズでデコード、前後の先読み、GIF / WEBP アニメ再生）
- [ ] image-sizechange からリサイズ・切り抜き・連結を移植

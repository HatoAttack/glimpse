// Core・ショートカット解釈・画像読み込み・サムネイルの動作確認（GUI なし）
using System.Text;
using System.Windows.Forms;
using ImageViewer.App;
using ImageViewer.Core.Commands;
using ImageViewer.Core.Imaging;

Console.OutputEncoding = Encoding.UTF8;
bool failed = false;

void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "OK  " : "NG  ") + msg);
    if (!cond) failed = true;
}

string[] Paths(int n) => Enumerable.Range(0, n).Select(i => $@"C:\img{i}.jpg").ToArray();

// ---- 選択枚数による実行可否 ----
var single = new TestCommand("t.single", "Ctrl+E", min: 1, max: 1);
var multi = new TestCommand("t.multi", "Ctrl+Shift+C", min: 2, max: int.MaxValue);
Check(!single.CanExecute(Paths(0)), "1枚限定: 0枚では実行不可");
Check(single.CanExecute(Paths(1)), "1枚限定: 1枚で実行可");
Check(!single.CanExecute(Paths(2)), "1枚限定: 2枚では実行不可");
Check(!multi.CanExecute(Paths(1)), "2枚以上: 1枚では実行不可");
Check(multi.CanExecute(Paths(5)), "2枚以上: 5枚で実行可");

// ---- レジストリ ----
var reg = new CommandRegistry();
reg.Register(single);
reg.Register(multi);
bool dupThrown = false;
try { reg.Register(new TestCommand("t.single", null)); } catch (InvalidOperationException) { dupThrown = true; }
Check(dupThrown, "ID 重複の登録は例外");
Check(reg.Find("t.multi") == multi, "ID で検索できる");
Check(reg.FindShortcutConflicts().Count == 0, "ショートカット重複なし");

reg.OverrideShortcut("t.multi", "ctrl + e");
var conflicts = reg.FindShortcutConflicts();
Check(conflicts.Count == 1 && conflicts[0].Ids.Count == 2, "上書きで重複したら検出（大文字小文字・空白無視）");
reg.OverrideShortcut("t.multi", null);
Check(reg.ShortcutOf(multi) == null && reg.FindShortcutConflicts().Count == 0, "null で上書きするとショートカットなし");

reg.Register(new TestCommand("t.other", null, category: "編集"));
Check(reg.ByCategory().Select(g => g.Key).SequenceEqual(new[] { "画像", "編集" }), "カテゴリは登録順にまとまる");

// ---- ショートカット文字列の解釈 ----
Check(Shortcuts.Parse("Ctrl+Shift+C") == (Keys.Control | Keys.Shift | Keys.C), "Ctrl+Shift+C");
Check(Shortcuts.Parse("alt + 1") == (Keys.Alt | Keys.D1), "数字キー・小文字・空白");
Check(Shortcuts.Parse("F5") == Keys.F5, "ファンクションキー");
Check(Shortcuts.Parse("Del") == Keys.Delete, "別名 Del");
Check(Shortcuts.Parse("Ctrl") == Keys.None, "修飾キーのみは無効");
Check(Shortcuts.Parse("Ctrl+Hoge") == Keys.None, "不明なキーは無効");
Check(Shortcuts.Parse(null) == Keys.None, "null は無効");
Check(Shortcuts.Parse("Ctrl+Yen") == (Keys.Control | Keys.Oem5) && Shortcuts.Parse("¥") == Keys.Oem5, "¥ キー（Yen / ¥）");
Check(Shortcuts.Parse("^") == Keys.Oem7 && Shortcuts.Parse("Caret") == Keys.Oem7, "^ キー（^ / Caret）");

// ---- 対応形式の判定・列挙 ----
Check(ImageFormats.IsSupported(@"C:\a\B.JPG") && ImageFormats.IsSupported("x.webp"), "拡張子の大文字小文字を無視");
Check(!ImageFormats.IsSupported("x.txt") && !ImageFormats.IsSupported("noext"), "対象外の拡張子");

string dir = Path.Combine(Path.GetTempPath(), "image_viewer_test");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);
foreach (var name in new[] { "b.png", "A.jpg", "c.txt", "d.WEBP" })
    File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 0 });
Directory.CreateDirectory(Path.Combine(dir, "sub.jpg"));
var listed = ImageFormats.ListImages(dir).Select(f => f.Name).ToArray();
Check(listed.SequenceEqual(new[] { "A.jpg", "b.png", "d.WEBP" }), $"列挙: 画像のみ・名前順・フォルダ除外 ({string.Join(",", listed)})");
Directory.Delete(dir, true);

// ---- 画像読み込み ----
Directory.CreateDirectory(dir);
await LoaderTests.RunAsync(Check, dir);
Directory.Delete(dir, true);

// ---- サムネイル・グリッド ----
Directory.CreateDirectory(dir);
ThumbnailTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- 並び順 ----
OrderingTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- 名前の変更 ----
RenameTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- リサイズ・形式変換 / 切り抜き / 連結 ----
EditingTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- ファイラとしての移動 ----
NavigationTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- フォルダジャンプ ----
JumpTests.Run(Check, dir);
Directory.Delete(dir, true);

// ---- 更新の確認・exe の入れ替え ----
UpdateTests.Run(Check, dir);
Directory.Delete(dir, true);

Console.WriteLine(failed ? "\n失敗あり" : "\nすべて OK");
return failed ? 1 : 0;

sealed class TestCommand(string id, string? shortcut, int min = 1, int max = int.MaxValue, string category = "画像")
    : ImageCommandBase
{
    public override string Id => id;
    public override string Name => id;
    public override string Category => category;
    public override string? DefaultShortcut => shortcut;
    protected override int MinSelection => min;
    protected override int MaxSelection => max;
    public override Task ExecuteAsync(CommandContext context) => Task.CompletedTask;
}

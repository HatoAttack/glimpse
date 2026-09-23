// "Ctrl+Shift+C" 形式の文字列と WinForms の Keys の相互変換
namespace ImageViewer.App;

public static class Shortcuts
{
    /// <summary>解釈できない・修飾キーだけの場合は Keys.None</summary>
    public static Keys Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Keys.None;
        Keys result = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Keys part = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Keys.Control,
                "shift" => Keys.Shift,
                "alt" => Keys.Alt,
                "del" => Keys.Delete,
                "esc" => Keys.Escape,
                "enter" => Keys.Enter,
                // JIS 配列の ¥ キーと US 配列の \ キーはどちらも Oem5
                "yen" or "¥" or "\\" => Keys.Oem5,
                // JIS 配列の ^ キー（US 配列では ' のキー）
                "caret" or "^" => Keys.Oem7,
                _ when raw.Length == 1 && char.IsDigit(raw[0]) => Keys.D0 + (raw[0] - '0'),
                _ => Enum.TryParse<Keys>(raw, ignoreCase: true, out var k) ? k : Keys.None,
            };
            if (part == Keys.None) return Keys.None;
            result |= part;
        }
        // 修飾キーだけのものはショートカットとして使えない
        return (result & Keys.KeyCode) == Keys.None ? Keys.None : result;
    }
}

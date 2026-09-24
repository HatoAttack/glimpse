// 配色（ライト / ダーク / システムに合わせる）
// 色はコードに直接書かず Palette の役割名で使う。配色を増やすときは Palette を足すだけで済むようにする。
// Windows の標準部品（ツリー・スクロールバー・タイトルバー）は、描き直さずに Windows のダーク用の見た目に切り替える
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ImageViewer.App.Theming;

public enum ThemeMode { System, Light, Dark }

/// <summary>画面の色の役割</summary>
public sealed record Palette
{
    public required bool IsDark { get; init; }
    /// <summary>一覧・サイドバーの地</summary>
    public required Color Background { get; init; }
    /// <summary>ツールバー・フッター・メニューの面</summary>
    public required Color Surface { get; init; }
    /// <summary>アドレスバーなど入力欄の地</summary>
    public required Color Field { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    /// <summary>アイコン・ボタンの文字</summary>
    public required Color TextStrong { get; init; }
    /// <summary>補足の文字（件数・情報）</summary>
    public required Color TextSecondary { get; init; }
    /// <summary>さらに控えめな文字（ヒント・見出し）</summary>
    public required Color TextMuted { get; init; }
    public required Color Disabled { get; init; }
    /// <summary>ボタンにマウスを乗せたとき / 押している間</summary>
    public required Color Hover { get; init; }
    public required Color Pressed { get; init; }
    public required Color SelectionFill { get; init; }
    public required Color SelectionBorder { get; init; }
    public required Color SelectionText { get; init; }
    /// <summary>チェックの印（選択の青と区別できる橙）</summary>
    public required Color Check { get; init; }
    /// <summary>チェックの数などの文字</summary>
    public required Color CheckText { get; init; }
    public required Color Danger { get; init; }
    /// <summary>注意（上書きになる など）</summary>
    public required Color Warning { get; init; }
    /// <summary>サムネイルを読み込み中・読めないときの枠</summary>
    public required Color Placeholder { get; init; }

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Background = Hex(0xF7F7F5), Surface = Hex(0xFFFFFF), Field = Hex(0xF0F0ED), Border = Hex(0xE4E4E0),
        Text = Hex(0x1C1C1A), TextStrong = Hex(0x3A3A37), TextSecondary = Hex(0x5F5F5A), TextMuted = Hex(0x8A8A84),
        Disabled = Hex(0xB5B5B0), Hover = Hex(0xEDEDE9), Pressed = Hex(0xE2E2DD),
        SelectionFill = Hex(0xE6ECF7), SelectionBorder = Hex(0x2F6BD8), SelectionText = Hex(0x1D4FA3),
        Check = Hex(0xE87000), CheckText = Hex(0xB35600), Danger = Hex(0xB42318), Warning = Hex(0xB54708), Placeholder = Hex(0xECECE8),
    };

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Background = Hex(0x1B1B1A), Surface = Hex(0x242423), Field = Hex(0x2E2E2C), Border = Hex(0x363633),
        Text = Hex(0xEDEDEA), TextStrong = Hex(0xD6D6D1), TextSecondary = Hex(0xA8A8A2), TextMuted = Hex(0x8F8F89),
        Disabled = Hex(0x5A5A56), Hover = Hex(0x333331), Pressed = Hex(0x3C3C39),
        SelectionFill = Hex(0x1F3354), SelectionBorder = Hex(0x5B8FEA), SelectionText = Hex(0xA9C5F5),
        Check = Hex(0xE87000), CheckText = Hex(0xFFB066), Danger = Hex(0xFF8A7A), Warning = Hex(0xFDB022), Placeholder = Hex(0x2C2C2A),
    };

    private static Color Hex(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
}

public static class Theme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static Palette Current { get; private set; } = Palette.Light;

    /// <summary>配色が変わった（設定を変えた・システムに合わせていて Windows の設定が変わった）</summary>
    public static event EventHandler? Changed;

    private static bool _initialized;

    /// <summary>最初の画面のコンストラクタで呼ぶ（UI スレッドの SynchronizationContext を覚えるため）</summary>
    public static void Initialize(ThemeMode mode)
    {
        Mode = mode;
        Current = Resolve(mode);
        ToolStripManager.Renderer = new ThemedMenuRenderer();
        if (_initialized) return;
        _initialized = true;
        // システムの設定の変化は UI とは別のスレッドで届くことがあるので、画面を作ったスレッドへ回してから切り替える
        var ui = SynchronizationContext.Current;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
            if (ui != null) ui.Post(_ => RefreshIfSystem(), null);
            else RefreshIfSystem();
        };
    }

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        Refresh();
    }

    private static void RefreshIfSystem()
    {
        if (Mode == ThemeMode.System) Refresh();
    }

    private static void Refresh()
    {
        var next = Resolve(Mode);
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Palette Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => Palette.Light,
        ThemeMode.Dark => Palette.Dark,
        _ => SystemPrefersDark() ? Palette.Dark : Palette.Light,
    };

    /// <summary>Windows の「アプリ モードを選ぶ」がダークか（読めなければライト）</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static ThemeMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static string? ToSetting(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => null,
    };

    // ---- Windows の標準部品 ----

    /// <summary>タイトルバーをダーク / ライトに（Windows 10 20H1 以降。古い Windows では何もしない）</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        int dark = Current.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// <summary>スクロールバー・ツリーの開閉の印などを、Windows のダーク用 / 通常の見た目に</summary>
    public static void ApplyNativeTheme(Control control)
    {
        if (!control.IsHandleCreated) return;
        _ = SetWindowTheme(control.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}

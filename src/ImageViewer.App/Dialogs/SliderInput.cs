// スライダーの横に置く、数字を直接打てる欄（スライダーと値を互いに合わせる）
namespace ImageViewer.App.Dialogs;

internal static class SliderInput
{
    /// <summary>
    /// slider と値を合わせる数字の欄を作る。scale はスライダーの 1 目盛りの細かさ（10 ならスライダーの 15 が欄の 1.5）。
    /// 欄の値はスライダーの範囲に収める。スライダーを使えなくすると欄も使えなくなる
    /// </summary>
    public static NumericUpDown Link(TrackBar slider, int scale = 1, int width = 56)
    {
        var box = new NumericUpDown
        {
            Minimum = (decimal)slider.Minimum / scale,
            Maximum = (decimal)slider.Maximum / scale,
            DecimalPlaces = scale switch { 1 => 0, 10 => 1, _ => 2 },
            Increment = 1m / scale,
            Value = (decimal)slider.Value / scale,
            Width = width,
            TextAlign = HorizontalAlignment.Right,
            Enabled = slider.Enabled,
            Margin = new Padding(3, 3, 0, 3),
        };
        slider.ValueChanged += (_, _) =>
        {
            decimal v = (decimal)slider.Value / scale;
            if (box.Value != v) box.Value = v;
        };
        box.ValueChanged += (_, _) =>
        {
            int v = Math.Clamp((int)Math.Round(box.Value * scale, MidpointRounding.AwayFromZero), slider.Minimum, slider.Maximum);
            if (slider.Value != v) slider.Value = v;
        };
        slider.EnabledChanged += (_, _) => box.Enabled = slider.Enabled;
        return box;
    }
}

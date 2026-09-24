// 写真の撮影情報（EXIF）。詳細パネルに出す分だけ。無い項目は null
using System.Globalization;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace ImageViewer.Core.Imaging;

public sealed record PhotoInfo
{
    public DateTime? TakenAt { get; init; }
    /// <summary>メーカーと機種（機種名がメーカー名で始まるときは機種名だけ）</summary>
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public double? FNumber { get; init; }
    /// <summary>シャッター速度（秒）</summary>
    public double? ExposureSeconds { get; init; }
    public int? Iso { get; init; }

    public bool IsEmpty => TakenAt == null && Camera == null && Lens == null && FNumber == null && ExposureSeconds == null && Iso == null;

    /// <summary>"f/2.8 · 1/250 · ISO 200"（無い項目は省く。全部無ければ null）</summary>
    public string? SettingsText
    {
        get
        {
            var parts = new List<string>();
            if (FNumber is double f) parts.Add($"f/{f.ToString("0.#", CultureInfo.InvariantCulture)}");
            if (ExposureSeconds is double s) parts.Add(FormatExposure(s));
            if (Iso is int iso) parts.Add($"ISO {iso}");
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    /// <summary>1 秒未満は "1/250"、それ以上は "2s" / "1.5s"</summary>
    public static string FormatExposure(double seconds) =>
        seconds is > 0 and < 1 ? $"1/{Math.Round(1 / seconds).ToString(CultureInfo.InvariantCulture)}"
        : $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s";

    /// <summary>機種名がメーカー名で始まっていれば機種名だけ（"Canon" + "Canon EOS R5" → "Canon EOS R5"）</summary>
    public static string? CameraName(string? make, string? model)
    {
        make = Clean(make);
        model = Clean(model);
        if (model == null) return make;
        if (make == null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
        // "NIKON CORPORATION" と "NIKON Z 6" のように、メーカー名の最初の語で始まるときも機種名だけ
        string firstWord = make.Split(' ')[0];
        return model.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    /// <summary>ImageSharp で読んだ EXIF から。撮影情報が 1 つも無ければ null</summary>
    public static PhotoInfo? FromExif(ExifProfile? exif)
    {
        if (exif == null) return null;
        string? Text(ExifTag<string> tag) => exif.TryGetValue(tag, out var v) ? Clean(v.Value) : null;

        DateTime? taken = null;
        foreach (var tag in new[] { ExifTag.DateTimeOriginal, ExifTag.DateTimeDigitized })
            if (ParseExifDate(Text(tag)) is DateTime d)
            {
                taken = d;
                break;
            }
        double? fNumber = exif.TryGetValue(ExifTag.FNumber, out var fn) && fn.Value.Denominator != 0 ? fn.Value.ToDouble() : null;
        double? exposure = exif.TryGetValue(ExifTag.ExposureTime, out var et) && et.Value.Denominator != 0 ? et.Value.ToDouble() : null;
        int? iso = exif.TryGetValue(ExifTag.ISOSpeedRatings, out var isoValues) && isoValues.Value is { Length: > 0 } values ? values[0] : null;

        var info = new PhotoInfo
        {
            TakenAt = taken,
            Camera = CameraName(Text(ExifTag.Make), Text(ExifTag.Model)),
            Lens = Text(ExifTag.LensModel),
            FNumber = fNumber is > 0 ? fNumber : null,
            ExposureSeconds = exposure is > 0 ? exposure : null,
            Iso = iso is > 0 ? iso : null,
        };
        return info.IsEmpty ? null : info;
    }

    /// <summary>EXIF の日時 "2026:09:10 10:21:05"</summary>
    public static DateTime? ParseExifDate(string? text) =>
        DateTime.TryParseExact(text?.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Clean(string? s)
    {
        s = s?.Trim('\0', ' ');
        return string.IsNullOrEmpty(s) ? null : s;
    }
}

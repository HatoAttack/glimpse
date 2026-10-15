# 手動確認（docs/manual-test.md）に使う使い捨ての画像を作る。Windows PowerShell 5.1 で動く（追加のインストール不要）
# 使い方: powershell -ExecutionPolicy Bypass -File docs\manual-test-setup.ps1 [-Root 作る場所]
# 既定の作る場所は %TEMP%\glimpse-manual。あれば消して作り直す
param([string]$Root = (Join-Path $env:TEMP 'glimpse-manual'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$main = New-Item -ItemType Directory -Path (Join-Path $Root 'images')
$other = New-Item -ItemType Directory -Path (Join-Path $Root 'other')
$zipSrc = New-Item -ItemType Directory -Path (Join-Path $Root 'zip-src')

# 左半分が色 a・右半分が色 b、真ん中に番号を書いた画像（向きと並びが目で分かるように）
function New-Picture([int]$w, [int]$h, [System.Drawing.Color]$a, [System.Drawing.Color]$b, [string]$label) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $a), 0, 0, [int]($w / 2), $h)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $b), [int]($w / 2), 0, $w - [int]($w / 2), $h)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([Math]::Max(12, [int]($h / 6))), ([System.Drawing.FontStyle]::Bold)
    $g.DrawString($label, $font, [System.Drawing.Brushes]::White, 10, 10)
    $g.Dispose()
    return $bmp
}

function Save([System.Drawing.Bitmap]$bmp, [string]$path, [System.Drawing.Imaging.ImageFormat]$format) {
    $bmp.Save($path, $format)
    $bmp.Dispose()
}

$red = [System.Drawing.Color]::FromArgb(200, 40, 40)
$blue = [System.Drawing.Color]::FromArgb(40, 70, 200)
$green = [System.Drawing.Color]::FromArgb(40, 150, 70)
$fmt = [System.Drawing.Imaging.ImageFormat]

# 名前順・並べ替え・連続移動用の連番 JPEG
for ($i = 1; $i -le 12; $i++) {
    Save (New-Picture 1600 1000 $red $blue ('{0:D2}' -f $i)) (Join-Path $main ('photo{0:D2}.jpg' -f $i)) $fmt::Jpeg
}
Save (New-Picture 800 800 $green $blue 'PNG') (Join-Path $main 'square.png') $fmt::Png
Save (New-Picture 600 400 $blue $green 'BMP') (Join-Path $main 'plain.bmp') $fmt::Bmp
Save (New-Picture 1200 800 $red $green 'TIFF') (Join-Path $main 'single.tif') $fmt::Tiff

# 透過 PNG（右半分が透明）
$alpha = New-Object System.Drawing.Bitmap 600, 400, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($alpha)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $red), 0, 0, 300, 400)
$g.Dispose()
Save $alpha (Join-Path $main 'alpha.png') $fmt::Png

# 複数ページの TIFF（3 ページ）
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/tiff' }
$enc = [System.Drawing.Imaging.Encoder]::SaveFlag
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$pages = @((New-Picture 800 600 $red $blue 'P1'), (New-Picture 800 600 $green $blue 'P2'), (New-Picture 800 600 $blue $red 'P3'))
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter $enc, ([long][System.Drawing.Imaging.EncoderValue]::MultiFrame)
$pages[0].Save((Join-Path $main 'pages.tif'), $codec, $params)
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter $enc, ([long][System.Drawing.Imaging.EncoderValue]::FrameDimensionPage)
$pages[0].SaveAdd($pages[1], $params)
$pages[0].SaveAdd($pages[2], $params)
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter $enc, ([long][System.Drawing.Imaging.EncoderValue]::Flush)
$pages[0].SaveAdd($params)
$pages | ForEach-Object { $_.Dispose() }

# 拡張子を変えた TIFF（中身で読むことの確認）
Copy-Item (Join-Path $main 'single.tif') (Join-Path $main 'tiff_inside.png')

# 大きい画像（読み込み中の連続移動・メモリの確認）
Save (New-Picture 8000 6000 $blue $red 'BIG') (Join-Path $main 'big.jpg') $fmt::Jpeg

# 別のフォルダ（ドラッグ・コピーの行き先）と ZIP
Save (New-Picture 400 300 $green $red 'OTHER') (Join-Path $other 'other.png') $fmt::Png
for ($i = 1; $i -le 3; $i++) {
    Save (New-Picture 640 480 $red $green ('Z{0}' -f $i)) (Join-Path $zipSrc ('inzip{0}.jpg' -f $i)) $fmt::Jpeg
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($zipSrc, (Join-Path $main 'archive.zip'))
Remove-Item $zipSrc -Recurse -Force

Write-Host "作りました: $Root"
Write-Host "手で足すもの（手元の写真などから、images にコピー）: EXIF 付きの JPEG（縦向きの写真があればなおよい）、アニメーション GIF・WEBP、HEIC（拡張機能があれば）"

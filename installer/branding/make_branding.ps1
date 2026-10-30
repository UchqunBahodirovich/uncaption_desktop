<#
.SYNOPSIS
  Renders the Inno Setup brand images (welcome/finish panel and the small header logo).

.DESCRIPTION
  One image per DPI step (100 %–250 %) so Windows picks a sharp one; Inno Setup chooses the
  best match from the comma-separated list in unicaption_setup.iss. Colours are the app tokens:
  brand #FF3D00, text #1C2430, white surfaces. Re-run after changing the logo:
    powershell -ExecutionPolicy Bypass -File installer\branding\make_branding.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$icon = New-Object System.Drawing.Icon((Join-Path $root 'src\Unicaption.App\Assets\app_icon.ico'), 256, 256)
$logo = $icon.ToBitmap()
$scales = @(100, 125, 150, 175, 200, 225, 250)

$brand = [System.Drawing.Color]::FromArgb(255, 0xFF, 0x3D, 0x00)
$brandDeep = [System.Drawing.Color]::FromArgb(255, 0xD9, 0x34, 0x00)
$white = [System.Drawing.Color]::White

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

foreach ($s in $scales) {
    $k = $s / 100.0

    # ── Welcome / finish panel: brand gradient, soft glow, logo on a white tile, wordmark ──
    $w = [int][math]::Round(164 * $k); $h = [int][math]::Round(314 * $k)
    $bmp, $g = New-Canvas $w $h
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $w, $h), $brand, $brandDeep)
    $g.FillRectangle($bg, 0, 0, $w, $h)
    foreach ($c in @(@(0.15, 0.10, 1.10, 60), @(0.95, 0.85, 0.90, 45))) {
        $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[3], 255, 255, 255))
        $r = $w * $c[2] / 2
        $g.FillEllipse($glow, [float]($w * $c[0] - $r), [float]($h * $c[1] - $r), [float]($r * 2), [float]($r * 2))
        $glow.Dispose()
    }
    $tile = 76 * $k; $tx = ($w - $tile) / 2; $ty = $h * 0.30
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 0, 0, 0))
    $g.FillPath($shadow, (New-RoundedPath $tx ($ty + 3 * $k) $tile $tile (20 * $k)))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), (New-RoundedPath $tx $ty $tile $tile (20 * $k)))
    $pad = 12 * $k
    $g.DrawImage($logo, [float]($tx + $pad), [float]($ty + $pad), [float]($tile - 2 * $pad), [float]($tile - 2 * $pad))
    $center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'
    $title = New-Object System.Drawing.Font('Segoe UI Semibold', [float](17 * $k), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sub = New-Object System.Drawing.Font('Segoe UI', [float](11 * $k), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('Unicaption', $title, (New-Object System.Drawing.SolidBrush $white), [System.Drawing.RectangleF]::new(0, [float]($ty + $tile + 18 * $k), $w, 30 * $k), $center)
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 255, 255))
    $g.DrawString("Live captions`nand translation", $sub, $soft, [System.Drawing.RectangleF]::new(0, [float]($ty + $tile + 44 * $k), $w, 40 * $k), $center)
    $g.Dispose()
    $bmp.Save((Join-Path $PSScriptRoot "WizardImage-$s.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()

    # ── Small header logo: the app icon on white ──
    $sw = [int][math]::Round(55 * $k)
    $bmp, $g = New-Canvas $sw $sw
    $g.Clear($white)
    $m = 4 * $k
    $g.DrawImage($logo, [float]$m, [float]$m, [float]($sw - 2 * $m), [float]($sw - 2 * $m))
    $g.Dispose()
    $bmp.Save((Join-Path $PSScriptRoot "WizardSmallImage-$s.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
$logo.Dispose(); $icon.Dispose()
Write-Host "Brand images written to $PSScriptRoot"

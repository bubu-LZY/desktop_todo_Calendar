Add-Type -AssemblyName System.Drawing

function Create-TaskListIcon {
    param(
        [string]$OutputPath,
        [int]$Size = 256
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # 画笔颜色
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(51, 51, 51), [float]($Size * 0.04))
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(51, 51, 51))

    # 圆点大小
    $dotSize = [int]($Size * 0.08)

    # 三行的Y位置
    $line1Y = [int]($Size * 0.3)
    $line2Y = [int]($Size * 0.5)
    $line3Y = [int]($Size * 0.7)

    # 左侧圆点X位置
    $dotX = [int]($Size * 0.2)
    # 右侧横线起始X位置
    $lineStartX = [int]($Size * 0.35)
    # 右侧横线结束X位置
    $lineEndX = [int]($Size * 0.8)

    # 画三个圆点
    $graphics.FillEllipse($brush, $dotX - $dotSize/2, $line1Y - $dotSize/2, $dotSize, $dotSize)
    $graphics.FillEllipse($brush, $dotX - $dotSize/2, $line2Y - $dotSize/2, $dotSize, $dotSize)
    $graphics.FillEllipse($brush, $dotX - $dotSize/2, $line3Y - $dotSize/2, $dotSize, $dotSize)

    # 画三条横线
    $lineThickness = [int]($Size * 0.03)
    $graphics.FillRectangle($brush, $lineStartX, $line1Y - $lineThickness/2, $lineEndX - $lineStartX, $lineThickness)
    $graphics.FillRectangle($brush, $lineStartX, $line2Y - $lineThickness/2, $lineEndX - $lineStartX, $lineThickness)
    $graphics.FillRectangle($brush, $lineStartX, $line3Y - $lineThickness/2, $lineEndX - $lineStartX, $lineThickness)

    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $graphics.Dispose()
    $bitmap.Dispose()
    $pen.Dispose()
    $brush.Dispose()
}

# 创建不同尺寸的PNG
$sizes = @(16, 32, 48, 64, 128, 256)
$outputDir = Join-Path $PSScriptRoot "..\MicaAgenda.App\Assets"

foreach ($size in $sizes) {
    $outputPath = Join-Path $outputDir "task-icon-$size.png"
    Create-TaskListIcon -OutputPath $outputPath -Size $size
    Write-Output "Created: $outputPath"
}

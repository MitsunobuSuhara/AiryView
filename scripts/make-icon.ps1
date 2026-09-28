$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$taskSource = [Drawing.Bitmap]::new((Join-Path $taskRoot 'assets\icons\AiryView-symbol-white-transparent.png'))
$taskSizes = @(16,24,32,48,64,128,256)
$taskPngs = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [Drawing.Bitmap]::new($taskSize,$taskSize)
        $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
        $taskMemory = [IO.MemoryStream]::new()
        try {
            $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $taskGraphics.Clear([Drawing.Color]::Transparent)
            # 白い鶴を見やすく保ち、タイトルバーと同じ青緑の濃淡を円の背景へ使う。
            $taskBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
                [Drawing.Rectangle]::new(0,0,$taskSize,$taskSize),
                [Drawing.Color]::FromArgb(20,47,64), [Drawing.Color]::FromArgb(12,52,76), 0.0)
            $taskBlend = [Drawing.Drawing2D.ColorBlend]::new(3)
            $taskBlend.Colors = @([Drawing.Color]::FromArgb(20,47,64),[Drawing.Color]::FromArgb(20,80,106),[Drawing.Color]::FromArgb(12,52,76))
            $taskBlend.Positions = @([single]0,[single]0.48,[single]1)
            $taskBrush.InterpolationColors = $taskBlend
            try { $taskGraphics.FillEllipse($taskBrush,0,0,$taskSize-1,$taskSize-1) } finally { $taskBrush.Dispose() }
            # タスクバーでツルがほかのアプリアイコンより小さく見えないよう、
            # 小さいサイズだけを少し拡大する。128px以上の資料用表示は変えない。
            $taskInset = if ($taskSize -le 64) { -0.23 * $taskSize } else { 0 }
            $taskGraphics.DrawImage($taskSource,$taskInset,$taskInset,$taskSize-2*$taskInset,$taskSize-2*$taskInset)
            $taskBitmap.Save($taskMemory,[Drawing.Imaging.ImageFormat]::Png)
            $taskPngs.Add($taskMemory.ToArray())
        } finally { $taskMemory.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
    }
    $taskFile = [IO.File]::Create((Join-Path $taskRoot 'assets\icons\AiryView.ico'))
    $taskWriter = [IO.BinaryWriter]::new($taskFile)
    try {
        $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
        $taskOffset = 6 + 16*$taskSizes.Count
        for ($taskIndex=0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
            $taskByteSize = if ($taskSizes[$taskIndex] -eq 256) {0} else {$taskSizes[$taskIndex]}
            $taskWriter.Write([byte]$taskByteSize); $taskWriter.Write([byte]$taskByteSize)
            $taskWriter.Write([byte]0); $taskWriter.Write([byte]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskPngs[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskPngs[$taskIndex].Length
        }
        foreach ($taskPng in $taskPngs) { $taskWriter.Write($taskPng) }
    } finally { $taskWriter.Dispose(); $taskFile.Dispose() }
} finally { $taskSource.Dispose() }

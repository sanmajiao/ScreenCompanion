$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'release') -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 自带的 .NET Framework 4.x 编译器。' }
Add-Type -AssemblyName System.Drawing
$taskBitmap = [System.Drawing.Bitmap]::new(32,32)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskGraphics.Clear([System.Drawing.Color]::Transparent)
$taskBrush1 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(42,115,205))
$taskBrush2 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(72,179,147))
$taskPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White,2)
$taskGraphics.FillRectangle($taskBrush1,1,5,19,18)
$taskGraphics.FillRectangle($taskBrush2,21,10,10,14)
$taskGraphics.DrawRectangle($taskPen,2,6,17,16)
$taskGraphics.DrawLine($taskPen,8,27,15,27)
$taskGraphics.DrawLine($taskPen,11,23,11,27)
$taskIcon = [System.Drawing.Icon]::FromHandle($taskBitmap.GetHicon())
$taskIconStream = [System.IO.File]::Create((Join-Path $taskRoot 'src\app.ico'))
$taskIcon.Save($taskIconStream)
$taskIconStream.Dispose()
$taskGraphics.Dispose()
$taskBrush1.Dispose()
$taskBrush2.Dispose()
$taskPen.Dispose()
$taskBitmap.Dispose()
$sources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName
$buildArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/main:ScreenCompanion.Program', ('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')), '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll', ('/out:' + (Join-Path $taskRoot 'release\副屏助手.exe'))) + $sources
$buildArgs += '/win32icon:' + (Join-Path $taskRoot 'src\app.ico')
& $compiler @buildArgs
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Get-FileHash -LiteralPath (Join-Path $taskRoot 'release\副屏助手.exe') -Algorithm SHA256

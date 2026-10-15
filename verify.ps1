param([switch]$Run)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath (Join-Path $taskRoot 'release\副屏助手.exe'))) { & (Join-Path $taskRoot 'build.ps1') }
$taskCommon = @('/nologo','/target:exe','/platform:x64','/optimize+','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Core.dll',('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')))
foreach ($taskCheck in @('Verify','FeatureCheck','WindowCompatibility','SettingsCheck','TrayPopupCheck','KeyboardCheck','ResponseCheck','WallpaperCheck','TrayPointerCheck','StartMenuCheck','LatencyCheck')) {
    $taskOutput = Join-Path $taskRoot ('verification\' + $taskCheck + '.exe')
    $taskMain = if ($taskCheck -eq 'Verify') { 'Verification' } else { $taskCheck }
    $taskArgs = $taskCommon + @(('/main:ScreenCompanion.' + $taskMain), ('/out:' + $taskOutput), (Join-Path $taskRoot ('verification\' + $taskCheck + '.cs')))
    if ($taskCheck -in @('FeatureCheck','TrayPopupCheck','KeyboardCheck','ResponseCheck','WallpaperCheck','TrayPointerCheck','StartMenuCheck','LatencyCheck')) { $taskArgs += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName }
    else { $taskArgs += Join-Path $taskRoot 'src\Native.cs' }
    & $taskCompiler @taskArgs
    if ($LASTEXITCODE -ne 0) { throw ('验证程序编译失败: ' + $taskCheck) }
    Write-Output ('Compiled: ' + $taskCheck)
}
if ($Run) {
    if (Get-Process -Name '副屏助手' -ErrorAction SilentlyContinue) { throw '请先退出正在运行的副屏助手，再启动图形交互检查。' }
    foreach ($taskCheck in @('Verify','FeatureCheck','WindowCompatibility','SettingsCheck','TrayPopupCheck','KeyboardCheck','ResponseCheck','WallpaperCheck','TrayPointerCheck','StartMenuCheck','LatencyCheck')) {
        & (Join-Path $taskRoot ('verification\' + $taskCheck + '.exe'))
        if ($LASTEXITCODE -ne 0) { throw ('实机检查失败: ' + $taskCheck) }
    }
} else { Write-Output '仅完成编译。实际交互检查需要副屏和空闲桌面，可运行 verify.ps1 -Run。' }

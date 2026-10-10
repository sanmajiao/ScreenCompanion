param([string]$BinaryPath)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BinaryPath)) {
    & (Join-Path $taskRoot 'build.ps1')
    $BinaryPath = Join-Path $taskRoot 'release\副屏助手.exe'
}
$taskBinary = (Resolve-Path -LiteralPath $BinaryPath).Path
$taskVersion = ([Version](Get-Item -LiteralPath $taskBinary).VersionInfo.FileVersion).ToString(3)
$taskDist = Join-Path $taskRoot 'dist'
$taskStageRoot = Join-Path $taskDist ('stage-' + [Guid]::NewGuid().ToString('N'))
$taskStage = Join-Path $taskStageRoot 'ScreenCompanion'
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
Copy-Item -LiteralPath $taskBinary -Destination (Join-Path $taskStage '副屏助手.exe')
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskStage '使用说明.md')
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskStage 'LICENSE')
$taskBinaryHash = (Get-FileHash -LiteralPath $taskBinary -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $taskStage 'SHA256.txt'),$taskBinaryHash + '  副屏助手.exe' + [Environment]::NewLine,[Text.UTF8Encoding]::new($true))
$taskZipName = 'ScreenCompanion-v' + $taskVersion + '-win-x64.zip'
$taskZip = Join-Path $taskDist $taskZipName
Compress-Archive -LiteralPath $taskStage -DestinationPath $taskZip -CompressionLevel Optimal -Force
$taskZipHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $taskDist 'SHA256SUMS.txt'),$taskZipHash + '  ' + $taskZipName + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Write-Output ('Portable release: ' + $taskZip)
Write-Output ('EXE SHA256: ' + $taskBinaryHash)
Write-Output ('ZIP SHA256: ' + $taskZipHash)

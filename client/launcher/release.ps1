$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskBase = Join-Path $taskRoot 'dist/PureISP-BS-Portable'
$taskRelease = Join-Path $taskRoot 'dist/PureISP-BS-v1.0.4-Win64/PureISP-BS-Portable'
[IO.Directory]::CreateDirectory($taskRelease) | Out-Null
foreach ($taskName in 'PureISP-Core.exe','config.yaml','geoip.metadb') {
    Copy-Item -LiteralPath (Join-Path $taskBase $taskName) -Destination $taskRelease -Force
}
$taskResources = Join-Path $taskRelease 'resources'
[IO.Directory]::CreateDirectory($taskResources) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskBase 'resources') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskResources -Recurse -Force
}
$taskUi = Join-Path $taskRelease 'ui'
[IO.Directory]::CreateDirectory($taskUi) | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'server/src/public/console.html') -Destination (Join-Path $taskUi 'index.html') -Force
$taskUsageName = (-join ([char[]] @(0x4F7F, 0x7528, 0x8BF4, 0x660E))) + '.txt'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'USAGE.txt') -Destination (Join-Path $taskRelease $taskUsageName) -Force
& (Join-Path $PSScriptRoot 'build.ps1') -OutputPath (Join-Path $taskRelease 'PureISP.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets/pureisp.ico') -Destination (Join-Path $taskResources 'app.ico') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets/pureisp.png') -Destination (Join-Path $taskResources 'app.png') -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = Join-Path $taskRoot 'dist/PureISP-BS-v1.0.4-Win64.zip'
$taskStagingZip = Join-Path $taskRoot ('dist/pureisp-release-' + [Guid]::NewGuid().ToString('N') + '.zip')
$taskArchive = [IO.Compression.ZipFile]::Open($taskStagingZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $taskAllowedFiles = @('PureISP.exe', 'PureISP-Core.exe', 'config.yaml', 'geoip.metadb', $taskUsageName)
    Get-ChildItem -LiteralPath $taskRelease -Recurse -File | ForEach-Object {
        $taskRelative = $_.FullName.Substring($taskRelease.Length + 1).Replace('\', '/')
        if ($taskRelative -in $taskAllowedFiles -or $taskRelative.StartsWith('resources/') -or $taskRelative -eq 'ui/index.html') {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive, $_.FullName, ('PureISP-BS-Portable/' + $taskRelative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $taskArchive.Dispose() }
Move-Item -LiteralPath $taskStagingZip -Destination $taskZip -Force
Write-Output "Release ready: $taskZip"

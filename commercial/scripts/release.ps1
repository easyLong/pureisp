$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'commercial/package.json') -Raw | ConvertFrom-Json).version
$taskZip = Join-Path $taskRoot ("dist/PureISP-License-Service-v$taskVersion.zip")
$taskTemporary = Join-Path $taskRoot ('dist/license-service-' + [Guid]::NewGuid().ToString('N') + '.zip')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::Open($taskTemporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    $taskServiceRoot = Join-Path $taskRoot 'commercial'
    Get-ChildItem -LiteralPath $taskServiceRoot -Recurse -File | ForEach-Object {
        $taskRelative = $_.FullName.Substring($taskServiceRoot.Length + 1).Replace('\','/')
        if ($taskRelative -in @('package.json','package-lock.json','.env.example','README.md','start.sh','stop.sh','restart.sh','status.sh','logs.sh') -or $taskRelative.StartsWith('src/') -or $taskRelative.StartsWith('scripts/') -or $taskRelative.StartsWith('public/') -or $taskRelative.StartsWith('test/')) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive, $_.FullName, ('PureISP-License-Service/' + $taskRelative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $taskArchive.Dispose() }
Move-Item -LiteralPath $taskTemporary -Destination $taskZip -Force
Write-Output "Account service: $taskZip"

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskRelease = Join-Path $taskRoot 'dist/PureISP-License-v1.1.4-Win64/PureISP-License-Update'
[IO.Directory]::CreateDirectory($taskRelease) | Out-Null
& (Join-Path $PSScriptRoot 'build-account.ps1') -OutputPath (Join-Path $taskRelease 'PureISP.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'account-service.example.json') -Destination (Join-Path $taskRelease 'account-service.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ACCOUNT-USAGE.txt') -Destination (Join-Path $taskRelease 'USAGE.txt') -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = Join-Path $taskRoot 'dist/PureISP-License-v1.1.4-Win64.zip'
$taskTemporary = Join-Path $taskRoot ('dist/account-' + [Guid]::NewGuid().ToString('N') + '.zip')
$taskArchive = [IO.Compression.ZipFile]::Open($taskTemporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskName in 'PureISP.exe','account-service.json','USAGE.txt') {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive, (Join-Path $taskRelease $taskName), ('PureISP-License-Update/' + $taskName), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $taskArchive.Dispose() }
Move-Item -LiteralPath $taskTemporary -Destination $taskZip -Force
# The separate service archive never includes .env, account snapshots or node_modules.
$taskServiceVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'commercial/package.json') -Raw | ConvertFrom-Json).version
$taskServiceZip = Join-Path $taskRoot ("dist/PureISP-License-Service-v$taskServiceVersion.zip")
$taskTemporary = Join-Path $taskRoot ('dist/account-service-' + [Guid]::NewGuid().ToString('N') + '.zip')
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
Move-Item -LiteralPath $taskTemporary -Destination $taskServiceZip -Force
Write-Output "Client update: $taskZip"
Write-Output "Account service: $taskServiceZip"

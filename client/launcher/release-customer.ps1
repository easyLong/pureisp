$ErrorActionPreference = 'Stop'

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskBase = Join-Path $taskRoot 'dist/PureISP-BS-Portable'
$taskVersion = '1.1.3'
$taskPackageRoot = Join-Path $taskRoot ("dist/PureISP-Client-v$taskVersion-Win64")
$taskClientRoot = Join-Path $taskPackageRoot 'PureISP-Client'
$taskZip = Join-Path $taskRoot ("dist/PureISP-Client-v$taskVersion-Win64.zip")
$taskKeySource = Join-Path $taskRoot ('dist/config-key-' + [Guid]::NewGuid().ToString('N') + '.cs')

if (-not (Test-Path -LiteralPath $taskBase)) { throw "Base client directory not found: $taskBase" }

if (Test-Path -LiteralPath $taskPackageRoot) {
    Remove-Item -LiteralPath $taskPackageRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($taskClientRoot) | Out-Null

foreach ($taskName in 'PureISP-Core.exe','geoip.metadb') {
    Copy-Item -LiteralPath (Join-Path $taskBase $taskName) -Destination $taskClientRoot -Force
}

$taskResources = Join-Path $taskClientRoot 'resources'
[IO.Directory]::CreateDirectory($taskResources) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskBase 'resources') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskResources -Recurse -Force
}

$taskUi = Join-Path $taskClientRoot 'ui'
[IO.Directory]::CreateDirectory($taskUi) | Out-Null
Copy-Item -LiteralPath (Join-Path $taskBase 'ui/index.html') -Destination (Join-Path $taskUi 'index.html') -Force

$taskKeyBytes = New-Object byte[] 32
$taskRandom = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $taskRandom.GetBytes($taskKeyBytes) } finally { $taskRandom.Dispose() }
$taskKeyBase64 = [Convert]::ToBase64String($taskKeyBytes)
$taskKeySourceText = 'using System; internal static class ConfigKeyProvider { internal static readonly byte[] Key = Convert.FromBase64String("' + $taskKeyBase64 + '"); }'
[IO.File]::WriteAllText($taskKeySource, $taskKeySourceText, (New-Object System.Text.UTF8Encoding($false)))
& node (Join-Path $PSScriptRoot 'encrypt-config.mjs') --input (Join-Path $taskBase 'config.yaml') --output (Join-Path $taskClientRoot 'config.enc') --key $taskKeyBase64
if ($LASTEXITCODE -ne 0) { throw 'Config encryption failed.' }
& (Join-Path $PSScriptRoot 'build-account.ps1') -OutputPath (Join-Path $taskClientRoot 'PureISP.exe') -ProtectedConfig -ConfigKeySource $taskKeySource
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'account-service.example.json') -Destination (Join-Path $taskClientRoot 'account-service.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CUSTOMER-USAGE.txt') -Destination (Join-Path $taskClientRoot 'USAGE.txt') -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskTemporary = Join-Path $taskRoot ('dist/pureisp-customer-' + [Guid]::NewGuid().ToString('N') + '.zip')
$taskArchive = [IO.Compression.ZipFile]::Open($taskTemporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $taskClientRoot -Recurse -File | ForEach-Object {
        $taskRelative = $_.FullName.Substring($taskClientRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $taskArchive,
            $_.FullName,
            ('PureISP-Client/' + $taskRelative),
            [IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
} finally {
    $taskArchive.Dispose()
}
Move-Item -LiteralPath $taskTemporary -Destination $taskZip -Force
Remove-Item -LiteralPath $taskKeySource -Force
Write-Output "Customer package: $taskZip"
Write-Output "Customer folder: $taskClientRoot"

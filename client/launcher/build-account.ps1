param(
    [string]$OutputPath,
    [switch]$ProtectedConfig,
    [string]$ConfigKeySource
)
$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '../../dist/PureISP-License-v1.1.3-Win64/PureISP-License-Update/PureISP.exe' }
$taskDefines = 'PAID_EDITION'
$taskSources = @(
    (Join-Path $PSScriptRoot 'PureISPLauncher.cs'),
    (Join-Path $PSScriptRoot 'SystemProxy.cs'),
    (Join-Path $PSScriptRoot 'IdeLocator.cs'),
    (Join-Path $PSScriptRoot 'AccountClient.cs'),
    (Join-Path $PSScriptRoot 'ConfigCrypto.cs')
)
if ($ProtectedConfig) {
    if (-not $ConfigKeySource -or -not (Test-Path -LiteralPath $ConfigKeySource)) { throw 'Protected config builds require a generated config key source.' }
    $taskDefines += ',PROTECTED_CONFIG'
    $taskSources += [IO.Path]::GetFullPath($ConfigKeySource)
}
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$taskOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
$taskIcon = Join-Path $PSScriptRoot 'assets/pureisp.ico'
$taskArguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', "/define:$taskDefines", '/main:AccountEntryPoint',
    "/out:$taskOutput", "/win32icon:$taskIcon", "/resource:$taskIcon,PureISP.Icon",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll'
) + $taskSources
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Account launcher compilation failed.' }
Write-Output "Built $taskOutput"

param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (-not $OutputPath) {
    $OutputPath = Join-Path $PSScriptRoot '../../dist/PureISP-BS-v1.0.4-Win64/PureISP-BS-Portable/PureISP.exe'
}
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework C# compiler not found.' }
$taskSource = Join-Path $PSScriptRoot 'PureISPLauncher.cs'
$taskOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
$taskIcon = Join-Path $PSScriptRoot 'assets/pureisp.ico'
if (-not (Test-Path -LiteralPath $taskIcon)) { & (Join-Path $PSScriptRoot 'build-icon.ps1') }
& $taskCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$taskOutput" "/win32icon:$taskIcon" "/resource:$taskIcon,PureISP.Icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $taskSource (Join-Path $PSScriptRoot 'SystemProxy.cs') (Join-Path $PSScriptRoot 'IdeLocator.cs') (Join-Path $PSScriptRoot 'TransitSelector.cs')
if ($LASTEXITCODE -ne 0) { throw 'PureISP launcher compilation failed.' }
Write-Output "Built $taskOutput"

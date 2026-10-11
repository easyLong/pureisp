$ErrorActionPreference = 'Stop'
$taskDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pureisp-transit-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
$taskLauncher = Join-Path $taskDirectory 'PureISP.exe'
& (Join-Path $PSScriptRoot 'build-account.ps1') -OutputPath $taskLauncher
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$taskTests = Join-Path $taskDirectory 'TransitRegressionTests.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskTests" "/reference:$taskLauncher" /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'TransitRegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transit test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../dist/PureISP-BS-Portable/PureISP-Core.exe') -Destination $taskDirectory
& $taskTests $taskDirectory
if ($LASTEXITCODE -ne 0) { throw "Transit tests failed. Artifacts: $taskDirectory" }
Write-Output "Transit tests passed. Artifacts: $taskDirectory"

$ErrorActionPreference = 'Stop'
$taskDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pureisp-launcher-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
$taskLauncher = Join-Path $taskDirectory 'PureISP.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $taskLauncher
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$taskTests = Join-Path $taskDirectory 'RegressionTests.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskTests" "/reference:$taskLauncher" /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'RegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression test compilation failed.' }
$taskDist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../dist/PureISP-BS-Portable'))
Copy-Item -LiteralPath (Join-Path $taskDist 'PureISP-Core.exe') -Destination $taskDirectory
Copy-Item -LiteralPath (Join-Path $taskDist 'geoip.metadb') -Destination $taskDirectory
Copy-Item -LiteralPath (Join-Path $taskDist 'resources') -Destination $taskDirectory -Recurse
[IO.Directory]::CreateDirectory((Join-Path $taskDirectory 'ui')) | Out-Null
$taskConfig = [IO.File]::ReadAllText((Join-Path $taskDist 'config.yaml')) -replace '(?m)^allow-lan: true', 'allow-lan: false'
[IO.File]::WriteAllText((Join-Path $taskDirectory 'config.yaml'), $taskConfig, (New-Object Text.UTF8Encoding($false)))
& $taskTests $taskDirectory
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed. Artifacts: $taskDirectory" }
Write-Output "Tests passed. Artifacts: $taskDirectory"

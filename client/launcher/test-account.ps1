$ErrorActionPreference = 'Stop'
$taskDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pureisp-account-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
$taskLauncher = Join-Path $taskDirectory 'PureISP.exe'
& (Join-Path $PSScriptRoot 'build-account.ps1') -OutputPath $taskLauncher
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$taskTests = Join-Path $taskDirectory 'AccountRegressionTests.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskTests" "/reference:$taskLauncher" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'AccountRegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Account test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../dist/PureISP-BS-Portable/PureISP-Core.exe') -Destination $taskDirectory
$taskFixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../commercial/test/client-fixture.js'))
$taskNode = (Get-Command node).Source
$taskNodeArgs = @(('"' + $taskFixture + '"'), ('"' + $taskDirectory + '"'))
$taskProcess = Start-Process -FilePath $taskNode -ArgumentList $taskNodeArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskDirectory 'fixture.log') -RedirectStandardError (Join-Path $taskDirectory 'fixture-error.log')
try {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'fixture.json'))) {
        if ($taskProcess.HasExited -or [DateTime]::UtcNow -gt $taskDeadline) { throw "Account fixture startup failed. Artifacts: $taskDirectory" }
        Start-Sleep -Milliseconds 100
    }
    & $taskTests $taskDirectory
    if ($LASTEXITCODE -ne 0) { throw "Account tests failed. Artifacts: $taskDirectory" }
} finally { if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id } }
Write-Output "Account tests passed. Artifacts: $taskDirectory"

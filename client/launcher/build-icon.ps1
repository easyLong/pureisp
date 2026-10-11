$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$taskBuilder = Join-Path ([IO.Path]::GetTempPath()) ('pureisp-icon-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $taskCompiler /nologo /target:exe /reference:System.Drawing.dll "/out:$taskBuilder" (Join-Path $PSScriptRoot 'IconBuilder.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Icon builder compilation failed.' }
    & $taskBuilder (Join-Path $PSScriptRoot 'assets')
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
} finally { if (Test-Path -LiteralPath $taskBuilder) { Remove-Item -LiteralPath $taskBuilder } }

param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & $Dotnet restore Shortener.slnx --configfile NuGet.Config --disable-parallel -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $Dotnet build Shortener.slnx -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $env:DOTNET_HOST_PATH = (Get-Command $Dotnet).Source
    & $Dotnet tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll --integration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }

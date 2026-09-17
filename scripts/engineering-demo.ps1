param([string]$Dotnet = 'dotnet', [string]$WorkspaceDirectory, [string]$EvidenceDirectory,
    [switch]$AutoApproveOffline)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$WorkspaceDirectory) { $WorkspaceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('shortener-engineering-' + [guid]::NewGuid().ToString('N')) }
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path $projectRoot ('evidence-local/' + [guid]::NewGuid().ToString('N')) }
$env:DOTNET_HOST_PATH = (Get-Command $Dotnet).Source
Push-Location $projectRoot
try {
    & $Dotnet restore Shortener.slnx --configfile NuGet.Config --disable-parallel -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $Dotnet build Shortener.slnx -c Release --no-restore -m:1 /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $demoArgs = @('tools/EngineeringDemo/bin/Release/net10.0/EngineeringDemo.dll', $projectRoot,
        [IO.Path]::GetFullPath($WorkspaceDirectory), [IO.Path]::GetFullPath($EvidenceDirectory))
    if ($AutoApproveOffline) { $demoArgs += '--auto-approve-offline' }
    & $Dotnet @demoArgs
    if ($LASTEXITCODE -ne 0) { throw 'Engineering demonstration failed; inspect workspace validation.json and store/state.json' }
} finally { Pop-Location }

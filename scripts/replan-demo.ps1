param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
if (!$env:OPERATOR_KEY -or !$env:APPROVER_KEY) { throw 'Set both role keys.' }
$op = @{ 'X-Api-Key'=$env:OPERATOR_KEY }
$human = @{ 'X-Api-Key'=$env:APPROVER_KEY }
$payload = Get-Content (Join-Path $PSScriptRoot '../scenarios/ambiguous.json') -Raw
$run = Invoke-RestMethod "$BaseUrl/api/runs" -Method Post -Headers $op -ContentType 'application/json' -Body $payload
$nodes = @($run.nodes | ForEach-Object { $_.spec })
($nodes | Where-Object id -eq 'design').task += ' Add a comparison of 30-day retention versus indefinite retention, including privacy and storage consequences.'
$proposal = @{ nodes=$nodes; reason='Retention remains ambiguous; require an explicit comparison before implementation'; revision=$run.revision } | ConvertTo-Json -Depth 20
$proposed = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/replan" -Method Post -Headers $op -ContentType 'application/json' -Body $proposal
$proposed.proposal | ConvertTo-Json -Depth 20
$accept = (Read-Host 'Type approve to accept this plan change; anything else rejects it') -eq 'approve'
$decision = @{ revision=$run.revision; accept=$accept; reason='Human review of retention plan change' } | ConvertTo-Json
Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/plan-decision" -Method Post -Headers $human -ContentType 'application/json' -Body $decision | ConvertTo-Json -Depth 20
Write-Host "Run $($run.id) is waiting for scope clarification. POST its current revision and supported answers to /api/runs/$($run.id)/clarifications before requirements approval, or POST /api/runs/$($run.id)/stop."

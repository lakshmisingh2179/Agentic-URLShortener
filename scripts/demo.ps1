param(
    [ValidateSet('greenfield','brownfield','ambiguous')][string]$Scenario = 'greenfield',
    [string]$BaseUrl = 'http://localhost:8080',
    [switch]$AutoApproveOfflineDemo
)
$ErrorActionPreference = 'Stop'
if (!$env:OPERATOR_KEY -or !$env:APPROVER_KEY) { throw 'Set OPERATOR_KEY and APPROVER_KEY in this shell.' }
$operatorHeaders = @{ 'X-Api-Key' = $env:OPERATOR_KEY }
$approvalHeaders = @{ 'X-Api-Key' = $env:APPROVER_KEY }
$health = Invoke-RestMethod "$BaseUrl/health"
if ($AutoApproveOfflineDemo -and $health.agentMode -ne 'offline') { throw 'Automatic demo approvals are only allowed in offline mode.' }
$payload = Get-Content (Join-Path $PSScriptRoot "../scenarios/$Scenario.json") -Raw
$run = Invoke-RestMethod "$BaseUrl/api/runs" -Method Post -Headers $operatorHeaders -ContentType 'application/json' -Body $payload
Write-Host "Run: $($run.id)"
if ($Scenario -eq 'ambiguous') {
    Write-Host 'Resolve scope: operator-only creation; optional expiry; case-sensitive aliases.'
    if (!$AutoApproveOfflineDemo -and (Read-Host 'Type accept to record these answers') -ne 'accept') {
        $null = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/stop" -Method Post -Headers $operatorHeaders
        throw 'Scope not accepted; run stopped.'
    }
    $answers = @{ revision=$run.revision; answers=@{ creation='operator-only'; retention='optional-expiry'; aliases='case-sensitive' } } | ConvertTo-Json
    $null = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/clarifications" -Method Post -Headers $operatorHeaders -ContentType 'application/json' -Body $answers
}
$deadline = (Get-Date).AddMinutes(15)
while ((Get-Date) -lt $deadline) {
    $run = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)" -Headers $operatorHeaders
    if ($run.status -in @('Completed','RolledBack','Stopped')) { break }
    if ($run.status -eq 'Paused' -and ($run.findingsProposal -or $run.fallbackProposal)) {
        $proposal = if ($run.findingsProposal) { $run.findingsProposal } else { $run.fallbackProposal }
        $decisionPath = if ($run.findingsProposal) { 'findings-decision' } else { 'fallback-decision' }
        $proposal | ConvertTo-Json -Depth 20 | Write-Host
        $acceptProposal = $AutoApproveOfflineDemo -or ((Read-Host 'Type approve to accept this proposal; anything else rolls back') -eq 'approve')
        $body = @{ revision=$run.revision; accept=[bool]$acceptProposal; reason='Reviewed proposal in scenario demo; offline approvals may be explicitly simulated' } | ConvertTo-Json
        $run = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/$decisionPath" -Method Post -Headers $approvalHeaders -ContentType 'application/json' -Body $body
        continue
    }
    foreach ($node in $run.nodes) {
        $ready = @($node.spec.dependsOn | Where-Object {
            $dependency = $_
            @($run.nodes | Where-Object { $_.spec.id -eq $dependency -and $_.status -eq 'Succeeded' }).Count -eq 0
        }).Count -eq 0
        if ($run.status -eq 'Active' -and $node.status -eq 'Pending' -and $node.spec.approval -and !$node.approved -and $ready) {
            Write-Host "`nApproval requested: $($node.spec.id), revision $($run.revision)"
            Write-Host $run.context
            $run.nodes | Where-Object status -eq 'Succeeded' | ForEach-Object { Write-Host $_.output }
            $accept = $false
            if ($AutoApproveOfflineDemo) { $accept = $true; $reason = 'Explicitly enabled offline demonstration approval' }
            else {
                $accept = (Read-Host 'Review the context and artifacts. Type approve to accept; anything else rejects and rolls back') -eq 'approve'
                $reason = Read-Host 'Enter your decision reason'
                if ([string]::IsNullOrWhiteSpace($reason)) { $reason = 'Decision made in interactive demo' }
            }
            $decision = @{ nodeId=$node.spec.id; revision=$run.revision; accept=$accept; reason=$reason } | ConvertTo-Json
            $null = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/approval" -Method Post -Headers $approvalHeaders -ContentType 'application/json' -Body $decision
        }
    }
    Start-Sleep -Milliseconds 400
}
if ($run.status -notin @('Completed','RolledBack','Stopped')) {
    $null = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/stop" -Method Post -Headers $operatorHeaders
    throw "Demo timed out; safe-stop requested for $($run.id)"
}
$run | ConvertTo-Json -Depth 20
Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/audit" -Headers $operatorHeaders | Format-Table sequence, actor, action
if ($run.status -ne 'Completed') { Write-Host "Workflow ended in $($run.status)." }

#!/usr/bin/env pwsh
# Optional semantic review helper. It prepares a judge prompt from run artifacts.
[CmdletBinding()]
param(
  [ValidateSet('copilot','claude','both')][string]$Provider = 'both',
  [string]$RunDir,
  [switch]$Execute
)
$ErrorActionPreference = 'Stop'

if (-not $RunDir) {
  $latest = Get-ChildItem '.architrave/runs' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($latest) { $RunDir = $latest.FullName }
}
if (-not $RunDir -or -not (Test-Path $RunDir -PathType Container)) { [Console]::Error.WriteLine('semantic-review: run dir not found'); exit 2 }

$prompt = Join-Path $RunDir 'semantic-review-prompt.md'
Set-Content -Path $prompt -Encoding utf8 -Value @"
You are an adversarial semantic reviewer for an Architrave run.

Review the run artifacts in $RunDir against gates/rubric.md. Focus on:
- visible intake quality;
- Tournament of Options quality;
- Recommended Plan quality;
- contract/architecture fit;
- deterministic gate evidence;
- safety, capability honesty, and missing tests.

Return PASS / REVISE / FAIL with findings ordered by severity.
"@

Write-Host "semantic-review prompt: $prompt"
$agentFile = if (Test-Path 'agents/adversarial-judge.agent.md') {
  'agents/adversarial-judge.agent.md'
} elseif (Test-Path '.github/agents/adversarial-judge.agent.md') {
  '.github/agents/adversarial-judge.agent.md'
} else {
  [Console]::Error.WriteLine('semantic-review: canonical agent not found: adversarial-judge.agent.md')
  exit 2
}
if (-not $Execute) {
  if ($Provider -in @('copilot','both')) { Write-Host "suggested command: copilot -C `"$PWD`" --agent architrave:adversarial-judge --model gpt-5.6-sol --reasoning-effort max --available-tools view,grep,glob --allow-tool view --allow-tool grep --allow-tool glob --no-ask-user --silent --no-color -p (Get-Content `"$prompt`" -Raw)" }
  if ($Provider -in @('claude','both')) { Write-Host "suggested command: claude --model claude-opus-4.8 --effort max --tools Read,Grep,Glob --allowedTools Read,Grep,Glob --append-system-prompt-file `"$agentFile`" -p (Get-Content `"$prompt`" -Raw)" }
  exit 0
}

$nonceFile = [System.IO.Path]::GetTempFileName()
try {
  $nonce = [guid]::NewGuid().ToString('D').ToLowerInvariant()
  [System.IO.File]::WriteAllText($nonceFile, $nonce + "`n", [System.Text.UTF8Encoding]::new($false))
  $body = (Get-Content $prompt -Raw) + "`n`nRead $nonceFile and include EVIDENCE_NONCE: <value> in your response; the value is absent from this prompt. End with one line exactly VERDICT: PASS, VERDICT: REVISE, or VERDICT: FAIL."
  $failed = $false
  function Test-VerifiedPass([string]$Output,[string]$Nonce) {
    $lines = @($Output -split "`r?`n")
    $nonEmpty = @($lines | Where-Object { $_.Trim().Length -gt 0 })
    $nonceLines = @($lines | Where-Object { $_ -eq "EVIDENCE_NONCE: $Nonce" })
    $verdictLines = @($lines | Where-Object { $_ -match '^VERDICT: (PASS|REVISE|FAIL)$' })
    return $nonceLines.Count -eq 1 -and $verdictLines.Count -eq 1 -and $nonEmpty.Count -gt 0 -and $nonEmpty[-1] -eq 'VERDICT: PASS'
  }
  if ($Provider -in @('copilot','both')) {
    $output = (& copilot -C "$PWD" --agent architrave:adversarial-judge --model gpt-5.6-sol --reasoning-effort max --available-tools view,grep,glob --allow-tool view --allow-tool grep --allow-tool glob --no-ask-user --silent --no-color -p $body 2>&1 | Out-String)
    Write-Host $output -NoNewline
    if ($LASTEXITCODE -ne 0 -or -not (Test-VerifiedPass $output $nonce)) { [Console]::Error.WriteLine('semantic-review: copilot judge did not return a verified PASS'); $failed = $true }
  }
  if ($Provider -in @('claude','both')) {
    $output = (& claude --model claude-opus-4.8 --effort max --tools Read,Grep,Glob --allowedTools Read,Grep,Glob --append-system-prompt-file $agentFile -p $body 2>&1 | Out-String)
    Write-Host $output -NoNewline
    if ($LASTEXITCODE -ne 0 -or -not (Test-VerifiedPass $output $nonce)) { [Console]::Error.WriteLine('semantic-review: claude judge did not return a verified PASS'); $failed = $true }
  }
  if ($failed) { exit 1 }
} finally { Remove-Item $nonceFile -Force -ErrorAction SilentlyContinue }
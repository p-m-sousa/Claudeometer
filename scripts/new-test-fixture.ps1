<#
.SYNOPSIS
Creates a deterministic Claude Code data folder for manually verifying Claude Usage on Windows.

.DESCRIPTION
Writes two synthetic session transcripts whose timestamps are anchored to the tester's own local
"today" and "yesterday", so the expected values in WINDOWS_TEST_CHECKLIST.md hold in any time zone
without changing the system clock. Nothing outside the target folder is touched, and no real Claude
Code data is read or modified.

.EXAMPLE
./scripts/new-test-fixture.ps1
ClaudeUsage.exe --data-dir "$env:TEMP\claude-usage-fixture\.claude"
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:TEMP 'claude-usage-fixture\.claude'),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDirectory = Join-Path $Path 'projects\-c-demo-alpha'
if ((Test-Path -LiteralPath $projectDirectory) -and -not $Force) {
    throw "$projectDirectory already exists. Pass -Force to overwrite it."
}

New-Item -ItemType Directory -Force -Path $projectDirectory | Out-Null

$today = (Get-Date).Date
$yesterday = $today.AddDays(-1)

function Get-Stamp {
    param([datetime]$Local)
    return $Local.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
}

function New-UserLine {
    param([datetime]$Local)
    $stamp = Get-Stamp -Local $Local
    return '{"type":"user","timestamp":"' + $stamp +
        '","message":{"role":"user","content":"PRIVATE_PROMPT_CANARY"}}'
}

function New-AssistantLine {
    param(
        [datetime]$Local,
        [string]$MessageId,
        [string]$RequestId,
        [string]$Model,
        [int]$InputTokens,
        [int]$OutputTokens,
        [int]$CacheRead,
        [int]$CacheCreation,
        [string]$BlockType
    )

    $stamp = Get-Stamp -Local $Local
    $usage = '"usage":{"input_tokens":' + $InputTokens + ',"output_tokens":' + $OutputTokens +
        ',"cache_read_input_tokens":' + $CacheRead +
        ',"cache_creation_input_tokens":' + $CacheCreation + '}'
    if ($BlockType -eq 'tool_use') {
        $content = '"content":[{"type":"tool_use","name":"Read","input":{"file":"PRIVATE_TOOL_CANARY"}}]'
    }
    else {
        $content = '"content":[{"type":"text","text":"PRIVATE_REPLY_CANARY"}]'
    }

    return '{"type":"assistant","timestamp":"' + $stamp + '","requestId":"' + $RequestId +
        '","cwd":"C:/PRIVATE_PATH_CANARY","sessionId":"PRIVATE_SESSION_CANARY","message":{"id":"' +
        $MessageId + '","model":"' + $Model + '",' + $usage + ',' + $content + '}}'
}

# Session one begins yesterday and continues into today. One response is split across three lines
# with the same message id and the same usage payload, exactly as Claude Code writes it.
$sessionOne = @(
    (New-UserLine -Local $yesterday.AddHours(20)),
    (New-AssistantLine -Local $yesterday.AddHours(20).AddSeconds(5) -MessageId 'msg_y' -RequestId 'req_y' `
        -Model 'claude-opus-5' -InputTokens 1000 -OutputTokens 2000 -CacheRead 3000 -CacheCreation 4000 -BlockType 'tool_use'),
    (New-AssistantLine -Local $today.AddHours(9) -MessageId 'msg_a' -RequestId 'req_a' `
        -Model 'claude-opus-5' -InputTokens 100 -OutputTokens 200 -CacheRead 300 -CacheCreation 400 -BlockType 'text'),
    (New-AssistantLine -Local $today.AddHours(9).AddSeconds(1) -MessageId 'msg_a' -RequestId 'req_a' `
        -Model 'claude-opus-5' -InputTokens 100 -OutputTokens 200 -CacheRead 300 -CacheCreation 400 -BlockType 'tool_use'),
    (New-AssistantLine -Local $today.AddHours(9).AddSeconds(2) -MessageId 'msg_a' -RequestId 'req_a' `
        -Model 'claude-opus-5' -InputTokens 100 -OutputTokens 200 -CacheRead 300 -CacheCreation 400 -BlockType 'tool_use'),
    (New-AssistantLine -Local $today.AddHours(10) -MessageId 'msg_b' -RequestId 'req_b' `
        -Model 'claude-sonnet-5' -InputTokens 5 -OutputTokens 6 -CacheRead 7 -CacheCreation 8 -BlockType 'tool_use'),
    (New-AssistantLine -Local $today.AddHours(10).AddSeconds(1) -MessageId 'msg_c' -RequestId 'req_c' `
        -Model '<synthetic>' -InputTokens 999999 -OutputTokens 0 -CacheRead 0 -CacheCreation 0 -BlockType 'text'),
    '{"type":"queue-operation","timestamp":"' + (Get-Stamp -Local $today.AddHours(10).AddSeconds(2)) +
        '","operation":"add"}'
)

# Session two begins today, so it supplies today's session count.
$sessionTwo = @(
    (New-UserLine -Local $today.AddHours(11)),
    (New-AssistantLine -Local $today.AddHours(11).AddSeconds(5) -MessageId 'msg_d' -RequestId 'req_d' `
        -Model 'claude-opus-5' -InputTokens 10 -OutputTokens 20 -CacheRead 30 -CacheCreation 40 -BlockType 'text')
)

Set-Content -LiteralPath (Join-Path $projectDirectory 'aaaaaaaa-1111-2222-3333-444444444444.jsonl') `
    -Value $sessionOne -Encoding UTF8
Set-Content -LiteralPath (Join-Path $projectDirectory 'bbbbbbbb-1111-2222-3333-444444444444.jsonl') `
    -Value $sessionTwo -Encoding UTF8

# A tool-result sidecar that must never be opened. Its tokens must not appear anywhere.
$sidecarDirectory = Join-Path $projectDirectory 'aaaaaaaa-1111-2222-3333-444444444444\tool-results'
New-Item -ItemType Directory -Force -Path $sidecarDirectory | Out-Null
Set-Content -LiteralPath (Join-Path $sidecarDirectory 'results.jsonl') -Encoding UTF8 -Value @(
    (New-AssistantLine -Local $today.AddHours(12) -MessageId 'msg_leak' -RequestId 'req_leak' `
        -Model 'must-not-appear-model' -InputTokens 500000 -OutputTokens 0 -CacheRead 0 -CacheCreation 0 -BlockType 'text')
)

Write-Host "Fixture written to: $Path"
Write-Host ''
Write-Host 'Expected TODAY     : input 115, output 226, cache read 337, cache creation 448, processed 1,126'
Write-Host '                     responses 3, tool calls 3, messages 5, sessions 1'
Write-Host 'Expected YESTERDAY : processed 10,000, responses 1, tool calls 1, messages 2, sessions 1'
Write-Host 'Expected ALL TIME  : processed 11,126 (claude-opus-5 11,100, claude-sonnet-5 26)'
Write-Host ''
Write-Host ('Run:  ClaudeUsage.exe --data-dir "' + $Path + '"')

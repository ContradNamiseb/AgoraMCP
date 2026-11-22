<#
.SYNOPSIS
  Install AgoraMCP push plugins and lifecycle hooks with the resolved publish\agora.exe path.

.DESCRIPTION
  Repo templates under plugins/hooks/ and plugins/opencode-agora-notify/ keep bare
  `agora` so git stays free of machine-specific paths. This script materializes
  those templates into your user config directories with the absolute path to
  `<repo>/publish/agora.exe` (or an explicit `-AgoraExe`), backing up any existing
  config files before modifying them.

.EXAMPLE
  pwsh -File plugins/install-hooks.ps1 -Client all
  pwsh -File plugins/install-hooks.ps1 -Client codex -DryRun
#>
param(
    [ValidateSet('all', 'codex', 'cursor', 'kimi', 'zcode', 'opencode', 'antigravity')]
    [string]$Client = 'all',
    [string]$AgoraExe = '',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AgoraExe)) {
    $AgoraExe = (Join-Path $repoRoot 'publish\agora.exe').Replace('\', '/')
} else {
    $AgoraExe = $AgoraExe.Replace('\', '/')
}
if (-not (Test-Path $AgoraExe)) {
    throw "agora binary not found at '$AgoraExe'. Run publish.ps1 first or pass -AgoraExe."
}

$utf8 = [Text.UTF8Encoding]::new($false)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Backup-IfExists([string]$path) {
    if ((Test-Path $path) -and -not $DryRun) {
        Copy-Item -LiteralPath $path -Destination "$path.bak-$stamp" -Force
    }
}

function Should-Install([string]$name) {
    return ($Client -eq 'all' -or $Client -eq $name)
}

if (Should-Install 'codex') {
    $dst = Join-Path $HOME '.codex\hooks.json'
    $src = (Get-Content (Join-Path $PSScriptRoot 'hooks\codex-hooks.json') -Raw).Replace('"agora hook-check', "`"$AgoraExe hook-check")
    if ($DryRun) { Write-Output "[dry-run] would write $dst using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, $src, $utf8)
        Write-Output "installed $dst"
    }
}

if (Should-Install 'cursor') {
    $dst = Join-Path $HOME '.cursor\hooks.json'
    $src = (Get-Content (Join-Path $PSScriptRoot 'hooks\cursor-hooks.json') -Raw).Replace('"agora hook-check', "`"$AgoraExe hook-check")
    if ($DryRun) { Write-Output "[dry-run] would write $dst using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, $src, $utf8)
        Write-Output "installed $dst"
    }
}

if (Should-Install 'opencode') {
    $dst = Join-Path $HOME '.config\opencode\plugins\agora-ping.js'
    $src = (Get-Content (Join-Path $PSScriptRoot 'opencode-agora-notify\agora-ping.js') -Raw).Replace('return "agora";', "return `"$AgoraExe`";")
    if ($DryRun) { Write-Output "[dry-run] would write $dst using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, $src, $utf8)
        Write-Output "installed $dst"
    }
}

if (Should-Install 'antigravity') {
    $dir = Join-Path $HOME '.gemini\config\sidecars\agora-bridge'
    if ($DryRun) { Write-Output "[dry-run] would sync bridge.mjs, beacon.mjs, sidecar.json to $dir using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'antigravity-agora-bridge\bridge.mjs') (Join-Path $dir 'bridge.mjs') -Force
        Copy-Item (Join-Path $PSScriptRoot 'antigravity-agora-bridge\beacon.mjs') (Join-Path $dir 'beacon.mjs') -Force
        $sc = (Get-Content (Join-Path $PSScriptRoot 'antigravity-agora-bridge\sidecar.json') -Raw).Replace('"AGORA_EXE": "agora"', "`"AGORA_EXE`": `"$AgoraExe`"")
        [IO.File]::WriteAllText((Join-Path $dir 'sidecar.json'), $sc, $utf8)
        Write-Output "installed $dir"
    }
}

if (Should-Install 'kimi') {
    $dst = Join-Path $HOME '.kimi-code\config.toml'
    if (-not (Test-Path $dst)) {
        Write-Output "skipped kimi ($dst not found)"
    } else {
        $text = Get-Content $dst -Raw
        if ($text -match 'hook-check --agent kimicode') {
            Write-Output "kimi hooks already present in $dst"
        } elseif ($DryRun) {
            Write-Output "[dry-run] would append [[hooks]] to $dst using $AgoraExe"
        } else {
            Backup-IfExists $dst
            $snippet = (Get-Content (Join-Path $PSScriptRoot 'hooks\kimi-hooks.toml') -Raw)
            $snippet = ($snippet -split "`n" | Where-Object { -not $_.TrimStart().StartsWith('#') }) -join "`n"
            $snippet = $snippet.Replace('"agora hook-check', "`"$AgoraExe hook-check")
            [IO.File]::WriteAllText($dst, $text.TrimEnd() + "`n`n" + $snippet.Trim() + "`n", $utf8)
            Write-Output "updated $dst (run 'kimi doctor' to verify)"
        }
    }
}

if (Should-Install 'zcode') {
    $dst = Join-Path $HOME '.zcode\cli\config.json'
    if (-not (Test-Path $dst)) {
        Write-Output "skipped zcode ($dst not found)"
    } elseif ($DryRun) {
        Write-Output "[dry-run] would merge agora hook-check entries into $dst using $AgoraExe"
    } else {
        Backup-IfExists $dst
        $cfg = Get-Content $dst -Raw | ConvertFrom-Json
        if (-not $cfg.hooks) { $cfg | Add-Member -NotePropertyName hooks -NotePropertyValue ([PSCustomObject]@{ enabled = $true; events = [PSCustomObject]@{} }) }
        if (-not $cfg.hooks.events) { $cfg.hooks | Add-Member -NotePropertyName events -NotePropertyValue ([PSCustomObject]@{}) }
        $events = $cfg.hooks.events
        foreach ($ev in @(
            @{ Name = 'SessionStart'; Record = $true },
            @{ Name = 'UserPromptSubmit'; Record = $true },
            @{ Name = 'PostToolUse'; Record = $false },
            @{ Name = 'Stop'; Record = $false }
        )) {
            $existing = if ($events.PSObject.Properties[$ev.Name]) { @($events.$($ev.Name)) } else { @() }
            if (($existing | ConvertTo-Json -Depth 6) -match 'hook-check') { continue }
            $argsList = @('hook-check', '--agent', 'zcode', '--format', 'zcode', '--event', $ev.Name)
            if ($ev.Record) { $argsList += '--record-session' }
            $grp = [PSCustomObject]@{
                hooks = @([PSCustomObject]@{ type = 'process'; command = $AgoraExe; args = $argsList; timeoutMs = 5000 })
            }
            $merged = @($existing) + @($grp)
            if ($events.PSObject.Properties[$ev.Name]) { $events.$($ev.Name) = $merged }
            else { $events | Add-Member -NotePropertyName $ev.Name -NotePropertyValue $merged }
        }
        [IO.File]::WriteAllText($dst, ($cfg | ConvertTo-Json -Depth 12) + "`n", $utf8)
        Write-Output "updated $dst"
    }
}

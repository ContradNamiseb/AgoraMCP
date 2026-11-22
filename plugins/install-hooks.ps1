<#
.SYNOPSIS
  Install AgoraMCP push plugins and lifecycle hooks with the resolved publish/agora[.exe] path.

.DESCRIPTION
  Repo templates under plugins/hooks/ and plugins/opencode-agora-notify/ keep bare
  `agora` so git stays free of machine-specific paths. This script auto-detects
  Windows (publish/agora.exe) vs Linux/macOS (publish/agora), merges hooks into
  your user config directories, preserves any existing hooks, skips unchanged
  files (no duplicate backups on re-run), and backs up every modified file before
  writing.

.EXAMPLE
  pwsh -File plugins/install-hooks.ps1 -Client all
  pwsh -File plugins/install-hooks.ps1 -Client codex -DryRun
#>
param(
    [ValidateSet('all', 'codex', 'cursor', 'kimi', 'zcode', 'opencode', 'antigravity')]
    [string]$Client = 'all',
    [string]$AgoraExe = '',
    [string]$HomeDir = '',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

$isWin = ($IsWindows -eq $true) -or ($env:OS -eq 'Windows_NT')
$repoRoot = Split-Path -Parent $PSScriptRoot

function Join-Segments([string]$base, [string[]]$segments) {
    $p = $base
    foreach ($seg in $segments) {
        $p = Join-Path $p $seg
    }
    return $p
}

if ([string]::IsNullOrWhiteSpace($AgoraExe)) {
    $primary = Join-Segments $repoRoot @('publish', $(if ($isWin) { 'agora.exe' } else { 'agora' }))
    $secondary = Join-Segments $repoRoot @('publish', $(if ($isWin) { 'agora' } else { 'agora.exe' }))
    if (Test-Path -LiteralPath $primary) {
        $AgoraExe = $primary.Replace('\', '/')
    } elseif (Test-Path -LiteralPath $secondary) {
        $AgoraExe = $secondary.Replace('\', '/')
    } else {
        $AgoraExe = $primary.Replace('\', '/')
    }
} else {
    $AgoraExe = $AgoraExe.Replace('\', '/')
}
if (-not (Test-Path -LiteralPath $AgoraExe)) {
    throw "agora binary not found at '$AgoraExe'. Publish first (publish.ps1 on Windows or 'dotnet publish src/AgoraMCP.McpServer -c Release -o publish' on Linux/macOS) or pass -AgoraExe."
}

$targetHome = if ([string]::IsNullOrWhiteSpace($HomeDir)) { $HOME } else { $HomeDir }
$utf8 = [Text.UTF8Encoding]::new($false)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$hasSpaces = $AgoraExe -match '\s'
$warnedSpacePath = $false

function Backup-IfExists([string]$path) {
    if ((Test-Path -LiteralPath $path) -and -not $DryRun) {
        Copy-Item -LiteralPath $path -Destination "$path.bak-$stamp" -Force
    }
}

function Should-Install([string]$name) {
    return ($Client -eq 'all' -or $Client -eq $name)
}

function Warn-SpacePathOnce([string]$clientName) {
    if ($hasSpaces -and -not $script:warnedSpacePath) {
        $script:warnedSpacePath = $true
        Write-Warning (
            "AgoraExe path '$AgoraExe' contains whitespace. String-command hooks ($clientName) are quoted as " +
            '"<path>" hook-check ..., which works under cmd.exe and bash, but fails if a client invokes hooks via ' +
            'PowerShell (which requires & "<path>"). Prefer a space-free path or put publish/ on PATH.'
        )
    }
}

function Format-CommandString([string]$exe, [string]$hookArgs, [string]$clientName) {
    if ($exe -match '\s') {
        Warn-SpacePathOnce $clientName
        return "`"$exe`" $hookArgs"
    }
    return "$exe $hookArgs"
}

function Escape-TomlBasicString([string]$value) {
    return $value.Replace('\', '\\').Replace('"', '\"')
}

function Ensure-Property($obj, [string]$name, $defaultValue) {
    if (-not $obj.PSObject.Properties[$name]) {
        $obj | Add-Member -NotePropertyName $name -NotePropertyValue $defaultValue
    }
    return $obj.$name
}

# Returns $true if the file differs from $content (and writes + backs up unless -DryRun);
# returns $false when already byte-identical so re-runs never pile up .bak files (O1).
function Sync-FileIfChanged([string]$path, [string]$content) {
    if (Test-Path -LiteralPath $path) {
        $current = [IO.File]::ReadAllText($path, $utf8)
        if ($current -ceq $content) {
            return $false
        }
    }
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Backup-IfExists $path
        [IO.File]::WriteAllText($path, $content, $utf8)
    }
    return $true
}

if (Should-Install 'codex') {
    $dst = Join-Segments $targetHome @('.codex', 'hooks.json')
    $cfg = if (Test-Path -LiteralPath $dst) {
        Get-Content -LiteralPath $dst -Raw | ConvertFrom-Json
    } else {
        [PSCustomObject]@{ hooks = [PSCustomObject]@{} }
    }
    $hooksObj = Ensure-Property $cfg 'hooks' ([PSCustomObject]@{})
    $changed = $false
    foreach ($ev in @(
        @{ Name = 'SessionStart'; Record = $true },
        @{ Name = 'UserPromptSubmit'; Record = $true },
        @{ Name = 'PostToolUse'; Record = $false },
        @{ Name = 'Stop'; Record = $false }
    )) {
        $existing = if ($hooksObj.PSObject.Properties[$ev.Name]) { @($hooksObj.$($ev.Name)) } else { @() }
        if (($existing | ConvertTo-Json -Depth 6) -match 'hook-check\s+--agent\s+codex') { continue }
        $flag = if ($ev.Record) { ' --record-session' } else { '' }
        $cmdStr = Format-CommandString $AgoraExe "hook-check --agent codex --format codex --event $($ev.Name)$flag" 'codex'
        $entry = [PSCustomObject]@{
            hooks = @(
                [PSCustomObject]@{
                    type = 'command'
                    command = $cmdStr
                    timeout = 5
                }
            )
        }
        $merged = @($existing) + @($entry)
        if ($hooksObj.PSObject.Properties[$ev.Name]) { $hooksObj.$($ev.Name) = $merged }
        else { $hooksObj | Add-Member -NotePropertyName $ev.Name -NotePropertyValue $merged }
        $changed = $true
    }
    if (-not $changed) {
        Write-Output "codex hooks already present in $dst"
    } elseif ($DryRun) {
        Write-Output "[dry-run] would merge agora hook-check entries into $dst using $AgoraExe"
    } else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, ($cfg | ConvertTo-Json -Depth 12) + "`n", $utf8)
        Write-Output "updated $dst"
    }
}

if (Should-Install 'cursor') {
    $dst = Join-Segments $targetHome @('.cursor', 'hooks.json')
    $cfg = if (Test-Path -LiteralPath $dst) {
        Get-Content -LiteralPath $dst -Raw | ConvertFrom-Json
    } else {
        [PSCustomObject]@{ version = 1; hooks = [PSCustomObject]@{} }
    }
    $null = Ensure-Property $cfg 'version' 1
    $hooksObj = Ensure-Property $cfg 'hooks' ([PSCustomObject]@{})
    $changed = $false
    foreach ($ev in @(
        @{ Name = 'beforeSubmitPrompt'; Record = $true },
        @{ Name = 'stop'; Record = $false }
    )) {
        $existing = if ($hooksObj.PSObject.Properties[$ev.Name]) { @($hooksObj.$($ev.Name)) } else { @() }
        if (($existing | ConvertTo-Json -Depth 6) -match 'hook-check\s+--agent\s+cursor') { continue }
        $flag = if ($ev.Record) { ' --record-session' } else { '' }
        $cmdStr = Format-CommandString $AgoraExe "hook-check --agent cursor --format cursor --event $($ev.Name)$flag" 'cursor'
        $entry = [PSCustomObject]@{ command = $cmdStr }
        $merged = @($existing) + @($entry)
        if ($hooksObj.PSObject.Properties[$ev.Name]) { $hooksObj.$($ev.Name) = $merged }
        else { $hooksObj | Add-Member -NotePropertyName $ev.Name -NotePropertyValue $merged }
        $changed = $true
    }
    if (-not $changed) {
        Write-Output "cursor hooks already present in $dst"
    } elseif ($DryRun) {
        Write-Output "[dry-run] would merge agora hook-check entries into $dst using $AgoraExe"
    } else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, ($cfg | ConvertTo-Json -Depth 12) + "`n", $utf8)
        Write-Output "updated $dst"
    }
}

if (Should-Install 'opencode') {
    $dst = Join-Segments $targetHome @('.config', 'opencode', 'plugins', 'agora-ping.js')
    $escapedJs = $AgoraExe.Replace('\', '\\').Replace('"', '\"')
    $srcPath = Join-Segments $PSScriptRoot @('opencode-agora-notify', 'agora-ping.js')
    $src = [IO.File]::ReadAllText($srcPath, $utf8).Replace('return "agora";', "return `"$escapedJs`";")
    if (Sync-FileIfChanged $dst $src) {
        if ($DryRun) { Write-Output "[dry-run] would update $dst using $AgoraExe" }
        else { Write-Output "installed $dst" }
    } else {
        Write-Output "opencode plugin already up to date in $dst"
    }
}

if (Should-Install 'antigravity') {
    $dir = Join-Segments $targetHome @('.gemini', 'config', 'sidecars', 'agora-bridge')
    $bridgeDst = Join-Path $dir 'bridge.mjs'
    $beaconDst = Join-Path $dir 'beacon.mjs'
    $sidecarDst = Join-Path $dir 'sidecar.json'

    $bridgeContent = [IO.File]::ReadAllText((Join-Segments $PSScriptRoot @('antigravity-agora-bridge', 'bridge.mjs')), $utf8)
    $beaconContent = [IO.File]::ReadAllText((Join-Segments $PSScriptRoot @('antigravity-agora-bridge', 'beacon.mjs')), $utf8)
    $escapedJsonExe = $AgoraExe.Replace('\', '\\').Replace('"', '\"')
    $sidecarContent = [IO.File]::ReadAllText((Join-Segments $PSScriptRoot @('antigravity-agora-bridge', 'sidecar.json')), $utf8).Replace('"AGORA_EXE": "agora"', "`"AGORA_EXE`": `"$escapedJsonExe`"")

    $updatedFiles = @()
    if (Sync-FileIfChanged $bridgeDst $bridgeContent) { $updatedFiles += 'bridge.mjs' }
    if (Sync-FileIfChanged $beaconDst $beaconContent) { $updatedFiles += 'beacon.mjs' }
    if (Sync-FileIfChanged $sidecarDst $sidecarContent) { $updatedFiles += 'sidecar.json' }

    if ($updatedFiles.Count -eq 0) {
        Write-Output "antigravity sidecar already up to date in $dir"
    } elseif ($DryRun) {
        Write-Output "[dry-run] would back up and update $($updatedFiles -join ', ') in $dir using $AgoraExe"
    } else {
        Write-Output "updated $($updatedFiles -join ', ') in $dir"
    }
}

if (Should-Install 'kimi') {
    $dst = Join-Segments $targetHome @('.kimi-code', 'config.toml')
    if (-not (Test-Path -LiteralPath $dst)) {
        Write-Output "skipped kimi ($dst not found)"
    } else {
        $text = Get-Content -LiteralPath $dst -Raw
        if ($text -match 'hook-check --agent kimicode') {
            Write-Output "kimi hooks already present in $dst"
        } elseif ($DryRun) {
            if ($hasSpaces) { Warn-SpacePathOnce 'kimi' }
            Write-Output "[dry-run] would append [[hooks]] to $dst using $AgoraExe"
        } else {
            Backup-IfExists $dst
            $promptCmd = Escape-TomlBasicString (Format-CommandString $AgoraExe 'hook-check --agent kimicode --format kimi --event UserPromptSubmit --record-session' 'kimi')
            $stopCmd = Escape-TomlBasicString (Format-CommandString $AgoraExe 'hook-check --agent kimicode --format kimi --event Stop' 'kimi')
            $snippet = @"
[[hooks]]
event = "UserPromptSubmit"
matcher = ""
command = "$promptCmd"
timeout = 5

[[hooks]]
event = "Stop"
matcher = ""
command = "$stopCmd"
timeout = 5
"@
            [IO.File]::WriteAllText($dst, $text.TrimEnd() + "`n`n" + $snippet.Trim() + "`n", $utf8)
            Write-Output "updated $dst (run 'kimi doctor' to verify)"
        }
    }
}

if (Should-Install 'zcode') {
    $dst = Join-Segments $targetHome @('.zcode', 'cli', 'config.json')
    if (-not (Test-Path -LiteralPath $dst)) {
        Write-Output "skipped zcode ($dst not found)"
    } else {
        $cfg = Get-Content -LiteralPath $dst -Raw | ConvertFrom-Json
        $hooksRoot = Ensure-Property $cfg 'hooks' ([PSCustomObject]@{ enabled = $true; events = [PSCustomObject]@{} })
        $events = Ensure-Property $hooksRoot 'events' ([PSCustomObject]@{})
        $changed = $false
        foreach ($ev in @(
            @{ Name = 'SessionStart'; Record = $true },
            @{ Name = 'UserPromptSubmit'; Record = $true },
            @{ Name = 'PostToolUse'; Record = $false },
            @{ Name = 'Stop'; Record = $false }
        )) {
            $existing = if ($events.PSObject.Properties[$ev.Name]) { @($events.$($ev.Name)) } else { @() }
            $existingJson = $existing | ConvertTo-Json -Depth 6
            if ($existingJson -match '"hook-check"' -and $existingJson -match '"zcode"') { continue }
            $argsList = @('hook-check', '--agent', 'zcode', '--format', 'zcode', '--event', $ev.Name)
            if ($ev.Record) { $argsList += '--record-session' }
            $grp = [PSCustomObject]@{
                hooks = @([PSCustomObject]@{ type = 'process'; command = $AgoraExe; args = $argsList; timeoutMs = 5000 })
            }
            $merged = @($existing) + @($grp)
            if ($events.PSObject.Properties[$ev.Name]) { $events.$($ev.Name) = $merged }
            else { $events | Add-Member -NotePropertyName $ev.Name -NotePropertyValue $merged }
            $changed = $true
        }
        if (-not $changed) {
            Write-Output "zcode hooks already present in $dst"
        } elseif ($DryRun) {
            Write-Output "[dry-run] would merge agora hook-check entries into $dst using $AgoraExe"
        } else {
            Backup-IfExists $dst
            [IO.File]::WriteAllText($dst, ($cfg | ConvertTo-Json -Depth 12) + "`n", $utf8)
            Write-Output "updated $dst"
        }
    }
}

<#
.SYNOPSIS
  Install AgoraMCP push plugins and lifecycle hooks with the resolved publish\agora.exe path.

.DESCRIPTION
  Repo templates under plugins/hooks/ and plugins/opencode-agora-notify/ keep bare
  `agora` so git stays free of machine-specific paths. This script merges those
  hooks into your user config directories using the absolute path to
  `<repo>/publish/agora.exe` (or an explicit `-AgoraExe`), preserving any existing
  hooks and backing up every modified file before writing.

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

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AgoraExe)) {
    $AgoraExe = (Join-Path $repoRoot 'publish\agora.exe').Replace('\', '/')
} else {
    $AgoraExe = $AgoraExe.Replace('\', '/')
}
if (-not (Test-Path -LiteralPath $AgoraExe)) {
    throw "agora binary not found at '$AgoraExe'. Run publish.ps1 first or pass -AgoraExe."
}

$targetHome = if ([string]::IsNullOrWhiteSpace($HomeDir)) { $HOME } else { $HomeDir }
$utf8 = [Text.UTF8Encoding]::new($false)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Backup-IfExists([string]$path) {
    if ((Test-Path -LiteralPath $path) -and -not $DryRun) {
        Copy-Item -LiteralPath $path -Destination "$path.bak-$stamp" -Force
    }
}

function Should-Install([string]$name) {
    return ($Client -eq 'all' -or $Client -eq $name)
}

function Format-CommandString([string]$exe, [string]$hookArgs) {
    # Quote executable path whenever it contains whitespace so spaces in repo
    # paths never split the command string (I2).
    if ($exe -match '\s') {
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

if (Should-Install 'codex') {
    $dst = Join-Path $targetHome '.codex\hooks.json'
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
        $cmdStr = Format-CommandString $AgoraExe "hook-check --agent codex --format codex --event $($ev.Name)$flag"
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
    $dst = Join-Path $targetHome '.cursor\hooks.json'
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
        $cmdStr = Format-CommandString $AgoraExe "hook-check --agent cursor --format cursor --event $($ev.Name)$flag"
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
    $dst = Join-Path $targetHome '.config\opencode\plugins\agora-ping.js'
    $escapedJs = $AgoraExe.Replace('\', '\\').Replace('"', '\"')
    $src = (Get-Content (Join-Path $PSScriptRoot 'opencode-agora-notify\agora-ping.js') -Raw).Replace('return "agora";', "return `"$escapedJs`";")
    if ($DryRun) { Write-Output "[dry-run] would write $dst using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Backup-IfExists $dst
        [IO.File]::WriteAllText($dst, $src, $utf8)
        Write-Output "installed $dst"
    }
}

if (Should-Install 'antigravity') {
    $dir = Join-Path $targetHome '.gemini\config\sidecars\agora-bridge'
    $bridgeDst = Join-Path $dir 'bridge.mjs'
    $beaconDst = Join-Path $dir 'beacon.mjs'
    $sidecarDst = Join-Path $dir 'sidecar.json'
    if ($DryRun) { Write-Output "[dry-run] would back up and sync bridge.mjs, beacon.mjs, sidecar.json to $dir using $AgoraExe" }
    else {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Backup-IfExists $bridgeDst
        Backup-IfExists $beaconDst
        Backup-IfExists $sidecarDst
        Copy-Item (Join-Path $PSScriptRoot 'antigravity-agora-bridge\bridge.mjs') $bridgeDst -Force
        Copy-Item (Join-Path $PSScriptRoot 'antigravity-agora-bridge\beacon.mjs') $beaconDst -Force
        $scObj = Get-Content (Join-Path $PSScriptRoot 'antigravity-agora-bridge\sidecar.json') -Raw | ConvertFrom-Json
        $envObj = Ensure-Property $scObj 'env' ([PSCustomObject]@{})
        if ($envObj.PSObject.Properties['AGORA_EXE']) { $envObj.AGORA_EXE = $AgoraExe }
        else { $envObj | Add-Member -NotePropertyName AGORA_EXE -NotePropertyValue $AgoraExe }
        [IO.File]::WriteAllText($sidecarDst, ($scObj | ConvertTo-Json -Depth 8) + "`n", $utf8)
        Write-Output "installed $dir"
    }
}

if (Should-Install 'kimi') {
    $dst = Join-Path $targetHome '.kimi-code\config.toml'
    if (-not (Test-Path -LiteralPath $dst)) {
        Write-Output "skipped kimi ($dst not found)"
    } else {
        $text = Get-Content -LiteralPath $dst -Raw
        if ($text -match 'hook-check --agent kimicode') {
            Write-Output "kimi hooks already present in $dst"
        } elseif ($DryRun) {
            Write-Output "[dry-run] would append [[hooks]] to $dst using $AgoraExe"
        } else {
            Backup-IfExists $dst
            $promptCmd = Escape-TomlBasicString (Format-CommandString $AgoraExe 'hook-check --agent kimicode --format kimi --event UserPromptSubmit --record-session')
            $stopCmd = Escape-TomlBasicString (Format-CommandString $AgoraExe 'hook-check --agent kimicode --format kimi --event Stop')
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
    $dst = Join-Path $targetHome '.zcode\cli\config.json'
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

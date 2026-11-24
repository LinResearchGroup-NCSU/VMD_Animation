<#
.SYNOPSIS
  Build, run, deploy and test the VMD -> Unity VR viewer.

.DESCRIPTION
  One Windows OpenXR player serves every headset. The PC computes and renders, and the headset only
  displays and sends input:
    - Valve Index through SteamVR
    - Meta Quest through Meta Horizon Link / Air Link, or through Steam Link (SteamVR)
    - Meta XR Simulator on this PC, for testing without a headset
  The OpenXR runtime is chosen per launch through XR_RUNTIME_JSON; the system-wide runtime setting
  (which needs admin rights to change) is never touched.

  Commands (run from the repository root as "vr <command>", or ".\tools\vr.ps1 <command>"):

    build  [-Stereo SPI|MultiPass|All] [-KeepSettingsChanges]
        Batch-mode Unity build of the Windows player into Unity_VR_Client\Builds\Windows\<Stereo>.
        The Unity Editor must not have the project open.

    run    [-Runtime xrsim|steamvr|metalink|desktop|<path to runtime .json>] [-Stereo SPI|MultiPass]
           [-Seconds 60] [-Stop] [-PlayerArgs "<extra player arguments>"]
        Starts the built player against the chosen runtime, follows its log and prints a summary.
        Default runtime: xrsim (Meta XR Simulator). With xrsim the player renders with Vulkan
        (-force-vulkan): the simulator has no direct D3D11 renderer, and its D3D11-to-Vulkan
        conversion gave black frames on an Intel GPU. Headset runtimes keep D3D11.
        -PlayerArgs takes one quoted string, e.g. -PlayerArgs "-vmdPort 45455 -force-d3d11".

    deploy [-Target all|index|quest] [-SkipBuild] [-LocalExamples]
        Builds SPI and MultiPass (unless -SkipBuild) and writes the lab bundle
        Unity_VR_Client\Builds\deploy\VMD_VR_Lab_<date>_<commit>.zip with double-click launchers.
        -LocalExamples also packs the local examples, that is the git-ignored animation\*.tcl
        scripts and data\ files (not movies or subfolders), and lists them in EXTRA_EXAMPLES.txt;
        the name ends in _examples.

    test
        Runs the VMD bridge regression tests (VMD_VR_Bridge\_test_*.tcl) in VMD text mode.

    help
        Shows this text.

  Environment overrides: UNITY_EDITOR (Unity.exe), VMD_EXE (vmd.exe), VMD_XRSIM_JSON (Meta XR
  Simulator runtime JSON), VMD_VR_PORT (bridge port, default 45454).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'run', 'deploy', 'test', 'help')]
    [string]$Command = 'help',

    [string]$Stereo,
    [string]$Runtime = 'xrsim',
    [int]$Seconds = 60,
    [switch]$Stop,
    [ValidateSet('all', 'index', 'quest')]
    [string]$Target = 'all',
    [switch]$SkipBuild,
    [switch]$LocalExamples,
    [switch]$KeepSettingsChanges,

    # One quoted string: "powershell -File" (used by vr.cmd) cannot pass a "--" separator.
    [string]$PlayerArgs = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'Unity_VR_Client'
$Builds = Join-Path $Project 'Builds'
$Logs = Join-Path $Builds 'logs'
$ExeName = 'Unity_VR_Client.exe'
$StereoModes = @('SPI', 'MultiPass')

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }
function Fail([string]$Text) { Write-Host "ERROR: $Text" -ForegroundColor Red; exit 1 }

# Quote one command-line argument for a Windows process.
function Quote-Arg([string]$Value) {
    if ($Value -eq '') { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Join-Args([string[]]$Values) { ($Values | ForEach-Object { Quote-Arg $_ }) -join ' ' }

function Get-StereoList([string]$Value, [string]$Default) {
    if (-not $Value) { $Value = $Default }
    if ($Value -ieq 'All') { return $StereoModes }
    foreach ($mode in $StereoModes) { if ($mode -ieq $Value) { return @($mode) } }
    Fail "-Stereo must be SPI, MultiPass or All, not '$Value'."
}

function Get-PlayerPath([string]$Mode) { Join-Path $Builds "Windows\$Mode\$ExeName" }

# ---------------------------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------------------------

function Get-UnityEditor {
    if ($env:UNITY_EDITOR) {
        if (Test-Path -LiteralPath $env:UNITY_EDITOR) { return $env:UNITY_EDITOR }
        Fail "UNITY_EDITOR points to a missing file: $env:UNITY_EDITOR"
    }
    $versionFile = Join-Path $Project 'ProjectSettings\ProjectVersion.txt'
    $line = Get-Content -LiteralPath $versionFile | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
    if (-not ($line -match '^m_EditorVersion:\s*(\S+)')) { Fail "Cannot read the Unity version from $versionFile." }
    $version = $Matches[1]
    $hubRoots = @(Join-Path $env:ProgramFiles 'Unity\Hub\Editor')
    $secondary = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path -LiteralPath $secondary) {
        $extra = (Get-Content -LiteralPath $secondary -Raw).Trim().Trim('"')
        if ($extra) { $hubRoots += $extra }
    }
    foreach ($hubRoot in $hubRoots) {
        $candidate = Join-Path $hubRoot "$version\Editor\Unity.exe"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    Fail "Unity $version is not installed under $($hubRoots -join ', '). Install it with Unity Hub or set UNITY_EDITOR."
}

# True while a Unity Editor holds the project's lock file.
function Test-ProjectOpen {
    $lock = Join-Path $Project 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lock)) { return $false }
    try {
        $stream = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

# Tracked files under the paths Unity may rewrite during a build, that differ from HEAD.
$SettingsPaths = @('Unity_VR_Client/ProjectSettings', 'Unity_VR_Client/Assets/XR', 'Unity_VR_Client/Assets/Settings')
function Get-ModifiedSettings {
    $lines = & git -C $Root status --porcelain=v1 --untracked-files=no -- $SettingsPaths 2>$null
    $files = @()
    foreach ($entry in $lines) { if ($entry.Length -gt 3) { $files += $entry.Substring(3).Trim('"') } }
    return $files
}

function Invoke-UnityBuild([string]$Mode) {
    $unity = Get-UnityEditor
    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
    $output = Join-Path $Builds "Windows\$Mode"
    $log = Join-Path $Logs "build_$Mode.log"
    $arguments = @('-batchmode', '-quit', '-buildTarget', 'Win64', '-projectPath', $Project,
        '-executeMethod', 'VMDBuild.BuildWindows', '-vmdStereo', $Mode, '-vmdOutput', $output, '-logFile', $log)
    Write-Step "Building $Mode player (log: $log)"
    $started = Get-Date
    $process = Start-Process -FilePath $unity -ArgumentList (Join-Args $arguments) -PassThru -WindowStyle Hidden
    $null = $process.Handle
    $process.WaitForExit()
    $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    $summary = @()
    if (Test-Path -LiteralPath $log) {
        $summary = Select-String -LiteralPath $log -Pattern '\[VMDBuild\]|error CS\d+|Build Finished, Result' | ForEach-Object { $_.Line.Trim() }
    }
    $summary | Select-Object -Last 12 | ForEach-Object { Write-Host "    $_" }
    $player = Join-Path $output $ExeName
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $player)) {
        Fail "The $Mode build failed (exit code $($process.ExitCode), $minutes min). See $log"
    }
    Write-Ok "$Mode player: $player ($minutes min)"
}

function Invoke-Build([string[]]$Modes) {
    if (Test-ProjectOpen) {
        Fail "The Unity Editor has Unity_VR_Client open. Close it (or save and quit), then run the build again."
    }
    $before = Get-ModifiedSettings
    try {
        foreach ($mode in $Modes) { Invoke-UnityBuild $mode }
    } finally {
        $changed = @(Get-ModifiedSettings | Where-Object { $before -notcontains $_ })
        if ($changed.Count -gt 0) {
            if ($KeepSettingsChanges) {
                Write-Warn "Unity changed these tracked files during the build (kept, -KeepSettingsChanges):"
                $changed | ForEach-Object { Write-Warn "  $_" }
            } else {
                & git -C $Root checkout -- $changed
                Write-Warn "Restored tracked files Unity rewrote during the build:"
                $changed | ForEach-Object { Write-Warn "  $_" }
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# OpenXR runtime lookup (shared by run; the lab launchers do the same in tools\bundle\run_player.cmd)
# ---------------------------------------------------------------------------------------------

$RuntimeFiles = @{
    steamvr  = 'steamxr_win64.json'
    metalink = 'oculus_openxr_64.json'
    xrsim    = 'meta_openxr_simulator.json'
}

function Get-RegisteredRuntimes {
    $found = @()
    foreach ($hive in @('HKLM:', 'HKCU:')) {
        $key = "$hive\SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes"
        if (Test-Path $key) { $found += (Get-Item $key).Property }
    }
    return $found
}

function Find-RuntimeJson([string]$Kind) {
    if ($Kind -match '\.json$') {
        if (Test-Path -LiteralPath $Kind) { return (Resolve-Path -LiteralPath $Kind).Path }
        Fail "Runtime file not found: $Kind"
    }
    $file = $RuntimeFiles[$Kind]
    if (-not $file) { Fail "-Runtime must be xrsim, steamvr, metalink, desktop or a runtime .json path, not '$Kind'." }

    $candidates = @()
    if ($Kind -eq 'xrsim' -and $env:VMD_XRSIM_JSON) { $candidates += $env:VMD_XRSIM_JSON }
    $candidates += Get-RegisteredRuntimes | Where-Object { (Split-Path -Leaf $_) -ieq $file }
    switch ($Kind) {
        'steamvr' {
            $vrpath = Join-Path $env:LOCALAPPDATA 'openvr\openvrpaths.vrpath'
            if (Test-Path -LiteralPath $vrpath) {
                try { (Get-Content -LiteralPath $vrpath -Raw | ConvertFrom-Json).runtime | ForEach-Object { $candidates += (Join-Path $_ $file) } } catch { }
            }
            $candidates += Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\SteamVR\$file"
        }
        'metalink' { $candidates += Join-Path $env:ProgramFiles "Oculus\Support\oculus-runtime\$file" }
        'xrsim' {
            foreach ($base in @($env:ProgramFiles, $env:LOCALAPPDATA, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
                foreach ($dir in @(Get-ChildItem -LiteralPath $base -Directory -Filter 'Meta*' -ErrorAction SilentlyContinue)) {
                    $hit = Get-ChildItem -LiteralPath $dir.FullName -Recurse -Depth 4 -Filter $file -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($hit) { $candidates += $hit.FullName }
                }
            }
        }
    }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    $hint = @{
        steamvr  = 'Install SteamVR (Steam > Library > SteamVR) and start it once.'
        metalink = 'Install the Meta Horizon Link PC app.'
        xrsim    = 'Install Meta XR Simulator for Windows, or set VMD_XRSIM_JSON to its meta_openxr_simulator.json.'
    }[$Kind]
    Fail "No $Kind OpenXR runtime ($file) found. $hint"
}

# ---------------------------------------------------------------------------------------------
# run
# ---------------------------------------------------------------------------------------------

# -PlayerArgs split into separate arguments (whitespace-separated; no quoting inside).
function Get-PlayerArgList { @($PlayerArgs -split '\s+' | Where-Object { $_ }) }

function Get-BridgePort {
    $port = 45454
    if ($env:VMD_VR_PORT -match '^\d+$') { $port = [int]$env:VMD_VR_PORT }
    $extra = @(Get-PlayerArgList)
    for ($i = 0; $i -lt $extra.Count - 1; $i++) {
        if ($extra[$i] -ieq '-vmdPort' -and $extra[$i + 1] -match '^\d+$') { $port = [int]$extra[$i + 1] }
    }
    return $port
}

function Test-BridgeListening([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $attempt = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($attempt.AsyncWaitHandle.WaitOne(500) -and $client.Connected) { return $true }
        return $false
    } catch { return $false } finally { $client.Close() }
}

function Invoke-Run {
    $mode = @(Get-StereoList $Stereo 'SPI')[0]
    $player = Get-PlayerPath $mode
    if (-not (Test-Path -LiteralPath $player)) { Fail "No $mode player at $player. Run 'vr build -Stereo $mode' first." }

    $desktop = $Runtime -ieq 'desktop'
    $runtimeJson = $null
    if (-not $desktop) { $runtimeJson = Find-RuntimeJson $Runtime.ToLowerInvariant() }

    $port = Get-BridgePort
    if (Test-BridgeListening $port) { Write-Ok "VMD bridge is listening on 127.0.0.1:$port." }
    else { Write-Warn "No VMD bridge on 127.0.0.1:$port. In VMD: source animation/render.tcl, then source start_vr.tcl. The player retries by itself." }

    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
    $label = 'desktop'
    if (-not $desktop) { $label = ($Runtime -replace '[^A-Za-z0-9]+', '_') }
    if ($label.Length -gt 24) { $label = 'custom' }
    $log = Join-Path $Logs ("run_{0}_{1}.log" -f $label, (Get-Date -Format 'yyyyMMdd_HHmmss'))

    $arguments = @('-logFile', $log)
    if ($desktop) { $arguments += '-desktop' }
    $extra = @(Get-PlayerArgList)
    # Meta XR Simulator renders through Vulkan; give it Vulkan frames unless a graphics API was chosen.
    $isXrsim = $runtimeJson -and ((Split-Path -Leaf $runtimeJson) -ieq $RuntimeFiles['xrsim'])
    if ($isXrsim -and -not ($extra | Where-Object { $_ -match '^-force-(d3d11|d3d12|vulkan|glcore)' })) { $arguments += '-force-vulkan' }
    $arguments += $extra

    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $player
    $info.Arguments = Join-Args $arguments
    $info.WorkingDirectory = Split-Path -Parent $player
    $info.UseShellExecute = $false
    # The player also echoes its log to stdout; discard that copy (the log file is followed below).
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($info.EnvironmentVariables.ContainsKey('XR_RUNTIME_JSON')) { $info.EnvironmentVariables.Remove('XR_RUNTIME_JSON') }
    if ($runtimeJson) { $info.EnvironmentVariables['XR_RUNTIME_JSON'] = $runtimeJson }

    if ($desktop) { Write-Step "Starting $mode player in Desktop mode" }
    else { Write-Step "Starting $mode player with OpenXR runtime $runtimeJson" }
    Write-Host "    log: $log"
    $process = [System.Diagnostics.Process]::Start($info)
    $null = $process.StandardOutput.BaseStream.CopyToAsync([System.IO.Stream]::Null)
    $null = $process.StandardError.BaseStream.CopyToAsync([System.IO.Stream]::Null)

    $markers = [ordered]@{ Mode = $null; XR = $null; Fitted = $null; View = $null; VMD = $null }
    $pattern = '\[VMDXRBootstrap\]|\[VMDClient\] (Connected|Connection error)|\[VMDView\]|Exception|Fatal|OpenXR.*(error|fail)|XR_ERROR'
    # In Desktop mode the OpenXR loader still probes for a runtime at startup and logs errors; ignore them.
    $ignore = '^\s*$'
    if ($desktop) { $ignore = 'OpenXR-Loader|\[XR\]' }
    $deadline = (Get-Date).AddSeconds($Seconds)
    $position = 0L
    $pending = ''
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $log) {
            $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try {
                if ($stream.Length -gt $position) {
                    $stream.Position = $position
                    $reader = New-Object System.IO.StreamReader($stream)
                    $pending += $reader.ReadToEnd()
                    $position = $stream.Length
                }
            } finally { $stream.Close() }
            $parts = @($pending -split "`r?`n")
            $pending = $parts[-1]
            $complete = @()
            if ($parts.Count -gt 1) { $complete = $parts[0..($parts.Count - 2)] }
            foreach ($line in $complete) {
                if ($line -notmatch $pattern -or $line -match $ignore) { continue }
                Write-Host "    | $line"
                if ($line -match '\[VMDXRBootstrap\] (Desktop|Simulator|Headset) mode') { $markers.Mode = $Matches[1] }
                if ($line -match 'XR started') { $markers.XR = 'started' }
                if ($line -match 'reloading in desktop mode') { $markers.XR = 'failed (desktop fallback)' }
                if ($line -match 'Fitted molecule') { $markers.Fitted = 'yes' }
                if ($line -match '\[VMDView\]') { $markers.View = 'logged' }
                if ($line -match '\[VMDClient\] Connected') { $markers.VMD = 'connected' }
            }
        }
        if ($process.HasExited) { Write-Warn "The player exited (code $($process.ExitCode))."; break }
        # Done when everything expected has appeared (Desktop mode logs no XR start or fit).
        if ($desktop -and $markers.Mode -and $markers.VMD) { break }
        if (-not $desktop -and $markers.XR -and $markers.Fitted -and $markers.VMD) { break }
    }

    Write-Step 'Summary'
    $passed = $true
    $expectedMode = 'Headset'
    if ($desktop) { $expectedMode = 'Desktop' }
    foreach ($name in $markers.Keys) {
        $value = $markers[$name]
        if (-not $value) { $value = '-' }
        Write-Host ("    {0,-7} {1}" -f $name, $value)
    }
    if ($markers.Mode -ne $expectedMode) { $passed = $false }
    if (-not $desktop -and $markers.XR -ne 'started') { $passed = $false }
    if (-not $markers.Fitted -and -not $desktop) { $passed = $false }
    if ($passed) { Write-Ok 'PASS' } else { Write-Host '    FAIL (see the log above)' -ForegroundColor Red }

    if ($Stop -and -not $process.HasExited) {
        $process.Kill()
        Write-Host '    Player stopped (-Stop).'
    } elseif (-not $process.HasExited) {
        Write-Host '    The player keeps running; close it with Alt+F4.'
    }
    if (-not $passed) { exit 1 }
}

# ---------------------------------------------------------------------------------------------
# deploy
# ---------------------------------------------------------------------------------------------

# Keep the lines of a [[index]] / [[quest]] block only when that target is deployed.
function Expand-Template([string]$Text, [string]$DeployTarget) {
    $output = New-Object System.Collections.Generic.List[string]
    $keep = $true
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^\[\[(index|quest)\]\]$') { $keep = ($DeployTarget -eq 'all' -or $DeployTarget -eq $Matches[1]); continue }
        if ($line -match '^\[\[/(index|quest)\]\]$') { $keep = $true; continue }
        if ($keep) { $output.Add($line) }
    }
    return ($output -join "`r`n")
}

function Copy-Player([string]$Mode, [string]$Destination) {
    $source = Join-Path $Builds "Windows\$Mode"
    if (-not (Test-Path -LiteralPath (Join-Path $source $ExeName))) { Fail "No $Mode player in $source. Run 'vr build -Stereo All' or drop -SkipBuild." }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $source | Where-Object { $_.Name -notmatch '_DoNotShip$|_ButDontShipItWithYourGame$' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force }
    # Assemblies compiled on this PC record their debug-symbol path, which contains the Windows
    # user name (C:\Users\<name>\...). Only debuggers read that path, so in the bundle's copies the
    # name is replaced by the same number of x characters; the files keep their size and layout.
    $userName = Split-Path -Leaf $env:USERPROFILE
    if ($userName) {
        $latin1 = [System.Text.Encoding]::GetEncoding(28591)
        $pattern = '(?i)\\Users\\' + [regex]::Escape($userName) + '\\'
        $replacement = '\Users\' + ('x' * $userName.Length) + '\'
        Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter '*.dll' | ForEach-Object {
            $text = $latin1.GetString([System.IO.File]::ReadAllBytes($_.FullName))
            if ($text -match $pattern) {
                [System.IO.File]::WriteAllBytes($_.FullName, $latin1.GetBytes(($text -replace $pattern, $replacement.Replace('$', '$$'))))
            }
        }
    }
}

function Invoke-Deploy {
    if (-not $SkipBuild) { Invoke-Build $StereoModes }

    $commit = (& git -C $Root rev-parse --short HEAD).Trim()
    $dirty = & git -C $Root status --porcelain --untracked-files=no
    if ($dirty) { $commit += '-dirty' }
    $name = 'VMD_VR_Lab_{0}_{1}' -f (Get-Date -Format 'yyyyMMdd'), $commit
    if ($Target -ne 'all') { $name += "_$Target" }
    if ($LocalExamples) { $name += '_examples' }
    $deployRoot = Join-Path $Builds 'deploy'
    $staging = Join-Path $deployRoot $name
    $bundle = Join-Path $staging 'VMD_VR_Lab'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $bundle | Out-Null

    Write-Step "Assembling $name"
    foreach ($mode in $StereoModes) { Copy-Player $mode (Join-Path $bundle $mode) }

    Copy-Item -LiteralPath (Join-Path $Root 'start_vr.tcl') -Destination $bundle
    $bridge = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'VMD_VR_Bridge')
    Get-ChildItem -LiteralPath (Join-Path $Root 'VMD_VR_Bridge') -Filter '*.tcl' | Where-Object { $_.Name -notlike '_test_*' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bridge.FullName }
    $animation = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'animation')
    foreach ($file in @('render.tcl', 'split_large_reps.tcl', 'vr_prepare.tcl')) { Copy-Item -LiteralPath (Join-Path $Root "animation\$file") -Destination $animation.FullName }
    $data = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'data')
    foreach ($file in @('mutl_open_CA.pdb', 'Example_Trajectory.xtc')) { Copy-Item -LiteralPath (Join-Path $Root "data\$file") -Destination $data.FullName }
    if ($LocalExamples) {
        # Local examples are the git-ignored files, taken from this checkout as they are. The
        # tracked animation scripts are movie tools, not VR examples, and are left out.
        $ignored = @(& git -C $Root ls-files --others --ignored --exclude-standard -- animation data)
        $extraScripts = @($ignored | Where-Object { $_ -match '^animation/[^/]+\.tcl$' } | ForEach-Object { Get-Item -LiteralPath (Join-Path $Root $_) })
        $extraScripts | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $animation.FullName }
        $extraData = @($ignored | Where-Object { $_ -match '^data/[^/]+$' -and $_ -notmatch '\.mp4$' } | ForEach-Object { Get-Item -LiteralPath (Join-Path $Root $_) })
        $extraData | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $data.FullName }
        $lines = @('Extra examples in this bundle (local test data, not in the repository).', '',
            'In the VMD Tk Console, after cd to this folder (see INSTRUCTIONS.txt), run one of these',
            'instead of "source animation/render.tcl", then "source start_vr.tcl":', '')
        foreach ($script in $extraScripts) { $lines += "  source animation/$($script.Name)" }
        $lines += @('', 'A scene whose script uses drawing methods Unity cannot draw itself (Lines, Licorice,',
            'Points, smoothing, transparent materials) can be adapted first, before start_vr.tcl',
            '(see the comments at the top of animation/vr_prepare.tcl):', '',
            '  source animation/vr_prepare.tcl', '  vr_prepare -check', '  vr_prepare', '',
            'vr_unprepare restores the original reps.')
        $lines += @('', 'A large system that looks coarse in the headset can be split into smaller QuickSurf',
            'reps first (see the comments at the top of animation/split_large_reps.tcl), for example:', '',
            '  source animation/split_large_reps.tcl', '  split_large_reps top -unit block 1268', '',
            'unsplit_large_reps restores the original reps.', '', 'Data files:')
        foreach ($file in $extraData) { $lines += "  data/$($file.Name)" }
        [System.IO.File]::WriteAllText((Join-Path $bundle 'EXTRA_EXAMPLES.txt'), (($lines -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
        Write-Ok ('Local examples: ' + ((@($extraScripts | ForEach-Object Name) + @($extraData | ForEach-Object Name)) -join ', '))
    }
    $docs = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'docs')
    Copy-Item -LiteralPath (Join-Path $Root 'docs\VR_ISSUES.md') -Destination $docs.FullName

    $templates = Join-Path $PSScriptRoot 'bundle'
    Copy-Item -LiteralPath (Join-Path $templates 'run_player.cmd') -Destination $bundle
    $launchers = @{ index = @('Start_Index_SteamVR.cmd'); quest = @('Start_Quest_MetaLink.cmd', 'Start_Quest_SteamLink.cmd') }
    $selected = @('Start_Desktop.cmd')
    if ($Target -eq 'all' -or $Target -eq 'index') { $selected += $launchers.index }
    if ($Target -eq 'all' -or $Target -eq 'quest') { $selected += $launchers.quest }
    foreach ($launcher in $selected) { Copy-Item -LiteralPath (Join-Path $templates $launcher) -Destination $bundle }
    $instructions = Get-Content -LiteralPath (Join-Path $templates 'INSTRUCTIONS.txt') -Raw
    $instructions = (Expand-Template $instructions $Target).Replace('{{BUNDLE}}', $name)
    [System.IO.File]::WriteAllText((Join-Path $bundle 'INSTRUCTIONS.txt'), $instructions, (New-Object System.Text.UTF8Encoding($false)))

    $zip = Join-Path $deployRoot "$name.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    # Write entries with "/" separators; ZipFile.CreateFromDirectory in Windows PowerShell stores "\",
    # which some unzip tools turn into file names instead of folders.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefix = $staging.TrimEnd('\').Length + 1
        foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File) {
            $entryName = $file.FullName.Substring($prefix).Replace('\', '/')
            $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    Remove-Item -LiteralPath $staging -Recurse -Force
    $sizeMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    Write-Ok "Bundle: $zip ($sizeMb MB)"
    Write-Ok "Launchers: $($selected -join ', ')"
    if ($dirty) { Write-Warn 'The working tree has uncommitted changes; the bundle name ends in -dirty.' }
}

# ---------------------------------------------------------------------------------------------
# test
# ---------------------------------------------------------------------------------------------

function Get-Vmd {
    if ($env:VMD_EXE) {
        if (Test-Path -LiteralPath $env:VMD_EXE) { return $env:VMD_EXE }
        Fail "VMD_EXE points to a missing file: $env:VMD_EXE"
    }
    foreach ($candidate in @((Join-Path $env:ProgramFiles 'VMD\vmd.exe'), (Join-Path ${env:ProgramFiles(x86)} 'University of Illinois\VMD\vmd.exe'))) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    Fail 'vmd.exe not found. Install VMD or set VMD_EXE.'
}

function Invoke-Test {
    $vmd = Get-Vmd
    $bridgeDir = Join-Path $Root 'VMD_VR_Bridge'
    if (-not $env:VMD_COORDINATE_TEST_ROOT) { $env:VMD_COORDINATE_TEST_ROOT = Join-Path $env:TEMP 'VMDCoordinateTests' }
    $failed = 0
    foreach ($test in @('bridge', 'static_export', 'trajectory', 'cartoon')) {
        $script = "_test_$test.tcl"
        Write-Step "VMD test $script"
        $started = Get-Date
        $process = Start-Process -FilePath $vmd -ArgumentList (Join-Args @('-dispdev', 'text', '-e', ".\$script", '-eofexit')) `
            -WorkingDirectory $bridgeDir -PassThru -WindowStyle Hidden
        $null = $process.Handle
        if (-not $process.WaitForExit(600000)) { $process.Kill(); Write-Warn 'Timed out after 10 minutes.'; $failed++; continue }

        # Each script reports in its own place and format (see its header comment).
        $passCount = $null; $failCount = $null; $source = $null
        switch ($test) {
            'bridge' {
                $report = Get-ChildItem -LiteralPath $bridgeDir -Filter '_test_bridge_result_*.txt' |
                    Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-2) } | Sort-Object LastWriteTime | Select-Object -Last 1
                if ($report) {
                    $source = $report.FullName
                    $total = Select-String -LiteralPath $source -Pattern 'Total\s+\d+\s+Passed\s+(\d+)\s+Skipped\s+\d+\s+Failed\s+(\d+)' | Select-Object -Last 1
                    if ($total) { $passCount = [int]$total.Matches[0].Groups[1].Value; $failCount = [int]$total.Matches[0].Groups[2].Value }
                }
            }
            'static_export' {
                $source = Join-Path $Project 'Temp\static-export-tests\results.txt'
                if ((Test-Path -LiteralPath $source) -and (Get-Item -LiteralPath $source).LastWriteTime -ge $started.AddSeconds(-2)) {
                    $lines = Get-Content -LiteralPath $source
                    $passCount = @($lines | Where-Object { $_ -like 'PASS *' }).Count
                    $failCount = @($lines | Where-Object { $_ -like 'FAIL *' }).Count
                }
            }
            default {
                $name = 'bridge-result.txt'
                if ($test -eq 'cartoon') { $name = 'cartoon-result.txt' }
                $source = Join-Path $env:VMD_COORDINATE_TEST_ROOT $name
                if ((Test-Path -LiteralPath $source) -and (Get-Item -LiteralPath $source).LastWriteTime -ge $started.AddSeconds(-2)) {
                    $counts = (Get-Content -LiteralPath $source -Raw).Trim() -split '\s+'
                    if ($counts.Count -eq 2) { $passCount = [int]$counts[0]; $failCount = [int]$counts[1] }
                }
            }
        }
        if ($null -eq $passCount) { Write-Warn "No result from this run (VMD exit code $($process.ExitCode)); expected $source"; $failed++; continue }
        $line = "passed $passCount, failed $failCount  ($source)"
        if ($failCount -gt 0 -or $passCount -eq 0) { Write-Host "    $line" -ForegroundColor Red; $failed++ } else { Write-Ok $line }
        if ($process.ExitCode -ne 0) { Write-Warn "VMD exited with code $($process.ExitCode) after writing the results." }
    }
    if ($failed -gt 0) { Fail "$failed test script(s) failed." }
    Write-Ok 'All bridge tests passed.'
}

# ---------------------------------------------------------------------------------------------

switch ($Command) {
    'build' { Invoke-Build (Get-StereoList $Stereo 'SPI') }
    'run' { Invoke-Run }
    'deploy' { Invoke-Deploy }
    'test' { Invoke-Test }
    default { Get-Help $PSCommandPath -Detailed | Out-String | Write-Host }
}

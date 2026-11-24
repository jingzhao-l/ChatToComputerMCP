# Real, offline-source npm tarball installation on a clean Windows user profile.
# Run on native Windows x64 or ARM64 with Node 24 and the .NET 8 SDK available.
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $Root
function Get-LoginRunValue {
    $RunKey = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
    if ($null -eq $RunKey) { return $null }
    $Entry = $RunKey.PSObject.Properties["ChatToComputerMCP"]
    if ($null -eq $Entry) { return $null }
    return [string]$Entry.Value
}
$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$NodeArchitecture = (& node -p "process.arch").Trim()
if ($LASTEXITCODE -ne 0 -or $Architecture -notin @("x64", "arm64") -or $NodeArchitecture -ne $Architecture) {
    throw "Native Node architecture mismatch: OS=$Architecture Node=$NodeArchitecture"
}
$PreviousLoginRunValue = Get-LoginRunValue
$Version = (& node -p "require('./package.json').version").Trim()
if ($LASTEXITCODE -ne 0 -or $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Unable to read stable package version"
}
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$Sandbox = Join-Path $TempRoot ("ctcmcp-npm-clean-" + [Guid]::NewGuid().ToString("N"))
$Prefix = Join-Path $Sandbox "cli"
$FakeHome = Join-Path $Sandbox "home"
$LocalData = Join-Path $FakeHome "AppData/Local"
$Roaming = Join-Path $FakeHome "AppData/Roaming"
$Cache = Join-Path $Sandbox "npm-cache"
foreach ($directory in @($Prefix, $FakeHome, $LocalData, $Roaming, $Cache)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$Overrides = @{
    USERPROFILE = $FakeHome
    HOME = $FakeHome
    LOCALAPPDATA = $LocalData
    APPDATA = $Roaming
    NPM_CONFIG_CACHE = $Cache
}
$Before = @{}
foreach ($key in $Overrides.Keys) {
    $Before[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
}
$PackedArchive = $null
$Success = $false
try {
    $Packed = @(& npm pack --ignore-scripts --silent --pack-destination $Sandbox)
    if ($LASTEXITCODE -ne 0 -or $Packed.Count -ne 1) {
        throw "npm pack failed to produce exactly one archive"
    }
    $PackedArchive = Join-Path $Sandbox $Packed[0].Trim()
    if (-not (Test-Path -LiteralPath $PackedArchive -PathType Leaf)) {
        throw "npm pack archive is missing"
    }
    foreach ($key in $Overrides.Keys) {
        [Environment]::SetEnvironmentVariable($key, $Overrides[$key], "Process")
    }

    & npm install --prefix $Prefix --offline --ignore-scripts --no-audit --no-fund --no-save $PackedArchive
    if ($LASTEXITCODE -ne 0) { throw "Offline npm CLI installation failed" }
    $CLI = Join-Path $Prefix "node_modules/.bin/chat-to-computer-mcp.cmd"
    if (-not (Test-Path -LiteralPath $CLI -PathType Leaf)) {
        throw "npm did not create the Windows CLI entrypoint"
    }
    $CLIversion = (& $CLI version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $CLIversion -ne $Version) {
        throw "Installed CLI version mismatch: $CLIversion"
    }
    & $CLI install
    if ($LASTEXITCODE -ne 0) { throw "Native build from npm source archive failed" }
    $Installed = Join-Path $LocalData "ChatToComputerMCP/releases/$Version/ChatToComputerMCP.exe"
    if (-not (Test-Path -LiteralPath $Installed -PathType Leaf)) {
        throw "Native executable missing after clean npm install: $Installed"
    }
    $Status = (& $CLI status | Out-String)
    if ($LASTEXITCODE -ne 0 -or $Status -notmatch 'Installed: yes' -or
        $Status -notmatch [Regex]::Escape("Installed version: $Version")) {
        throw "CLI did not recognize the installed version: $Status"
    }
    if (Test-Path -LiteralPath (Join-Path $LocalData "FileMCP")) {
        throw "New package touched the legacy FileMCP profile"
    }
    if (-not $PreviousLoginRunValue -and (Get-LoginRunValue)) {
        throw "Installing a source tarball silently enabled login startup"
    }
    Write-Host "windows-npm-clean-install: ok ($Version, native Windows $Architecture)"
    $Success = $true
} finally {
    foreach ($key in $Before.Keys) {
        [Environment]::SetEnvironmentVariable($key, $Before[$key], "Process")
    }
    if ($PackedArchive -and (Test-Path -LiteralPath $PackedArchive -PathType Leaf)) {
        Remove-Item -LiteralPath $PackedArchive -Force
    }
    if ($Success) {
        Remove-Item -LiteralPath $Sandbox -Recurse -Force
    } else {
        Write-Warning "Clean-install artifacts retained for debugging at $Sandbox (ephemeral runner)"
    }
}

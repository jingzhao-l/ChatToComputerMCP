$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

trap {
    $Message = $_.Exception.ToString()
    $Message = $Message.Replace("%", "%25")
    $Message = $Message.Replace("`r", "%0D")
    $Message = $Message.Replace("`n", "%0A")
    Write-Host "::error file=tests/test_windows_app.ps1,title=Windows app smoke failure::$Message"
    exit 1
}

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

if ($env:OS -ne "Windows_NT") {
    throw "Windows app smoke test must run on Windows."
}

# Source-side GUI assertions complement the real native window smoke below.
# The XAML compiler also verifies that handlers exist at build time.
$Markup = Get-Content (Join-Path $Root "windows/src/FileMCP.App/MainWindow.xaml") -Raw
foreach ($Control in @("LoginStartupBox", "LoginAutoConnectBox",
                        "LoginAlwaysOnBox", "ApplyLoginSettingsButton", "ReceiptStatusText")) {
    if ($Markup -notmatch ('x:Name="' + [regex]::Escape($Control) + '"')) {
        throw "Missing Windows startup/receipt GUI control: $Control"
    }
}
foreach ($RequiredControl in @("CheckUpdatesButton", "InstallUpdateButton",
    "AutoCheckUpdatesBox", "AutoInstallUpdatesBox", "UpdateStatusText")) {
    if ($Markup -notmatch ('x:Name="' + [regex]::Escape($RequiredControl) + '"')) {
        throw "Missing Windows updater UI control: $RequiredControl"
    }
}
foreach ($Control in @("OverviewTab", "OverviewConnectionStatus",
        "OverviewWorkspaceSummary", "OverviewPermissions", "OverviewCommandCapacity")) {
    if ($Markup -notmatch ('x:Name="' + [regex]::Escape($Control) + '"')) {
        throw "Missing Windows overview UI control: $Control"
    }
}
$OverviewIndex = $Markup.IndexOf('<TabItem Header="Overview"')
$ConnectionIndex = $Markup.IndexOf('<TabItem Header="Connection"')
$SettingsIndex = $Markup.IndexOf('<TabItem Header="Settings"')
$TasksIndex = $Markup.IndexOf('<TabItem Header="Tasks"')
$ActivityIndex = $Markup.IndexOf('<TabItem Header="Activity"')
if ($OverviewIndex -lt 0 -or $OverviewIndex -ge $ConnectionIndex -or
    $ConnectionIndex -ge $SettingsIndex -or $SettingsIndex -ge $TasksIndex -or
    $TasksIndex -ge $ActivityIndex) {
    throw "Windows navigation tabs are missing or out of order"
}
Write-Host "windows-overview-navigation: ok"

Write-Host "windows-update-ui-controls: ok"

$Archive = Join-Path $Root "dist/ChatToComputerMCP-v0.4.0-windows-x64.zip"
if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
    throw "Windows x64 release archive is missing: $Archive"
}

$SmokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("filemcp-windows-app-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $SmokeRoot | Out-Null
Expand-Archive -LiteralPath $Archive -DestinationPath $SmokeRoot -Force
$Exe = Join-Path $SmokeRoot "ChatToComputerMCP.exe"
$TunnelClient = Join-Path $SmokeRoot "tunnel-client.exe"
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "ChatToComputerMCP.exe is missing from the Windows x64 release archive."
}
if (-not (Test-Path -LiteralPath $TunnelClient -PathType Leaf)) {
    throw "tunnel-client.exe is missing from the Windows x64 release archive."
}
$Ripgrep = Join-Path $SmokeRoot "rg.exe"
if (-not (Test-Path -LiteralPath $Ripgrep -PathType Leaf)) {
    throw "rg.exe is missing from the Windows x64 release archive."
}
$RipgrepVersion = & $Ripgrep --version
if ($LASTEXITCODE -ne 0 -or ($RipgrepVersion | Select-Object -First 1) -notmatch '^ripgrep 15\.2\.0 ') {
    throw "Unexpected packaged ripgrep version: $RipgrepVersion"
}
$TunnelVersion = & $TunnelClient --version
if ($LASTEXITCODE -ne 0 -or $TunnelVersion -notmatch '^0\.0\.12\+881c9a8fed7cccbe6607cd419863bbca506b8215 ') {
    throw "Unexpected packaged tunnel-client version: $TunnelVersion"
}

# A headless, isolated startup-policy test. Does not register any real
# HKCU startup entry or connect to an MCP tunnel.
$StartupSelfTest = Start-Process -FilePath $Exe -ArgumentList @("--selftest-startup") -Wait -PassThru
if ($StartupSelfTest.ExitCode -ne 0) {
    throw "Windows startup policy self-test failed: $($StartupSelfTest.ExitCode)"
}
$StartupSelfTest.Dispose()
Write-Host "windows-login-startup-policy: ok"

# Supervisor must exit immediately when not explicitly registered.
$RunKey = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
$RunValue = $null
if ($null -ne $RunKey) {
    $RunEntry = $RunKey.PSObject.Properties["ChatToComputerMCP"]
    if ($null -ne $RunEntry) {
        $RunValue = $RunEntry.Value
    }
}
if (-not $RunValue) {
    $SupervisorTest = Start-Process -FilePath $Exe -ArgumentList @("--supervise") -PassThru
    try {
        if (-not $SupervisorTest.WaitForExit(10000)) {
            throw "Windows unconfigured supervisor did not exit"
        }
        if ($SupervisorTest.ExitCode -ne 0) {
            throw "Windows unconfigured supervisor returned $($SupervisorTest.ExitCode)"
        }
        Write-Host "windows-disabled-supervisor: ok"
    }
    finally {
        if (-not $SupervisorTest.HasExited) {
            Stop-Process -Id $SupervisorTest.Id -Force -ErrorAction SilentlyContinue
        }
        $SupervisorTest.Dispose()
    }
}

$StartedAt = Get-Date
$Process = Start-Process -FilePath $Exe -PassThru
try {
    $Deadline = (Get-Date).AddSeconds(12)
    $WindowReady = $false
    while ((Get-Date) -lt $Deadline) {
        Start-Sleep -Milliseconds 500
        $Process.Refresh()
        if ($Process.HasExited) {
            $EventText = ""
            try {
                $Events = Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $StartedAt.AddSeconds(-2) } -ErrorAction Stop |
                    Where-Object { $_.ProviderName -in @(".NET Runtime", "Application Error", "Windows Error Reporting") -and $_.Message -match "ChatToComputerMCP" } |
                    Select-Object -First 8
                if ($Events) {
                    $EventText = ($Events | ForEach-Object { "[$($_.ProviderName)] $($_.Message)" }) -join "`n---`n"
                }
            } catch {
                $EventText = "Could not read Application event log: $($_.Exception.Message)"
            }
            throw "ChatToComputerMCP.exe exited during WPF startup with exit code $($Process.ExitCode).`n$EventText"
        }
        if ($Process.MainWindowHandle -ne 0 -and $Process.MainWindowTitle -eq "ChatToComputerMCP") {
            $WindowReady = $true
            break
        }
    }
    if (-not $WindowReady) {
        throw "ChatToComputerMCP.exe stayed alive but did not create the ChatToComputerMCP main window within 12 seconds."
    }

    if (-not $Process.CloseMainWindow()) {
        throw "ChatToComputerMCP.exe main window did not accept a normal close request."
    }
    Start-Sleep -Seconds 2
    $Process.Refresh()
    if ($Process.HasExited) {
        throw "ChatToComputerMCP.exe exited after closing the main window instead of remaining active in the system tray."
    }

    Write-Host "windows-app-startup: ok"
    Write-Host "windows-close-to-tray: ok"
    Write-Host "windows-packaged-tunnel-client: ok"
}
finally {
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $Process.Dispose()
    Remove-Item -LiteralPath $SmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}

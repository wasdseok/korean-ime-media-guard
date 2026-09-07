param(
    [ValidateSet('Install','Uninstall','Verify')][string]$Action = 'Install',
    [string]$SandboxRoot,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$appId = 'CodexImeMediaGuard.Restore.v1'
$expectedHash = '7628F7E4D11E6513D57C0C3044EE145A6410A4E31089C504702D397009362D03'
$sourceDir = $PSScriptRoot
$managedFiles = @('ImeMediaGuard.exe','ImeMediaGuard.cs','Setup.ps1','uninstall.cmd','README-ko.md','policy-tests.json')
if ($SandboxRoot) {
    if (-not $NoLaunch) { throw 'SandboxRoot requires NoLaunch.' }
    $testBase = [IO.Path]::GetFullPath($SandboxRoot)
    $installDir = Join-Path $testBase 'KoreanInputGuard'
    $startupDir = Join-Path $testBase 'Startup'
} else {
    $installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'KoreanInputGuard'
    $startupDir = [Environment]::GetFolderPath('Startup')
}
$installDir = [IO.Path]::GetFullPath($installDir)
$exePath = Join-Path $installDir 'ImeMediaGuard.exe'
$statusPath = Join-Path $installDir 'status.json'
$markerPath = Join-Path $installDir 'installation.json'
$shortcutPath = Join-Path $startupDir '한글 입력 보정.lnk'
$description = 'CodexImeMediaGuard: portable restore v1'

function Assert-ManagedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($installDir + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a file operation outside the installation directory.'
    }
}
function Assert-DirectoryOwner {
    if (Test-Path -LiteralPath $installDir) {
        $item = Get-Item -LiteralPath $installDir
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected installation directory.' }
        if (Test-Path -LiteralPath $markerPath) {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            if ($marker.appId -ne $appId -or $marker.directory -ne $installDir) { throw 'Installation ownership mismatch.' }
        } elseif (@(Get-ChildItem -LiteralPath $installDir -Force).Count -gt 0) {
            throw 'The destination already contains unrelated files. No changes were made.'
        }
    }
}
function Assert-ShortcutOwner {
    if (Test-Path -LiteralPath $shortcutPath) {
        $wsh = New-Object -ComObject WScript.Shell
        $existing = $wsh.CreateShortcut($shortcutPath)
        if ($existing.TargetPath -ne $exePath -or $existing.Description -ne $description) {
            throw 'An older or different startup shortcut already exists. Keep the current working setup; use this restore package after reinstalling Windows.'
        }
    }
}
function Stop-InstalledGuard {
    if ($SandboxRoot) { return }
    $session = (Get-Process -Id $PID).SessionId
    foreach ($process in @(Get-Process -Name ImeMediaGuard -ErrorAction SilentlyContinue)) {
        if ($process.SessionId -ne $session) { continue }
        if ($process.Path -ne $exePath) {
            if ($Action -eq 'Install') { throw 'A guard from a different folder is already running. No replacement was started.' }
            continue
        }
        Stop-Process -Id $process.Id -ErrorAction Stop
        $process.WaitForExit(10000) | Out-Null
        if (-not $process.HasExited) { throw 'The previous guard did not exit.' }
    }
}
function Verify-Payload {
    $sourceExe = Join-Path $sourceDir 'ImeMediaGuard.exe'
    if ((Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Executable checksum mismatch. Do not install this copy.' }
    foreach ($name in $managedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $name) -PathType Leaf)) { throw ('Missing package file: ' + $name) }
    }
}
try {
    if ($Action -eq 'Verify') {
        Verify-Payload
        Write-Host 'Package files and executable SHA-256: OK. No settings changed.'
        exit 0
    }
    Assert-DirectoryOwner
    Assert-ShortcutOwner
    if ($Action -eq 'Uninstall') {
        if (-not (Test-Path -LiteralPath $markerPath)) { Write-Host 'No installation from this package was found.'; exit 0 }
        Stop-InstalledGuard
        if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
        foreach ($name in @($managedFiles) + @('status.json','status.json.tmp','installation.json')) {
            $path = Join-Path $installDir $name
            Assert-ManagedPath $path
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
        if (@(Get-ChildItem -LiteralPath $installDir -Force).Count -eq 0) { Remove-Item -LiteralPath $installDir }
        Write-Host 'Guard and its automatic startup were removed. Unrelated files were preserved.'
        exit 0
    }
    Verify-Payload
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This package requires x64 Windows.' }
    if (-not (Test-Path -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\clr.dll'))) { throw '.NET Framework 4.x is required.' }
    Stop-InstalledGuard
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    New-Item -ItemType Directory -Path $startupDir -Force | Out-Null
    foreach ($name in $managedFiles) {
        $source = Join-Path $sourceDir $name
        $destination = Join-Path $installDir $name
        Assert-ManagedPath $destination
        if ([IO.Path]::GetFullPath($source) -eq $destination) { continue }
        $copied = $false
        for ($attempt=0; $attempt -lt 5; $attempt++) {
            try { Copy-Item -LiteralPath $source -Destination $destination -Force; $copied=$true; break }
            catch { if ($attempt -eq 4) { throw }; Start-Sleep -Milliseconds 200 }
        }
        if (-not $copied) { throw 'Copy failed.' }
    }
    if ((Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Installed executable checksum mismatch.' }
    $arguments = '--status-path "' + $statusPath + '"'
    $wsh = New-Object -ComObject WScript.Shell
    $link = $wsh.CreateShortcut($shortcutPath)
    $link.TargetPath = $exePath
    $link.Arguments = $arguments
    $link.WorkingDirectory = $installDir
    $link.Description = $description
    $link.WindowStyle = 7
    $link.Save()
    $verifyLink = $wsh.CreateShortcut($shortcutPath)
    if ($verifyLink.TargetPath -ne $exePath -or $verifyLink.Arguments -ne $arguments) { throw 'Startup shortcut verification failed.' }
    [ordered]@{appId=$appId;directory=$installDir;installedAt=(Get-Date).ToString('o');sha256=$expectedHash;shortcut=$shortcutPath} |
        ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
    if (-not $NoLaunch) {
        $launched = Start-Process -FilePath $exePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $ready = $false
        for ($attempt=0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 250
            if ($launched.HasExited) { throw ('The guard exited with code ' + $launched.ExitCode) }
            if (Test-Path -LiteralPath $statusPath) {
                try {
                    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                    if ($status.pid -eq $launched.Id -and $status.state -eq 'running' -and $status.hookInstalled) { $ready=$true; break }
                } catch { }
            }
        }
        if (-not $ready) { throw 'Files are installed, but startup could not be verified. See README-ko.md.' }
    }
    Write-Host ('Installed: ' + $installDir)
    Write-Host 'Automatic startup is registered for the current Windows user.'
    if ($NoLaunch) { Write-Host 'NoLaunch: guard was not started.' } else { Write-Host 'Guard is running. Test Korean typing now.' }
    exit 0
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

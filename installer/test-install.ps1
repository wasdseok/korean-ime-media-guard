[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D91B7A0A-2789-4F0D-AEE4-8D671C539223}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'TypingTune is already installed. Run isolated validation before the user installation.' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$id = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $env:TEMP ('TypingTune Installer Test ' + $id)
$installDir = Join-Path $testRoot 'app'
$groupName = 'TypingTune-InstallerTest-' + $id
$groupDir = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Assert-Check([string]$Name, [bool]$Value) {
    $checks.Add([pscustomobject]@{name=$Name; passed=$Value})
    if (-not $Value) { throw ('Installer validation failed: ' + $Name) }
}
function Run-Setup([string]$LogName) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NODESKTOPICON','/TASKS=""',('/DIR="' + $installDir + '"'),('/GROUP="' + $groupName + '"'),('/LOG="' + (Join-Path $testRoot $LogName) + '"'))
    $setupProcess = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($LogName + ': exit zero') ($setupProcess.ExitCode -eq 0)
}
try {
    Run-Setup 'install.log'
    $installedExe = Join-Path $installDir 'TypingTune.exe'
    Assert-Check 'executable installed' (Test-Path -LiteralPath $installedExe -PathType Leaf)
    Assert-Check 'uninstaller registered for current user' (Test-Path -LiteralPath $uninstallKey)
    $registered = Get-ItemProperty -LiteralPath $uninstallKey
    Assert-Check 'registered install path matches test directory' ($registered.InstallLocation.TrimEnd('\') -eq $installDir)
    $shortcutFiles = @(Get-ChildItem -LiteralPath $groupDir -Filter '*.lnk')
    $shellObject = New-Object -ComObject WScript.Shell
    $appShortcut = @($shortcutFiles | Where-Object { $shellObject.CreateShortcut($_.FullName).TargetPath -eq $installedExe })
    Assert-Check 'Start Menu shortcut targets installed executable' ($appShortcut.Count -eq 1)
    Assert-Check 'default shortcut has no tray argument' ([string]::IsNullOrEmpty($shellObject.CreateShortcut($appShortcut[0].FullName).Arguments))
    $sourceExe = Join-Path $projectRoot 'dist\TypingTune.exe'
    Assert-Check 'installed application matches packaged application' ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash)
    $userSentinel = Join-Path $installDir 'user-diagnostic-preserved.json'
    [System.IO.File]::WriteAllText($userSentinel, '{"purpose":"user data retention validation"}')
    $selfTestPath = Join-Path $testRoot 'installed-self-test.json'
    $testProcess = Start-Process -FilePath $installedExe -ArgumentList @('--self-test', ('"' + $selfTestPath + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert-Check 'installed application self-test exit zero' ($testProcess.ExitCode -eq 0)
    Assert-Check 'installed self-test produced report' (Test-Path -LiteralPath $selfTestPath -PathType Leaf)
    $selfTestResult = Get-Content -LiteralPath $selfTestPath -Raw -Encoding utf8 | ConvertFrom-Json
    Assert-Check 'installed core tests all passed' ($selfTestResult.core.passed -eq $true -and $selfTestResult.core.failed -eq 0)
    Assert-Check 'installed diagnostic tests all passed' ($selfTestResult.diagnostics.allPassed -eq $true -and $selfTestResult.diagnostics.failed -eq 0)
    Run-Setup 'upgrade.log'
    Assert-Check 'upgrade preserves user data' (Test-Path -LiteralPath $userSentinel -PathType Leaf)
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert-Check 'uninstall exit zero' ($uninstallProcess.ExitCode -eq 0)
    Assert-Check 'application removed' (-not (Test-Path -LiteralPath $installedExe))
    Assert-Check 'uninstall registration removed' (-not (Test-Path -LiteralPath $uninstallKey))
    Assert-Check 'Start Menu shortcut group removed' (-not (Test-Path -LiteralPath $groupDir))
    Assert-Check 'uninstall preserves user-created diagnostic' (Test-Path -LiteralPath $userSentinel -PathType Leaf)
}
finally {
    # If a check fails, remove only this script's exact temporary installation.
    # Keep the directory and its logs so the failure can be investigated.
    if (Test-Path -LiteralPath $uninstallKey) {
        $leftoverRegistration = Get-ItemProperty -LiteralPath $uninstallKey
        $leftoverUninstaller = Join-Path $installDir 'unins000.exe'
        if ($leftoverRegistration.InstallLocation.TrimEnd('\') -eq $installDir -and (Test-Path -LiteralPath $leftoverUninstaller -PathType Leaf)) {
            $cleanupProcess = Start-Process -FilePath $leftoverUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'cleanup.log') + '"')) -WindowStyle Hidden -PassThru -Wait
            $checks.Add([pscustomobject]@{name='temporary installation cleanup'; passed=($cleanupProcess.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $uninstallKey))})
        }
    }
    $report = [ordered]@{ createdUtc=[DateTime]::UtcNow.ToString('o'); testRoot=$testRoot; checks=@($checks.ToArray()); desktopShortcutTested=$false; hardwareTypingTested=$false }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'installer-validation.json') -Encoding utf8
}
$report | ConvertTo-Json -Depth 6

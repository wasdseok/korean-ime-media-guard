[CmdletBinding()]
param(
    [string]$CompilerPath = $env:INNO_SETUP_COMPILER,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
if (-not $SkipBuild) {
    & (Join-Path $projectRoot 'build.ps1')
    if (-not $?) { throw 'TypingTune build failed.' }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) { $CompilerPath = $compilerCommand.Source }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6.7 or later is required. Pass -CompilerPath with the full ISCC.exe path, or set INNO_SETUP_COMPILER.'
}
$requiredFiles = @('dist\TypingTune.exe', 'dist\TypingTune.exe.config', 'dist\TypingTune.ico', 'README-ko.md', 'PRIVACY.md', 'LLM-JSON-GUIDE.md', 'LICENSE', 'installer\TypingTune.iss')
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relativePath) -PathType Leaf)) {
        throw ('Required package file not found: ' + $relativePath)
    }
}
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectRoot 'dist\TypingTune.exe')).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+(\.0)?$') { throw ('Unsupported application version: ' + $version) }
$versionParts = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectRoot 'dist\TypingTune.exe'))
$version = '{0}.{1}.{2}' -f $versionParts.ProductMajorPart, $versionParts.ProductMinorPart, $versionParts.ProductBuildPart
$artifacts = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
& $CompilerPath ('/DBuildRoot=' + $projectRoot) ('/DAppVersion=' + $version) (Join-Path $projectRoot 'installer\TypingTune.iss')
if ($LASTEXITCODE -ne 0) { throw ('Inno Setup compilation failed with exit code ' + $LASTEXITCODE) }
$installer = Join-Path $artifacts ('TypingTune-Setup-' + $version + '.exe')
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer was not generated.' }
function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
$hash = Get-Sha256 $installer
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(($installer + '.sha256.txt'), ($hash + '  ' + [System.IO.Path]::GetFileName($installer) + "`n"), $encoding)
$summary = [ordered]@{
    product = 'TypingTune'
    version = $version
    installer = [System.IO.Path]::GetFileName($installer)
    installerSha256 = $hash
    applicationSha256 = Get-Sha256 (Join-Path $projectRoot 'dist\TypingTune.exe')
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    packageScope = 'current-user'
    desktopOpensDiagnostic = $true
    automaticStartupDefault = $false
}
[System.IO.File]::WriteAllText((Join-Path $artifacts 'package-manifest.json'), (($summary | ConvertTo-Json -Depth 4) + "`n"), $encoding)
$summary | ConvertTo-Json -Depth 4

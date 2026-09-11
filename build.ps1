param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
$dist=Join-Path $projectRoot 'dist'
$null=New-Item -ItemType Directory -Path $dist -Force
Add-Type -AssemblyName System.Drawing
$iconImages=@()
foreach($size in @(16,32,48,256)) {
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphic=[Drawing.Graphics]::FromImage($bitmap)
    $graphic.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphic.Clear([Drawing.Color]::Transparent)
    $scale=$size/64.0
    $graphic.ScaleTransform($scale,$scale)
    $back=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(20,45,61))
    $mint=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(71,213,173))
    $white=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(237,245,250))
    $graphic.FillRectangle($back,2,2,60,60)
    $graphic.FillRectangle($mint,13,12,38,9)
    $graphic.FillRectangle($white,28,20,9,23)
    foreach($x in @(14,25,36,47)) {$graphic.FillRectangle($mint,$x,49,6,5)}
    $memory=New-Object IO.MemoryStream
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $iconImages+=,[pscustomobject]@{Size=$size;Bytes=$memory.ToArray()}
    $memory.Dispose();$white.Dispose();$mint.Dispose();$back.Dispose();$graphic.Dispose();$bitmap.Dispose()
}
$iconPath=Join-Path $dist 'TypingTune.ico'
$iconStream=[IO.File]::Create($iconPath)
$iconWriter=New-Object IO.BinaryWriter($iconStream)
try {
    $iconWriter.Write([uint16]0);$iconWriter.Write([uint16]1);$iconWriter.Write([uint16]$iconImages.Count)
    $offset=6+16*$iconImages.Count
    foreach($item in $iconImages) {
        $dimension=[byte]0;if($item.Size -lt 256){$dimension=[byte]$item.Size}
        $iconWriter.Write($dimension);$iconWriter.Write($dimension);$iconWriter.Write([byte]0);$iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1);$iconWriter.Write([uint16]32);$iconWriter.Write([uint32]$item.Bytes.Length);$iconWriter.Write([uint32]$offset)
        $offset+=$item.Bytes.Length
    }
    foreach($item in $iconImages){$iconWriter.Write([byte[]]$item.Bytes)}
} finally {$iconWriter.Dispose();$iconStream.Dispose()}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw '.NET Framework C# compiler was not found.'}
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$exePath=Join-Path $dist 'TypingTune.exe'
$compilerArgs=@('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:'+$exePath),('/win32icon:'+$iconPath),('/win32manifest:'+(Join-Path $projectRoot 'app.manifest')),
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll')+$sourceFiles
& $compiler @compilerArgs
if($LASTEXITCODE -ne 0){throw 'TypingTune compilation failed.'}
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><System.Windows.Forms.ApplicationConfigurationSection><add key="DpiAwareness" value="PerMonitorV2"/></System.Windows.Forms.ApplicationConfigurationSection></configuration>
'@ | Set-Content -LiteralPath ($exePath+'.config') -Encoding UTF8
if(-not $SkipTests) {
    $report=Join-Path $dist 'self-test-results.json'
    $run=Start-Process -FilePath $exePath -ArgumentList ('--self-test "'+$report+'"') -WindowStyle Hidden -PassThru
    if(-not $run.WaitForExit(60000)){throw 'Self-tests timed out.'}
    if($run.ExitCode -ne 0){throw ('Self-tests failed: '+$report)}
    $result=Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    if($result.core.failed -ne 0 -or $result.diagnostics.failed -ne 0){throw 'A test suite reported failure.'}
    [pscustomobject]@{CoreTests=$result.core.total;CoreFailed=$result.core.failed;DiagnosticTests=$result.diagnostics.total;DiagnosticFailed=$result.diagnostics.failed} | ConvertTo-Json
}
Write-Output ('Built: '+$exePath)

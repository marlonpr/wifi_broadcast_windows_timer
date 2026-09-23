[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SkipVcRedistDownload,
    [switch]$SkipSmokeTest,
    [string]$InnoCompiler,
    [string]$SigningCertificateThumbprint
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $InstallerDir
$Project = Join-Path $Root "windows-controller\FactoryTimer.Controller\FactoryTimer.Controller.csproj"
$Solution = Join-Path $Root "FactoryTimer.slnx"
$PublishDir = Join-Path $Root "artifacts\publish\win-x64"
$OutputDir = Join-Path $Root "artifacts\installer"
$PrereqDir = Join-Path $InstallerDir "prereqs"
$VcRedist = Join-Path $PrereqDir "VC_redist.x64.exe"
$IssFile = Join-Path $InstallerDir "FactoryTimer.iss"
$ControllerIcon = Join-Path $Root "windows-controller\FactoryTimer.Controller\Assets\ESP32ControllerWindows.ico"

function Resolve-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw ".NET 10 SDK is required on the BUILD PC. Install the x64 .NET 10 SDK, then rerun this script."
    }
    $major = (& dotnet --version).Split('.')[0]
    if ([int]$major -lt 10) {
        throw ".NET 10 SDK or newer is required on the BUILD PC. Found: $(& dotnet --version)"
    }
}

function Resolve-InnoCompiler {
    param([string]$Requested)
    if ($Requested) {
        if (-not (Test-Path -LiteralPath $Requested)) { throw "Inno Setup compiler not found: $Requested" }
        return (Resolve-Path $Requested).Path
    }
    $candidates = @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Inno Setup 6 is required on the BUILD PC. Install it, or pass -InnoCompiler <path-to-ISCC.exe>."
}

function Ensure-VcRedist {
    New-Item -ItemType Directory -Force -Path $PrereqDir | Out-Null
    if (Test-Path -LiteralPath $VcRedist) { return }
    if ($SkipVcRedistDownload) {
        throw "Missing $VcRedist. Put the official Microsoft VC_redist.x64.exe there or rerun without -SkipVcRedistDownload."
    }
    $url = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
    Write-Host "Downloading official Microsoft Visual C++ x64 Redistributable..."
    Invoke-WebRequest -Uri $url -OutFile $VcRedist -UseBasicParsing
}

function Assert-PublishLooksSelfContained {
    $required = @(
        "FactoryTimer.Controller.exe",
        "coreclr.dll",
        "hostfxr.dll"
    )
    foreach ($name in $required) {
        $path = Join-Path $PublishDir $name
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Self-contained publish validation failed: missing $name in $PublishDir"
        }
    }

    $publishedIcon = Join-Path $PublishDir "Assets\ESP32ControllerWindows.ico"
    if (-not (Test-Path -LiteralPath $publishedIcon)) {
        throw "Branding validation failed: published application icon is missing: $publishedIcon"
    }

    $winUi = Get-ChildItem -LiteralPath $PublishDir -Filter "Microsoft.UI.Xaml*.dll" -File -ErrorAction SilentlyContinue
    $winAppRuntime = Get-ChildItem -LiteralPath $PublishDir -Filter "Microsoft.WindowsAppRuntime*.dll" -File -ErrorAction SilentlyContinue
    if (-not $winUi -and -not $winAppRuntime) {
        throw "Windows App SDK self-contained validation failed: expected Windows App SDK runtime DLLs were not found in the publish directory."
    }
}

Resolve-DotNet
$Iscc = Resolve-InnoCompiler -Requested $InnoCompiler
Ensure-VcRedist

if (-not (Test-Path -LiteralPath $ControllerIcon)) {
    throw "Application icon is missing: $ControllerIcon"
}

if (Get-Process -Name "FactoryTimer.Controller" -ErrorAction SilentlyContinue) {
    throw "FactoryTimer.Controller.exe is running. Close it before building so DLLs are not locked."
}

New-Item -ItemType Directory -Force -Path $PublishDir, $OutputDir | Out-Null
Remove-Item -LiteralPath $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Write-Host "Restoring solution..."
& dotnet restore $Solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

if (-not $SkipTests) {
    Write-Host "Running protocol tests..."
    & dotnet test (Join-Path $Root "tests\FactoryTimer.Protocol.Tests\FactoryTimer.Protocol.Tests.csproj") -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Protocol tests failed." }

    Write-Host "Running controller-core tests..."
    & dotnet test (Join-Path $Root "tests\FactoryTimer.Controller.Core.Tests\FactoryTimer.Controller.Core.Tests.csproj") -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Controller.Core tests failed." }
}

Write-Host "Publishing self-contained win-x64 controller..."
& dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64-self-contained `
    -p:Version=$Version `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Assert-PublishLooksSelfContained

if (-not $SkipSmokeTest) {
    Write-Host "Smoke-testing published controller startup..."
    $PublishedExe = Join-Path $PublishDir "FactoryTimer.Controller.exe"
    $smoke = Start-Process -FilePath $PublishedExe -PassThru
    Start-Sleep -Seconds 5
    if ($smoke.HasExited) {
        $exitCode = $smoke.ExitCode
        throw "Published controller exited during startup smoke test (exit code=$exitCode). Installer was not built. Check Windows Application Event Log for FactoryTimer.Controller/Microsoft.UI.Xaml errors."
    }
    Stop-Process -Id $smoke.Id -Force
    $smoke.WaitForExit()
    Write-Host "Published controller stayed alive for 5 seconds: startup smoke test passed."
}

Write-Host "Compiling installer with Inno Setup..."
& $Iscc "/DMyAppVersion=$Version" "/DSourceDir=$PublishDir" "/DPrereqDir=$PrereqDir" "/DOutputDir=$OutputDir" $IssFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$Setup = Get-ChildItem -LiteralPath $OutputDir -Filter "FactoryTimerSetup-$Version-win-x64.exe" -File | Select-Object -First 1
if (-not $Setup) { throw "Installer was not produced in $OutputDir" }

if ($SigningCertificateThumbprint) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        throw "-SigningCertificateThumbprint was supplied, but signtool.exe was not found in PATH."
    }
    Write-Host "Signing installer..."
    & $signtool.Source sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Setup.FullName
    if ($LASTEXITCODE -ne 0) { throw "Installer signing failed." }
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $Setup.FullName
Write-Host ""
Write-Host "Installer ready: $($Setup.FullName)"
Write-Host "SHA256: $($hash.Hash)"

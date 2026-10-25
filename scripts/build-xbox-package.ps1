<#
.SYNOPSIS
  Builds the x64 sideload package for Xbox Developer Mode on a Windows machine.

.DESCRIPTION
  Requires Visual Studio 2022 with the "Universal Windows Platform development" workload
  (C# + C++ v143 UWP tools) and Windows SDK 10.0.22621. Fails loudly if they are missing.

  Signing: pass -PfxPath/-PfxPassword for your own certificate (subject must be CN=NesXboxDev,
  matching Package.appxmanifest), or -SelfSign to create a throwaway certificate for this build.
  Never commit .pfx files or passwords.

.EXAMPLE
  pwsh scripts/build-xbox-package.ps1 -Configuration Release -SelfSign
#>
param(
  [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
  [string] $PfxPath,
  [string] $PfxPassword,
  [switch] $SelfSign,
  [switch] $Unsigned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'xbox\NesXbox.sln'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found: install Visual Studio 2022.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.Universal Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the UWP workload (Microsoft.VisualStudio.Workload.Universal) and C++ x64 tools is required.' }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
$sdk = "${env:ProgramFiles(x86)}\Windows Kits\10\Platforms\UAP\10.0.22621.0"
if (-not (Test-Path $sdk)) { throw "Windows SDK 10.0.22621.0 (UAP platform) not found at $sdk." }

$signArgs = @('/p:AppxPackageSigningEnabled=false')
if (-not $Unsigned) {
  if ($SelfSign) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=NesXboxDev' -KeyUsage DigitalSignature `
      -FriendlyName 'NES Xbox throwaway dev cert' -CertStoreLocation 'Cert:\CurrentUser\My' `
      -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $PfxPath = Join-Path $env:TEMP "nesxbox-$([guid]::NewGuid()).pfx"
    $PfxPassword = [guid]::NewGuid().ToString()
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password (ConvertTo-SecureString $PfxPassword -AsPlainText -Force) | Out-Null
  }
  if (-not $PfxPath) { throw 'Pass -PfxPath/-PfxPassword, -SelfSign, or -Unsigned.' }
  $signArgs = @('/p:AppxPackageSigningEnabled=true', "/p:PackageCertificateKeyFile=$PfxPath", "/p:PackageCertificatePassword=$PfxPassword")
}

& $msbuild $sln /restore /m /p:Configuration=$Configuration /p:Platform=x64 `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
  "/p:AppxPackageDir=$root\out\AppPackages\" @signArgs /bl:"$root\out\msbuild.binlog" `
  /clp:Summary "/flp1:errorsonly;logfile=$root\out\errors.log"
if ($LASTEXITCODE -ne 0) {
  # Surface errors as CI annotations (readable without downloading the full log).
  if ($env:GITHUB_ACTIONS -and (Test-Path "$root\out\errors.log")) {
    Get-Content "$root\out\errors.log" | Select-Object -Unique -First 50 | ForEach-Object { Write-Host "::error::$_" }
  }
  throw "MSBuild failed ($LASTEXITCODE). See out\errors.log and out\msbuild.binlog."
}
if ($SelfSign -and $PfxPath) { Remove-Item $PfxPath -Force }
Get-ChildItem "$root\out\AppPackages" -Recurse -Include *.msix, *.appx | ForEach-Object { Write-Host "package: $($_.FullName)" }

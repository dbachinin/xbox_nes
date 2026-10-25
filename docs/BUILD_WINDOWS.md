# Build the UWP package on Windows

Ubuntu is the editing/testing workstation; UWP compilation and packaging need Microsoft's toolchain.
Use a Windows 10/11 PC, a VM, a remote builder or the `windows-xbox-package` GitHub workflow.

## Requirements

* Visual Studio 2022 (Community is fine) with workload **Universal Windows Platform development**
  (`Microsoft.VisualStudio.Workload.Universal`) including **C++ (v143) Universal Windows Platform tools**
  and **MSVC v143 x64/x86 build tools**.
* Windows 11 SDK **10.0.22621.0**.
* Internet for NuGet restore (`Microsoft.NETCore.UniversalWindowsPlatform` 6.2.14).

Unattended install of the workload:

```powershell
vs_community.exe --quiet --wait --add Microsoft.VisualStudio.Workload.Universal `
  --add Microsoft.VisualStudio.ComponentGroup.UWP.VC --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
  --add Microsoft.VisualStudio.Component.Windows11SDK.22621
```

## Build

```powershell
git clone <repo> nes-xbox; cd nes-xbox
pwsh scripts/build-xbox-package.ps1 -Configuration Release -SelfSign     # throwaway signature
# or: -PfxPath C:\keys\nesxbox.pfx -PfxPassword ****                       # your own certificate
# or: -Unsigned                                                            # compile-only checkpoint
```

The script checks for the workload and SDK and stops with a clear error if missing, builds
`xbox\NesXbox.sln` for x64 (NesCoreNative → `nescore.dll` first, then the C# app; Release uses .NET Native),
writes `out\msbuild.binlog`, and puts the package in `out\AppPackages\NesXbox_0.1.0.0_x64_Test\`
together with `Dependencies\x64\` (VCLibs, .NET Native framework/runtime).

Equivalent raw command:

```powershell
msbuild xbox\NesXbox.sln /restore /m /p:Configuration=Release /p:Platform=x64 `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxPackageSigningEnabled=true /p:PackageCertificateKeyFile=C:\keys\nesxbox.pfx /p:PackageCertificatePassword=****
```

In Visual Studio: open `xbox\NesXbox.sln`, choose `Release | x64`, target **Remote Machine** (your Xbox's
IP, after pairing in Dev Home) to deploy and debug directly.

## Signing

* The manifest publisher is `CN=NesXboxDev`; the certificate subject must match.
* `-SelfSign` creates a throwaway certificate per build (also what CI does). Such a package is meant
  for Dev Mode testing; it is **not** a trusted release signature. Keep a persistent personal
  certificate if you want in-place updates to keep working (the publisher hash must stay the same).
* Never commit `.pfx` files, passwords or tokens (`.gitignore` blocks `*.pfx`).

## CI

`.github/workflows/windows-xbox-package.yml` runs on `windows-2022`, fails explicitly if the UWP workload
or SDK 22621 is absent, builds with a throwaway certificate and uploads the package (artifact name says
`UNTRUSTED-SIGNATURE`) plus the MSBuild binlog. Status of this pipeline: see STATUS.md — it has not run yet.

## Desktop testing note

The manifest targets only `Windows.Xbox` (the shipping target). To try the app on a Windows PC, temporarily
change `TargetDeviceFamily Name` to `Windows.Universal` in `Package.appxmanifest`; do not ship that change.

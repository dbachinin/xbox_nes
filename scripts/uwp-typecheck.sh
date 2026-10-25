#!/usr/bin/env bash
# Linux pre-check for the UWP app: compiles xbox/NesXbox/*.cs against the Windows SDK 22621 projections
# (net9.0-windows + UseUwp, EnableWindowsTargeting) with stubs for XAML-generated fields.
# Catches typos and API misuse before a CI round trip. It is NOT a UWP build: XAML/x:Bind is not
# compiled, and the projection contains some APIs the UWP contract does not (e.g. Windows.Devices.Portable).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$root/build/uwp-typecheck"
mkdir -p "$work"

python3 -I - "$root/xbox/NesXbox" "$work/XamlStubs.g.cs" <<'EOF'
import sys, glob
import xml.etree.ElementTree as ET
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
P = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
code = []
for f in glob.glob(sys.argv[1] + '/**/*.xaml', recursive=True):
    t = ET.parse(f).getroot()
    ns, name = t.get(X + 'Class').rsplit('.', 1)
    fields = [f'        internal global::Windows.UI.Xaml.Controls.{el.tag[len(P):]} {el.get(X + "Name")} = null;'
              for el in t.iter() if el.get(X + 'Name') and el.tag.startswith(P)]
    code.append(f'namespace {ns} {{ partial class {name} {{\n' + '\n'.join(fields) + '\n        void InitializeComponent() { }\n    } }')
open(sys.argv[2], 'w').write('#pragma warning disable CS0649, CS0169, CS0414\n' + '\n'.join(code) + '\n')
EOF

cat > "$work/TypeCheck.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows10.0.22621.0</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <UseUwp>true</UseUwp>
    <LangVersion>9.0</LangVersion>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>CA1416;NETSDK1219</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$root/xbox/NesXbox/**/*.cs" Exclude="$root/xbox/NesXbox/obj/**;$root/xbox/NesXbox/bin/**" />
    <Compile Include="XamlStubs.g.cs" />
    <ProjectReference Include="$root/xbox/NesXbox.Logic/NesXbox.Logic.csproj" />
  </ItemGroup>
</Project>
EOF

DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build "$work/TypeCheck.csproj" -nologo -v q

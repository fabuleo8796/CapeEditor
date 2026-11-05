# Builds CapeEditor.dll without the .NET SDK, using the C# compiler that ships with Unity (or any csc.dll).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -GameDir "D:\Games\Flipping is Hard" -Install
#
# Needs: Flipping is Hard with BepInEx 6 (IL2CPP) run at least once (so BepInEx\interop exists), and a Unity editor
# (any 2021+ version has Editor\Data\NetCoreRuntime\dotnet.exe and Editor\Data\DotNetSdkRoslyn\csc.dll).
# -Install copies the DLL to <game>\BepInEx\plugins\CapeEditor\ (close the game first: it locks plugin DLLs).
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Flipping is Hard",
    [string]$UnityEditor = "",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

# the compiler: Unity's bundled .NET runtime + Roslyn
if (-not $UnityEditor) {
    $hub = "C:\Program Files\Unity\Hub\Editor"
    if (Test-Path $hub) {
        $found = Get-ChildItem $hub -Directory | Where-Object { Test-Path (Join-Path $_.FullName "Editor\Data\DotNetSdkRoslyn\csc.dll") } |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($found) { $UnityEditor = $found.FullName }
    }
}
$dotnet = Join-Path $UnityEditor "Editor\Data\NetCoreRuntime\dotnet.exe"
$csc = Join-Path $UnityEditor "Editor\Data\DotNetSdkRoslyn\csc.dll"
if (-not (Test-Path $dotnet) -or -not (Test-Path $csc)) { throw "No Unity editor with a C# compiler found. Pass -UnityEditor 'C:\Program Files\Unity\Hub\Editor\<version>'." }
if (-not (Test-Path (Join-Path $GameDir "BepInEx\interop\Assembly-CSharp.dll"))) { throw "No BepInEx\interop in '$GameDir'. Install BepInEx 6 IL2CPP and start the game once." }

$lines = New-Object System.Collections.Generic.List[string]
$lines.AddRange([string[]]@("-nologo", "-nostdlib", "-target:library", "-langversion:10", "-unsafe", "-optimize", "-deterministic", "-nowarn:CS1701,CS1702,CS0618"))
New-Item -ItemType Directory -Force (Join-Path $root "build") | Out-Null
$out = Join-Path $root "build\CapeEditor.dll"
$lines.Add("-out:`"$out`"")
# .NET 6 as the game's BepInEx runs it
Get-ChildItem (Join-Path $GameDir "dotnet") -Filter *.dll | Where-Object { ($_.Name -like "System*" -or $_.Name -in @("mscorlib.dll", "netstandard.dll")) -and $_.Name -notlike "*Native*" } |
    ForEach-Object { $lines.Add("-r:`"$($_.FullName)`"") }
foreach ($core in @("BepInEx.Core.dll", "BepInEx.Unity.IL2CPP.dll", "BepInEx.Unity.Common.dll", "0Harmony.dll", "Il2CppInterop.Runtime.dll", "Il2CppInterop.Common.dll")) {
    $lines.Add("-r:`"$(Join-Path $GameDir "BepInEx\core\$core")`"")
}
# the game's interop assemblies (UnityEngine.dll there is a tiny facade that makes types ambiguous: skip it)
Get-ChildItem (Join-Path $GameDir "BepInEx\interop") -Filter *.dll | Where-Object { $_.Name -ne "UnityEngine.dll" } |
    ForEach-Object { $lines.Add("-r:`"$($_.FullName)`"") }
foreach ($res in @("Geist-SemiBold.ttf", "Geist-Medium.ttf", "OFL-Geist.txt")) {
    $lines.Add("-resource:`"$(Join-Path $root "src\Resources\$res")`",$res")
}
Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $lines.Add("`"$($_.FullName)`"") }

$rsp = Join-Path $root "build\CapeEditor.rsp"
[System.IO.File]::WriteAllLines($rsp, $lines)
& $dotnet $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "The build failed (see the errors above)." }
Write-Host "Built $out"

if ($Install) {
    if (Get-Process -Name "Flipping is Hard" -ErrorAction SilentlyContinue) { throw "Close the game first: it locks the plugin DLL." }
    $dest = Join-Path $GameDir "BepInEx\plugins\CapeEditor"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $out $dest -Force
    Write-Host "Installed to $dest"
}

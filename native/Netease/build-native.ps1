param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$connectorRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputDirectory = Join-Path $connectorRoot "src\Netease\bridge"
$objectDirectory = Join-Path $PSScriptRoot "obj"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $objectDirectory | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "vswhere.exe was not found."
}
$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($visualStudio)) {
    throw "Visual C++ x64 build tools were not found."
}

$vcvars = Join-Path $visualStudio "VC\Auxiliary\Build\vcvars64.bat"
$source = Join-Path $PSScriptRoot "AwooNcmCefBridge.cpp"
$cefRoot = Join-Path $PSScriptRoot "cef-4472"
$dll = Join-Path $outputDirectory "AwooNcmCefBridge.dll"
$pdb = Join-Path $objectDirectory "AwooNcmCefBridge.pdb"
$object = Join-Path $objectDirectory "AwooNcmCefBridge.obj"
$optimization = if ($Configuration -eq "Debug") { "/Od /Zi" } else { "/O2" }
$compile = @(
    "`"$vcvars`"", "&&", "cl.exe", "/nologo", "/std:c++20",
    "/EHsc", "/W4", "/WX", "/wd4100", "/DUNICODE", "/D_UNICODE", $optimization,
    "/I`"$cefRoot`"",
    "/Fo`"$object`"", "/Fd`"$pdb`"", "/LD", "`"$source`"",
    "/link", "/OUT:`"$dll`"", "kernel32.lib", "user32.lib"
) -join " "
& $env:ComSpec /d /s /c $compile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output $dll

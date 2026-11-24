<#
.SYNOPSIS
  Builds meshoptimizer as a shared library (static CRT, x64) and copies the DLL,
  its LICENSE.md and a VERSION.txt into the SceneKit native folder.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build_meshoptimizer.ps1
  powershell -ExecutionPolicy Bypass -File tools\build_meshoptimizer.ps1 -Tag v1.3 -Out csharp\SceneKit\native\win-x64

  Needs CMake and Visual Studio 2022 (C++ workload). PowerShell 5.1 compatible.
  The source zip is downloaded and built in a new empty folder under $env:TEMP.
#>
param(
    [string]$Tag = "v1.3",
    [string]$Out = "csharp\SceneKit\native\win-x64"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is very slow with the progress bar in 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $repoRoot $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

# 1. Download and extract into a new empty folder.
$work = Join-Path $env:TEMP ("meshopt-build-" + [guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $work | Out-Null
$zip = Join-Path $work "src.zip"
$url = "https://github.com/zeux/meshoptimizer/archive/refs/tags/$Tag.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
$extract = Join-Path $work "src"
Expand-Archive -Path $zip -DestinationPath $extract
$top = @(Get-ChildItem -Path $extract -Directory)
if ($top.Count -ne 1) { throw "Expected one top folder in the zip, found $($top.Count)" }
$src = $top[0].FullName
$build = Join-Path $work "build"

# 2. Configure: shared library, static CRT (no vcruntime140.dll needed on the target PC).
& cmake -S $src -B $build -G "Visual Studio 17 2022" -A x64 `
    -DMESHOPT_BUILD_SHARED_LIBS=ON -DMESHOPT_INSTALL=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }

# 3. Build.
& cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }

# 4. Copy DLL and license, write VERSION.txt.
$dll = Join-Path $build "Release\meshoptimizer.dll"
if (-not (Test-Path $dll)) { throw "Build output not found: $dll" }
$outDll = Join-Path $Out "meshoptimizer.dll"
Copy-Item -Path $dll -Destination $outDll -Force
Copy-Item -Path (Join-Path $src "LICENSE.md") -Destination (Join-Path $Out "LICENSE.md") -Force
$hash = (Get-FileHash -Algorithm SHA256 -Path $outDll).Hash
$version = @(
    "meshoptimizer $Tag",
    "source: $($top[0].Name) ($url)",
    "built: $(Get-Date -Format 'yyyy-MM-dd')",
    "sha256: $hash"
)
Set-Content -Path (Join-Path $Out "VERSION.txt") -Value $version -Encoding ASCII
Write-Host "Wrote $outDll"
$version | ForEach-Object { Write-Host "  $_" }

# 5. Show dependents and the simplify exports.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$dumpbin = @(& $vswhere -latest -find "**\Hostx64\x64\dumpbin.exe") | Select-Object -First 1
if (-not $dumpbin) { throw "dumpbin.exe not found through vswhere" }
& $dumpbin /dependents $outDll
& $dumpbin /exports $outDll | Select-String "meshopt_simplify"

Write-Host "Build folder (safe to delete): $work"

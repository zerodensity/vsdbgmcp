<#
.SYNOPSIS
  Builds vsdbgmcp: the shim, the tests, and the Visual Studio extension.

.DESCRIPTION
  Two toolchains, because the pieces need different ones.

  The shim and tests are .NET 10 and build with the dotnet CLI.

  The extension is packaged by MSBuild tasks written for .NET Framework, so only the
  MSBuild that ships inside Visual Studio can run them. That MSBuild cannot resolve
  Microsoft.NET.Sdk unless the .NET SDK component is installed into Visual Studio,
  which is why the extension project uses the legacy project format instead.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,
    [switch]$ShimOnly,

    # Copy the shim to a stable location and print the client configuration for it.
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }

    # Any Visual Studio will do. The extension development workload is what actually
    # matters, but vswhere's component query is unreliable across releases, so let the
    # build fail with its own message rather than guessing here.
    $found = & $vswhere -latest -prerelease -products * -find "MSBuild\Current\Bin\MSBuild.exe"

    foreach ($path in @($found)) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    return $null
}

Write-Host "==> shim and tests ($Configuration)" -ForegroundColor Cyan
dotnet build "$root\src\VsDbgMcp.Shim\VsDbgMcp.Shim.csproj" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "shim build failed" }

if (-not $SkipTests) {
    Write-Host "==> tests" -ForegroundColor Cyan
    dotnet test "$root\tests\VsDbgMcp.Tests\VsDbgMcp.Tests.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

if ($ShimOnly) {
    Write-Host "shim only, skipping the extension" -ForegroundColor Yellow
    exit 0
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Warning @"
No Visual Studio with the extension development workload was found, so the VSIX was
not built. Install the 'Visual Studio extension development' workload, or pass
-ShimOnly to build just the shim.
"@
    exit 0
}

Write-Host "==> extension ($Configuration)" -ForegroundColor Cyan
Write-Host "    $msbuild" -ForegroundColor DarkGray

& $msbuild "$root\src\VsDbgMcp.Host\VsDbgMcp.Host.csproj" -t:Restore -v:q -nologo -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) { throw "extension restore failed" }

& $msbuild "$root\src\VsDbgMcp.Host\VsDbgMcp.Host.csproj" -t:Rebuild -v:m -nologo -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) { throw "extension build failed" }

$vsix = Get-ChildItem "$root\src\VsDbgMcp.Host\bin\$Configuration\*.vsix" | Select-Object -First 1
$exe = "$root\src\VsDbgMcp.Shim\bin\$Configuration\net10.0\vsdbgmcp.exe"

if ($Install) {
    # Agents keep the shim running, and a running exe cannot be overwritten. Pointing
    # the client at a copy rather than at the build output means a rebuild does not
    # fail just because something is connected.
    $target = Join-Path $env:LOCALAPPDATA 'vsdbgmcp\bin'
    New-Item -ItemType Directory -Force $target | Out-Null

    # An agent that is connected right now holds this exe open, and Windows will not
    # let a running image be overwritten. It will let one be renamed, though, so move
    # the old files aside and write the new ones beside them. The running agent keeps
    # using what it already loaded until it restarts.
    Get-ChildItem $target -File -Filter '*.superseded' -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

    foreach ($file in Get-ChildItem $target -File -ErrorAction SilentlyContinue) {
        try { Rename-Item $file.FullName ($file.Name + '.superseded') -Force }
        catch { }
    }

    try {
        Copy-Item "$root\src\VsDbgMcp.Shim\bin\$Configuration\net10.0\*" $target -Recurse -Force
    }
    catch {
        throw "Could not copy the shim to $target.`n$_"
    }

    if (Get-ChildItem $target -File -Filter '*.superseded' -ErrorAction SilentlyContinue) {
        Write-Host "an agent is still running the previous shim; it picks this up on restart" -ForegroundColor Yellow
    }

    $exe = Join-Path $target 'vsdbgmcp.exe'
    Write-Host "installed shim to $target" -ForegroundColor Green
}

Write-Host ""
Write-Host "shim:      $exe" -ForegroundColor Green
Write-Host "extension: $($vsix.FullName)" -ForegroundColor Green
Write-Host ""
Write-Host "Install the extension by double-clicking the .vsix, then point your agent at the shim:"
Write-Host "  claude mcp add -s user vsdbg -- `"$exe`"" -ForegroundColor Gray
if (-not $Install) {
    Write-Host ""
    Write-Host "Run with -Install to copy the shim somewhere stable first, so rebuilding" -ForegroundColor DarkGray
    Write-Host "does not fail while an agent has it open." -ForegroundColor DarkGray
}

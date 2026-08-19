<#
.SYNOPSIS
  Builds vsdbgmcp: the shim, the tests, and the Visual Studio extension.

.DESCRIPTION
  Two toolchains, because the pieces need different ones.

  The shim and tests are .NET 10 and build with the dotnet CLI. The shim is published
  self-contained so that installing the extension is the whole installation: a machine
  with Visual Studio and no .NET runtime still gets a working shim.

  The extension is packaged by MSBuild tasks written for .NET Framework, so only the
  MSBuild that ships inside Visual Studio can run them. That MSBuild cannot resolve
  Microsoft.NET.Sdk unless the .NET SDK component is installed into Visual Studio,
  which is why the extension project uses the legacy project format instead.

  The published shim is picked up from artifacts\shim and carried inside the VSIX, so
  it has to be built before the extension.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,
    [switch]$ShimOnly,

    # Copy the shim straight to where the extension stages it, for working on the shim
    # without reinstalling the extension.
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$shimStage = Join-Path $root 'artifacts\shim'

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

# One version, in two files that cannot see each other. The manifest is the one a
# release is cut from, so it wins, and a mismatch stops the build rather than shipping
# an extension whose assemblies claim a different number.
$manifestPath = Join-Path $root 'src\VsDbgMcp.Host\source.extension.vsixmanifest'
$version = ([xml](Get-Content $manifestPath)).PackageManifest.Metadata.Identity.Version
$propsVersion = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version

if ($version -ne $propsVersion) {
    throw "version mismatch: vsixmanifest says $version, Directory.Build.props says $propsVersion"
}

Write-Host "==> vsdbgmcp $version ($Configuration)" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "==> tests" -ForegroundColor Cyan
    dotnet test "$root\tests\VsDbgMcp.Tests\VsDbgMcp.Tests.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

Write-Host "==> shim (self-contained win-x64)" -ForegroundColor Cyan
if (Test-Path $shimStage) { Remove-Item $shimStage -Recurse -Force }

dotnet publish "$root\src\VsDbgMcp.Shim\VsDbgMcp.Shim.csproj" `
    -c $Configuration -r win-x64 --self-contained true `
    -o $shimStage --nologo
if ($LASTEXITCODE -ne 0) { throw "shim publish failed" }

$exe = Join-Path $shimStage 'vsdbgmcp.exe'
if (-not (Test-Path $exe)) { throw "shim publish produced no vsdbgmcp.exe" }

if ($ShimOnly -and -not $Install) {
    Write-Host "shim only, skipping the extension" -ForegroundColor Yellow
}

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'vsdbgmcp\bin'
    New-Item -ItemType Directory -Force $target | Out-Null

    # An agent that is connected right now holds this exe open, and Windows will not
    # let a running image be overwritten. It will let one be renamed, though, so move
    # the old files aside and write the new ones beside them. The running agent keeps
    # using what it already loaded until it restarts.
    Get-ChildItem $target -File -Filter '*.superseded*' -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

    foreach ($file in Get-ChildItem $target -File -ErrorAction SilentlyContinue) {
        try { Rename-Item $file.FullName ($file.Name + '.superseded') -Force }
        catch { }
    }

    try {
        Copy-Item "$shimStage\*" $target -Recurse -Force
    }
    catch {
        throw "Could not copy the shim to $target.`n$_"
    }

    if (Get-ChildItem $target -File -Filter '*.superseded*' -ErrorAction SilentlyContinue) {
        Write-Host "an agent is still running the previous shim; it picks this up on restart" -ForegroundColor Yellow
    }

    $exe = Join-Path $target 'vsdbgmcp.exe'
    Write-Host "installed shim to $target" -ForegroundColor Green
}

if ($ShimOnly) {
    Write-Host ""
    Write-Host "shim: $exe" -ForegroundColor Green
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

Write-Host "==> extension" -ForegroundColor Cyan
Write-Host "    $msbuild" -ForegroundColor DarkGray

& $msbuild "$root\src\VsDbgMcp.Host\VsDbgMcp.Host.csproj" -t:Restore -v:q -nologo -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) { throw "extension restore failed" }

& $msbuild "$root\src\VsDbgMcp.Host\VsDbgMcp.Host.csproj" -t:Rebuild -v:m -nologo -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) { throw "extension build failed" }

$vsix = Get-ChildItem "$root\src\VsDbgMcp.Host\bin\$Configuration\*.vsix" | Select-Object -First 1

# An extension that ships without the shim installs to a panel with nothing behind it,
# and a missing icon or licence is only noticed by the Marketplace. All of it arrives
# through packaging metadata that fails silently, so look inside the package.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsix.FullName)
try {
    $entries = $zip.Entries.FullName
    foreach ($required in 'shim/vsdbgmcp.exe', 'Resources/icon.png', 'Resources/preview.png', 'LICENSE.txt') {
        if ($entries -notcontains $required) { throw "the VSIX is missing $required" }
    }
    $shimFiles = ($entries | Where-Object { $_.StartsWith('shim/') }).Count
}
finally { $zip.Dispose() }

Write-Host ""
Write-Host "extension: $($vsix.FullName)  ($([math]::Round($vsix.Length / 1MB, 1)) MB, $shimFiles shim files)" -ForegroundColor Green
Write-Host ""
Write-Host "Install by double-clicking the .vsix and restarting Visual Studio. The extension"
Write-Host "copies the shim to %LOCALAPPDATA%\vsdbgmcp\bin on startup; point your agent there"
Write-Host "once, globally:"
Write-Host "  claude mcp add -s user vsdbg -- `"$env:LOCALAPPDATA\vsdbgmcp\bin\vsdbgmcp.exe`"" -ForegroundColor Gray

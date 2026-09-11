# Runs against a dedicated experimental VS profile and a fresh copy of the fixture.
[CmdletBinding()]
param(
    [ValidatePattern('^CodexMcp[A-Za-z0-9]+$')][string]$RootSuffix = 'CodexMcpValidation',
    [switch]$SkipBuild,
    [switch]$Profiles,
    [switch]$ShimUpgrade,
    [ValidateRange(15, 300)][int]$StartupSeconds = 120
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskRun = Join-Path $taskRoot ('artifacts\live\' + [Guid]::NewGuid().ToString('N'))
$taskData = Join-Path $taskRun 'data'
$taskFixture = Join-Path $taskRun 'fixture'
New-Item -ItemType Directory -Force $taskData,$taskFixture | Out-Null
Write-Host "Live artifacts: $taskRun"
$taskVswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$taskVs = (& $taskVswhere -latest -prerelease -products * -format json | ConvertFrom-Json)[0]
if (!$taskVs) { throw 'Visual Studio is required.' }
$taskMsbuild = Join-Path $taskVs.installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$taskDevenv = $taskVs.productPath
# Never reuse or close an already running test window, much less a normal VS window.
$taskExisting = Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" |
    Where-Object { $_.CommandLine -match ('(?i)/RootSuffix\s+"?' + [regex]::Escape($RootSuffix) + '(?:"|\s|$)') }
if ($taskExisting) { throw "The $RootSuffix test profile is already running. Close that test window before rerunning." }

if (!$SkipBuild) {
    $taskStage = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts\shim'))
    if (!$taskStage.StartsWith($taskRoot + '\')) { throw 'Invalid staging path.' }
    & (Join-Path $taskRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
foreach ($taskTracked in (& git -C $taskRoot ls-files -- tests/fixtures/cpp)) {
    $taskRelative = $taskTracked.Substring('tests/fixtures/cpp/'.Length)
    $taskDestination = Join-Path $taskFixture $taskRelative
    New-Item -ItemType Directory -Force (Split-Path $taskDestination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskTracked) -Destination $taskDestination
}
& $taskMsbuild (Join-Path $taskRoot 'src\VsDbgMcp.Host\VsDbgMcp.Host.csproj') /t:Build /p:Configuration=Release /p:DeployExtension=true "/p:DeployTargetInstanceId=$($taskVs.instanceId)" "/p:VSSDKTargetPlatformRegRootSuffix=$RootSuffix" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Experimental deployment failed.' }

$taskChild = $null
$taskOldShim = $null
$taskOtherShim = $null
$taskSmokeShim = Join-Path $taskRoot 'artifacts\shim\vsdbgmcp.exe'

function Start-TestShim([string]$Path) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new($Path)
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardInput = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    $taskStart.Environment['VSDBGMCP_DATA_DIR'] = $taskData
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"shim-upgrade-test","version":"1"}}}')
        $taskProcess.StandardInput.Flush()
        $taskRead = $taskProcess.StandardOutput.ReadLineAsync()
        if (!$taskRead.Wait(20000)) { throw 'Test shim did not initialize.' }
        $taskReply = $taskRead.Result | ConvertFrom-Json
        if (!$taskReply.result.serverInfo) { throw 'Test shim returned no MCP server identity.' }
        $taskProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $taskProcess.StandardInput.Flush()
        return $taskProcess
    }
    catch {
        if (!$taskProcess.HasExited) { $taskProcess.Kill() }
        $taskProcess.Dispose()
        throw
    }
}

try {
    if ($ShimUpgrade) {
        # Build an older-version real MCP shim into only this run's private data
        # directory. Keep its client-side stdio open through extension startup.
        $taskOldBin = Join-Path $taskData 'bin'
        & dotnet publish (Join-Path $taskRoot 'src\VsDbgMcp.Shim\VsDbgMcp.Shim.csproj') -c Release -r win-x64 --self-contained true `
            -o $taskOldBin '-p:Version=0.0.0' "-p:ArtifactsPath=$(Join-Path $taskRun 'old-shim-build')" --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Old-version test shim build failed.' }
        $taskOldShim = Start-TestShim (Join-Path $taskOldBin 'vsdbgmcp.exe')
        $taskOtherShim = Start-TestShim $taskSmokeShim
        $taskSmokeShim = Join-Path $taskOldBin 'vsdbgmcp.exe'
    }
    # ResetSettings applies only to this named experimental profile. It also avoids
    # asking the first-run UI which development settings the test instance should use.
    $taskArgs = @('/RootSuffix', $RootSuffix, '/NoSplash', '/ResetSettings', 'General',
        ('"' + (Join-Path $taskFixture 'DebugTarget.sln') + '"'), '/Log', ('"' + (Join-Path $taskRun 'ActivityLog.xml') + '"'))
    $taskChild = Start-Process -FilePath $taskDevenv -ArgumentList $taskArgs -PassThru -WindowStyle Hidden -Environment @{ VSDBGMCP_DATA_DIR = $taskData }
    $taskRecord = Join-Path $taskData ("inst-$($taskChild.Id).json")
    $taskClock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($taskChild.HasExited) { throw "Experimental VS exited: $($taskChild.ExitCode). See $taskRun." }
        if (Test-Path -LiteralPath $taskRecord) {
            try { $taskRegistered = Get-Content -LiteralPath $taskRecord -Raw | ConvertFrom-Json } catch { $taskRegistered = $null }
            if ($taskRegistered -and $taskRegistered.Workspace.File -eq (Join-Path $taskFixture 'DebugTarget.sln')) { break }
        }
        if ($taskClock.Elapsed.TotalSeconds -ge $StartupSeconds) {
            throw "Experimental VS did not register the fixture. Finish first-run setup for /RootSuffix $RootSuffix and rerun. See $taskRun."
        }
        Start-Sleep -Milliseconds 500
    } while ($true)
    if ($ShimUpgrade) {
        if (!$taskOldShim.WaitForExit(30000)) { throw 'The updated extension did not retire the old installed shim.' }
        if ($taskOtherShim.HasExited) { throw 'Extension staging stopped a shim from another directory.' }
        $taskInstalledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskSmokeShim).FileVersion
        $taskBundledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskRoot 'artifacts\shim\vsdbgmcp.exe')).FileVersion
        if ($taskInstalledVersion -ne $taskBundledVersion) { throw 'The staged shim does not match the bundled version.' }
        @{ oldShimExited = $true; otherShimSurvived = $true; installedVersion = $taskInstalledVersion } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRun 'shim-upgrade.json')
        Write-Host "Shim upgrade: old installed process exited; other-directory shim survived; installed $taskInstalledVersion."
    }
    $taskPythonArgs = @((Join-Path $PSScriptRoot 'smoke.py'), '--shim', $taskSmokeShim, '--data', $taskData,
        '--fixture', $taskFixture, '--pid', $taskChild.Id, '--output', (Join-Path $taskRun 'results.json'))
    if ($Profiles) { $taskPythonArgs += '--profiles' }
    & python @taskPythonArgs
    if ($LASTEXITCODE -ne 0) { throw "Live checks failed. See $taskRun." }
}
finally {
    foreach ($taskShim in @($taskOldShim, $taskOtherShim)) {
        if ($taskShim) {
            if (!$taskShim.HasExited) { $taskShim.Kill() }
            $null = $taskShim.WaitForExit(5000)
            $taskShim.Dispose()
        }
    }
    # This handle is exclusively the process started above. No process-name cleanup.
    if ($taskChild -and !$taskChild.HasExited) {
        $null = $taskChild.CloseMainWindow()
        if (!$taskChild.WaitForExit(5000)) { Stop-Process -Id $taskChild.Id -Force }
    }
}

# Runs against a dedicated experimental VS profile and a fresh copy of the fixture.
[CmdletBinding()]
param(
    [ValidatePattern('^CodexMcp[A-Za-z0-9]+$')][string]$RootSuffix = 'CodexMcpValidation',
    [switch]$SkipBuild,
    [switch]$Profiles,
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
try {
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
    $taskPythonArgs = @((Join-Path $PSScriptRoot 'smoke.py'), '--shim', (Join-Path $taskRoot 'artifacts\shim\vsdbgmcp.exe'), '--data', $taskData,
        '--fixture', $taskFixture, '--pid', $taskChild.Id, '--output', (Join-Path $taskRun 'results.json'))
    if ($Profiles) { $taskPythonArgs += '--profiles' }
    & python @taskPythonArgs
    if ($LASTEXITCODE -ne 0) { throw "Live checks failed. See $taskRun." }
}
finally {
    # This handle is exclusively the process started above. No process-name cleanup.
    if ($taskChild -and !$taskChild.HasExited) {
        $null = $taskChild.CloseMainWindow()
        if (!$taskChild.WaitForExit(5000)) { Stop-Process -Id $taskChild.Id -Force }
    }
}

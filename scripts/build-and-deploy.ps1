<#
.SYNOPSIS
    Build the Linux dedicated server and the web client, then deploy both to the game box.

.DESCRIPTION
    This script does what section 9 of PLAN.md describes:
      1. Refuse to build from a dirty tree (the build stamp would say "<hash>-dirty").
      2. Build the Linux dedicated server into Builds/Server.
      3. Undo the reorder of DefaultPrefabObjects.asset that the build makes.
      4. Build the web client into Builds/Web, then undo the reorder again.
      5. Print the build results and stamps, then run scripts/deploy.sh through Git Bash.
    Close the Unity editor first, because batch mode can't open a project that's already open.

.EXAMPLE
    scripts\build-and-deploy.ps1                   # build and deploy server + web
    scripts\build-and-deploy.ps1 -Target web       # just the web client
    scripts\build-and-deploy.ps1 -SkipDeploy       # build only
    scripts\build-and-deploy.ps1 -SkipBuild        # deploy the existing builds
    scripts\build-and-deploy.ps1 -DeployHost 1.2.3.4   # before DNS resolves
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'server', 'web')]
    [string]$Target = 'all',
    [switch]$SkipBuild,
    [switch]$SkipDeploy,
    [switch]$AllowDirty,
    [string]$DeployHost,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.10f1\Editor\Unity.exe',
    [string]$GitBash = 'C:\Program Files\Git\bin\bash.exe'
)

$ErrorActionPreference = 'Stop'

# Run from the repo root. Unity deletes the output folder before building and fails
# if a shell is sitting inside it.
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$PrefabAsset = 'Game/Assets/DefaultPrefabObjects.asset'
$DoServer = $Target -in 'all', 'server'
$DoWeb = $Target -in 'all', 'web'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Restore-PrefabAsset {
    git checkout -- $PrefabAsset
    if ($LASTEXITCODE -ne 0) { throw "git checkout of $PrefabAsset failed" }
}

function Invoke-UnityBuild([string]$Method, [string]$LogName) {
    $log = Join-Path 'Builds' $LogName
    Write-Step "Running $Method (log: $log)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $unityArgs = @(
        '-batchmode', '-quit',
        '-projectPath', 'Game',
        '-executeMethod', "Jam.EditorTools.BuildScripts.$Method",
        '-logFile', $log
    )
    $proc = Start-Process -FilePath $Unity -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
    $sw.Stop()

    # Print the build result and stamp lines from the log.
    if (Test-Path $log) {
        Select-String -Path $log -Pattern '\[BuildScripts\] .*: ' | ForEach-Object { Write-Host "    $($_.Line)" }
    }
    if ($proc.ExitCode -ne 0) {
        throw "$Method failed with exit code $($proc.ExitCode) after $([int]$sw.Elapsed.TotalSeconds)s. See $log"
    }
    Write-Host "    finished in $([int]$sw.Elapsed.TotalSeconds)s"
}

if (-not $SkipBuild) {
    if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity (pass -Unity <path>)" }

    # Batch mode can't open a project that's already open in the editor.
    $editor = Get-Process -Name Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Unity }
    if ($editor) { throw 'The Unity editor is running. Close it first.' }

    # 1. Commit first, so the build stamp shows the commit instead of "<hash>-dirty".
    #    Untracked files don't make the stamp dirty, so ignore them here.
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) {
        Write-Host ($dirty -join "`n")
        if ($AllowDirty) {
            Write-Warning 'Working tree is dirty; the build stamp will say "-dirty".'
        } else {
            throw 'Working tree has uncommitted changes. Commit first, or pass -AllowDirty.'
        }
    }
    Write-Step "Building commit $(git rev-parse --short HEAD)"

    New-Item -ItemType Directory -Force -Path 'Builds' | Out-Null

    try {
        # 2. Linux dedicated server -> Builds/Server (about 1 minute)
        if ($DoServer) { Invoke-UnityBuild 'BuildServer' 'build-server.log' }

        # 3. The server build reorders DefaultPrefabObjects.asset (harmless). Undo it, or the
        #    web build's stamp says "-dirty".
        Restore-PrefabAsset

        # 4. Web build -> Builds/Web (about 4 minutes)
        if ($DoWeb) { Invoke-UnityBuild 'BuildWeb' 'build-web.log' }
    }
    finally {
        Restore-PrefabAsset
    }
}

# 5. Upload the builds and restart the game server. Needs the SSH key at ~/.ssh/nov26jam.
if (-not $SkipDeploy) {
    if (-not (Test-Path $GitBash)) { throw "Git Bash not found at $GitBash (pass -GitBash <path>)" }
    if ($DeployHost) { $env:DEPLOY_HOST = $DeployHost }
    Write-Step "Deploying ($Target)"
    # Call Git Bash explicitly: a bare "bash" can resolve to WSL.
    & $GitBash scripts/deploy.sh $Target
    if ($LASTEXITCODE -ne 0) { throw "deploy.sh failed with exit code $LASTEXITCODE" }

    $hostName = if ($DeployHost) { $DeployHost } else { 'jam.pmullins.dev' }
    Write-Host ''
    Write-Host "Check it: hard-refresh https://$hostName/ (Ctrl+F5); the debug panel's client and server stamps should both show $(git rev-parse --short HEAD)."
}

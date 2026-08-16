[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipRestore,
    [switch]$SkipMortalSetup,
    [switch]$ForceMortalSetup
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'Mahjong.Plugin.Dalamud\DomanMahjongSolverDebug.csproj'
$LocalDotnet = Join-Path $Root '.build-env\dotnet'
$DotnetExe = Join-Path $LocalDotnet 'dotnet.exe'
$Output = Join-Path $Root 'OUTPUT'
$DevOutput = Join-Path $Output 'DomanMahjongSolverDebug'
$LocalAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($LocalAppData)) {
    $LocalAppData = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)) 'AppData\Local'
}
# Keep the NuGet cache outside the deeply nested source tree. The previous
# repository-local cache could push assembly paths past the Windows MAX_PATH
# boundary and silently leave dependency DLLs unavailable to MSBuild.
$NugetPackages = Join-Path $LocalAppData 'DomanMahjongSolverDebug\NuGet'
$InstallScript = Join-Path $PSScriptRoot 'dotnet-install.ps1'
$RequiredMajor = 10
$SdkChannel = '10.0'

function Write-Step([string]$Message) {
    Write-Host "`n============================================================"
    Write-Host " $Message"
    Write-Host "============================================================"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    Write-Host "> $FilePath $($Arguments -join ' ')"

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $rawOutput = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }

    $lines = @($rawOutput | ForEach-Object { $_.ToString() })
    foreach ($line in $lines) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { Write-Host $line }
    }

    if ($null -eq $exitCode) { $exitCode = 0 }
    if ($exitCode -ne 0) {
        $tail = ($lines | Select-Object -Last 20) -join [Environment]::NewLine
        if ([string]::IsNullOrWhiteSpace($tail)) {
            throw "Command failed with exit code ${exitCode}: $FilePath"
        }
        throw "Command failed with exit code ${exitCode}: $FilePath`n$tail"
    }
}

function Get-SdkVersions {
    param([Parameter(Mandatory)][string]$Candidate)

    try {
        $lines = & $Candidate --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) { return @() }

        return @(
            $lines |
                ForEach-Object {
                    if ($_ -match '^(?<Version>\d+\.\d+\.\d+)\s+\[') {
                        [version]$Matches.Version
                    }
                } |
                Where-Object { $_ -and $_.Major -eq $RequiredMajor } |
                Sort-Object -Descending
        )
    } catch {
        return @()
    }
}

function Get-UsableDotnet {
    $candidates = @()
    if (Test-Path $DotnetExe) { $candidates += $DotnetExe }

    $system = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($system) { $candidates += $system.Source }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $versions = Get-SdkVersions -Candidate $candidate
        if ($versions.Count -gt 0) {
            return [pscustomobject]@{
                Executable = $candidate
                SdkVersion = $versions[0].ToString()
            }
        }
    }

    return $null
}


function Reset-StaleBuildState {
    Write-Step 'Reset stale build state'

    # project.assets.json stores absolute NuGet package paths. Remove every
    # project bin/obj directory so a cache-path change can never reuse stale
    # references from an earlier build package.
    $projectDirectories = Get-ChildItem -Path $Root -Filter '*.csproj' -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Directory.FullName } |
        Sort-Object -Unique

    foreach ($projectDirectory in $projectDirectories) {
        foreach ($name in @('bin', 'obj')) {
            $candidate = Join-Path $projectDirectory $name
            if (Test-Path $candidate) {
                Write-Host "[INFO] Removing stale directory: $candidate"
                Remove-Item -LiteralPath $candidate -Recurse -Force
            }
        }
    }

    $oldNugetCache = Join-Path $Root '.build-env\nuget-packages'
    if (Test-Path $oldNugetCache) {
        Write-Host "[INFO] Removing obsolete long-path NuGet cache: $oldNugetCache"
        try {
            Remove-Item -LiteralPath $oldNugetCache -Recurse -Force
        } catch {
            Write-Host "[WARN] Could not remove obsolete cache. It will not be used: $($_.Exception.Message)"
        }
    }
}

function Set-CompatibleGlobalJson {
    param([Parameter(Mandatory)][string]$Version)

    $globalJsonPath = Join-Path $Root 'global.json'
    $content = [ordered]@{
        sdk = [ordered]@{
            version = $Version
            rollForward = 'latestPatch'
            allowPrerelease = $false
        }
    } | ConvertTo-Json -Depth 4

    # .NET accepts UTF-8 with or without BOM. Use UTF-8 explicitly for Windows PowerShell 5.1.
    [System.IO.File]::WriteAllText($globalJsonPath, $content + [Environment]::NewLine, (New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false))
    Write-Host "[INFO] Selected .NET SDK $Version"
    Write-Host "[INFO] Updated global.json: $globalJsonPath"
}

function Install-LocalDotnet {
    Write-Step ".NET $RequiredMajor SDK local setup"
    New-Item -ItemType Directory -Force -Path $LocalDotnet | Out-Null

    if (-not (Test-Path $InstallScript)) {
        Write-Host '[INFO] Downloading official Microsoft dotnet-install.ps1...'
        $urls = @(
            'https://dot.net/v1/dotnet-install.ps1',
            'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1'
        )
        $downloaded = $false
        foreach ($url in $urls) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $InstallScript
                $downloaded = $true
                break
            } catch {
                Write-Host "[WARN] Download failed: $url"
            }
        }
        if (-not $downloaded) {
            throw 'Could not download dotnet-install.ps1. Check Internet access, proxy, antivirus, or TLS settings.'
        }
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $InstallScript `
        -Channel $SdkChannel `
        -Quality GA `
        -Architecture x64 `
        -InstallDir $LocalDotnet `
        -NoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $DotnetExe)) {
        throw ".NET SDK installation failed. Expected: $DotnetExe"
    }
}

Write-Step 'Doman Mahjong Solver Debug - one-click build'
Write-Host "Root: $Root"
Write-Host "Project: $Project"

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

$dotnetInfo = Get-UsableDotnet
if (-not $dotnetInfo) {
    Install-LocalDotnet
    $dotnetInfo = Get-UsableDotnet
}
if (-not $dotnetInfo) {
    throw ".NET $RequiredMajor SDK could not be located after setup."
}

$dotnet = $dotnetInfo.Executable
Set-CompatibleGlobalJson -Version $dotnetInfo.SdkVersion

Write-Step 'Build environment'
$activeSdk = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw '.NET SDK version check failed.' }
if ($activeSdk -ne $dotnetInfo.SdkVersion) {
    throw "Selected SDK $($dotnetInfo.SdkVersion), but dotnet resolved $activeSdk. global.json or PATH is inconsistent."
}
& $dotnet --info
if ($LASTEXITCODE -ne 0) { throw '.NET SDK verification failed.' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = $NugetPackages
New-Item -ItemType Directory -Force -Path $NugetPackages | Out-Null
Write-Host "[INFO] NuGet package cache: $NugetPackages"

# Always reset stale MSBuild assets. This is required when moving from the
# old repository-local cache, whose dependency paths could exceed 260 chars.
Reset-StaleBuildState

if ($Clean) {
    Write-Step 'Clean'
    Invoke-Checked $dotnet @('clean', $Project, '-c', 'Release', '--nologo')
}

if (-not $SkipRestore) {
    Write-Step 'Restore dependencies'
    try {
        Invoke-Checked $dotnet @(
            'restore', $Project,
            '--packages', $NugetPackages,
            '--locked-mode',
            '--nologo'
        )
    } catch {
        Write-Host '[WARN] Locked restore failed. Retrying normal restore and updating lock information...'
        Invoke-Checked $dotnet @(
            'restore', $Project,
            '--packages', $NugetPackages,
            '--force-evaluate',
            '--nologo'
        )
    }

    # The plugin requires this assembly at compile time. Verify it explicitly
    # so an incomplete NuGet extraction produces a clear error and one clean
    # recovery attempt instead of a misleading IServiceCollection failure.
    $diPackageRoot = Join-Path $NugetPackages 'microsoft.extensions.dependencyinjection.abstractions\9.0.0'
    $diAssembly = Join-Path $diPackageRoot 'lib\net9.0\Microsoft.Extensions.DependencyInjection.Abstractions.dll'
    if (-not (Test-Path $diAssembly)) {
        Write-Host '[WARN] DependencyInjection.Abstractions was not extracted correctly. Re-downloading the DI packages...'
        foreach ($packageName in @(
            'microsoft.extensions.dependencyinjection',
            'microsoft.extensions.dependencyinjection.abstractions'
        )) {
            $packagePath = Join-Path $NugetPackages $packageName
            if (Test-Path $packagePath) {
                Remove-Item -LiteralPath $packagePath -Recurse -Force
            }
        }

        Invoke-Checked $dotnet @(
            'restore', $Project,
            '--packages', $NugetPackages,
            '--force',
            '--no-cache',
            '--nologo'
        )
    }

    if (-not (Test-Path $diAssembly)) {
        throw "Required dependency DLL was not restored: $diAssembly"
    }
    Write-Host "[OK] Verified dependency DLL: $diAssembly"
}

Write-Step 'Release build and Dalamud package'
$buildArgs = @('build', $Project, '-c', 'Release', '--nologo')
if (-not $SkipRestore) { $buildArgs += '--no-restore' }
Invoke-Checked $dotnet $buildArgs

if (-not $SkipMortalSetup) {
    Write-Step 'Install and verify Mortal AI runtime'
    $mortalInstaller = Join-Path $PSScriptRoot 'Install-MortalRuntime.ps1'
    if (-not (Test-Path $mortalInstaller)) { throw "Mortal installer not found: $mortalInstaller" }
    $mortalArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $mortalInstaller,
        '-Root', $Root,
        '-Output', $Output
    )
    if ($ForceMortalSetup) { $mortalArgs += '-Force' }
    Invoke-Checked 'powershell.exe' $mortalArgs
}

Write-Step 'Collect developer plugin files'
# Preserve Mortal installer diagnostics written into OUTPUT. Only replace the
# developer-plugin subdirectory and package ZIP from a previous build.
if (Test-Path $DevOutput) { Remove-Item -Recurse -Force $DevOutput }
$oldPackage = Join-Path $Output 'DomanMahjongSolverDebug-latest.zip'
if (Test-Path $oldPackage) { Remove-Item -LiteralPath $oldPackage -Force }
New-Item -ItemType Directory -Force -Path $DevOutput | Out-Null

$releaseRoot = Join-Path $Root 'Mahjong.Plugin.Dalamud\bin\Release'
$latestZip = Get-ChildItem -Path $releaseRoot -Filter latest.zip -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($latestZip) {
    Write-Host "[INFO] Package: $($latestZip.FullName)"
    Expand-Archive -Path $latestZip.FullName -DestinationPath $DevOutput -Force
    Copy-Item $latestZip.FullName (Join-Path $Output 'DomanMahjongSolverDebug-latest.zip') -Force
} else {
    Write-Host '[WARN] latest.zip was not found; collecting build output directly.'
    $dll = Get-ChildItem -Path $releaseRoot -Filter DomanMahjongSolverDebug.dll -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $dll) { throw 'DomanMahjongSolverDebug.dll was not generated.' }
    Copy-Item (Join-Path $dll.DirectoryName '*') $DevOutput -Recurse -Force
}

$pluginDll = Get-ChildItem -Path $DevOutput -Filter DomanMahjongSolverDebug.dll -Recurse -File |
    Select-Object -First 1
if (-not $pluginDll) { throw 'Packaged developer plugin DLL was not found.' }

$manifest = Get-ChildItem -Path $DevOutput -Filter DomanMahjongSolverDebug.json -Recurse -File | Select-Object -First 1
if (-not $manifest) {
    $sourceManifest = Join-Path $Root 'Mahjong.Plugin.Dalamud\DomanMahjongSolverDebug.json'
    if (Test-Path $sourceManifest) { Copy-Item $sourceManifest $DevOutput -Force }
}

$resolvedDll = $pluginDll.FullName
Set-Content -Path (Join-Path $Output 'DEV_PLUGIN_DLL_PATH.txt') -Value $resolvedDll -Encoding UTF8

# Dalamud loads a developer plugin as a package, not as an isolated assembly:
# the adjacent manifest, dependency assemblies and runtime assets must stay
# beside the registered DLL.  Publishing only DomanMahjongSolverDebug.dll made
# the copied registration directory unrecognizable to Dalamud, so it never
# appeared in the plugin list.  Mirror the verified package to the designated
# registration directory while retaining the DLL path as the stable entrypoint.
$versionProps = Join-Path $Root 'Directory.Build.props'
$versionMatch = Select-String -Path $versionProps -Pattern '<Version>(?<version>[^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch -or [string]::IsNullOrWhiteSpace($versionMatch.Matches[0].Groups['version'].Value)) {
    throw "Could not determine plugin version from $versionProps"
}
$pluginVersion = $versionMatch.Matches[0].Groups['version'].Value.Trim()
$registrationRoot = Join-Path (Split-Path -Parent $Root) '_output_dll'
$registrationPackage = Join-Path $registrationRoot ("Akochan-Selectable-v" + $pluginVersion)
New-Item -ItemType Directory -Force -Path $registrationPackage | Out-Null
Copy-Item -Path (Join-Path $DevOutput '*') -Destination $registrationPackage -Recurse -Force
$registrationDll = Join-Path $registrationPackage 'DomanMahjongSolverDebug.dll'
if (-not (Test-Path -LiteralPath $registrationDll)) {
    throw "Registration DLL was not copied: $registrationDll"
}

Set-Content -Path (Join-Path $Output 'REGISTER_IN_DALAMUD.txt') -Encoding UTF8 -Value @"
Doman Mahjong Solver Debug

1. Start XIVLauncher and log in.
2. Run /xlsettings in FFXIV.
3. Open Experimental.
4. Add the following DLL under Dev Plugin Locations:

$registrationDll

5. Open /xlplugins, then enable Doman Mahjong Solver Debug.
6. Open the plugin with /mjdebug.
7. In Settings, keep AI provider set to Mortal AI (installed runtime).
8. Enable Auto-play from the main window after checking the live state.

Mortal runtime: $(Join-Path $LocalAppData 'DomanMahjongSolverDebug\MortalRuntime')
This debug plugin has a separate InternalName, command, configuration, and log location from the normal plugin.
"@

Write-Step 'Completed'
Write-Host "Developer DLL: $resolvedDll"
Write-Host "Registration DLL: $registrationDll"
Write-Host "Package folder: $DevOutput"
Write-Host "Dalamud ZIP: $(Join-Path $Output 'DomanMahjongSolverDebug-latest.zip')"
if (-not $SkipMortalSetup) { Write-Host "Mortal runtime: $(Join-Path $LocalAppData 'DomanMahjongSolverDebug\MortalRuntime')" }

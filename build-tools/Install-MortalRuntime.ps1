[CmdletBinding()]
param(
    [string]$Root,
    [string]$Output,
    [switch]$Force,
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $Root 'OUTPUT' }
$LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($LocalAppData)) {
    $LocalAppData = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'AppData\Local'
}

$StateRoot = Join-Path $LocalAppData 'DomanMahjongSolverDebug'
$RuntimeRoot = Join-Path $StateRoot 'MortalRuntime'
$BotDir = Join-Path $RuntimeRoot 'bot'
$VenvDir = Join-Path $RuntimeRoot 'venv'
$VenvPython = Join-Path $VenvDir 'Scripts\python.exe'
$ToolsDir = Join-Path $StateRoot 'Tools'
$UvExe = Join-Path $ToolsDir 'uv.exe'
$Downloads = Join-Path $StateRoot 'Downloads'
$UvPythonDir = Join-Path $StateRoot 'Python'
$UvCacheDir = Join-Path $StateRoot 'UvCache'
$SmokeTest = @(
    (Join-Path $Root 'tools\external-ai\mortal_smoke_test.py'),
    (Join-Path $Root 'external-ai\mortal_smoke_test.py')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
$ReleaseUrl = 'https://github.com/shinkuan/Akagi-MjaiBot-Mortal/releases/latest/download/release4p.zip'
$UvUrl = 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip'
$VoidShineModelUrl = 'https://huggingface.co/VoidShine/mortal-298k/resolve/main/mortal_298k.pth?download=true'
$VoidShineModelName = 'VoidShine/mortal-298k'
$VoidShineModelMarker = Join-Path $RuntimeRoot 'VOIDSHINE_MORTAL_298K.txt'
$VoidShineModelManifest = Join-Path $RuntimeRoot 'MORTAL_MODEL_MANIFEST.json'
$VoidShineModelCache = Join-Path $Downloads 'mortal_298k.pth'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"

    # Windows PowerShell 5.1 converts anything written to a native process's
    # stderr into NativeCommandError records. With ErrorActionPreference=Stop,
    # even a successful command such as `uv python install` can therefore abort
    # the build. Capture both streams under Continue, replay them as ordinary
    # console text, and use the native exit code as the only success criterion.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $rawOutput = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }

    $lines = @($rawOutput | ForEach-Object { $_.ToString() })
    if (-not $Quiet) {
        foreach ($line in $lines) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-Host $line
            }
        }
    }

    if ($null -eq $exitCode) { $exitCode = 0 }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $tail = ($lines | Select-Object -Last 20) -join [Environment]::NewLine
        if ([string]::IsNullOrWhiteSpace($tail)) {
            throw "Command failed with exit code ${exitCode}: $FilePath"
        }
        throw "Command failed with exit code ${exitCode}: $FilePath`n$tail"
    }

    return [pscustomobject]@{
        ExitCode = [int]$exitCode
        Output = $lines
        Text = ($lines -join [Environment]::NewLine).Trim()
    }
}

function Invoke-Download([string]$Uri, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $last = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Write-Host "[INFO] Downloading ($attempt/3): $Uri"
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Destination
            } catch {
                $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
                if (-not $curl) { throw }
                Write-Host "[WARN] Invoke-WebRequest failed; retrying this attempt with curl.exe"
                Invoke-NativeCommand $curl.Source @(
                    '-L', '--fail', '--retry', '2', '--connect-timeout', '30',
                    '-o', $Destination, $Uri
                ) | Out-Null
            }
            if (-not (Test-Path $Destination) -or (Get-Item $Destination).Length -le 0) {
                throw 'Downloaded file is empty.'
            }
            return
        } catch {
            $last = $_
            if (Test-Path $Destination) { Remove-Item $Destination -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    throw "Download failed: $Uri`n$($last.Exception.Message)"
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    Invoke-NativeCommand -FilePath $FilePath -Arguments $Arguments | Out-Null
}

function Test-BotLayout {
    $required = @(
        (Join-Path $BotDir 'bot.py'),
        (Join-Path $BotDir 'model.py'),
        (Join-Path $BotDir 'mortal.pth'),
        (Join-Path $BotDir '_libriichi_loader.py'),
        (Join-Path $BotDir 'libriichi\libriichi-3.12-x86_64-pc-windows-msvc.pyd')
    )
    return @($required | Where-Object { -not (Test-Path $_) }).Count -eq 0
}

function Install-BotFiles([string]$ArchivePath) {
    $extract = Join-Path $Downloads 'release4p-extracted'
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    try {
        Expand-Archive -Path $ArchivePath -DestinationPath $extract -Force
    } catch {
        throw "Could not extract Mortal release archive: $ArchivePath`n$($_.Exception.Message)"
    }

    $botPy = Get-ChildItem -Path $extract -Filter bot.py -File -Recurse |
        Sort-Object { $_.FullName.Length } |
        Select-Object -First 1
    if (-not $botPy) { throw 'release4p.zip did not contain bot.py.' }

    if (Test-Path $BotDir) { Remove-Item $BotDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $BotDir | Out-Null
    Copy-Item (Join-Path $botPy.Directory.FullName '*') $BotDir -Recurse -Force
}

Write-Host "[INFO] Mortal runtime root: $RuntimeRoot"
New-Item -ItemType Directory -Force -Path $RuntimeRoot, $ToolsDir, $Downloads, $UvCacheDir, $UvPythonDir | Out-Null

if ($Force -or -not (Test-BotLayout)) {
    $botZip = Join-Path $Downloads 'release4p.zip'
    if ($Force -or -not (Test-Path $botZip)) { Invoke-Download $ReleaseUrl $botZip }

    try {
        Install-BotFiles $botZip
    } catch {
        Write-Host "[WARN] Cached Mortal archive failed validation. Downloading a clean copy..."
        if (Test-Path $botZip) { Remove-Item $botZip -Force -ErrorAction SilentlyContinue }
        Invoke-Download $ReleaseUrl $botZip
        Install-BotFiles $botZip
    }
}
if (-not (Test-BotLayout)) { throw "Mortal release layout is incomplete: $BotDir" }

# Install the selected public checkpoint after the Akagi Mortal runtime files are
# present. Keep the runtime's original model as a local rollback copy, but make
# mortal.pth authoritative so bot.py uses VoidShine/mortal-298k without any
# change to the MJAI transport used by the plugin.
$activeModel = Join-Path $BotDir 'mortal.pth'
$stockModel = Join-Path $BotDir 'mortal.stock.pth'
$markerValid = (Test-Path $VoidShineModelMarker) -and ((Get-Content $VoidShineModelMarker -Raw).Trim() -eq $VoidShineModelName)
if ($Force -or -not $markerValid) {
    if ((Test-Path $activeModel) -and -not (Test-Path $stockModel)) {
        Copy-Item $activeModel $stockModel -Force
    }
    if ($Force -or -not (Test-Path $VoidShineModelCache)) {
        Invoke-Download $VoidShineModelUrl $VoidShineModelCache
    }
    $modelLength = (Get-Item $VoidShineModelCache).Length
    if ($modelLength -lt 50MB) {
        if (Test-Path $VoidShineModelCache) { Remove-Item $VoidShineModelCache -Force -ErrorAction SilentlyContinue }
        throw "VoidShine/mortal-298k download is unexpectedly small: $modelLength bytes"
    }
    Copy-Item $VoidShineModelCache $activeModel -Force
    Set-Content -Path $VoidShineModelMarker -Value $VoidShineModelName -Encoding UTF8
}
if (-not (Test-Path $activeModel)) { throw "Mortal model is missing: $activeModel" }

# Persist an evidence manifest for the exact active file. At runtime the plugin
# hashes mortal.pth again and compares it with this manifest, so a stale marker
# or a manually replaced checkpoint cannot be mistaken for the 298k model.
$activeModelInfo = Get-Item $activeModel
$activeModelHash = (Get-FileHash -Algorithm SHA256 -Path $activeModel).Hash.ToLowerInvariant()
$modelManifest = [ordered]@{
    schema = 1
    model = $VoidShineModelName
    checkpoint = 298000
    source_url = $VoidShineModelUrl
    active_file = $activeModelInfo.FullName
    bytes = [int64]$activeModelInfo.Length
    sha256 = $activeModelHash
    installed_utc = [DateTime]::UtcNow.ToString('o')
}
$modelManifest | ConvertTo-Json -Depth 4 | Set-Content -Path $VoidShineModelManifest -Encoding UTF8
Write-Host "[INFO] Mortal model: $VoidShineModelName checkpoint=298000 bytes=$($activeModelInfo.Length) sha256=$activeModelHash"

if ($Force -or -not (Test-Path $UvExe)) {
    $uvZip = Join-Path $Downloads 'uv-windows.zip'
    $uvExtract = Join-Path $Downloads 'uv-extracted'
    if ($Force -or -not (Test-Path $uvZip)) { Invoke-Download $UvUrl $uvZip }
    if (Test-Path $uvExtract) { Remove-Item $uvExtract -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $uvExtract | Out-Null
    Expand-Archive -Path $uvZip -DestinationPath $uvExtract -Force
    $foundUv = Get-ChildItem -Path $uvExtract -Filter uv.exe -File -Recurse | Select-Object -First 1
    if (-not $foundUv) { throw 'uv archive did not contain uv.exe.' }
    Copy-Item $foundUv.FullName $UvExe -Force
}

$env:UV_PYTHON_INSTALL_DIR = $UvPythonDir
$env:UV_CACHE_DIR = $UvCacheDir
$env:UV_NO_PROGRESS = '1'
$env:PYTHONUTF8 = '1'
Invoke-Checked $UvExe @('python', 'install', '3.12')

$needsVenv = $Force -or -not (Test-Path $VenvPython)
if (-not $needsVenv) {
    $pythonProbe = Invoke-NativeCommand $VenvPython @(
        '-c', 'import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)'
    ) -AllowFailure -Quiet
    $needsVenv = $pythonProbe.ExitCode -ne 0
}
if ($needsVenv) {
    if (Test-Path $VenvDir) { Remove-Item $VenvDir -Recurse -Force }
    Invoke-Checked $UvExe @('venv', $VenvDir, '--python', '3.12', '--managed-python')
}

$depsReady = $false
if (Test-Path $VenvPython) {
    $dependencyProbe = Invoke-NativeCommand $VenvPython @(
        '-c', 'import torch, numpy, requests; print(torch.__version__)'
    ) -AllowFailure -Quiet
    $depsReady = $dependencyProbe.ExitCode -eq 0
}
if ($Force -or -not $depsReady) {
    # Install PyTorch's regular Python dependencies from PyPI first, then pin
    # the actual torch wheel to the official CPU index. --no-deps prevents the
    # CPU-only index from being asked for unrelated packages.
    Invoke-Checked $UvExe @(
        'pip', 'install', '--python', $VenvPython,
        'numpy>=1.24', 'requests>=2.28',
        'filelock', 'typing-extensions>=4.10',
        'sympy>=1.13.3', 'networkx', 'jinja2', 'fsspec'
    )
    Invoke-Checked $UvExe @(
        'pip', 'install', '--python', $VenvPython,
        '--index-url', 'https://download.pytorch.org/whl/cpu',
        '--no-deps',
        'torch<2.9'
    )
}

if (-not $SkipSmokeTest) {
    if ([string]::IsNullOrWhiteSpace($SmokeTest) -or -not (Test-Path $SmokeTest)) {
        throw "Smoke-test script not found under $Root"
    }
    Write-Host '[INFO] Running Mortal model and JSONL round-trip smoke test...'
    Invoke-Checked $VenvPython @($SmokeTest, '--bot', $BotDir, '--timeout', '180')
} else {
    Write-Host '[INFO] Skipping smoke test (in-plugin first-run install).'
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
Set-Content -Path (Join-Path $Output 'MORTAL_RUNTIME_PATH.txt') -Value $RuntimeRoot -Encoding UTF8
Set-Content -Path (Join-Path $RuntimeRoot 'DOMAN_RUNTIME_VERSION.txt') -Value '0.8.0.89' -Encoding UTF8
$pythonVersion = (Invoke-NativeCommand $VenvPython @('--version') -Quiet).Text
$torchVersion = (Invoke-NativeCommand $VenvPython @('-c', 'import torch; print(torch.__version__)') -Quiet).Text
$readyNote = if ($SkipSmokeTest) { 'Mortal runtime installed (smoke test skipped).' } else { 'Mortal runtime smoke test passed.' }
Set-Content -Path (Join-Path $Output 'MORTAL_READY.txt') -Encoding UTF8 -Value @"
$readyNote
Runtime: $RuntimeRoot
Python: $pythonVersion
PyTorch: $torchVersion
Bot: $BotDir
Mode: local public model — VoidShine/mortal-298k (298k)
"@
Write-Host "[OK] Mortal runtime is ready: $RuntimeRoot"

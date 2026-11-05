[CmdletBinding()]
param(
    [string]$ServerUrl,
    [string]$LlamaServer,
    [string]$QwenModel,
    [string]$CudaLibraryDirectory,
    [switch]$Cpu
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ([bool]$LlamaServer -ne [bool]$QwenModel) {
        throw 'Supply both -LlamaServer and -QwenModel to start a local server with the app.'
    }
    $settingsFile = Join-Path $PSScriptRoot 'settings.json'
    $source = if (Test-Path -LiteralPath $settingsFile) { $settingsFile } else { 'settings.example.json' }
    $config = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    if ($ServerUrl) { $config.serverUrl = $ServerUrl }
    if ($LlamaServer) {
        if (-not $ServerUrl) { $config.serverUrl = 'http://127.0.0.1:8080/v1' }
        $config.localServer = [ordered]@{
            executable = (Resolve-Path -LiteralPath $LlamaServer).Path
            modelFile = (Resolve-Path -LiteralPath $QwenModel).Path
        }
    }
    if ($Cpu) { $config.device = 'cpu'; $config.computeType = 'int8' }
    if ($CudaLibraryDirectory) {
        $config.cudaLibraryDirectory = (Resolve-Path -LiteralPath $CudaLibraryDirectory).Path
    }
    if (-not (Test-Path -LiteralPath $config.python)) {
        py -3.11 -m venv .venv
        if ($LASTEXITCODE -ne 0) { throw 'Install x64 Python 3.11 with the Windows py launcher.' }
    }
    $python = (Resolve-Path -LiteralPath $config.python).Path
    & $python -m pip install --disable-pip-version-check -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw 'Installing the Whisper runtime failed.' }

    $modelDir = $config.whisperDirectory
    $files = @('model.bin', 'config.json', 'preprocessor_config.json', 'tokenizer.json', 'vocabulary.json')
    $missing = $files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $modelDir $_)) }
    if ($missing -and [IO.Path]::GetFullPath($modelDir) -ne [IO.Path]::GetFullPath('models/whisper-large-v3-turbo')) {
        foreach ($required in @('model.bin', 'config.json', 'tokenizer.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $modelDir $required))) {
                throw "Custom Whisper directory is missing $required. Provision that model or use the default whisperDirectory."
            }
        }
    } elseif ($missing) {
        $hf = Join-Path (Split-Path $python) 'hf.exe'
        & $hf download dropbox-dash/faster-whisper-large-v3-turbo @files --revision 0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf --local-dir $modelDir
        if ($LASTEXITCODE -ne 0) { throw 'Downloading the local Whisper model failed.' }
    }
    if (Test-Path -LiteralPath 'Desktop/TalkToMe.csproj') {
        dotnet restore Desktop/TalkToMe.csproj --packages "$PSScriptRoot/.cache/nuget"
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Install the .NET 8 SDK and check NuGet access.' }
        dotnet publish Desktop/TalkToMe.csproj --no-restore -c Release -o build/app
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the desktop app failed.' }
    } elseif (-not (Test-Path -LiteralPath 'build/app/TalkToMe.exe')) {
        throw 'TalkToMe.exe is missing. Extract the complete Windows release archive and run setup again.'
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath $settingsFile
    Write-Host 'Ready. Open TalkToMe.cmd, then focus a text field.'
} finally {
    Pop-Location
}

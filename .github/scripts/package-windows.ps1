param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$zipPath = [IO.Path]::GetFullPath($Destination)
$sourcePrefix = $sourcePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($zipPath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The archive must be outside the source folder.'
}
$parent = [IO.Path]::GetDirectoryName($zipPath)
[IO.Directory]::CreateDirectory($parent) | Out-Null

# tar -a -C <folder> . writes a literal ./ root that Windows Explorer cannot
# reliably extract. CreateFromDirectory emits ordinary relative ZIP entry names.
[IO.Compression.ZipFile]::CreateFromDirectory($sourcePath, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -match '(^|/)\.{1,2}(/|$)|\\|^/' -or $entry.FullName.Contains(':')) {
            throw "Non-portable archive path: $($entry.FullName)"
        }
    }
    foreach ($required in 'TalkToMe.exe', 'TalkToMe.CrashMonitor.exe', 'TalkToMe.FocusHost.exe') {
        if ($null -eq $zip.GetEntry($required)) { throw "Missing root entry: $required" }
    }
} finally { $zip.Dispose() }

# Use the built-in Windows ZIP extractor and compare every packaged file.
$checkPath = Join-Path $parent ('zip-check-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $checkPath
    $files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Force)
    if (@(Get-ChildItem -LiteralPath $checkPath -Recurse -File -Force).Count -ne $files.Count) {
        throw 'Extraction changed the file inventory.'
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourcePrefix.Length)
        $extracted = Join-Path $checkPath $relative
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $extracted).Hash) {
            throw "Extraction changed file contents: $relative"
        }
    }
    Write-Output "Verified ZIP paths and Windows extraction of $($files.Count) files."
} finally {
    $resolvedCheck = [IO.Path]::GetFullPath($checkPath)
    $parentPrefix = $parent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedCheck.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a verification folder outside the archive directory.'
    }
    if (Test-Path -LiteralPath $resolvedCheck) { Remove-Item -LiteralPath $resolvedCheck -Recurse -Force }
}

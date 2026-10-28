param(
    [string]$Version = "0.5.1",
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "dotnet\src\Wisp.Windows\bin\$Platform\$Configuration\net10.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path (Join-Path $src "Wisp.exe"))) {
    throw "Windows build not found at $src. Build the Release app first."
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$dst = Join-Path $dist "Wisp-Windows-x64-$Version.zip"
if (Test-Path $dst) { Remove-Item $dst }

# Build the archive by hand so entry names use forward slashes. GNU tar's
# "-a" on Windows creates a tar file even with a .zip name, and
# Compress-Archive writes backslashes; neither is a portable ZIP.
$zip = [System.IO.Compression.ZipFile]::Open($dst, "Create")
try {
    Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($src.Length + 1).Replace('\', '/')
        $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $_.LastWriteTime
        $out = $entry.Open()
        try {
            $in = [System.IO.File]::OpenRead($_.FullName)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
        }
        finally {
            $out.Dispose()
        }
    }
}
finally {
    $zip.Dispose()
}

Write-Output "Created $dst"

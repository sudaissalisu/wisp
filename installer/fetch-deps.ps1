$ErrorActionPreference = "Stop"
$dir = Join-Path $PSScriptRoot "deps"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$items = @(
    @{
        Url = "https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe"
        Out = Join-Path $dir "WindowsAppRuntimeInstall-x64.exe"
    },
    @{
        Url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe"
        Out = Join-Path $dir "windowsdesktop-runtime-10.0.13-win-x64.exe"
    }
)

foreach ($item in $items) {
    if (Test-Path $item.Out) { continue }
    Write-Host "Downloading $($item.Url)"
    Invoke-WebRequest -Uri $item.Url -OutFile $item.Out
}
Write-Host "Runtime installers are in $dir"

param([string]$Action = "")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Name = "StackchanSaver"
$Out  = "build"

function Invoke-DebugBuild {
    Write-Host "==> Debug build..."
    dotnet build "$Name.csproj" -c Debug -o "$Out\debug" --nologo -v minimal
    Write-Host "Built: $Out\debug\$Name.exe"
}

function Invoke-ReleaseBuild {
    Write-Host "==> Release build (self-contained single file)..."
    $releaseDir = "$Out\release"
    dotnet publish "$Name.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        "-p:PublishSingleFile=true" `
        --nologo -v minimal `
        -o $releaseDir
    $scr = "$Out\$Name.scr"
    Copy-Item "$releaseDir\$Name.exe" $scr -Force
    Write-Host "Built: $scr"
    return (Resolve-Path $scr).Path
}

$act = $Action.ToLower()

if ($act -eq "run") {
    Invoke-DebugBuild | Out-Null
    Write-Host "==> Launching fullscreen... (press any key / move mouse to exit)"
    & "$PSScriptRoot\$Out\debug\$Name.exe" /s

} elseif ($act -eq "release") {
    Invoke-ReleaseBuild | Out-Null
    Write-Host "Run '.\build.ps1 install' or '.\build.ps1 open' to install."

} elseif ($act -eq "install") {
    $scr  = Invoke-ReleaseBuild
    $dest = "$env:SystemRoot\System32\$Name.scr"
    Write-Host "==> Copying to System32 (requires admin)..."
    $cmd  = "Copy-Item -Path '$scr' -Destination '$dest' -Force"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command $cmd"
    Write-Host "Installed: $dest"
    Write-Host "Open Settings > Personalization > Lock screen > Screen saver"

} elseif ($act -eq "open") {
    $scr  = Invoke-ReleaseBuild
    $dest = "$env:SystemRoot\System32\$Name.scr"
    Write-Host "==> Copying to System32 (requires admin)..."
    $cmd  = "Copy-Item -Path '$scr' -Destination '$dest' -Force"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command $cmd"
    Write-Host "==> Opening screen saver settings..."
    Start-Process rundll32 -ArgumentList "desk.cpl,ScreenSaverSetup"

} else {
    Invoke-DebugBuild
}
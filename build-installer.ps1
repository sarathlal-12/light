# Builds Releases\light-win-Setup.exe (self-contained, .NET bundled).
# Needs: dotnet tool install -g vpk --version 1.2.161
param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Test-Path publish) { Remove-Item -Recurse -Force publish }
dotnet publish -c Release -r win-x64 --self-contained true -o publish -p:Version=$Version
vpk pack --packId light --packVersion $Version --packDir publish --mainExe light.exe `
    --packTitle ".light" --packAuthors ".light" --icon Assets\light.ico --outputDir Releases

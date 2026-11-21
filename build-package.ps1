$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:USERPROFILE\.dotnet\tools;$env:PATH"

Write-Host "1. Running Tests..." -ForegroundColor Cyan
dotnet test tests/DiscordSoundboard.Tests/DiscordSoundboard.Tests.csproj -c Release

Write-Host "2. Publishing win-x64 binary..." -ForegroundColor Cyan
$publishDir = "$scriptDir\src\DiscordSoundboard\bin\publish\win-x64"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src/DiscordSoundboard/DiscordSoundboard.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=false -o $publishDir

Write-Host "3. Staging package layout..." -ForegroundColor Cyan
$stagingDir = "$scriptDir\src\DiscordSoundboard\bin\staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path "$stagingDir\runtimes\win-x64" -Force | Out-Null

Copy-Item "$scriptDir\src\DiscordSoundboard\manifest.json" "$stagingDir\manifest.json"
Copy-Item "$scriptDir\src\DiscordSoundboard\Assets\icon.svg" "$stagingDir\icon.svg"
Copy-Item "$publishDir\*" "$stagingDir\runtimes\win-x64\" -Recurse

Write-Host "4. Packing .macroDeckPlugin artifact..." -ForegroundColor Cyan
$outputArtifact = "$scriptDir\com.sertaycil.discord-soundboard-1.0.0.macroDeckPlugin"
macrodeck-plugin pack --source $stagingDir --output $outputArtifact --force

Write-Host "5. Validating .macroDeckPlugin artifact..." -ForegroundColor Cyan
macrodeck-plugin validate --artifact $outputArtifact

Write-Host "Done! Package is 100% valid and ready at: $outputArtifact" -ForegroundColor Green

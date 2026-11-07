# Builds publish\installer\TurrisStudio-Setup-<version>.exe
#   1. publishes the app self-contained (no .NET install needed on the target PC)
#   2. compiles installer\TurrisStudio.iss with Inno Setup 6
# installer\deps must contain ffmpeg\ffmpeg.exe, ffmpeg\ffprobe.exe, ffmpeg\LICENSE and PawnIO_setup.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content "$root\src\TurrisStudio.App\TurrisStudio.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

foreach ($f in 'ffmpeg\ffmpeg.exe', 'ffmpeg\ffprobe.exe', 'ffmpeg\LICENSE', 'PawnIO_setup.exe') {
    if (-not (Test-Path "$PSScriptRoot\deps\$f")) { throw "Missing installer\deps\$f" }
}

Remove-Item "$root\publish\release" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\TurrisStudio.App" -c Release -r win-x64 --self-contained true -o "$root\publish\release"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup)' }
& $iscc "/DAppVersion=$version" "$PSScriptRoot\TurrisStudio.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Get-Item "$root\publish\installer\TurrisStudio-Setup-$version.exe"

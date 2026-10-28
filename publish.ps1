# Builds the tray app into .\publish and the console tool into .\publish\cli
$root = $PSScriptRoot
dotnet publish "$root\src\SamaLcd" -c Release -o "$root\publish" --self-contained false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish "$root\src\SamaLcd.Cli" -c Release -o "$root\publish\cli" --self-contained false
# video backgrounds are decoded with ffmpeg; reuse the copy installed with SAMA if there is none yet
$ffmpeg = "C:\Program Files (x86)\SAMA\ffmpeg.exe"
if (-not (Test-Path "$root\publish\ffmpeg.exe") -and (Test-Path $ffmpeg)) { Copy-Item $ffmpeg "$root\publish\ffmpeg.exe" }

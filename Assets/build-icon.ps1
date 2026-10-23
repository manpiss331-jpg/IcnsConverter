# Regenerates the app and installer icon assets from their SVG sources.
# Requires ImageMagick 7 with SVG support ("magick" on PATH).
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

magick -background none "$here\app-icon.svg" -resize 256x256 "$here\app-icon-256.png"
magick -background none "$here\app-icon.svg" -resize 128x128 "$here\app-icon-128.png"
magick -background none "$here\app-icon.svg" -define icon:auto-resize=256,128,64,48,32,24,16 "$here\app.ico"

magick -background none "$here\installer-icon.svg" -resize 256x256 "$here\installer-icon-256.png"
magick -background none "$here\installer-icon.svg" -define icon:auto-resize=256,128,64,48,32,24,16 "$here\installer.ico"

Write-Host "Rebuilt app and installer PNG/ICO assets"

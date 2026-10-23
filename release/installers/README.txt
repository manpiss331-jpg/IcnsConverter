ICNS Converter 1.0.0
====================

A Windows app for extracting every image size from macOS .icns icon files.

INSTALLING
----------
1. Run the setup file included in this package.
   - The x64 setup is for Windows 10/11 on Intel or AMD x64.
   - The ARM64 setup is for Windows 11 on ARM64.
2. Follow the setup wizard. The installer is per-user and does not require
   administrator rights.
3. The default install location is:
   C:\Users\<username>\AppData\Local\Programs\IcnsConverter
4. The setup shows the MIT license and short before/after notes.

The setup creates a Start Menu shortcut, an optional desktop shortcut, and
an entry in Settings > Apps for uninstalling.

USING THE APP
-------------
1. Open ICNS Converter.
2. Add .icns files or a folder (drag-and-drop works too).
3. Choose the output format and sizes.
4. Click Convert.

Converted files are written next to the source files by default. Existing
files are never overwritten; a number is appended instead. The app can also
open .icns files passed on the command line.

SUPPORTED OUTPUT
----------------
PNG, JPG, WEBP, ICO, BMP, TIFF, and GIF, including multi-size ICO output.

ARM64 NOTE
----------
The ARM64 installer currently contains the x64 build. Windows 11 ARM64 runs
it through its built-in x64 emulation. A native ARM64 build will be offered
once the .NET 10 ARM64 runtime pack is available.

UNINSTALLING
------------
Settings > Apps > Installed apps > ICNS Converter > Uninstall.
(On Windows 10: Settings > Apps > Apps & features.)

REQUIREMENTS
------------
- Windows 10 or Windows 11, 64-bit (x64 or ARM64).
- No .NET installation or additional DLLs are required.
- First launch may take a few seconds while bundled native components are
  unpacked.

INTEGRITY
---------
Verify the setup files against SHA256SUMS.txt when it is included. The
installers are unsigned; if Windows SmartScreen appears, choose "More info"
then "Run anyway" only if you trust the source.

LICENSE
-------
MIT License. See the license page displayed by the installer.

Thank you for using ICNS Converter.

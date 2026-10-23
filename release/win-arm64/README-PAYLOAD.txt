ARM64 PAYLOAD NOTICE

This folder currently contains the x64 build of ICNS Converter, copied here so
that an ARM64 installer could be produced without the .NET 10 ARM64 runtime
pack. Windows 11 ARM64 runs this x64 build through its built-in x64 emulation.

The resulting setup currently uses x64-compatible install mode and packages
the x64 build. It is not a native ARM64 package and is not ARM64-only. It
shows a notice before installing.

To produce a native ARM64 installer later:
  1. Publish the app for win-arm64 into this folder (IcnsConverter.exe).
  2. Run build-installer-arm64.ps1 again.

# Build Peel

Requires Windows x64 and the .NET 10 SDK. In this source folder:

```powershell
dotnet test tests/FileAction.Tests -c Release
dotnet publish src/FileAction.App -c Release -r win-x64 --self-contained true -o publish
```

The product executable is `publish\Peel.exe`. Inherited project paths and namespaces retain FileAction to preserve provenance; the app identity, executable, settings, mutex/pipe and Windows registrations are Peel.

Helper engines live in `tools\` next to `Peel.exe`: `ffmpeg.exe`, `ffprobe.exe`, `pandoc.exe` and `yt-dlp.exe`, plus an optional LibreOffice tree at `LibreOffice\program\soffice.com`. Peel downloads the missing ones from their official releases on first start (see `Shell/ToolSetup.cs`); they may also be found on PATH or in a normal LibreOffice installation. `scripts/fetch-tools.ps1` fetches FFmpeg and Pandoc for local tests.

Releases are built by `.github/workflows/build.yml`: pushing a tag `vX.Y.Z` builds, tests, compiles the installer (`installer/Peel.iss`) and publishes `Peel-Setup-X.Y.Z.exe` and a portable zip on the repository's Releases page. Installed copies update themselves from there.

Diagnostics: `Peel.exe --verify-peel <folder>` (workspace/preset/journal checks with rendered previews, isolated settings), `--self-test-workers <folder>`, `--glass-preview <folder> [dark|light] [bright]`, `--toast-preview <folder> [dark|light]` and `--link-preview <folder>`.

Read PEEL-CHANGES.md and THIRD-PARTY-NOTICES.md before modifying or distributing Peel.

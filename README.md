# Peel

Fast, local file conversion for Windows. Drag a file, tap **Shift**, **Ctrl** or **Alt**, and drop it on a glass wheel to convert, compress, trim or act on it. Or open the workspace, search an action and run it on a batch.

- **Wheel** – convert, tools and actions on a live-blur glass wheel that follows your Windows theme.
- **Workspace** – searchable actions, presets and recent results.
- **Video links** – copy a YouTube, Shorts, Instagram, TikTok or X link and Peel offers an MP4 (H.264 + AAC, up to 1080p) or MP3 (320 kbps) download into Downloads.
- **Notifications** – one glass card follows each job from progress to result.
- **Local** – files never leave your PC. Peel only goes online to download a video you chose, fetch its helper engines, and check for updates.

## Install

Download `Peel-Setup-x.y.z.exe` from [Releases](https://github.com/GuardaChuva22/peel/releases) and run it (Windows 10/11 x64, no admin rights needed).

The installer isn't code-signed yet, so Windows may show "Windows protected your PC": choose **More info → Run anyway**.

On first start Peel downloads FFmpeg, Pandoc and yt-dlp from their official releases (about 250 MB) and verifies each file against its published checksum. LibreOffice is optional, for Office documents (`winget install TheDocumentFoundation.LibreOffice`). Peel updates itself from this repository's releases.

## Build

See [BUILD-PEEL.md](BUILD-PEEL.md). Pushing a tag `vX.Y.Z` builds the installer and publishes a release.

## Credits

Peel is built on [FileAction](https://github.com/Metamorfolog/file_action) by Metamorfolog (MIT) and inspired by [Tangerine](https://tangerineformac.com/). See [PEEL-CHANGES.md](PEEL-CHANGES.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Licensed under the [MIT License](LICENSE).

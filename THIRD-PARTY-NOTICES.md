# Third-party software

Peel uses, or downloads on first start, the following software. Each keeps its own license.

| Component | Used for | License |
|---|---|---|
| [FileAction](https://github.com/Metamorfolog/file_action) by Metamorfolog | The project Peel is built on | MIT |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) / ImageMagick | Images | Apache-2.0 / ImageMagick License |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) / PDFium / SkiaSharp | PDF → images | MIT / BSD-3-Clause / MIT |
| [PdfPig](https://github.com/UglyToad/PdfPig) | PDF text | Apache-2.0 |
| [PDFsharp](https://github.com/empira/PDFsharp) 6.2 | Merge, split, rotate PDF | MIT |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | XLSX | MIT |
| [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader) | XLS | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | RAR, 7z, TAR… | MIT |
| [FFmpeg](https://ffmpeg.org) (`tools\ffmpeg.exe`, `tools\ffprobe.exe`, build by [BtbN](https://github.com/BtbN/FFmpeg-Builds)) | Audio and video | GPL-3.0 – source: https://github.com/FFmpeg/FFmpeg |
| [Pandoc](https://pandoc.org) (`tools\pandoc.exe`) | Documents | GPL-2.0-or-later – source: https://github.com/jgm/pandoc |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) (`tools\yt-dlp.exe`) | Downloading copied video links | Unlicense |
| [LibreOffice](https://www.libreoffice.org) (optional, installed separately) | Office files → PDF | MPL-2.0 |

FFmpeg, Pandoc and yt-dlp are not part of Peel's installer. Peel downloads them from the official release pages above on first start, checks them against the checksums published there, and runs them as separate programs on the command line.

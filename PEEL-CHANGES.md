# Peel — provenance and product changes

Peel is an independent Windows app derived from Metamorfolog/file_action (MIT License), captured at commit ac715f352e57e7e67fab1a22c7dc5b2d846fdf31; the upstream project published its MIT license in commit 58dd63ee4. Upstream conversion catalog, format backends, native drag infrastructure, shell helpers, floating shelf and parts of the editor infrastructure remain. Namespaces retain FileAction to make the lineage traceable. It is not claimed to be written entirely from scratch.

## Original Peel application work

- A new native workspace: file selection rail, batch input, context-aware action search, pinning, keyboard navigation, presets and recent results.
- A new native preferences window and visual system: approved peeled-p logo reconstructed as vector paths, white surfaces, #37F062 accent, mint cards, format tiles, rounded native controls and selected-action lighting. A compact translucent glass-style drag wheel shares pinned actions with the workspace. All main, preference, parameter, preview, progress, shelf and wheel-customization surfaces share the identity.
- A new job lifecycle and local journal: explicit queued/working/done/review/failed/cancelled/interrupted states, progress, cancellation, open/reveal, retry, persisted settings and privacy opt-out. Clearing history never deletes source or output files.
- Saved presets capture actual operation IDs and parameters; applying a preset uses a new file selection and publishes unique results.
- Separate app identity, executable, named-pipe/mutex namespace, settings, startup registration, shortcuts and Explorer integration. Updates come only from Peel's own GitHub releases (GuardaChuva22/peel).

## Reliability and conversion work added before the Peel interface

- Isolated conversion worker processes, a two-worker queue, timeouts, process-tree cancellation, shutdown cleanup and ownership-based partial-output manifests.
- Resume/unlock input recovery, input-aware hook health checks, modifier/chord fixes and malformed drag-descriptor handling.
- Native image preview editing and crop/redaction coordinates; local Windows face/text suggestions reviewed before saving.
- PDF-to-XLSX extraction, page order/duplication/rotation, Info/XMP metadata removal; extra media, metadata, redaction, visualization, collage and document page-rendering operations.
- Semantic tests of real output contents, native hook lifecycle checks, worker diagnostics and Peel workspace/preset/journal checks.

## Credits and boundaries

Original project: https://github.com/Metamorfolog/file_action

Product inspiration: https://tangerineformac.com/

Retain THIRD-PARTY-NOTICES.md. Standard independent engines include FFmpeg, Pandoc, LibreOffice, Magick.NET, PDFium, PDFsharp, PdfPig, ClosedXML and SharpCompress. FileAction is MIT-licensed by Metamorfolog; its copyright notice is kept in LICENSE. Peel is not affiliated with Tangerine or represented as an official FileAction release.

## 0.3.1 visual refinement

The wheel now has one genuinely translucent fill per region instead of an underlying filled disc and stacked near-opaque surfaces. Normal wedges retain approximately 48% of the background; the hub retains approximately 36%. Text is fully opaque with a narrow contrast halo. Patterned light/dark previews and an alpha export verify background transmission. This is alpha transparency, without OS blur or optical refraction.

The workspace uses compact full-width action rows, a quieter sidebar, smaller action buttons, fewer borders and a single source-preservation note. Empty and single-result states no longer use oversized nested cards. Conversion behavior and shortcuts are preserved.

## 0.4.0

- Glass wheel with live compositor blur, light and smoked variants following the Windows theme, a soft top sheen and a near-opaque selection.
- Peel's own glass notifications replace the Windows balloons: one card follows each job from progress to result, with Open / Show in folder.
- Copied YouTube, Shorts, Instagram, TikTok and X links offer an MP4 (H.264 + AAC, up to 1080p) or MP3 (320 kbps) download into Downloads, through yt-dlp.
- The drag wheel no longer opens for text selection, rubber bands or games; only for real drags of content Peel can read.
- Helper engines (FFmpeg, Pandoc, yt-dlp) are downloaded from their official releases on first start and checked against published checksums; yt-dlp keeps itself current.
- In-app updates from Peel's GitHub releases.

# Peel 0.3 visual contract

Peel is a quick native desktop file utility. Its identity is fresh, precise, quiet and fluid. The signature move is the green peeled p, followed by white glass for transient quick actions. Dense tools stay readable and calm.

- Accent #37F062; readable green text #147639; body #102018; secondary #64736A; canvas #F5F8F6; surfaces #FFFFFF; outlines #DEE7E1.
- Segoe UI Variable Text / Display, falling back to Segoe UI. Body 13–14, captions 11–12, section headings 20–24.
- Spacing follows 4px increments; principal interior spacing 16, 20, 24 and 28. Buttons 32px compact, 40px normal. Radii 9/11/15/20 with inner radii smaller than outer.
- White panels use fine green-gray edges and restrained shadows. Green glow is limited to selection/focus and the empty-state symbol. Floating wheel uses translucent layered gradients and white highlights, without continuous capture or expensive background animation.
- Keyboard actions, semantic native controls, selection/focus/loading/error/disabled/empty states stay visible. Windows animation preference controls wheel motion. No persistent glow animation. Bright green is never used for normal text on white.
- All conversion logic, settings, shortcuts, worker limits, file safety and drag behavior are retained.

The imagegen brandbook is visual direction. Native app screenshots and verification renders show the implemented UI.

0.3.1 refinement: workspace results use 54px rows instead of large two-column cards. Main canvas is white, sidebar pale neutral, actions are compact outlined native buttons, and bright green is reserved for selected/focused states and the empty-state primary action. The wheel uses one low-alpha fill per region; the outer rim has no filled disc behind the wedges.

0.3.2 glass: modern Frutiger Aero, not 2010 gloss. The wheel is one glass object lit from above: compositor blur, one milky body tint strong enough for text on any wallpaper (near-opaque when blur is unavailable), slices as slightly lighter panes, a soft curved sheen over the top half and on the hub, one rim bright at the top. No bevelled gradient strokes, double rims or text halos. Glass follows the Windows app theme (white glass + dark ink, smoked glass + white ink). Selection is near-opaque bright green with dark ink and a green glow, so a green wallpaper is never mistaken for it. The outer shadow is drawn only outside the disc, and the blur fades and grows in with the wheel.

Notifications (Shell/Toasts.cs) replace the Windows balloons. Glass cards of the same material (UI/Glass.cs), 360 wide, radius 18, stacked newest-at-bottom 12 px from the screen corner above the taskbar. One card follows a job: indeterminate glossy sweep, then progress with percent, then a drawn check with Open / Show in folder, or an error mark with Copy error. Cards slide in from the edge with a slight settle, older cards glide up, they leave by sliding out; hover pauses dismissal, a swipe right dismisses, a click runs the primary action. They never take focus and stay hidden over full-screen apps and presentations. Each card has its own rounded compositor blur that follows it frame by frame only while something moves.

Copied video links (Shell/LinkWatcher.cs, Jobs/VideoDownloader.cs): Windows' clipboard notice (no polling) checks whether the copied text is exactly one YouTube, Shorts, Instagram, TikTok or X video link; nothing leaves the PC until a choice is made. The card offers MP4 (H.264 + AAC, up to 1080p, editor-friendly) or MP3 (320 kbps with title/artist), fills in title, author, length and thumbnail as yt-dlp reports them, then becomes the download's progress card and saves to Downloads under the video title. Partial files live in Peel's own temp folder. The same link copied again within three minutes is ignored; the tray menu turns the feature off.

The drag wheel opens at once only when the shell's drag image proves a drag; otherwise (text selection, rubber band, games) it waits invisibly and appears only when content Peel can read reaches it.

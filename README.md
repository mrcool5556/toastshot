# Toastshot

**A fast, free screenshot tool for Windows.** Press **Print Screen**, drag a box, and it stays on screen with tools beside
it. Mark it up, copy it, save it, read its text, translate it, or record the area as a GIF or video. Everything happens on
your PC.

Toastshot is free and open source. If it's useful to you, you can support it (see [Support](#support)).


## What it does

- **Capture in place.** The selection stays on screen: move it, resize it, draw on it, then **Copy** (`Ctrl+C`), **Save**,
  **Print**, or open the full editor. Hold `Ctrl` while dragging to copy straight away.
- **Draw:** pen, marker, line, arrow, box, oval, text and redact, with 8 colours, sizes and undo.
- **Read text** with the OCR built into Windows: instant and offline.
- **Translate** and read handwriting with optional local AI (see [Local AI](#local-ai-optional)). It runs on your PC; nothing
  is uploaded.
- **Search Google Lens** for similar images (this one does upload the capture, to Google, when you click it).
- **Record a GIF** of any area (`Shift+Print Screen`). Recordings are shrunk to fit 20 MB, or save them as **MP4** at full
  size and frame rate.
- **Works in games.** Print Screen reaches Toastshot even in games that block other apps' hotkeys.
- Dark and light themes that follow Windows, **Start with Windows**, and updates from inside the app.

## Download and install

1. Download **Toastshot.zip** from the [latest release](../../releases/latest) and unzip it.
2. Double-click **Install Toastshot.cmd**. No admin rights needed, and you don't need anything else installed.

Toastshot goes to `%LOCALAPPDATA%\Programs\Toastshot`, with a Start menu shortcut. Uninstall it from *Settings › Apps*. When a
new version is out, Toastshot shows **See what's new**, listing every change since your version, with a one-click update.

> Windows may say "Windows protected your PC" for a new app it doesn't know yet. Choose **More info › Run anyway**.

## Hotkeys

| Key | Does |
|---|---|
| `Print Screen` | Capture a region (the screen freezes so menus and tooltips stay put) |
| `Shift+Print Screen` | Choose a GIF area; press again to start recording, and again to stop |
| `Esc` / right-click | Cancel |

Change them in **Settings**. Print Screen only reaches Toastshot if no other app holds it: quit Lightshot, ShareX and similar
tools, and on Windows 11 turn off *Use the Print screen key to open screen capture* (search Windows Settings for "Print
screen"). The main window says when a key is taken.

### After you select a region

- Drag inside the selection to **move** it, drag a corner or edge to **resize** it, or drag outside it for a new one.
- Pick a tool to draw: `P` pen · `M` marker · `L` line · `A` arrow · `B` box · `O` oval · `T` text · `R` redact. Pick it again
  (or press its key again) to go back to moving. The colour dot under the tools opens colours and size.
- `Ctrl+C` copies and closes at any time (before choosing, it copies the screen under the pointer); double-clicking the
  selection does too.
- `Ctrl+S` save · `Ctrl+P` print · `Ctrl+G` Google Lens · `Ctrl+E` read text · `Ctrl+T` translate · `Ctrl+Z` undo · `Esc` close.

### In the editor

The same tool keys, plus `Shift` while dragging for a square, circle or 45° line. `Ctrl+C` copies the image (unless you've
selected text in a box), and `Ctrl+Y` redoes.

## Local AI (optional)

Translate and the AI reader (reads handwriting and stylised text) use [Ollama](https://ollama.com) with one recommended model,
**Gemma 3 4B**: a 3.4 GB download that uses about 4 GB of graphics memory while it works.

- **Set up** from *Settings › Local AI*. Toastshot checks your graphics card, says whether the model fits, then installs Ollama
  (checking it's signed by Ollama; no admin) and downloads the model.
- **Graphics memory** is freed 2 minutes after the last use (or 30 seconds, 5 minutes, or never). *Unload now* frees it at once.
- **Off switch:** turn off *Use local AI* and Translate and the AI reader disappear; Toastshot never contacts Ollama.
- **Advanced:** any Ollama models, or another server.

Windows OCR reads only languages installed in Windows. To read Japanese, Korean, Chinese and so on, add the language in
*Settings › Time & language › Language & region*.

## GIFs and video

GIFs record at 15 fps for up to 60 seconds (both adjustable). New recordings are shrunk to fit 20 MB (*Settings › Keep GIFs
under*): frames are dropped down to 12 fps first, then the picture is scaled, keeping as much resolution as the size allows.
The size menu in the GIF window switches between the original, other limits and 75/50/25% scale. **Save MP4** (`Ctrl+Shift+S`)
uses Windows' built-in video encoder: full size and frame rate, usually a sixth of a 20 MB GIF or less.

## Privacy

Toastshot keeps your captures on your PC. It only goes online when you ask it to (Google Lens, setting up local AI) and to check
GitHub for updates about once a day (*Settings › About* turns that off).

- **Settings:** `%LOCALAPPDATA%\Toastshot\settings.json`
- **Log** (hotkeys, captures, problems; no images or text): `%LOCALAPPDATA%\Toastshot\toastshot.log`
- **GIF recordings** wait in `%TEMP%\Toastshot` until you save them; screenshots live only in memory until you copy or save them.

## Support

Toastshot is free and always will be. If it saves you time, you can support it with the **Sponsor** button on this page or
*Settings › About › Support*. Thank you!

## Build from source

Needs the .NET 8 SDK on Windows 10 (2004) or later.

```
dotnet build Toastshot.sln
dotnet test Toastshot.sln
powershell -ExecutionPolicy Bypass -File installer\build.ps1   # the installer package, in dist\
```

**Start Toastshot.cmd** builds and runs it. To publish a release, add the version's notes to `CHANGELOG.md`, then push a tag:
`git tag v0.2.0` and `git push origin v0.2.0`. GitHub Actions tests it, builds `Toastshot.zip` and publishes the release that
the in-app updater reads.

- **Tests** cover the GIF encoder (round-tripped through Windows' decoder), the shrinker and MP4 export, recording and
  playback, screen capture, drawing and undo, the capture overlay, hotkeys and settings, the clipboard, the Ollama client,
  the updater and Windows OCR. Tests that need a real desktop are tagged `Needs=Desktop` and skipped in CI.
- **Visual snapshots:** `VisualSnapshotTests` renders every window off-screen. Opt in with `TOASTSHOT_SNAPSHOT_DIR` (output)
  and `TOASTSHOT_SAMPLE_DIR` (a folder with `sample.png` and `sample.gif`); `TOASTSHOT_SNAPSHOT_THEME=light` renders the light theme.
- **Layout:** `src/Toastshot/Capture` (screen grabbing), `Overlay` (the capture overlay), `Editor` (drawing), `Gif` (encoder,
  recorder, player, shrinker, MP4), `Services` (OCR, Ollama and local AI, settings, hotkeys, updates, export), `Themes`.

## License

Toastshot is free software under the [GNU General Public License v3.0](LICENSE): you can use, study, share and change it, and
versions you share must stay free under the same licence.

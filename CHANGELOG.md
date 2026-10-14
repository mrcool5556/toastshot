# Changelog

Each version's section becomes its GitHub release notes, which Toastshot shows in its update window.
Write for people using Toastshot: what they can do now, what got fixed.

## 0.4.0

- **One installer:** download **ToastshotSetup.exe** and run it. There's no zip to unzip and no install script, which browsers and antivirus were wary of.
- Uninstall from Settings › Apps as before; your settings are kept.
- Updating from 0.3.x: use **Download** in Settings to get the setup once. Updates after that install themselves again.

## 0.3.2

### Fixed
- Clicking **Settings** again brings the open Settings window forward instead of opening another one.
- With more than one Settings window open, closing one turned the hotkeys back on while the other was still open.

## 0.3.1

- **Updating moved into Settings:** when a new version is out, Check now becomes **Update**, and what's new in every version since yours shows right below it.
- A small dot next to **Settings** in the main window means an update is ready. The banner above the Capture buttons is gone.

## 0.3.0

### New
- **Dark or light:** Settings › General › Theme lets you pick Dark, Light or follow Windows. It changes as you pick, so you can see it before saving.
- **Frame rate and length on the recording bar:** choose them right before you press Record. Toastshot remembers your choice for next time.
- **Settings in tabs:** General, GIF & video, Hotkeys, and Text & AI, instead of one long page.

### Changed
- AI vision is now called the **AI reader**, since all it does is read text in a screenshot.
- If Settings can't save a value, it takes you to that field, even on another tab.

## 0.2.0

The first public version.

### Capture
- Press **Print Screen** and drag a box. It stays on screen with tools beside it: move or resize it, draw on it, then copy, save, print, search Google Lens, read its text or translate it.
- **Ctrl+C** copies at any time; hold **Ctrl** while dragging to copy straight away.
- Works inside games that block normal hotkeys.

### Drawing
- Pen, marker, line, arrow, box, oval, text and redact, with 8 colours, sizes and undo.

### GIFs and video
- **Shift+Print Screen** records a GIF of an area.
- New recordings are shrunk to fit 20 MB (adjustable), or pick another size or scale.
- **Save MP4** saves the recording as video: full size and frame rate, far smaller than a GIF.

### Text and translation
- Windows OCR reads text instantly and offline.
- Optional local AI (Ollama with Gemma 3 4B) translates and reads handwriting, on your PC. Set it up from Settings; it frees graphics memory when idle.

### Everything else
- Dark and light themes that follow Windows.
- Start with Windows, an installer that needs no admin rights, and updates from inside the app.

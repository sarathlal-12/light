# .light

A small black island floating at the top centre of your screen, in the spirit of the iPhone's
Dynamic Island. It morphs on springs between a tiny pill, short live moments, and a panel of **knobs**.
Touch the top edge with your cursor, hover the island, or press **Alt+Space** to open it; Esc or moving away closes it.
All shortcuts (open, grab text, grab image) can be changed in **Look** (the gear) > Shortcuts: click one and press the new keys.
Don't need something? **Look > Features** has an on/off switch for each feature.

## Light colour

In the settings knob (gear), pick the colour .light glows in: **iOS** (green music, blue clipboard, orange convert),
White, Amber, Ice, Mint, Magenta, Violet, Coral, **Album art** (the playing song's main colour) or **Windows accent**.
It changes the waveform, dial needle, progress bars, selected tab and highlights, with a soft fade.

## Island style

In Look: **Capsule** (black pill with the time), **Thin line** (a glowing hairline), **Outline** (black with a lit rim)
or **Dot** (a tiny glowing dot). "Show the time" puts a clock in the resting island; a pinned note replaces it.
"Breathing light" makes the resting island glow slowly in and out.

## Rim light

A thin line of light on the island's edge, brightest on the side facing into the screen. While music plays it breathes
with the real sound level coming out of your PC; it flashes on Copied and dial turns, and goes dark when idle.

## Fullscreen

When a game, a fullscreen video or a presentation fills the main screen, the island fades away, stops taking clicks
and stays quiet (no moments). It comes back when you leave fullscreen; Alt+Space still opens it meanwhile.
Normally maximized windows don't count.

## Position

Eight spots: top left / centre / right, left middle, right middle, and bottom left / centre / right
(bottom spots sit just above the taskbar and grow upwards). Pick one from the tray icon's **Position** menu,
the settings knob (gear) in the panel, or drag the open island by its top bar and it snaps to the nearest spot.

## Live moments

- **Music playing**: the island widens: album cover on the left, green waveform on the right.
- **New song**: briefly shows the title, artist and cover, then shrinks back.
- **Copied**: a quick blue check whenever you copy something.
- **Claude Code**: when a Claude Code session needs your permission the island shows "Needs your OK" with the project
  name and pulses until you look; click it to bring that terminal forward. When Claude finishes you get a short
  "Done" (skipped if you're already looking at that window) with a soft pop. Turn it on in Look > Features > Claude Code status:
  .light adds a small hook to `~/.claude/settings.json` (keeping a `.light-backup` copy) and removes it when you turn it off.
- **Screenshot**: take a screenshot (or copy any picture) and a thumbnail shows for a few seconds. Drag it straight
  into any app or folder.
- **Phone**: notifications from your Android or iPhone (through Phone Link), with a **Copy** button for login codes,
  incoming calls, and "connected · 82%" when the phone connects. See [Phone](#phone).
- **Camera and mic**: a green dot while an app uses the camera, orange while one records from the microphone.
- **Charging**, **download finished** (drag the file off the island) and **new version** moments.
- **Focus timer**: while a timer runs the island shows the time left, with a light that drains away. At zero it rings
  (soft chime and pulsing light) until you click **Done**.

## Dials

Scroll the mouse wheel over the island and it turns like a knob:
- **Wheel** = PC volume, **Shift + wheel** = seek through the song (when the player allows it). On the closed island it becomes a tick-ruler dial; on the open music knob a small dial floats at the bottom.
- Over the **Clipboard** knob the wheel flips through your clips; **Enter** pastes the highlighted one into the app you were using.

## Knobs

- **Music** (green): whatever is playing on your PC (Spotify, YouTube in a browser, any app in
  Windows' media popup): cover, title, artist, progress bar, play/pause, next, previous.
- **Claude** (orange): every Claude Code session and what it's doing now: Working (with a timer), Needs your OK, or Done.
  Click one to bring its window forward.
- **Phone** (green): your phone's battery and its recent notifications (kept in memory only), plus the iPhone clipboard setup.
- **Clipboard** (blue): the last 50 things you copied (text and images). Search, pin, click to copy, or drag a clip
  into any app. Skips anything password managers mark as private; the pause button stops saving new copies.
- **Timer** (orange): a kitchen-timer dial. Scroll over it to set minutes (Shift = 5 at a time) or pick 5/15/25/45, then Start.
- **Notes** (green): quick notes saved as you type; pin one and it shows in the resting island.
- **Snippets** (blue): prompts you use again and again. Click one and it's pasted into the app you were using
  (the first line is its name; the rest is what gets pasted). Comes with five starter prompts for AI coding tools.
- **Shelf**: drop files on the island to hold them (only a link is kept), then drag them out into any app or folder
  later. Dragging files over the island shows two halves: **Shelf** and **File lab**.
- **File lab** (orange): convert images to PNG, JPG, ICO, BMP, GIF or TIFF. **Same** keeps the format to just resize or
  compress; **PDF** puts all the images into one PDF; "Remove location and camera info" cleans photos before sharing
  (on by default). Opens WebP/HEIC/AVIF too. Drag images onto the island.
- **Look** (gear): light colour, island style, clock, breathing light, features on/off, shortcuts, position, start with Windows.

## Phone

.light shows your phone's notifications through **Phone Link**, which comes with Windows. Nothing to install on the
phone for iPhone; Android uses the **Link to Windows** app (already on many Android phones).

1. Open **Phone Link** on your PC and pair your phone.
2. In Phone Link's settings, turn on **notifications**, and leave Phone Link running.
3. New notifications show on the island and in the **Phone** tab. A login code gets a **Copy** button.

Look › Features has switches for phone notifications, showing the message text (off by default: only the app and
sender show) and a pop sound. .light only reads notifications Windows already received; it can't reply or dismiss.

**Copy on your phone, paste on your PC**
- **Android**: turn on **Cross-device copy and paste** in Phone Link (or use the Microsoft SwiftKey keyboard with
  clipboard sync). What you copy on the phone lands in the PC's clipboard and in .light's history automatically.
- **iPhone**: iPhones don't share their clipboard in the background, so it's one tap with an Apple Shortcut:
  1. In .light open the **Phone** tab, click the clipboard button, and turn on **iPhone clipboard**. Windows asks
     whether .light may use the network: allow **Private networks**.
  2. On the iPhone, add the **[Send to .light](https://www.icloud.com/shortcuts/43c3ab80086b4feaae0ea014122bb30d)** Shortcut. When you add it, it asks for the **Address**,
     then the **Code**: type the ones .light shows on that page.
  3. Copy something and run the Shortcut. Put it where it's one tap away: **Control Center** (Add a Control ›
     Shortcut), the **Action Button**, **Back Tap** (Settings › Accessibility › Touch) or the share sheet.
  Phone and PC must be on the same Wi-Fi. .light only accepts text from your own network with the right code.

## Grab text

Press **Ctrl+Alt+T** (or the scan button in the Clipboard knob, or the tray menu), drag a box over any text on screen and
it's copied. It uses the text recognition built into Windows, on your PC; nothing is uploaded.

## Grab image

Press **Ctrl+Alt+S** (or the crop button in the Clipboard knob, or the tray menu), drag a box, and that part of the
screen is copied as a picture, ready to paste into a chat or a bug report.

## Download

Get **light-win-Setup.exe** from the [latest release](https://github.com/sarathlal-12/light/releases/latest) and run it.
It installs for your user only (no admin) and includes everything it needs. Windows 10 (19041) or 11, 64-bit.
The installer isn't code-signed yet, so SmartScreen may say "Windows protected your PC": click **More info > Run anyway**.

## Build from source

Needs the .NET 10 SDK.

```bash
dotnet run -c Release
```

Or build once and start `bin/Release/net10.0-windows10.0.19041.0/light.exe`.
To make the installer: `./build-installer.ps1 -Version 0.3.0` (needs `dotnet tool install -g vpk`).
Right-click the tray icon for "Start with Windows" and Quit.

Data lives in `%APPDATA%\.light` (settings, clipboard history, notes, snippets, shelf, error log).

## License

MIT, see [LICENSE](LICENSE).

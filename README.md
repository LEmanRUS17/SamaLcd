# SAMA LCD

**English** | [简体中文](README.zh-CN.md) | [日本語](README.ja.md) | [Русский](README.ru.md)

An open-source C# / .NET 8 replacement for **SAMA WaterCooling** (`Sama.exe`), designed for the round 2.1-inch, 480×480 display on a SAMA liquid-cooling pump.

## Getting started

Requirements: Windows, .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime. Some hardware sensors also require administrator access and the separately installed [PawnIO driver](https://pawnio.eu).

1. Fully exit the original SAMA software and disable its autostart. Only one application can open the display's COM port at a time.
2. Build and publish the project as described below, then run `publish/SamaLcd.exe`. It requests administrator access for hardware sensors.
3. **Image** manages backgrounds, crop, rotation and playback; **Data** selects themes and custom layouts; **Sensors** lists readings and keys; **App** contains language, startup, storage and diagnostics.

Closing the window minimizes the app to the tray. Use the tray menu to exit completely. Autostart runs in the tray without opening the settings window.

Settings: `%APPDATA%/SamaLcd/settings.json`. Custom layout: `%APPDATA%/SamaLcd/custom-layout.json`.

## Languages

The default interface language is **English**, including existing settings without a language field. **App → Language** switches immediately between English, Simplified Chinese, Japanese and Russian. The saved `language` codes are `en`, `zh-Hans`, `ja` and `ru`.

Settings, editor labels, sensor aliases, tray menus, dialogs and status messages are translated. Custom captions, file names, sensor IDs and theme contents retain their original text.

## Backgrounds and applying changes

- **Gallery** reads images, GIFs and videos from the `backgrounds` folder beside the app. Add files and choose **Refresh**. The first tile restores the theme's own background.
- **Color** provides a palette and custom color picker.
- **From file** accepts images, animated GIFs and videos, including MP4, MKV, WEBM, MOV and AVI. Scaling options are **Fill**, **Fit** and **Stretch**.

File/gallery selection, color, scaling, crop, rotation and device-playback choices update a local draft preview. **Not applied** indicates pending changes. The display keeps its last applied settings; editing does not convert or upload a new video.

Choose **Apply to display** to save and send the image configuration together, resuming playback if paused. **Discard changes** restores the applied configuration. Unchanged settings cannot be applied again. Language and brightness changes do not save image drafts; unapplied edits are discarded when the app exits completely. Brightness and pause remain immediate controls.

Long file names use up to two lines with an ellipsis; hover to see the full name. **Crop / zoom…** is aligned beside the scaling buttons.

### Square crop and video preview

Choose a file or gallery item, then **Crop / zoom…**. Drag to reposition and use the zoom slider or mouse wheel to remove black borders. The crop is square; the circular mask shows the physical display's visible area.

For videos, move the timeline to choose a visible frame, including when the opening frame is black. FFmpeg extracts preview frames even for formats the browser cannot decode. Seeking preserves crop position and zoom. **Use crop** updates the draft; **Cancel** changes nothing; **Remove crop** restores scaling. Finish with **Apply to display**. The selection applies to the entire video and never overwrites the original.

Still images and streamed GIFs use the selected source rectangle. Videos and device-played GIFs apply the same crop through FFmpeg before resizing and rotation. Crop changes invalidate converted-video caches.

### FFmpeg and playback

At startup the app checks for `ffmpeg.exe` beside the executable, on PATH, in its tools cache and in known SAMA installation folders. If absent, it downloads the essentials ZIP and SHA-256 from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), linked by [ffmpeg.org](https://ffmpeg.org/download.html), verifies the checksum and installs the executable and license. If the app folder is not writable, it uses `%LOCALAPPDATA%/SamaLcd/tools`. **App → Check FFmpeg** retries setup. Still images remain usable if setup fails.

**Play videos and GIFs on the display** is enabled by default. After applying, files are converted to 480×480, 24 fps H.264/yuv420p without audio, uploaded and looped locally. Theme widgets travel as a transparent overlay. Cache: `%LOCALAPPDATA%/SamaLcd/video`; uploaded names: `samalcd_*.mp4`.

With device playback disabled, or when it fails and the app falls back to streaming, frames travel over USB. Measured throughput on the tested device is about 2.7 MB/s, or roughly 4 fps when the entire frame changes. Local playback is smoother.

## Capacity and SD files

Capacity is read before conversion and checked again against the actual converted file size. Encoding first targets existing free space. If that cannot provide a usable bitrate, it targets total capacity and asks about cleanup after conversion. Two-pass size control uses a 512 KiB reserve, a 10% overhead margin and a maximum target bitrate of 2400 kbit/s. Videos are never shortened to make them fit.

Enough space permits upload. Otherwise, the app lists SD video names, sizes and reclaimable space for confirmation. Only the exact unchanged files approved by the user may be deleted. Cancel/Escape keeps files and pauses the operation. Changing the source or exiting invalidates a pending request. CLI sessions without a confirmation handler cannot delete files automatically.

**App → SD card files** manages `/mnt/SDCARD/video/`. Opening the tab or selecting **Refresh** reads names, sizes, total file usage and capacity. Each row's **Delete** confirms one file; **Clear videos…** confirms the displayed list. Deletion pauses playback to prevent automatic re-upload, then refreshes the list and free space. Changed lists or reconnected devices invalidate old confirmations. Other SD directories and internal storage are never cleared or formatted.

## Sensors and custom layouts

Unavailable readings appear as `--`. `gpu.*` aliases prefer the discrete GPU; `ram.*` describes physical memory. CPU temperature, clock and power, plus some motherboard, fan and pump readings, need PawnIO. The app shows a hint when CPU readings are unavailable.

**Data → Custom layout** provides a 4×4 to 16×16 grid. Drag items to move them and the bottom-right handle to resize. Arrow keys move the selected item; Delete removes it. Items include sensor values, bars, text, time and date, with colors, sizes, alignment and optional captions. Layout edits apply immediately.

## Themes

Themes live in `themes/<name>/theme.json` beside the app. Bundled themes: `Dashboard`, `Clock`, `Animation`, `Orbit`, `Gauges`, `Minimal`, `Neon`, `Terminal`. Theme folders can contain images and `.ttf`/`.otf` fonts.

```json
{
  "name": "My theme",
  "intervalMs": 1000,
  "background": {
    "color": "#000000",
    "image": "bg.gif",
    "fit": "cover"
  },
  "widgets": [
    {
      "type": "text",
      "x": 240,
      "y": 200,
      "text": "{cpu.temp:0}°",
      "size": 96,
      "bold": true,
      "color": "#FFFFFF"
    },
    {
      "type": "arc",
      "x": 240,
      "y": 240,
      "radius": 218,
      "thickness": 18,
      "value": "cpu.load",
      "startAngle": 135,
      "sweepAngle": 270,
      "color": "#00D1FF",
      "colorEnd": "#FF3B5C"
    },
    {
      "type": "bar",
      "x": 140,
      "y": 300,
      "width": 200,
      "height": 10,
      "value": "gpu.load",
      "max": 100
    },
    {
      "type": "image",
      "x": 0,
      "y": 0,
      "path": "logo.png",
      "width": 100,
      "height": 100
    },
    {
      "type": "circle",
      "x": 240,
      "y": 400,
      "radius": 58,
      "color": "#99000000"
    }
  ]
}
```

Text substitutions use `{key:format}`, such as `{gpu.temp:0.0}`, `{time:HH:mm:ss}` and `{date:dd.MM}`. Common keys: `cpu.temp`, `cpu.load`, `cpu.clock`, `cpu.power`, `gpu.temp`, `gpu.hotspot`, `gpu.load`, `gpu.clock`, `gpu.power`, `gpu.mem.load`, `gpu.fan`, `ram.load`, `ram.used`, `ram.free`, `board.temp`, `pump.rpm`, `fan.rpm`. LibreHardwareMonitor IDs are also supported; find them in **Sensors** or `samalcd-cli sensors`.

## Diagnostic logs

**App → Diagnostics** provides a persistent **Debug logs** switch, **Open log folder** and **Export diagnostic logs**. Basic operations and errors are always recorded. Debug adds interface interactions, crop gestures, FFmpeg arguments, USB text commands/replies and memory/transfer counters. Debug defaults to off and does not apply pending image edits.

Logs live in `%LOCALAPPDATA%/SamaLcd/logs`, with UTC timestamps, session/operation IDs, thread IDs and exception stacks. Every record is flushed; files rotate at 2 MiB with up to 10 retained files. Export creates a ZIP containing logs, a runtime/device/settings snapshot and a bounded read-only query of recent SAMA LCD Windows crash events. Media files, raw frames and entire user layouts are excluded; nothing is uploaded automatically.

For a recurring issue, enable Debug, reproduce the operations, restart if needed, then export the ZIP. An incomplete session is marked on the next start. Native failures can bypass managed exception handlers; Windows events provide additional evidence. WebView2 dialogs are queued after callbacks return, following [Microsoft's threading guidance](https://learn.microsoft.com/microsoft-edge/webview2/concepts/threading-model#reentrancy). Decoder-reader exceptions are caught and logged.

## Build and development

```powershell
dotnet build
dotnet test
.\publish.ps1
```

Publishing creates the tray app in `publish/` and the CLI in `publish/cli/`. Keep executables with their dependencies and resource folders.

- [SamaLcd.Core](src/SamaLcd.Core): device protocol, rendering, sensors, display loop and diagnostics.
- [SamaLcd](src/SamaLcd): Windows tray app and local WebView2 UI using bundled daisyUI/Tailwind assets.
- [SamaLcd.Cli](src/SamaLcd.Cli): command-line utilities.
- [Tests](tests/SamaLcd.Tests): protocol references, rendering, localization, storage consent and diagnostics.

Open `src/SamaLcd/wwwroot/index.html` in a normal browser to preview with mock data. Add widget types in `Rendering/Theme.cs` and drawing logic in `ThemeRenderer`.

Translations shared by native and web UI live in `src/SamaLcd.Core/Localization/*.json`. After catalog edits, run `node scripts/generate-locales.mjs`, then `dotnet test`. The committed browser bundle supports standalone preview. Keep all four README versions synchronized when documenting behavior changes.

## Command-line utilities

Use `publish/cli/samalcd-cli.exe` with:

| Command | Purpose |
|---|---|
| `ports`, `info`, `sensors` | List ports, identify the display, inspect sensors |
| `brightness 50`, `color "#FF0000"`, `image pic.png` | Set brightness, color or an image |
| `theme <folder> [seconds] [--bg <color-or-file>]` | Run a theme |
| `render <folder> out.png` | Render without accessing the device |
| `bench [frames]` | Measure transfer speed |
| `ls [folder]` | List files and storage |
| `upload <file> <remote-path>`, `play <remote-path>` | Upload or play a device file |
| `fit-video <source> <budget-bytes> <output>` | Encode within a byte budget |
| `raw <code> [text]` | Send an experimental command |

Use `--port COM3` to select a port. Exit other display software before accessing the device.

## Device protocol

The tested display exposes `VID_1D6B&PID_0121` in working mode and `VID_1A86&PID_CA21` / `CT21INCH` in boot mode. Opening the boot-mode port wakes it. A tested identity is `chs_5inch.dev1_rom1.88`. The protocol was reverse-engineered from `Sama.exe` and is related to Turing Smart Screen rev C; compatibility depends on hardware and firmware.

| Command | Code | Payload / reply |
|---|---|---|
| Hello | 1 | `C5 D3` → `chs_…rom<version>` |
| Storage | 100 | Internal total/used/free, SD total/used/free, in KiB |
| List directory | 101 | Path → `result:dir:…file:a.mp4/b.mp4/` |
| Delete file | 102 | File path |
| File size | 110 | Path → bytes; `0` if missing |
| Create/upload file | 111 | Path + size LE32 → `create_success`, bytes, `file_rev_done` |
| Play video | 120 | Path, flag 1 → `play_video_success` |
| Stop video / media | 121 / 150 | No text payload |
| Brightness | 123 | One byte; the original app scales 0–255 by 0.76 |
| Full frame | 200 | W×H×4 bytes BGRA |
| Overlay | 202 + 208 | BGRA + mask `[index:24][length:16]…EF 69` |
| Differential frame | 204 | Sequence BE32, optional mask length BE32, segments, `EF 69` |
| Status | 207 | `needReSend:X\|renderCnt:N` |

Commands use 250-byte packets: `[code][EF 69][length BE32][flag][0 0][payload at offset 10]`. Data packets carry 249 useful bytes each. Stop playback before uploading. The tested device has about 2.6 MiB internal storage; SD capacity varies. See [FrameCodec.cs](src/SamaLcd.Core/Device/FrameCodec.cs) for pixel/segment encoding and the tests for byte-level comparisons with original SAMA output.

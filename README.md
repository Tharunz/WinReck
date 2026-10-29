# WinReck

> The Windows audio recorder you have been searching for.

<img width="1414" height="762" alt="screenshot" src="https://github.com/user-attachments/assets/a225c8dc-69da-4d60-b184-b2dd830f5cfb" />

A native WPF audio recorder for Windows — clean dark UI, live waveform, seekable playback, and a no-nonsense recordings library.

---

## Installation

[![Download WinReck](https://a.fsdn.com/con/app/sf-download-button)](https://sourceforge.net/projects/winreck/files/latest/download)

Download the latest self-contained `.exe` from [SourceForge](https://sourceforge.net/projects/winreck/files/latest/download) or [GitHub Releases](../../releases) — no .NET install required.

---

## Features

- 🎙️ Record from any input device with live waveform visualization
- 🔊 Playback with seekable waveform and playhead
- 📁 Recordings library with inline rename, delete, and batch selection
- 💾 Saves as 48kHz 16-bit stereo WAV to `~/Music/WinReck Recordings/`
- 🎨 Dark theme, zero OS dialogs

---

## Build from Source

**Requirements:** Windows 10/11 · [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

```bash
git clone https://github.com/Tharunz/WinReck.git
cd WinReck/WinReckApp
dotnet build
dotnet run
```

### Publish self-contained EXE

```bash
cd WinReckApp
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../release
```

The output `release/WinReckApp.exe` is fully portable — no runtime needed.

---

## License

MIT

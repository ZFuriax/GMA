# Game Master Audio (GMA)

Lightweight, high-performance Windows music player and scene engine built for tabletop RPG game masters.

**Version:** 1.10 
**Platform:** Windows  
**Author:** Z  

---

## Download

Download the latest version from the [Releases](../../releases) page.

- `GMA-v1.09-win-x64.zip`

---

## Overview

Game Master Audio (GMA) is designed to provide smooth playback, fast control, and immersive audio layering without unnecessary complexity.

It includes everything needed to run a session:

- Dynamic wave normalization for consistent volume
- Seamless crossfading between tracks
- Looping for repeating audio segments
- Multiple playlists
- Scene Mode for layered music and ambience

---

## What's New in 1.10

- Scene Mode: play up to three ambience tracks alongside a music track with independent volume control
- Improved playback pipeline for better performance under load
- Rebuilt voice recognition system for improved accuracy
- More robust Bluetooth handling
- Simplified repeat behavior (Repeat One toggle)
- Voice phrase tools:
  - Record key phrases via microphone
  - Test phrase recognition directly
  - Append phrases to existing lists

---

## Key Features

### 🎵 Audio Playback
- Smooth playback engine
- Optional crossfade between tracks
- Dynamic wave normalization
- Low-latency playback
- Broad format support via FFmpeg

### 📂 Playlist Management
- Drag-and-drop folders
- Automatic file scanning
- Sortable columns
- Persistent playlists

### 🎚 Scene Mode
- 1 music track + up to 3 ambience tracks
- Per-track volume sliders
- Designed for immersive layered audio

### 🎤 Voice Activation
- Trigger playlists using spoken phrases
- Modes:
  - Off
  - Always On
  - Ctrl-Activated
- Supports 1–3 phrases per playlist

**Example:**
- “You enter the tavern” → Tavern ambience  
- “Roll for initiative” → Battle music  
- “You make camp” → Campfire ambience  

### 📊 Visualization
- Real-time audio spectrum visualizer
- Log-scaled frequency bands
- Adjustable gain

### 📈 Waveform Navigation
- Interactive waveform scrubbing
- Click-to-seek playback

### 🎛 Playback Controls
- Shuffle
- Repeat One
- Loop A–B
- Volume popup slider

---

## Quick Start

1. Download the latest release
2. Extract the ZIP file
3. Run `GMA.exe`
4. Drag audio files or folders into a playlist

---

## Requirements

- Windows 10 or later
- .NET Runtime
- FFmpeg binaries (included or installed separately)

---

## Supported Audio Formats

Provided via FFmpeg (depends on build):

- MP3
- WAV
- FLAC
- OGG
- AAC
- M4A

---

## Third-Party Software

### FFmpeg / FFprobe

GMA uses FFmpeg for audio decoding and waveform analysis.

- Website: https://ffmpeg.org/
- Source: https://github.com/FFmpeg/FFmpeg
- License: LGPL or GPL (depending on build)

If you distribute FFmpeg with GMA, you must comply with its license.

---

## License

See the [LICENSE](LICENSE) file for details.

This project uses FFmpeg, which is licensed separately under LGPL/GPL.

# 📦 ZeroSync — Enterprise Cloud & File Synchronization Manager

<p align="center">
  <a href="https://github.com/kzxl/ZeroSync"><img src="https://img.shields.io/badge/Type-Desktop%20Application-007ACC?style=flat-square&logo=windows" alt="Type: Desktop App" /></a>
  <a href="https://github.com/kzxl/ZeroSync"><img src="https://img.shields.io/badge/Ecosystem-Zero%20Universe-8A2BE2?style=flat-square" alt="Ecosystem: Zero Universe" /></a>
  <a href="https://github.com/kzxl/ZeroSync"><img src="https://img.shields.io/badge/Platform-Windows%20x64-brightgreen?style=flat-square" alt="Platform: Windows x64" /></a>
  <a href="https://github.com/kzxl/ZeroSync"><img src="https://img.shields.io/badge/Distribution-Standalone%20Single--File-2ea44f?style=flat-square" alt="Distribution: Standalone Single-File" /></a>
</p>

<p align="center">
  <strong>Modern desktop management suite for Rclone cloud backups, mirroring, and automated folder synchronization</strong><br/>
  WPF Dark UI (MVVM) • Multi-Profile Orchestration • Real-Time Watch Mode • Zero-Config Logging
</p>

---


## 📖 Overview

**ZeroSync** is a desktop file synchronization and backup orchestrator designed for workstations and servers. Acting as a frontend for the industry-standard **Rclone** engine, ZeroSync eliminates CLI complexity while providing full control over multi-cloud synchronization (Google Drive, OneDrive, Amazon S3, WebDAV, SFTP, and local/network storage).

Part of the sovereign **ZeroUniverse** application suite, ZeroSync ensures deterministic, reliable, and observable data movement across heterogeneous storage tiers.

| Component | Tech Stack | Role |
| :--- | :--- | :--- |
| **ZeroSync UI** | C# WPF (.NET Framework 4.6.2) | Dark theme MVVM interface, profile selector, realtime log viewer |
| **Engine** | Rclone (`rclone.exe`) | High-speed multi-threaded cloud transport, chunked transfers, deduplication |
| **Watcher** | `FileSystemWatcher` + Async Debounce | Realtime file modification detection and automated sync trigger |

---

## ✨ Key Features

| Feature | Description |
| :--- | :--- |
| 🎨 **Dark Theme UI** | Modern Fluent-inspired dark navy desktop interface with rounded cards |
| 👤 **Multi-Profile Management** | Create, switch, and maintain isolated synchronization profiles |
| 🔄 **3 Sync Operation Modes** | Direct support for `Copy` (add new/updated), `Sync` (exact mirror), and `Move` (transfer and delete source) |
| ⚙ **Granular Rclone Controls** | Tune concurrent transfers, checksum checkers, bandwidth limits (`--bwlimit`), log verbosity, and custom flags |
| 👁 **Automated Watch Mode** | Background file watcher with debounce timer to automatically push newly generated files |
| 📝 **Real-Time Log Streamer** | In-app live stdout/stderr console with autoscroll and structured session headers |
| 🔌 **Connectivity Diagnostics** | 1-click remote path verification (`rclone lsd`) before starting batch jobs |
| 💾 **Persistent Configuration** | Atomic JSON configuration store (`config.json`) with auto-recovery on startup |

---

## 🏗 Architecture & Internal Structure

```
ZeroSync/
├── Core/                          # MVVM Infrastructure
│   ├── RelayCommand.cs            # ICommand implementation
│   ├── ViewModelBase.cs           # INotifyPropertyChanged base
│   └── Converters.cs              # WPF value converters
│
├── Model/
│   └── AppConfig.cs               # AppConfig + SyncProfile + RcloneSyncMode
│
├── Service/
│   ├── RcloneService.cs           # Process wrapper, stdout/stderr streaming
│   ├── ConfigService.cs           # Atomic JSON persistence (Newtonsoft.Json)
│   └── WatcherService.cs          # FileSystemWatcher + async debounce engine
│
├── ViewModels/
│   └── MainViewModel.cs           # MVVM orchestrator — commands and state bindings
│
├── Themes/
│   └── Styles.xaml                # Dark theme resource dictionary
│
├── MainWindow.xaml / .xaml.cs     # Minimal code-behind shell UI
├── App.xaml / .xaml.cs            # Application entry point
└── SyncDB.csproj                  # WPF Project File
```

---

## 📋 System Requirements

| Requirement | Details |
| :--- | :--- |
| **Operating System** | Windows 10, Windows 11, or Windows Server 2012 R2+ |
| **Runtime** | .NET Framework 4.6.2 or higher |
| **Engine Binary** | `rclone.exe` (placed alongside `SyncDB.exe` / `ZeroSync.exe` or in system `PATH`) |
| **Storage Providers** | Google Drive, OneDrive, AWS S3, Cloudflare R2, MinIO, or any Rclone-supported backend |

---

## 🚀 Quick Start & Usage

### 1. Configure Rclone Remote (One-Time Setup)

Open your terminal and configure your cloud storage providers via the interactive Rclone CLI:

```bash
# Launch interactive config wizard
rclone config

# Follow prompts to configure a remote (e.g., 'gdrive')
# Verify remote connection:
rclone lsd gdrive:
```

### 2. Launch ZeroSync

1. Place `rclone.exe` in the application root directory.
2. Launch `SyncDB.exe`.
3. Configure your sync profile in the interface:
   - **Source Directory**: Path to local backup folder (e.g., `D:\Data_Backup`).
   - **Remote Path**: Target remote and directory (e.g., `gdrive:Company_Backups`).
   - **Sync Mode**: `Copy` (safe default), `Sync` (mirror), or `Move`.
4. Click **▶ Start Sync** to execute immediately, or toggle **Watch Mode** for continuous synchronization.

---

## 📂 Multi-Profile Use Cases

ZeroSync supports unlimited isolated profiles:
- **Production Database Backup**: `D:\SQL_Backup` ➔ `gdrive:Prod_DB` (`Copy`, Watch Mode 30s)
- **Document Archives**: `E:\Documents` ➔ `s3:Corp_Archive` (`Sync`, `--bwlimit 2M`)
- **Staging Dumps**: `D:\Staging` ➔ `onedrive:Staging` (`Move`)

Profiles are persisted in `config.json` and selectable on-the-fly via the header dropdown.

---

## 🔄 Execution Pipeline

```
[User / Application Event]
       │
       ▼
 [ZeroSync WPF Client]
       │
       ├── Save profile state to config.json
       ├── Write session header to logs/rclone.log
       ▼
 [rclone.exe Subprocess]
       │
       ├── Stream copy/sync/move: local_path ──► remote:path
       ├── Throttle parameters (--transfers, --checkers, --bwlimit)
       ▼
 [Cloud Storage (Google Drive / S3 / OneDrive)]

 ─── Real-Time Watch Pipeline ───
 [FileSystemWatcher] ──► [Debounce Engine (15s)] ──► [Auto-Trigger Rclone Process]
```

---

## 🔨 Build from Source

### Prerequisites
- Visual Studio 2022 / 2019 or MSBuild 15+
- .NET Framework 4.6.2 Developer Pack

### Build Command

```bash
# Build Release using MSBuild
MSBuild.exe SyncDB.slnx /p:Configuration=Release /v:m

# Artifact generated at:
# SyncDB/bin/Release/SyncDB.exe
```

---

## 📄 License

Released under the **MIT License**. Part of the sovereign **ZeroUniverse** industrial computing ecosystem.

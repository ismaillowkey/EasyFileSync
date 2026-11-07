# EasyFileSync

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-blue.svg)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![UI Toolkit](https://img.shields.io/badge/UI-Avalonia%2011-orange.svg)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-lightgrey.svg)]()

**EasyFileSync** is a high-performance, bidirectional file synchronization and backup desktop utility built with modern C# (.NET 10) and Avalonia UI. Designed with an intuitive folder-to-folder comparison grid inspired by industry-standard tools like GoodSync and Beyond Compare, EasyFileSync provides lightning-fast analysis, robust conflict resolution, parallel transfer queues, and automated background sync schedules.

---

## Key Features

- **Folder-to-Folder Comparison Grid**
  - Side-by-side hierarchical tree view with visual indicators (same, modified, orphan, conflict).
  - Clear direction glyphs and action labels for every item.
  - Expand All, Collapse All, and single-folder tree toggling.

- **Versatile Synchronization Modes**
  - **Two-Way Sync**: Keeps both sides identical, propagating changes and deletions symmetrically.
  - **Update Right / Mirror**: Backs up source to target, updating newer files and removing orphaned items.
  - **Update Left**: Propagates changes from remote/target back to local source.

- **Automated Synchronization (Auto Sync)**
  - **Real-Time Monitoring**: Automatically triggers synchronization when files are created, modified, or deleted (with configurable debounce delay).
  - **Periodic Interval**: Runs every *N* hours and minutes.
  - **Daily Schedule**: Runs at a specific daily clock time (e.g., 02:00 AM).
  - **On Device Connection**: Triggers automatically when an external drive or network share becomes accessible.

- **Parallel Transfer Engine**
  - High-throughput parallel folder and file transfers (configurable 1 to 10 threads).
  - Live task queue showing progress percentage, current file, and transfer speed.
  - Per-task control: Pause, Resume, Stop, and Retry.

- **Advanced Include / Exclude Filters**
  - Rule-based exclusion by file name, directory name, or relative path pattern.
  - Supports wildcards (`*.tmp`, `node_modules`, `*/bin/Debug/*`).
  - Right-click context menu shortcuts to exclude specific files or paths directly from the comparison tree.

- **Multi-Protocol Storage Support**
  - **Local Storage**: Fast direct I/O for SSDs, HDDs, and USB flash drives.
  - **SMB / Windows Network Shares**: Automatic UNC share discovery, domain authentication, and background keep-alive.
  - **FTP / FTPS**: Secure remote file transfer powered by FluentFTP.

- **Job & Profile Management**
  - Organize sync jobs into custom groups.
  - Job cloning, renaming, and bulk operations (Analyze All, Sync All, Stop All).
  - One-click **Export All Jobs** and **Import All Jobs** to backup and migrate job profiles across computers.

- **Modern & Responsive UI**
  - Native Windows appearance with Segoe UI typography and crisp SVG iconography.
  - Dynamic responsive layouts with automatic scaling across standard and high-DPI displays.
  - System tray minimization for unobtrusive background sync monitoring.

---

## Solution Architecture

```
EasyFileSync/
├── EasyFileSync.slnx                  # Solution definition
├── Directory.Build.props              # Global build properties and versioning
├── run_or_build.bat                   # Interactive build and publish batch script
├── src/
│   ├── EasyFileSync.Core/             # Core synchronization library (.NET 10.0)
│   │   ├── Engine/                    # SyncEngine, AutoRunner, JobStore, StateStore
│   │   ├── Models/                    # JobConfig, SyncPlan, SyncAction, TaskStatus
│   │   └── Storage/                   # Local, SMB, FTP endpoints and FilterMatcher
│   │
│   └── EasyFileSync.App/              # Desktop GUI Application (Avalonia UI 11.3)
│       ├── Assets/                    # Vector SVG icons and resources
│       └── Views/                     # MainWindow and modal dialogs
```

---

## Prerequisites

- **Operating System**: Windows 10 (version 1809 or higher) or Windows 11 (x64)
- **Development**:
  - [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
  - Visual Studio 2026 / Visual Studio Code / JetBrains Rider

---

## Building from Source

### 1. Clone the Repository
```bash
git clone https://github.com/your-username/EasyFileSync.git
cd EasyFileSync
```

### 2. Debug Build & Run
```bash
dotnet run --project src/EasyFileSync.App/EasyFileSync.App.csproj -c Debug
```

### 3. Publish Self-Contained Executable
To generate a standalone, single-file Windows executable that does not require the .NET Runtime to be installed on target machines:
```bash
dotnet publish src/EasyFileSync.App/EasyFileSync.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```
The output executable will be created at:
`publish/win-x64/EasyFileSync.exe`

### 4. Using the Build Helper Script
Windows users can simply double-click `run_or_build.bat` in the repository root for an interactive menu with options to run, build, publish, or clean build artifacts.

---

## Getting Started

1. **Create a Job**: Click **New Job** on the top toolbar or press `Ctrl+N`. Enter a job name and select your job type (Two-Way or Backup/Mirror).
2. **Select Folders**: Click the **Left Folder** and **Right Folder** boxes to select source and destination directories (local paths, network shares, or FTP servers).
3. **Analyze**: Click **Analyze** (`F5`) to scan both folders. EasyFileSync will compare metadata and preview all proposed actions in the comparison grid.
4. **Synchronize**: Click **Sync** (`F7`) to execute the transfer. Monitor ongoing progress in the **Transfer Queue** at the bottom.
5. **Automate**: Click **Auto Sync** on the toolbar to schedule automatic background sync on file modifications, periodic timers, or daily schedules.

---

## Data Storage & Migration

EasyFileSync stores job definitions, sync state snapshots, and transfer logs in:
```
%APPDATA%\EasyFileSync\
├── EasyFileSync.db        # SQLite database for file signatures and states
├── state/                 # Serialized job configurations
└── logs/                  # Detailed synchronization history logs
```

> **Seamless Upgrade**: If you previously used an earlier build of the application, EasyFileSync automatically detects and migrates legacy job profiles and databases on first launch without any data loss.

---

## Technologies Used

- [Avalonia UI](https://avaloniaui.net/) - Cross-platform XAML GUI framework
- [FluentFTP](https://github.com/robinrodricks/FluentFTP) - Optimized FTP/FTPS client library
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) - High-performance local state tracking
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) - Lightweight MVVM architecture primitives

---

## License

Copyright © 2026 EasyFileSync. All rights reserved.


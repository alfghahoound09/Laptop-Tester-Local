<h1 align="center">LaptopTester</h1>

<p align="center">
  <strong>Open-source Windows laptop hardware diagnostics, anti-fake verification, and peripheral test suite built with C# / WPF (.NET 10).</strong>
</p>

---

## Introduction

**LaptopTester** is a lightweight, portable Windows 10/11 diagnostic application designed for inspecting laptops before buying, selling, or servicing. Instead of trusting a single Windows reporting layer that can be altered through Registry or display hooks, LaptopTester cross-checks hardware specifications across multiple independent sources and combines hardware health checks, stress tests, and interactive peripheral tests into a single dark-themed WPF workspace.

- **Stack:** C# / WPF targeting `.NET 10` (`net10.0-windows`), `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `System.Management` (WMI), Win32 P/Invoke (`GetSystemFirmwareTable`, `winmm.dll`, `wlanapi.dll`, `bthprops.cpl`), and Windows Registry APIs.
- **Plain-English Diagnostics:** Clear labels and hardware explanations designed for quick pre-purchase inspections.
- **Keyboard Navigation:** Quick tab switching via `F1`–`F7`.

---

## Functions

### 1. System Overview Dashboard (`F1`)
- Detects and summarizes **Model**, **OS / Build**, **CPU** (cores/threads/max clock), **RAM** (installed capacity, type, speed), **GPU** (WMI with Registry `DriverDesc` fallback), **Storage**, **Display** (resolution, refresh rate, EDID panel identity), and **Battery** status.
- Displays current execution privilege (`Standard User` or `Administrator`) and session check count.
- Provides one-click shortcuts to Hardware Scan, Battery Health, Anti-Fake Audit, Benchmark, and the Quick Test Wizard.

### 2. Hybrid Priority-Queued Quick Test (`F2`)
- Evaluates laptops on a weighted **100-point scale** using a configurable **Priority Queue** (`P1` -> `P10`) with minimal user effort:
  - **Fail-Fast Critical Core (`P1` Hardware & Anti-Fake `18` pts, `P2` Storage & SMART `14` pts):** Runs first and triggers an immediate fail-fast critical alert if specs are spoofed or SMART health fails.
  - **Automated Power, Connectivity & Sensors (`P3` Battery `14` pts, `P4` USB / Wi-Fi / BT `6` pts, `P5` Webcam `6` pts):** Runs hands-free while reusing the initial hardware snapshot.
  - **1-Second Hybrid Audio Loopback (`P6` Stereo Speakers `6` pts, `P7` Microphone `6` pts):** Plays a brief `880 Hz` tone and measures mic RMS to auto-pass both speakers and microphone when detected (falling back cleanly to manual verify if muted/headphones).
  - **Express Inline Micro-Inputs (`P8` Display `12` pts, `P9` Touchpad `8` pts, `P10` Keyboard `10` pts):** Auto-checks driver/stuck-key status, then surfaces one active check at a time directly inside Quick Test (fullscreen color flash with `P`/`F`, inline pointer move+click+scroll auto-pass pad, and 6-key quick sweep).

### 3. Hardware Hub (`F3`)
- **Hardware Scan:** Lists CPU architecture/cache, per-slot RAM modules (`Capacity`, `Speed`, `Type`, `Manufacturer`, `PartNumber`), GPUs, physical drives, and connected monitors.
- **3-Source Anti-Fake Audit:** Cross-checks **Windows/WMI**, **Registry / OS API**, and raw **SMBIOS firmware tables** (`GetSystemFirmwareTable('RSMB')` with `mssmbios\Data` fallback) for CPU name, RAM total, System Model/BIOS, physical storage media (`SSD` vs `HDD` bus truth), and known spoofing process indicators.
- **Battery Health & Stress Test:** Reads design vs. full-charged capacity (`mWh`), health percentage, 4-source charge cycle count (kernel battery IOCTLs, instance-correlated `root\wmi`, SMBIOS Type `22`, and cached `powercfg /batteryreport` XML history/estimation), manufacture date/serial, voltage sag, and charge/discharge rate (`W`); includes conservation-limit notes and a 30-second multi-core CPU drain stress test.
- **Thermal & Benchmark:** Captures ACPI thermal zones and drive SMART temperatures (where exposed by the board/permissions), runs single-core and multi-core prime CPU benchmarks, measures RAM copy bandwidth (`GB/s`), and optionally includes sequential disk speed.
- **Storage Speed & System Audit:** Measures `128 MB` sequential read/write throughput (`MB/s`), checks `MSFT_PhysicalDisk` / `MSStorageDriver_FailurePredictStatus`, audits Device Manager error codes (`Win32_PnPEntity`), and lists `Run` / `RunOnce` / Startup folder entries.

### 4. Device Hub (`F4`)
- **Keyboard Test:** Interactive visual QWERTY layout that highlights pressed and tested keys in real time, tracks key count, and detects stuck keys.
- **Display Test:** Fullscreen test patterns for **Solid Colors** (Black, White, Red, Green, Blue, 50% Gray), **Gradient Ramp**, and **Pixel Alignment Grid** (`Space`/Click next, Right-Click/Left previous, `Esc` exit).
- **Touchpad & Mouse Test:** Drawing canvas (`InkCanvas`) with real-time Left, Right, Middle click counters and scroll wheel event counter.
- **Speaker & Microphone Test:** Pure PCM stereo WAV synthesis (**Left**, **Right**, **Both**, and **160–4000 Hz Sweep**) via `winmm.dll`, plus live `waveIn` microphone recording with RMS level bar and playback.
- **USB Live Watcher:** Enumerates USB controllers and connected USB devices with speed hints and a live WMI plug/unplug event watcher.
- **Wi-Fi + Bluetooth:** Scans available Wi-Fi networks (`wlanapi.dll`) with signal quality/PHY info and detects Bluetooth radios (`bthprops.cpl`).
- **Webcam, Charger & Physical Checklist:** Enumerates detected cameras, reads live AC charger wattage/voltage, and provides a physical hinge/chassis/fan inspection checklist.

### 5. Reports, History & Settings (`F5`–`F7`)
- **Report (`F5`):** Displays consolidated results, score `/100`, deductions, and custom notes; exports **HTML** (print-ready for Print-to-PDF) and **JSON** reports.
- **History (`F6`):** Persists and manages saved test sessions as JSON under `%LocalAppData%\LaptopTester\history`.
- **Settings (`F7`):** Includes **In-App GitHub Updates** (checks `releases/latest`, verifies `SHA256SUMS.txt`, and applies updates automatically without manual reinstalling), shows permission level, and opens the local saved reports folder.

---

## Project Structure

```text
LaptopTester.sln
├── src/
│   ├── LaptopTester.Core/   # SMBIOS, WMI, Registry, Audio, USB/Wi-Fi/BT, Benchmark, Scoring, Reporting
│   └── LaptopTester.App/    # .NET 10 WPF MVVM UI, Dark Theme, Views & ViewModels
└── tests/
    └── LaptopTester.Tests/  # Core hardware & diagnostic self-test harness
```

---

## Build & Run

**Requirements:** Windows 10/11 (64-bit) and [.NET 10 SDK](https://dotnet.microsoft.com/). Running as **Administrator** is optional (needed only if your board restricts ACPI thermal sensors).

```powershell
# Build solution
dotnet build LaptopTester.sln

# Run full xUnit test suite (pure unit + live hardware probes)
dotnet test LaptopTester.sln

# Run fast offline unit tests only (no hardware or audio playback)
dotnet test --filter "Category!=Hardware&Category!=Audible"

# Opt in to audible speaker playback tests
$env:LAPTOPTESTER_AUDIBLE=1; dotnet test --filter "Category=Audible"

# Launch WPF desktop app
dotnet run --project src/LaptopTester.App/LaptopTester.App.csproj

# Build portable .exe and Windows Setup installer .exe locally into ./dist
.\scripts\build-release.ps1
```

---

## CI/CD & Releasing (Patch / Minor / Major `.exe`)

- **CI ([ci.yml](.github/workflows/ci.yml)):** Runs on every push and pull request (`master`, `main`, `dev`) — builds `Release`, runs offline unit tests, and publishes a portable `win-x64` preview `.exe` artifact.
- **CD Release ([release.yml](.github/workflows/release.yml)):** Produces two self-contained `win-x64` executables (no `.NET 10` runtime installation needed by end users) plus `SHA256SUMS.txt`, and publishes a **GitHub Release**:
  1. **`LaptopTester-Setup-<version>-win-x64.exe`** — Windows installer built with [Inno Setup 6](installer/LaptopTester.iss) (per-user or all-users install, Start Menu & Desktop shortcuts, clean upgrade & uninstaller).
  2. **`LaptopTester-<version>-win-x64-portable.exe`** — Single-file portable executable ready to run from a USB drive.

### Creating a Patch or Release

- **From GitHub UI (Recommended):** Open **Actions → Release & Patch Publisher → Run workflow**, choose `patch` (`1.0.0` → `1.0.1`), `minor`, `major`, or `custom`, and run. The workflow automatically updates [Directory.Build.props](Directory.Build.props), commits, tags `vX.Y.Z`, builds both `.exe` files, and publishes the GitHub Release.
- **From CLI via Git Tag:**
  ```powershell
  # Bump patch version, commit, tag, and push to trigger Release CD
  .\scripts\bump-version.ps1 -Bump patch -Push
  ```

### Built-in In-App Updates (No Manual Reinstall Required)

Users do not need to revisit GitHub or manually re-run installers for future versions:

1. Open **Settings (`F7`)** (or click the **Update** pill in the title bar when a newer release is detected on startup) and click **Check for Updates**.
2. Review the new release version and release notes, then click **Update & Restart Now**.
3. LaptopTester downloads the matching release binary over HTTPS, verifies its SHA-256 hash against `SHA256SUMS.txt`, and applies the update automatically:
   - **Installed App:** Silently patches the existing installation folder in-place and relaunches automatically.
   - **Portable Single-File `.exe`:** Atomically replaces the running executable in-place and relaunches cleanly.
4. *(Optional for private repositories)* Enter a read-only GitHub Personal Access Token in **Settings** (saved locally to `%LocalAppData%\LaptopTester\updater.json`) or set `LAPTOPTESTER_GITHUB_TOKEN`.

---

## License

MIT License — See repository history for attribution.

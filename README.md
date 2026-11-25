<h1 align="center">LaptopTester</h1>

<p align="center">
  <strong>Open-source Windows laptop hardware diagnostics, anti-fake verification, and peripheral test suite built with C# / WPF (.NET 10).</strong>
</p>

---

## Introduction

**LaptopTester** is a lightweight, portable Windows 10/11 diagnostic application designed for inspecting laptops before buying, selling, or servicing. Instead of trusting a single Windows reporting layer that can be altered through Registry or display hooks, LaptopTester cross-checks hardware specifications across multiple independent sources and combines hardware health checks, stress tests, and interactive peripheral tests into a single dark-themed WPF workspace.

- **Stack:** C# / WPF targeting `.NET 10` (`net10.0-windows`), `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `System.Management` (WMI), Win32 P/Invoke (`GetSystemFirmwareTable`, `winmm.dll`, `wlanapi.dll`, `bthprops.cpl`), and Windows Registry APIs.
- **Bilingual UI:** Live Vietnamese / English switching (`VI` / `EN`).
- **Keyboard Navigation:** Quick tab switching via `F1`–`F7`.

---

## Functions

### 1. System Overview Dashboard (`F1`)
- Detects and summarizes **Model**, **OS / Build**, **CPU** (cores/threads/max clock), **RAM** (installed capacity, type, speed), **GPU** (WMI with Registry `DriverDesc` fallback), **Storage**, **Display** (resolution, refresh rate, EDID panel identity), and **Battery** status.
- Displays current execution privilege (`Standard User` or `Administrator`) and session check count.
- Provides one-click shortcuts to Hardware Scan, Battery Health, Anti-Fake Audit, Benchmark, and the Quick Test Wizard.

### 2. Guided 10-Step Quick Test Wizard (`F2`)
- Evaluates laptops on a weighted **100-point scale** across 10 diagnostic steps:
  1. Hardware & Anti-Fake (`18` pts)
  2. Storage & SMART Health (`14` pts)
  3. Battery & Real Capacity (`14` pts)
  4. Display & Dead Pixels (`12` pts)
  5. Keyboard (`10` pts)
  6. Touchpad & Pointer (`8` pts)
  7. Stereo Speakers (`6` pts)
  8. Microphone Recording (`6` pts)
  9. Webcam Check (`6` pts)
  10. USB / Wi-Fi / Bluetooth (`6` pts)
- Runs automated checks (`Hardware`, `Anti-Fake`, `Storage`, `Battery`, `Connectivity`), links directly to interactive peripheral tests, tracks point deductions, and outputs a purchase verdict (`Good`, `Consider carefully`, or `Do not buy`).

### 3. Hardware Hub (`F3`)
- **Hardware Scan:** Lists CPU architecture/cache, per-slot RAM modules (`Capacity`, `Speed`, `Type`, `Manufacturer`, `PartNumber`), GPUs, physical drives, and connected monitors.
- **3-Source Anti-Fake Audit:** Cross-checks **Windows/WMI**, **Registry / OS API**, and raw **SMBIOS firmware tables** (`GetSystemFirmwareTable('RSMB')` with `mssmbios\Data` fallback) for CPU name, RAM total, System Model/BIOS, physical storage media (`SSD` vs `HDD` bus truth), and known spoofing process indicators.
- **Battery Health & Stress Test:** Reads design vs. full-charged capacity (`mWh`), health percentage, cycle count, voltage, and charge/discharge rate (`W`); includes a pre-test checklist and a 30-second multi-core CPU drain stress test.
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
- **Settings (`F7`):** Toggles `Vietnamese` / `English` language, displays current privilege status, and opens the local history directory.

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

# Run core diagnostic self-tests
dotnet run --project tests/LaptopTester.Tests/LaptopTester.Tests.csproj

# Launch WPF desktop app
dotnet run --project src/LaptopTester.App/LaptopTester.App.csproj
```

---

## License

MIT License — See repository history for attribution.

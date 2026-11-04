# UPS Tray Monitor

A small Windows system-tray application that shows the status of a USB-connected
UPS: battery level, input voltage, output load and power. No vendor software
required.

![screenshot](assets/screenshot.png?raw=true)

## Features

- **Tray icon** (drawn at runtime, no `.ico` file) whose color reflects the state:
  green = on line, amber = on battery, red = critical, gray = no data.
- **Hover** over the tray icon → tooltip with status, battery, voltage, load,
  power and runtime (Windows tooltip limit: 128 characters).
- **Left-click** the tray icon → popup window with the same information in
  detail (battery bar, model, data source, last update time).
- **Right-click** → menu with *Show details*, *Refresh now*, *Start at Windows
  startup* and *Exit*.
- Polls the UPS every 5 seconds in the background; single instance.

## Requirements

- Windows 10/11 (64-bit)
- .NET 8 runtime (the app targets `net8.0-windows`).
  If you build it yourself, the .NET 8 SDK (or a newer SDK that can target .NET 8) is needed.

## Build

```powershell
dotnet build
```

## Run

```powershell
# Tray application (default)
dotnet run --project src\UpSTrayMonitor\UpSTrayMonitor.csproj

# Or the built executable
src\UpSTrayMonitor\bin\Debug\net8.0-windows\ups-tray-monitor.exe
```

### Useful command-line modes

```powershell
--once       Read one UPS sample, print it, exit (exit code 2 = no data found)
--console    Print a sample every 5 s (Ctrl+C to stop)
--dump       List every standard UPS HID usage exposed by HID devices on this machine
--screenshot [file] [online|battery|critical|nodata]
             Save a PNG of the details window with sample data
--help       Show help
```

## How it reads the UPS

Data sources are queried in this order and merged (first source wins per field):

1. **HID (USB)** – reads the standard UPS "Battery Status" HID usage page
   (0xFF) directly from the USB device: battery charge %, discharging flag,
   input voltage, output load %, active/apparent power, remaining runtime.
   Works with most UPS vendors (APC, CyberPower, Eaton, Tripp Lite,
   PowerExpert, …) without installing anything.
2. **WMI** – `UPS` / `UPSBattery` classes in `root\CIMV2\Power`, available
   when the UPS (or vendor software) installs a WMI-capable UPS driver.
3. **System power** – `GetSystemPowerStatus`, used only when the OS actually
   reports a battery/UPS power source (a plain desktop reports "no battery"
   and is ignored, so it will never fake a UPS status).

If nothing reports data, the app shows a gray "no data" state instead of
guessing.

## Troubleshooting

- **No data at all** – run `--dump` and check that your UPS appears with
  usages like `0x0B Battery Pack Charge`, `0x14 Input Mains Voltage`,
  `0x17 Output Load`. If your UPS uses non-standard usages, tell the vendor
  or check Device Manager that the UPS is recognized as a HID device.
- **Laptop battery shown instead of UPS** – the fallback source reports the
  system battery; plug in the UPS (sources 1-2 take priority automatically).
- **Uncaught error while the app is running** – errors are logged to
  `%TEMP%\ups-tray-monitor\error.log`.

## Start at login (optional)

Tick **Start at Windows startup** in the tray menu. The app writes a value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` pointing at the running
`.exe`; unticking the option removes it. (No admin rights needed.)

Alternatively, do it manually:

1. Press `Win+R`, type `shell:startup`, press Enter.
2. Drop a shortcut to `ups-tray-monitor.exe` (or a `dotnet.exe` shortcut with
   `exec <path>\ups-tray-monitor.dll`) into that folder.

## Project layout

```
src\UpSTrayMonitor\
├── Program.cs                     Entry point + CLI modes + crash logging
├── UpsModel.cs                    UpsState / UpsSample / UpsStatus
├── Providers\
│   ├── IUpsProvider.cs            Provider contract
│   ├── UpsMonitor.cs              Merges all providers into one sample
│   ├── HidUpsProvider.cs          USB HID reader (SetupAPI + hid.dll P/Invoke)
│   ├── WmiUpsProvider.cs          WMI UPS / UPSBattery classes
│   └── SystemPowerProvider.cs     GetSystemPowerStatus fallback
└── Tray\
    ├── TrayApplicationContext.cs  NotifyIcon, polling loop, context menu
    ├── StartupManager.cs          "Start at Windows startup" registry toggle
    ├── DetailsForm.cs             Popup window shown on left-click
    ├── StatusIcon.cs              Runtime-drawn tray icon (battery glyph)
    └── UpsPresentation.cs         Shared colors, status text, tooltip builder
```

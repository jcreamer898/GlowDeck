# GlowDeck

GlowDeck is a native Windows lighting studio for device groups, individual RGB zones, and saved scenes. The first implementation uses a WPF UI, read-only Windows hardware inventory, and the OpenRGB SDK for RGB output.

## Build and run

Run from the repository root:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore .\GlowDeck.sln
& "$env:USERPROFILE\.dotnet\dotnet.exe" build .\GlowDeck.sln
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project .\GlowDeck\GlowDeck.csproj
```

GlowDeck targets .NET 10 for Windows. `System.Management` is used only to read Windows Plug and Play and memory-module inventory.
The .NET 10 SDK is installed under the current Windows user profile. Use the explicit executable above if `dotnet` in `PATH` points to a runtime without an SDK.

## RGB backend

GlowDeck can download the official OpenRGB 1.0 portable release after you choose **Set up RGB backend**. It verifies the release archive checksum, stores it under `%LOCALAPPDATA%\GlowDeck\Backends`, and starts OpenRGB's SDK server as a background process while GlowDeck is open. You do not need to open or manage a second RGB-control window. If an OpenRGB SDK server is already running on `127.0.0.1:6742`, GlowDeck connects to it and does not stop that external process.

GlowDeck does not install drivers, create startup tasks, elevate itself, or apply colors during discovery. Lighting commands are sent only after an Apply action. OpenRGB 1.0 requires the separate PawnIO driver plus administrator or service access for RAM and some motherboard controllers; GlowDeck does not install or elevate that driver automatically. If OpenRGB cannot access a device, GlowDeck keeps it read-only and explains the likely permission requirement.

OpenRGB 1.0 includes support for the Lian Li UNI HUB SL v1.8 controller used by the `0CF2:A100` device ID. GlowDeck enables control only when the connected backend actually enumerates a controller and its LEDs. The initial Apply scope is case lighting, motherboard lighting, and memory; Razer and unrelated controllers remain visible but read-only. The app does not control fan speed or change BIOS, driver, or service settings. Animated scenes are software-rendered at a low update rate and run only while GlowDeck is open.

The UNI HUB SL setup panel configures fan counts for Channels 1, 2, and 4 (1–4 daisy-chained fans per channel). Each SL fan is represented as 16 LEDs. The defaults match the verified three-fan layout on this PC. Channel 3 is the case front strip and is intentionally left untouched by the fan setup. Configuring channels changes only the OpenRGB zone layout; it does not apply a color. OpenRGB saves the layout in GlowDeck's managed configuration.

Philips Hue is not included in this first version; the scene model is kept separate from the OpenRGB transport so a Hue Bridge adapter can be added later.

Scenes are stored locally at `%LOCALAPPDATA%\GlowDeck\scenes.json`.

## Tests

Run from the repository root:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" test .\GlowDeck.sln
```

The SDK test uses a local mock server; it does not send commands to physical lighting hardware.

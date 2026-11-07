# AI N64 Remapper Studio

A standalone Windows desktop configuration app for the [Zenith Control Labs N64 Remapper](https://github.com/ZenithControlLabs/N64_Remapper), built with C# and Windows Forms. Configure N64 buttons and stick response, watch live controller input, calibrate the stick, and keep local profiles.

[Download the preview](https://github.com/superweegeex-ship-it/AI-N64-Remapper-Studio/releases/tag/v0.1.0) · [All releases](https://github.com/superweegeex-ship-it/AI-N64-Remapper-Studio/releases) · [Report an issue](https://github.com/superweegeex-ship-it/AI-N64-Remapper-Studio/issues)

The first release is a preview. Use **All releases** to find preview builds. This independent, AI-assisted project implements the device protocol used by the original web configurator. Hardware and firmware are maintained by Zenith Control Labs.

## Features

- Native Windows interface with direct WinUSB configuration and HID input.
- Live raw and processed stick visualization, with pressed-button feedback.
- Guided 16-step calibration with a target marker and previous-step control.
- N64 button mapping, including the additional GPIO inputs.
- Eight output notches with editable coordinates and angular deadzones.
- Magnitude threshold, low-pass filter cutoff, and gate limiter settings.
- Local JSON profiles with import, export, and validation.
- A local draft editor, explicit apply, settings readback, and a separate flash save.
- Best-effort restoration of previous settings if apply or readback fails.
- Demo mode and built-in protocol diagnostics.

## Download and run

1. Open [Releases](https://github.com/superweegeex-ship-it/AI-N64-Remapper-Studio/releases) and download **N64-Remapper-Studio-Windows-x64.zip**.
2. Extract the ZIP into a regular folder and open **N64 Remapper Studio.exe**. Windows 10/11 x64 is required. The portable build includes its .NET desktop runtime.
3. Plug the N64 controller into the Remapper. Leave the console cable disconnected and attach the Remapper to the PC with a USB data cable. Close other configurators and connect one Remapper at a time.
4. Select **Connect**. The app reads the firmware version and settings and starts displaying live input. Select **Demo** to explore with simulated input.

The EXE is also attached separately to each release for users who prefer a single executable.

## Calibrate and configure

If calibration is required, open **Calibration & device** and select **Start calibration**. Follow the amber target, alternating between center and the eight gate positions. Hold the requested position until **Capture step** advances. Use **Previous step** to repeat a capture. The app reads the firmware's calibration status to confirm progress.

Edit **Stick tuning** or **Button mapping**, then select **Apply + verify**. Edits stay in the local draft until you apply them. The app sends changed values to device memory and compares them with a fresh read. If applying fails, it attempts to restore the previous settings and reports whether restoration was verified.

Select **Save to device** to request a flash write. To confirm persistence, wait for the save result, power-cycle the Remapper, reconnect, and read the device. The reviewed firmware exposes settings readback from memory and does not provide a usable flash-persistence acknowledgement.

**Read device** replaces the local draft with current device settings. Resetting defaults clears the current device settings and calibration in memory. Reset and bootloader actions require confirmation in the app.

## Profiles

**Export profile** saves the current local draft as JSON. **Open profile** loads a profile locally, ready to review and apply. Profiles contain N64 bindings, notch coordinates, angular deadzones, magnitude threshold, filter cutoff, and the gate limiter.

Calibration coefficients are local to the device and cannot be read or transferred through the reviewed protocol. Calibrate each physical controller on its Remapper. Profiles are files on the PC. The app does not select firmware profile slots.

## Build from source

Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows. Clone the repository and run these commands in its root folder.

```powershell
git clone https://github.com/superweegeex-ship-it/AI-N64-Remapper-Studio.git
cd AI-N64-Remapper-Studio
dotnet build .\N64Studio.csproj -c Release
dotnet run --project .\N64Studio.csproj -c Release -- --demo
```

To publish the portable Windows x64 executable, run the following command.

```powershell
dotnet publish .\N64Studio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o .\publish
```

The output is **publish/N64 Remapper Studio.exe**. The project has no third-party NuGet dependencies.

## Diagnostics and testing

Close the main app before running the hardware probe. These commands assume that the portable build is in **publish**.

```powershell
$app = Join-Path $PWD 'publish\N64 Remapper Studio.exe'
$testLog = Join-Path $PWD 'self-test.txt'
$test = Start-Process $app -ArgumentList "--self-test `"$testLog`"" -Wait -PassThru
Get-Content $testLog
if ($test.ExitCode -ne 0) { throw 'Self-test failed.' }

$probeLog = Join-Path $PWD 'probe.txt'
$probe = Start-Process $app -ArgumentList "--probe `"$probeLog`"" -Wait -PassThru
Get-Content $probeLog
if ($probe.ExitCode -ne 0) { throw 'Hardware probe failed.' }
```

The simulator checks profile serialization and required fields, settings apply, readback mismatch detection, verified restoration, preservation of the second remap table, stale-save rejection, the save request, 16-step calibration and undo, reset, and HID decoding.

Hardware checks performed on firmware **v1.4.0** confirmed matching repeated settings reads, live HID input in the plot, and verification of an unchanged draft. Changed writes, calibration, and flash-save requests were tested with the simulator. Flash persistence and a completed calibration still need end-to-end hardware validation.

GitHub Actions builds the Windows executable, runs the simulator checks, and publishes release assets. A first successful main-branch build creates the **v0.1.0 preview** if it does not exist. Later main-branch builds do not replace that release. Pushing a new **v*** tag publishes another preview release at that tag.

## Troubleshooting

If Windows shows the HID game controller but **Connect** fails, check Device Manager for the separate Remapper configuration interface, USB **VID 7318, PID 0002, MI 01**. The firmware advertises the built-in WinUSB driver for that interface. The game controller interface uses the Windows HID driver.

Check the data cable, controller connection, and the upstream firmware setup instructions. Close the browser configurator and any other copy of Studio that may be using the device. After a communication failure, reconnect and read the device before applying more changes. A failed connection or stale HID stream is reported in the interface.

## Project layout

| File | Purpose |
| --- | --- |
| N64Studio.csproj | Windows desktop project and build settings |
| Program.cs | Native interface, profile files, live plot, and application flow |
| Protocol.cs | Profile model, packet encoding, readback, apply, restoration, calibration, and save |
| WindowsUsb.cs | Windows device discovery, WinUSB access, and HID input |
| Demo.cs | In-memory device simulation |
| Diagnostics.cs | Simulator checks and hardware read probe |
| .github/workflows/release.yml | Windows build, checks, and GitHub releases |

## Device protocol and credits

The hardware documentation was inspected through the connected GitHub integration. Implementation is based on the existing configurator and firmware source at commit **a316769f114bf5d2be2ba79e77fc49d9a6f78ca7**.

- [N64 Remapper hardware and setup](https://github.com/ZenithControlLabs/N64_Remapper)
- [Original web configurator](https://zenithcontrollabs.github.io/Zenith_FW/web/platforms/remapper/)
- [Firmware command processor](https://github.com/ZenithControlLabs/Zenith_FW/blob/a316769f114bf5d2be2ba79e77fc49d9a6f78ca7/fw/lib/usb/webusb.c)
- [USB interface descriptors](https://github.com/ZenithControlLabs/Zenith_FW/blob/a316769f114bf5d2be2ba79e77fc49d9a6f78ca7/fw/lib/usb/descriptors.c)

Configuration uses bulk OUT **0x02** and IN **0x82**. Live input uses HID report ID **4**. The reviewed firmware updates a second remap table as a side effect of N64 remap commands. Studio preserves and verifies that table internally. The interface and exported profiles contain N64 settings only.

# v0.1.0 preview

Initial native Windows desktop configurator for the Zenith Control Labs N64 Remapper.

- N64 button and GPIO mapping, stick tuning, and local JSON profiles.
- Live raw and processed stick display with button feedback.
- Guided calibration with firmware status readback.
- Explicit apply and verification, best-effort restore on failure, and a separate flash-save action.
- Demo mode and built-in diagnostics.

## Downloads

**N64-Remapper-Studio-Windows-x64.zip** contains the portable Windows 10/11 x64 app and README. Extract it and run **N64 Remapper Studio.exe**. The EXE is also available as a separate download. The .NET desktop runtime is included.

**SHA256SUMS.txt** contains checksums for the EXE and Windows ZIP. GitHub's source-code archives contain the full project.

## Validation and current limitations

The simulator checks settings writes, profile files, readback mismatch recovery, calibration, save gating, and HID decoding. On a connected Remapper running v1.4.0, repeated settings reads matched, live input reached the plot, and applying an unchanged draft passed verification.

Changed writes, completed calibration, and flash persistence still need end-to-end hardware validation. Calibration coefficients cannot be exported through the reviewed device protocol. After saving, power-cycle and read the device to confirm persistence.

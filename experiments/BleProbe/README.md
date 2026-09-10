**English** · [Français](README.fr.md)

# BleProbe — archive of a negative result

A test bench kept **on purpose**, and deliberately **outside
`LuminaMonitor.sln`**: it isn't part of the application and doesn't build
with it.

It answered a single question, before the USB path was chosen: can Windows
11 drive an iPhone by publishing a **Bluetooth LE** (HOGP) keyboard/mouse
from user mode? Three HID profiles are tried — relative pointer, absolute
pointer, touch digitizer —
`LuminaMonitor.BleProbe <relative|absolute|digitizer>`.

**Answer: no**, for two reasons verified on a real machine:

1. dual-mode pairing is unstable — iOS merges the Classic and LE identities
   and wrecks the fresh HID link;
2. iOS only accepts an **absolute pointer over Bluetooth Classic**, a role
   Windows doesn't offer without a third-party kernel driver — which the
   project's rule forbids.

This wall is what sent the project to the USB cable. The code stays here so
the demonstration is verifiable rather than taken on faith; it receives no
maintenance.

To build it anyway:
`dotnet build experiments/BleProbe/LuminaMonitor.BleProbe.csproj`.

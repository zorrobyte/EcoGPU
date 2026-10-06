# EcoGPU

ASUS-style **GPU Eco mode** for any Windows laptop with an NVIDIA (or AMD) discrete GPU.

EcoGPU sits in the system tray. Unplug the charger and it turns the discrete GPU off; plug back in and it turns it on again. ASUS (Armoury Crate / [G-Helper](https://github.com/seerge/g-helper)) and Lenovo ([Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit)) offer this, but most other laptops (Razer, MSI, Gigabyte, etc.) don't.

### Does it actually save power?

It depends on whether anything is keeping the GPU awake:

- **Nothing using the GPU:** NVIDIA Optimus already puts it into its deepest sleep state (D3) a few seconds after the last app lets go. Turning it off saves little beyond that.
- **Something holding it:** a browser tab, Discord, an overlay, RGB/monitoring software, a game launcher. The GPU stays awake (D0) all the time, and it's hard to tell which app is responsible. In a rough test on a Razer Blade 16 (RTX 5090 Laptop), battery drain was about **3 W higher** with the GPU held awake at 0% load than with it disabled. On a laptop that idles around 30 W, that's roughly 10% more battery life.

Turning the GPU off guarantees the sleeping case no matter which apps are running. The tray menu shows whether the GPU is **On, asleep** or **On, awake**. It reads this from Windows without waking the GPU, so you can see when something is holding it.

## Modes

| Mode | What it does |
|---|---|
| **Optimized** (default) | Off on battery, on when plugged in |
| **Standard** | Always on |
| **Eco** | Always off |

Tray icon: orange filled chip = GPU on, grey chip with a green leaf = GPU off.

## How it works

ASUS laptops have a firmware switch (an ACPI call) that cuts the dGPU's power. Other laptops don't have one, so EcoGPU disables the GPU as a PnP device (`pnputil /disable-device`), the same as clicking *Disable device* in Device Manager. The driver unloads, every app's hold on the GPU is dropped, and the PCIe link powers down. Re-enabling brings it back in a second or two.

Before turning the GPU off, EcoGPU runs the same safety checks as G-Helper and Legion Toolkit:

- **No integrated GPU running** (laptop is in dGPU-only / MUX mode): it doesn't switch, because the screen would go black.
- **A display is connected to the dGPU** (e.g. an external monitor on a port wired to the NVIDIA GPU): it waits until that display is unplugged.
- **GPU busy** (over 10% load, e.g. a game is running): it waits and checks again every 30 s. The tray menu shows **Turn off now** if you want to force it.
- It doesn't switch while the laptop is going to sleep, and it waits a few seconds after a plug/unplug so a loose cable doesn't make it flip back and forth.

After re-enabling, it restarts the NVIDIA display container service if needed (G-Helper does this too), so NVIDIA Control Panel and the NVIDIA App keep working.

The menu also has **Release GPU**, the same as Legion Toolkit's *Deactivate GPU*: it restarts the device so apps let go of it and it can drop into its own low-power state, without disabling it.

## Install

1. Download `EcoGPU.exe` from [Releases](https://github.com/zorrobyte/EcoGPU/releases) and put it somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\EcoGPU\`.
2. Run it. It asks for admin rights because enabling and disabling a device requires them.
3. Right-click the tray icon, open **Options**, and turn on **Start with Windows**. This creates a scheduled task that starts EcoGPU elevated at logon, with no UAC prompt each time.

Needs Windows 10/11 and .NET Framework 4.8, which Windows 11 already includes. It's a single exe of about 40 KB.

The exe isn't code-signed, so SmartScreen may warn you the first time (*More info → Run anyway*). On PCs with **Smart App Control** on, Windows blocks unsigned apps completely. Build it yourself, or turn Smart App Control off. Windows only lets you turn it back on by reinstalling.

Command line:

```
EcoGPU.exe --enable-startup     register the logon task
EcoGPU.exe --disable-startup    remove it
```

## Things to know

- **Disabled stays disabled.** Windows remembers that the GPU is disabled across reboots. If you quit EcoGPU from the tray menu, it turns the GPU back on first (you can turn this off under *Options*). If you uninstall it while the GPU is off, re-enable the GPU in Device Manager under *Display adapters*.
- **Apps on the GPU lose it.** Turning the GPU off takes it away from any app using it. Most apps (browsers, Discord, video players) move to the integrated GPU without trouble. Games and 3D apps can crash, which is why EcoGPU waits while the GPU is busy.
- **Ports wired to the dGPU** (on many laptops the HDMI port, sometimes a USB-C port) don't work while the GPU is off.
- **Picking the GPU manually.** EcoGPU finds the NVIDIA GPU automatically. To choose a different device, set `DeviceInstanceId` in `%APPDATA%\EcoGPU\settings.ini` to the *Device instance path* shown in Device Manager.
- A log of every switch is written to `%APPDATA%\EcoGPU\EcoGPU.log` (*Options → Open log*).

## Build

```
dotnet build src/EcoGPU.csproj -c Release
```

Output: `src/bin/Release/net48/EcoGPU.exe`. Any .NET SDK 6 or newer works. It targets .NET Framework 4.8, so the exe runs without installing a runtime.

## Credits

The approach and safety checks come from [G-Helper](https://github.com/seerge/g-helper) by seerge and [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit) by Bartosz Cichecki.

## License

MIT

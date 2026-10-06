# EcoGPU

Stops apps from keeping your laptop's discrete NVIDIA GPU awake on battery.

On an Optimus laptop the NVIDIA GPU is supposed to power off when nothing needs it. In practice, apps get stuck on it: a browser, Discord, an overlay, a launcher, RGB or monitoring software. The GPU then stays awake for hours while you're on battery, and Windows won't tell you which app is responsible.

EcoGPU sits in the system tray. When you're unplugged and the GPU has been awake for a minute without doing real work, EcoGPU restarts it. That forces every app off it, and NVIDIA's driver then powers it down completely. It's like ASUS's GPU Eco mode ([G-Helper](https://github.com/seerge/g-helper)) or Legion Toolkit's *Deactivate GPU* ([LenovoLegionToolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit)), but it works on any brand: Razer, MSI, Gigabyte, and others.

## Modes

| Mode | What it does |
|---|---|
| **Optimized** (default) | GPU stays enabled. On battery, if apps keep it awake while idle, EcoGPU frees it so it can power off. Plugged in, it leaves the GPU alone. |
| **Standard** | GPU stays enabled and EcoGPU never touches it |
| **Eco** | GPU disabled in Device Manager |

The tray icon is a green leaf when the GPU is powered off and an orange chip when it's awake. The menu shows the current state (*awake* / *asleep, powered off* / *disabled*). EcoGPU reads that from Windows without waking the GPU. Tools like nvidia-smi or GPU-Z wake it up just by asking.

## Why it restarts the GPU instead of disabling it

On a Razer Blade 16 (RTX 5090 Laptop) we compared the two:

| GPU state | GPU | PCIe port |
|---|---|---|
| Enabled, idle, nothing holding it | D3 | **D3**: power fully cut (D3cold) |
| Disabled in Device Manager | D3 | **D0**: port still powered (D3hot) |

With the driver loaded, NVIDIA's runtime power management cuts power to the GPU *and* its PCIe port. A disabled GPU has no driver to do that, so its port stays powered. Disabling looks like the bigger hammer, but it doesn't save more power than a GPU that's idle and properly asleep. So Optimized mode gets apps off the GPU and lets the driver do the rest. Eco mode is still there if you want the GPU fully disabled.

## How Optimized mode works

Every 20 seconds on battery, EcoGPU checks the GPU's power state:

1. If it's been **awake for 60 seconds**, EcoGPU checks how busy it is. Over 10% load (a game, a render, an AI job) means real work, so EcoGPU leaves it alone and checks again later.
2. If it's idle, EcoGPU **restarts it** (`pnputil /restart-device`). Every app loses its hold on the GPU and falls back to the integrated GPU, and NVIDIA's driver powers the GPU off about 30 seconds later. The screen may flicker once.
3. If something grabs the GPU again right away, EcoGPU **backs off**: 1, 2, 4, 8, then 15 minutes between restarts, so it isn't restarting the GPU constantly. It also shows a notification naming the app, so you can close it or set it to *Power saving* in *Settings → System → Display → Graphics*.

It never releases the GPU while a display is connected to it (e.g. an external monitor on an HDMI port wired to the GPU), and it doesn't act while the laptop is going to sleep.

**Release GPU now** in the tray menu does the same restart on demand, whether you're plugged in or not.

## Install

1. Download `EcoGPU.exe` from [Releases](https://github.com/zorrobyte/EcoGPU/releases) and put it somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\EcoGPU\`.
2. Run it. It asks for admin rights, which restarting or disabling a device requires.
3. Right-click the tray icon, open **Options**, and turn on **Start with Windows**. This creates a scheduled task that starts EcoGPU elevated at logon, with no UAC prompt each time.

Needs Windows 10/11 and .NET Framework 4.8, which Windows 11 already includes. It's a single exe of about 40 KB.

The exe isn't code-signed, so SmartScreen may warn you the first time (*More info → Run anyway*). On PCs with **Smart App Control** on, Windows blocks unsigned apps completely. Build it yourself, or turn Smart App Control off. Windows only lets you turn it back on by reinstalling.

Command line:

```
EcoGPU.exe --enable-startup     register the logon task
EcoGPU.exe --disable-startup    remove it
```

## Settings

`%APPDATA%\EcoGPU\settings.ini` (*Options → Open settings folder*):

| Setting | Default | |
|---|---|---|
| `AutoRelease` | `True` | Optimized mode: free the GPU from apps on battery |
| `AwakeGraceSeconds` | `60` | How long the GPU can stay awake and idle before it's released |
| `DeviceInstanceId` | empty | Pick the GPU manually (the *Device instance path* from Device Manager). Empty means auto-detect. |

A log of every action is written to `%APPDATA%\EcoGPU\EcoGPU.log`.

## Things to know

- **Eco mode survives reboots.** Windows remembers that the GPU is disabled. Quitting EcoGPU turns it back on first (you can turn this off in *Options*). If you uninstall EcoGPU while in Eco mode, re-enable the GPU in Device Manager under *Display adapters*.
- **Apps lose the GPU on a release.** Browsers, Discord and video players switch to the integrated GPU without trouble. EcoGPU never releases the GPU while it's busy, so a running game won't be interrupted.
- **Battery life is mostly the screen.** On the test laptop, full brightness drew about 20 W of a 30 W total. Turning brightness down did more for battery life than anything the GPU did.

## Build

```
dotnet build src/EcoGPU.csproj -c Release
```

Output: `src/bin/Release/net48/EcoGPU.exe`. Any .NET SDK 6 or newer works.

## Credits

The idea and the safety checks come from [G-Helper](https://github.com/seerge/g-helper) by seerge and [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit) by Bartosz Cichecki.

## License

MIT

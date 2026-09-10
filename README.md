# ViperKey

<img src="ViperKeyLogo.png" alt="ViperKey" width="300">

A tiny Windows tray tool that types a predefined key sequence with a single hotkey — through a **virtual USB keyboard**, not simulated keys.

Because the keystrokes come from a real keyboard HID device, the sequence works anywhere normal input is accepted: games, anti-cheat-protected applications, remote-desktop sessions, and full-screen apps where `SendInput`-style injection is ignored.

### Why this is better than simulated keystrokes

ViperKey doesn't inject at the Win32/Windows input layer (no `SendInput`, no fabricated `WM_KEYDOWN` events). Instead it drives a **genuine USB keyboard device** — VIIPER creates it over USB/IP and the usbip-win2 driver presents it to Windows as real hardware, built on top of the ubiquitous USB/IP stack:

- **Indistinguishable at the layer that matters.** To the OS *and* to applications, the input arrives as ordinary USB HID report traffic — the same path a physical keyboard uses. Games and anti-cheat that inspect for injected input (message hooks, `SendInput` markers, synthetic-event flags) generally see nothing abnormal, because there is nothing abnormal — the bytes genuinely come off a USB bus.
- **Works where injection is blocked.** Full-screen/ExclusiveFullscreen games, elevated windows, anti-cheat that disables Win32 injection — a plug-in keyboard keeps working.
- **System-level keyboard semantics.** Real key-up/key-down pairing, N-key rollover, and driver-level behavior, not the constrained, easily-detected emulation of `keybd_event`/`SendInput`.
- **No kernel drivers in ViperKey.** All device logic lives in the userspace VIIPER process; the kernel side is the generic, signed usbip-win2 driver. Nothing to compile per-game, nothing to sign yourself. (Anti-cheat detection is never 100% guaranteed for any input method — but at the USB layer there is no synthetic-input signal to detect.)

As VIIPER describes it: input devices that are *"indistinguishable from real hardware to the operating system and applications."* The practical upshot: no detectable difference from typing on the keyboard in front of you.

```
viperkey.exe        ->  viiper.exe         ->  usbip-win2 driver ->  OS sees a real keyboard
polls hotkeys,          creates a virtual      creates the actual
streams HID             keyboard over          USB device
key states              USB/IP (TCP
                        127.0.0.1:3242)
```

## Purpose

You set a *trigger* and a *macro* in a JSON file. When you press the trigger combo, ViperKey sends the stored sequence to the virtual keyboard, one step at a time, at your chosen pace. An *abort* combo interrupts the running sequence at any point.

Use cases:

- Chained `/commands` in games (e.g. ``Shift+`+1``, `E`, then an emote) with reliable timing.
- Repetitive menu navigation that must look human.
- Any place where a real keyboard is expected.

## Requirements

| Component | What it is | Where it comes from |
|---|---|---|
| **ViperKey** (this repo) | Tray tool; reads config; emits HID key states | Build from `viperkey.cs` (see below) |
| **VIIPER** server | User-space server that creates the virtual USB keyboard and exposes a small TCP API on `127.0.0.1:3242` | https://github.com/Alia5/VIIPER |
| **usbip-win2** driver | Signed kernel-mode USB/IP client that makes the virtual device appear as real hardware | https://github.com/Alia5/usbip-win2 (the VIIPER installer installs it for you) |

## Install

Run the following in an **administrator** PowerShell once (installs VIIPER **and** the usbip-win2 driver, and registers VIIPER to start automatically):

```powershell
irm https://alia5.github.io/VIIPER/stable/install.ps1 | iex
```

The installer may ask for a reboot when the driver is added. When prompted, reboot.

VIIPER is installed to `%LOCALAPPDATA%\VIIPER\viiper.exe`.

> Manual alternatives: download `viiper.exe` from https://github.com/Alia5/VIIPER/releases (ViperKey finds it next to itself, then in `%LOCALAPPDATA%\VIIPER` and `%ProgramFiles%\VIIPER`), or install the usbip-win2 driver yourself from https://github.com/Alia5/usbip-win2/releases and make sure `usbip.exe` is in your `PATH`.

## Usage

1. Put `viperkey.exe` (or run it from the build output) in any folder.
2. **First run only creates a `viperkey.json` template — it does not fire anything until you edit that file.** Open the generated `viperkey.json`, replace the example `steps` with the sequence you actually want, save, then start the tool again (or just save the file while the tray app is running — it hot-reloads).
3. The tray icon appears; the tool logs `Trigger: Ctrl+K` etc. to the console/`viperkey.log` when `debug` is enabled.
4. Press-and-release the trigger combo. The sequence fires. Press the abort combo to stop it mid-run.

> **⚠ Important: focus matters.** ViperKey keyboards are *real* USB keyboards — nothing is window-targeted. The sequence always goes to whichever window currently has focus, exactly as if you typed it. Make sure the game/chat/app you want to receive the input is the active window before pressing the trigger. There is no "send to window X" option.

### Config (`viperkey.json`)

```jsonc
{
    "trigger_mods": "Ctrl",          // combos: Ctrl, Ctrl+Alt, Shift, Win...
    "trigger_key": "K",              // fires on the release of Ctrl+K
    "abort_mods": "Ctrl+Alt",
    "abort_key": "F12",
    "default_delay_ms": 1300,        // pause after each step (unless overridden)
    "debug": false,                  // true -> writes viperkey.log next to the exe
    "steps": [
        { "keys": "Shift+`+1" },
        { "keys": "`+1", "delay_ms": 2000 }   // optional per-step pause override
    ]
}
```

- **Steps** — each `keys` string is a *chord*: keys are joined with `+`, pressed in order, held briefly, released in reverse. A modifier prefixes only the key after it, so ``Shift+`+1`` means hold Shift, press the grave key, then `1`.
- **Per-step** `delay_ms` overrides `default_delay_ms` for the pause *after* that step.
- **Hot reload** — the file is watched; save it and the new config applies within ~0.3 s without restarting.
- **Tray menu** — Open Config, Reload, Run at Startup (registry), Exit.

## Architecture

ViperKey is a single-file C# program (`viperkey.cs`) with three moving pieces:

1. **Trigger & abort detection (`viperkey.exe`)**
   - A 15 ms poll loop reads the trigger combo state via `GetAsyncKeyState` and latches on press+release.
   - The abort combo is registered twice for reliability: a `RegisterHotKey` and a low-level keyboard hook. `SleepAbortable()` wakes every 10 ms during a sequence so an abort lands within a fraction of a second.

2. **VIIPER server (`viiper.exe`, TCP `127.0.0.1:3242`)**
   - ViperKey auto-starts it if it is not already running (and prepends the USBip `bin` folder to `PATH` when known).
   - Control: a request/response API over TCP (UTF-8 request path, NUL-terminated; JSON reply), e.g. `ping` → `"VIIPER"`, `bus/create` → `busId`, `bus/<id>/add {"type":"keyboard"}` → `devId`.
   - Data: a persistent streaming connection `bus/<id>/<devId>` carries raw keyboard-state packets of the form `[1 byte modifiers][1 byte key count][N bytes HID usage codes]`.
   - VIIPER translates those packets into USB HID transfers on the virtual bus.

3. **usbip-win2 kernel driver**
   - Provides the USB/IP virtual host controller. The keyboard device created through VIIPER appears to Windows as an ordinary USB keyboard — no per-device drivers needed.

```
 user presses Ctrl+K
        |
        v
 viperkey  --poll(GetAsyncKeyState)-->  hotkey latched
        |
        v
 viperkey  --SendState(mod,hid[]) --TCP:3242-->  viiper.exe
        |                                              |
        |                           bus/create + add keyboard
        |                                              v
        |                                    usbip-win2 VHCI bus
        |                                              v
        +------------------>  real USB keyboard seen by the OS
```

## Build from the single source file

`viperkey.cs` compiles directly — no project files, no NuGet. The tray icons are baked into the binary as base64 fallbacks; `icons/icon.ico` and `icons/alert.ico` are still honored if present next to the exe.

### Compile the exe with csc (PowerShell)

Use the .NET Framework csc directly with `/target:winexe` — this produces a **GUI-subsystem** exe with no console window (the `Add-Type -OutputAssembly` route would link it as a console app and pop a terminal on launch).

```powershell
cd D:\path\to\this\repo
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:viperkey.exe `
  /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll `
  viperkey.cs
```

Then run it:

```powershell
.\viperkey.exe
```

Since the exe has no console, nothing prints to the terminal; enable `"debug": true` in `viperkey.json` to get the log lines written to `viperkey.log` next to the exe.

### Load and run the source directly (no exe produced)

The in-memory compile produces no executable entry point, and its `AppDomain` base directory is PowerShell's install folder — not your working directory. So the private static `Main` is invoked via reflection, and `VIPERKEY_DIR` redirects `viperkey.json`/`viperkey.log`/icons to the current folder:

```powershell
cd D:\path\to\this\repo
$env:VIPERKEY_DIR = (Get-Location).Path
$t = Add-Type -Path .\viperkey.cs -PassThru `
  -ReferencedAssemblies System.Web.Extensions,System.Windows.Forms,System.Drawing,System.Management |
  Where-Object FullName -eq 'ViperKey'
$t.GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @())
```

The tool then runs inside the PowerShell session, so:
- Keep that window open — closing it exits the tool.
- `viperkey.json` is looked up in the **PowerShell current directory**. On first run a template is created there.
- It auto-starts `viiper.exe` from `%LOCALAPPDATA%\VIIPER` if not already running.

> Note: `System.Web.Extensions` supplies the built-in `JavaScriptSerializer` used to parse the config. `System.Management` is referenced defensively; the other two are UI (Forms/Drawing).

## Files

| Path | Purpose |
|---|---|
| `viperkey.cs` | Entire application (single file) |
| `ViperKeyLogo.png` | Project logo (shown in the README header) |
| `LICENSE` | GNU GPL v3 (ViperKey's own license) |
| `NOTICE` | Third-party attribution (VIIPER GPL-3.0, usbip-win2 BSD-2-Clause) |
| `icons/icon.ico`, `icons/alert.ico` | Tray icons; also embedded in the binary |
| `viperkey.json` | Per-user config — **not committed** (see `.gitignore`) |

## License

ViperKey is free software under the **GNU GPL v3-or-later** (see `LICENSE`). TL;DR: you may fork, modify, and redistribute it — forks must give credit (keep the copyright notice) **and** must stay free (any distributed modified version must be GPL too, with its source available). Attribution for the external components ViperKey talks to is kept in `NOTICE`.
# ScreenSwitch

[中文](README.md)

Two computers share one monitor, and maybe a game console or two. ScreenSwitch switches the monitor's input from a menu on either computer. Both computers then adjust their own desktops, so windows and the mouse no longer end up on a screen nobody can see.

The two computers can be Windows + Mac, two Windows PCs, or two Macs.

## Why

When a monitor switches to another input, most monitors stay connected to the computer that was showing. That computer still thinks the monitor is there and keeps its desktop extended onto a screen nobody is looking at. New windows, dialogs and the mouse can disappear there. Also, many monitors only accept DDC/CI commands on the active input, so the computer that isn't showing can't switch the monitor back by itself.

ScreenSwitch runs a background program on each computer. It gives you:

- **One-click switching.** From the tray or menu bar you can give the monitor to the other computer, give it to a device (each device has its own name and menu item), or take it back. You can also set hotkeys; none are set by default.
- **Automatic follow.** Each computer reads the monitor's current input over DDC/CI to tell whether the monitor is showing it.
  - When the monitor switches away, the computer mirrors it onto its own other screen. The monitor is not removed, so DDC can still be read.
  - When the monitor comes back, the computer restores the saved arrangement.
  - Switching with the monitor's own buttons is followed too.
- **Direct first, then relay.** To take the monitor back, a computer first sends the switch command itself. If the monitor only listens on the active input, as most do, the computer asks the other one over SSH to switch it.
- **Heads-up before handing over.** Before giving the monitor away, a computer tells the receiver to restore its desktop first, so you never see a flash of the mirrored picture.
- **Two layouts after taking back.** "Extend displays" (use both screens), or "Main screen only" (turn the other screen off).
- **Remembered arrangement.** When you change the arrangement in system settings, it is saved and restored on every take-back.

## Supported setups

Each computer has either only the shared monitor, or the shared monitor plus one screen of its own. That screen can be a desktop's second monitor, a laptop's built-in screen, or a second external monitor on a Mac.

| This computer's screens | When the monitor leaves | When it comes back |
|---|---|---|
| Shared monitor + another screen | Shared monitor mirrors the other screen | Extend again, or shared monitor only |
| Shared monitor only | No layout change | No layout change |

What has been tried on real hardware:

| Setup | Status |
|---|---|
| Windows (shared monitor + second monitor) with a MacBook (lid open), monitor listens on the active input only | ✅ The author's daily setup |
| Windows with only the shared monitor | ⚠️ Automated tests only |
| MacBook with the lid closed | ⚠️ Automated tests only |
| Mac with only the shared monitor (Mac mini, Mac Studio) | ⚠️ Automated tests only |
| Mac whose other screen is an external monitor | ⚠️ Automated tests only |
| Two Macs | ⚠️ Automated tests only (two instances share a fake monitor) |
| Two Windows PCs | ⚠️ Automated tests only (same) |
| Monitor accepts commands on inactive inputs (direct switch) | ⚠️ Automated tests only |
| Windows with the two screens on different GPUs (common on laptops) | ⚠️ Automated tests only |

If your setup is in the "automated tests only" rows, please open an issue and tell us how it went.

**Not supported:**

- three or more computers;
- more than one shared monitor;
- three or more screens on one computer;
- Linux;
- Intel Macs.

## Requirements

- **Shared monitor.** It must support DDC/CI with DDC/CI turned on in its on-screen menu (OSD), and it must switch inputs through VCP 0x60. Some LG models don't accept the standard 0x60 and won't work.
- **Cabling that passes DDC.** Many docks, adapters, DisplayLink devices and KVMs don't pass DDC. Connect the computers to the monitor directly if you can.
- **Windows.** Windows 10 (1809 or later) or Windows 11, 64-bit. The built-in .NET Framework 4.x and OpenSSH are enough; you don't need any development tools.
- **Mac.** Apple silicon with macOS 13 or later. You need Homebrew (for m1ddc and displayplacer) and the Xcode Command Line Tools (to build the menu bar app).
- **Network (optional).**
  - Both computers must be on the same LAN and able to SSH to each other: on Windows that means the OpenSSH server, on a Mac it means Remote Login.
  - It works without the network too. To take the monitor back on the computer that isn't showing, you then press the monitor's button, unless the monitor accepts direct switching.
  - For the other computer to ask a Windows PC to switch, the Windows account must be in the Administrators group.
- **UI language.** Chinese and English, following the system language.

## How it works

```
Computer A: ScreenSwitch  <── SSH ──>  Computer B: ScreenSwitch
     │ input 1                              │ input 2
     └──────────>    shared monitor   <──────┘
                         ▲ inputs 3, 4…
                    consoles and other devices
```

- **Ownership.** A computer reads VCP 0x60, the current input. If the value is the code of its own input, the monitor is showing this computer.
- **Release.** The shared monitor becomes a mirror of this computer's other screen (Windows uses the CCD API, macOS uses displayplacer). The monitor is not removed, so DDC still works and the computer notices when it comes back.
- **Take.** Restore the saved arrangement, or use only the shared monitor.
- **Between the computers**, SSH runs only a few restricted commands on the other side:
  - `request give peer` (give the monitor to me);
  - `request give code <n>` (switch to a device);
  - `request take` (the monitor is about to come to you);
  - `request info` (send your name, input code and devices).

  Each side's key is restricted to a command gate, so it never gets a shell. See [docs/design.md](docs/design.md) (Chinese) for the protocol.

## Install

You can ask your AI assistant to install it by following [AGENTS.md](AGENTS.md). It sets up both computers with commands and stops to tell you whenever you need to do something yourself (press the monitor's buttons, confirm as administrator, turn on Remote Login…).

Installing by hand is simple too; do it once on each computer:

- **Mac** ([mac/README-mac.md](mac/README-mac.md)): run `brew install m1ddc displayplacer`, then run `./install.sh` in the `mac/` folder.
- **Windows** ([README-windows.md](README-windows.md)): run `install.cmd`.

On first start the program opens a setup wizard. Switch the main screen to this computer with the monitor's buttons, choose the main screen, label what each of its inputs is connected to (this computer, the other computer, a device such as a game console, or nothing), then enter the other computer (you can skip this). Change anything later under "Settings…" in the menu.

**Authorize each other (optional).** On the "Other computer" tab of the settings window, copy "This computer's public key" into "Other computer's public key" in the other computer's settings and click Authorize. Do it on both computers, then click "Test connection". Windows asks for administrator confirmation once; on a Mac, also turn on Remote Login in System Settings.

**Input codes** are the numbers the monitor uses for its inputs; the switch command sends one of them. Common values are 15 = DisplayPort 1 (many monitors use it for USB-C too), 16 = DisplayPort 2, 17 = HDMI 1 and 18 = HDMI 2, but monitors differ.
- The program reads the monitor's list of inputs and the one this computer is on.
- You choose which input a console or other device is on in the settings. If it's wrong, the switch won't happen; change it there.

## Usage

The tray or menu bar menu is the same on both computers:

```
Take back main screen
Switch to Switch
──────────────
Extend displays     ✓
Main screen only
──────────────
Settings…
Quit
```

- When the monitor is showing this computer, the first item becomes "Switch to <other computer>" (missing if no other computer is set up). Then there is one "Switch to <device>" item per device.
- The icon shows whether the main screen is on this computer: bright = yes, dimmed = no.
- "Extend displays / Main screen only" is the layout after taking back; it isn't shown when there's only the main screen, or when the other screen is an external monitor (extend is the only option then).
- When something needs your attention (a hotkey is taken, the background program isn't running…), the icon gets a small exclamation mark and a "⚠ …" line appears at the top of the menu; click it to open the right place in the settings.
- No hotkeys by default; record them on the "Hotkeys" tab of the settings.

Remembering the screen arrangement, the log and starting at login are in the settings window.

## Known limitations

- When the monitor is on a console or other device, a monitor that listens only on the active input can't be switched back by software. You have to press its button.
- After switching to an input with no signal, some monitors don't fall back on their own and stay black until you press a button. Turn a console or other device on before switching to it.
- Two monitors of the same model can't be told apart.
- Both systems may turn on Do Not Disturb while the monitor is mirrored: macOS does this when mirroring, and according to Microsoft's docs Windows 11 does it by default when duplicating displays. Notifications may then only show up in the notification center.
- The Mac's "Main screen only" mode turns off the built-in screen with the undocumented `CGSConfigureDisplayEnabled`, which a future macOS may break.
- Proxy tools that take over `.local` names (fake-IP) block SSH to the other computer and need an exception. The platform guides' FAQs explain how.

## Security

- The computers only call each other's `request …` commands over SSH. Each side's key is restricted to the forced command `ssh-gate`, which allows only those commands: no shell, no port forwarding.
- Windows `setup-sshd.ps1`:
  - installs and starts the OpenSSH server;
  - turns off SSH password login for **all accounts** on that PC, so only keys work;
  - limits the port 22 firewall rule to the local subnet on private networks;
  - adds the other computer's key to `administrators_authorized_keys`, keeping the other keys there and making a backup first.

  Make sure you're fine with these changes before running it.
- On a Mac, turn on Remote Login, preferably allowed for the current user only (System Settings → General → Sharing → Remote Login). Authorize (`authorize`) only replaces earlier entries for the same key and keeps the others.
- To read the monitor's list of inputs, the Mac uses macOS's undocumented IOAVService functions (the same ones m1ddc uses). It only sends the DDC/CI Capabilities Request and doesn't change any monitor setting.

## Tested hardware

- Monitor: Titan Army 2750V (P275MV); Windows on DP (16), Mac on USB-C (15), Switch on HDMI 1 (17)
- Windows 11 Pro, NVIDIA RTX 4070 Ti SUPER, second monitor 2560×1440
- MacBook Air (M4)

[docs/hardware-notes.md](docs/hardware-notes.md) (Chinese) covers what was observed on this monitor: DDC only on the active input, garbage readings, and the "auto" input mode.

## Contributing

The most useful contribution is test results from your hardware. Useful details:
- your monitor model and the code each input reports;
- whether DDC reads and writes still work on other inputs after switching away;
- whether the monitor falls back on its own after switching to an input with no signal;
- whether the "automated tests only" setups above work for you.

Issues are welcome.

## License

[MIT](LICENSE)

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
- **Two layouts after taking back.** Extend (use both screens), or use only the shared monitor (turn the other screen off).
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
- **UI language.** Chinese only for now.

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

Install on both computers, each while the shared monitor is showing it. The installer runs an interactive setup. It finds the shared monitor and reads this computer's input code on its own. Then it asks for this computer's name, the other computer's address and your devices.

- **Mac** ([mac/README-mac.md](mac/README-mac.md)): run `brew install m1ddc displayplacer`, then run `./install.sh` in the `mac/` folder.
- **Windows** ([README-windows.md](README-windows.md)): run `install.cmd`, then `%USERPROFILE%\ScreenSwitch\ScreenSwitchCli.exe setup`.

**Authorize each other (optional).** Setup prints this computer's public key and the command to run on the other computer. On a Mac, that command is `~/screenctl/screenctl.sh authorize "<key>"`. On Windows, it is `setup-sshd.ps1 -PeerPubKey "<key>"` in an administrator PowerShell. Both platform guides walk through it.

**Input codes** are the numbers the monitor uses for its inputs; the switch command sends one of them. Common values are 15 = DP 1 (many monitors use it for USB-C too), 16 = DP 2, 17 = HDMI 1 and 18 = HDMI 2, but monitors differ.
- Each computer's own code is read automatically.
- For a console or other device, pick the code from that list by the port it is plugged into.
- If a code turns out wrong, the switch won't happen; fix it with `setup devices`.

## Usage

The tray or menu bar menu:

| State | Menu items |
|---|---|
| Monitor is showing this computer | Give to <other computer>; one "Give to <device>" item per device |
| Monitor is elsewhere | Take back; one item per device |

When this computer has another screen, the menu also offers the layout after taking back and the "remember arrangement" switch. No hotkeys are set by default; the platform guides show how to set them in the config file.

## Known limitations

- When the monitor is on a console or other device, a monitor that listens only on the active input can't be switched back by software. You have to press its button.
- After switching to an input with no signal, some monitors don't fall back on their own and stay black until you press a button. That is why the menu says to turn the device on first.
- Two monitors of the same model can't be told apart.
- Both systems may turn on Do Not Disturb while the monitor is mirrored: macOS does this when mirroring, and according to Microsoft's docs Windows 11 does it by default when duplicating displays. Notifications may then only show up in the notification center.
- The Mac's "shared monitor only" mode turns off the built-in screen with the undocumented `CGSConfigureDisplayEnabled`, which a future macOS may break.
- Proxy tools that take over `.local` names (fake-IP) block SSH to the other computer and need an exception. The platform guides' FAQs explain how.

## Security

- The computers only call each other's `request …` commands over SSH. Each side's key is restricted to the forced command `ssh-gate`, which allows only those commands: no shell, no port forwarding.
- Windows `setup-sshd.ps1`:
  - installs and starts the OpenSSH server;
  - turns off SSH password login for **all accounts** on that PC, so only keys work;
  - limits the port 22 firewall rule to the local subnet on private networks;
  - adds the other computer's key to `administrators_authorized_keys`, keeping the other keys there and making a backup first.

  Make sure you're fine with these changes before running it.
- On a Mac, turn on Remote Login, preferably allowed for the current user only (System Settings → General → Sharing → Remote Login). `authorize` only replaces earlier entries for the same key and keeps the others.

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

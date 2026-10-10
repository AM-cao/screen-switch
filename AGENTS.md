# Installing ScreenSwitch with an AI assistant

This file is for AI coding assistants (Claude Code, Codex, Cursor, ...) asked to install and set up ScreenSwitch for a user. Humans can use the setup wizard instead (it opens on first start).

## What it does

One monitor (the "main screen") is shared by two computers (Windows or Mac, any mix) and optionally game consoles. ScreenSwitch switches the monitor's input over DDC/CI and moves each computer's desktop out of the way when the main screen is elsewhere. The two computers ask each other to switch over SSH, with keys restricted to a forced command (`ssh-gate`).

Requirements: the main screen supports DDC/CI (enabled in its own menu) and the cable passes DDC; Windows 10 1809+ / Windows 11; macOS 13+ on Apple silicon; both computers on the same LAN for the SSH part (optional).

## Ground rules

- **Stop and ask the user** for: switching the monitor's input with its button; any administrator / UAC prompt; macOS System Settings switches (Remote Login, notifications); anything on the other computer.
- Never run `ddc-write`, never change display topology with `layout-*` commands, never disable security settings, never open SSH to public networks, never read or copy the user's private keys.
- Every command below exits 0 on success; on failure it exits 1 and prints a line starting with `error: ` (`detect` may add a list of candidates after it).

## Windows

Program: `%USERPROFILE%\ScreenSwitch\ScreenSwitchCli.exe` (below: `ss`).

1. Install: in the repository folder run `install.cmd`. It builds, installs, adds a Startup shortcut and starts the tray program, which opens the setup wizard window because nothing is configured yet. **Before running any of the steps below, ask the user to close that wizard window** (closing it writes nothing). Do not mix the two: either the user finishes the wizard and you skip to the checks, or the wizard is closed and you run the commands. Check: `ss status` prints JSON.
2. Ask the user to switch the main screen to this computer with the monitor's button. Then `ss detect`.
   - If it fails with several candidates, ask the user which display is the shared one and run `ss detect --main <id>` with the identifier printed for it (the model, or `model#instance` when two displays are the same model).
   - Check: `status.configured` is true, `status.self.code` is not 0.
3. Inputs: `ss ports` lists the monitor's inputs as JSON (`monitorKnown` false means the list could not be read; each entry in `ports` has `code`, `name`, `use` = `self|peer|device|none`, `device`). Ask the user what is connected to each input. Register devices with `ss device add <code> <name>` (code 1-255, decimal; `device rename <code> <name>` and `device remove <code>` also exist).
4. Other computer (optional): `ss peer set --os <windows|mac> --host <host or IP> --user <user> [--name <name>]`. This also sets up this computer's SSH client for that host. `ss peer clear` removes it.
5. Keys: `ss pubkey` prints this computer's key (it is created if missing). Hand it over (see "Hand-over"). To authorise the other computer's key run `ss authorize "<key>"`; without administrator rights it exits 1 and the `error:` line contains the command the user must run in an administrator PowerShell.
6. `ss peer test` (after both sides authorised each other) prints JSON `{"name":...,"code":...,"codeSaved":...}`. Check: exit 0, `code` not 0.
7. `ss autostart on`. Check: `status.autostart` is true and `status.problems` is empty.

Other settings: `ss set owned-layout extend|main-only`, `ss set auto-save on|off`, `ss set direct-take on|off`, `ss set language auto|zh|en`, `ss set hotkey peer <combo>|none`, `ss set hotkey <code> <combo>|none`, `ss set after-take <command>`.

## Mac

Program: `~/screenctl/screenctl.sh` (below: `sc`). Apple silicon, macOS 13+. Run everything in Terminal from the repository folder.

1. Dependencies: `command -v brew` — without Homebrew, **stop** and ask the user to install it (it asks for their login password). Then `brew install m1ddc displayplacer`. If `xcode-select -p` fails, run `xcode-select --install` and **stop** until the user has clicked Install and it has finished.
2. **Stop and ask the user** to: open the MacBook lid (with the lid closed the built-in screen is missing from the display list and detection fails; using the Mac closed later is fine); switch the main screen to this Mac with the monitor's button and make sure DDC/CI is on in the monitor's menu; if this Mac has another screen, set it to extend (not mirror) in System Settings → Displays.
3. Install: `cd mac && ./install.sh`. It builds and starts the daemon and the menu bar app, turns on start at login and prints `status` (`configured` false is expected). The menu bar app opens the setup wizard because nothing is configured yet. **Before running any of the steps below, ask the user to close that wizard window** (closing it writes nothing). Do not mix the two, as on Windows. If macOS asks whether the app may send notifications, **stop** and ask the user to click Allow.
4. `sc detect`.
   - Several candidates (exit 1, one `  <id>  <name>` line each): ask the user which display is the shared one, then `sc detect --main <id>` (the model, or `model#uuid` when two displays are the same model).
   - `error:` about no controllable display: **stop** and read the reason to the user (DDC/CI off in the monitor's menu, a cable or dock that does not pass DDC, the main screen not showing this Mac); run again after it is fixed. `error:` about the lid: ask the user to open it.
   - Check: `status.configured` is true, `status.self.code` is not 0, `status.trayRunning` is true (the daemon picks up the new config within a few seconds).
5. Inputs: `sc ports`, same JSON as on Windows (`monitorKnown` false means the monitor's list could not be read; then confirm with the user using 15 = DisplayPort 1, 16 = DisplayPort 2, 17 = HDMI 1, 18 = HDMI 2). Register devices with `sc device add <code> <name>`; the other computer's input is filled in by `peer test` in step 6.
6. Other computer (optional): `sc peer set --os <windows|mac> --host <host or IP> --user <user> [--name <name>]`, `sc pubkey`, then `sc authorize "<key>"` with the other computer's key. **Stop** and ask the user to turn on System Settings → General → Sharing → Remote Login, with "Allow access for" set to only this user. Check: `nc -z -G 2 127.0.0.1 22` exits 0. After both sides authorised each other: `sc peer test` prints `{"name":...,"code":...,"codeSaved":...}`. `Permission denied` in the error means the other side has not authorised this Mac's key yet; a timeout means a wrong address, a sleeping computer or SSH not running.
   For the hand-over block: address `echo "$(scutil --get LocalHostName).local"` (or the IP from `ipconfig getifaddr en0`), user `id -un`.
7. `sc autostart on` (install already turned it on). Check: `status.autostart` is true and `status.problems` is empty. Typical problems: the background program is not running → `launchctl kickstart -k gui/$(id -u)/com.user.screenctl` (log: `~/screenctl/screenctl.log`); the menu bar app is not running → `open ~/Applications/ScreenSwitch.app`; notifications are off → **stop** and ask the user to allow them in System Settings → Notifications; a shortcut could not be registered → pick another one or `sc set hotkey ... none`.

Other settings: `sc set owned-layout extend|main-only` (main-only turns off the built-in screen while the Mac has the main screen), `sc set auto-save on|off`, `sc set direct-take on|off`, `sc set hotkey peer <combo>|none`, `sc set hotkey <code> <combo>|none` (combos like `ctrl+opt+1`; `Ctrl+Alt+1` is accepted; it needs ctrl, opt or cmd). There is no `after-take` on the Mac. The question-and-answer `sc setup` also still works for people.

On the Mac, the equivalents of `ddc-write` and `layout-*` are `m1ddc ... set`, `displayplacer` and `~/screenctl/bin/display-helper`: never run them yourself.

## Hand-over between the two computers

Each computer usually has its own assistant. When one side is done, print this block and ask the user to paste it to the assistant on the other computer:

```
ScreenSwitch hand-over
name: <this computer's name>
system: <windows|mac>
address: <host name or IP>
user: <user name>
input code: <status.self.code>
public key: <output of pubkey>
```

## Final check

Ask the user to watch the main screen and click "Switch to <other computer>" in the menu on each side once, then read `status` on both sides (`problems` should be empty).

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

1. Install: in the repository folder run `install.cmd`. It builds, installs, adds a Startup shortcut and starts the tray program, which opens the setup wizard window because nothing is configured yet. Tell the user they can close the wizard (closing it writes nothing) or use it instead of the steps below. Check: `ss status` prints JSON.
2. Ask the user to switch the main screen to this computer with the monitor's button. Then `ss detect`.
   - If it fails with several candidates, ask the user which display is the shared one and run `ss detect --main <model>`.
   - Check: `status.configured` is true, `status.self.code` is not 0.
3. Inputs: `ss ports` lists the monitor's inputs as JSON (`monitorKnown` false means the list could not be read; each entry in `ports` has `code`, `name`, `use` = `self|peer|device|none`, `device`). Ask the user what is connected to each input. Register devices with `ss device add <code> <name>` (code 1-255, decimal; `device rename <code> <name>` and `device remove <code>` also exist).
4. Other computer (optional): `ss peer set --os <windows|mac> --host <host or IP> --user <user> [--name <name>]`. This also sets up this computer's SSH client for that host. `ss peer clear` removes it.
5. Keys: `ss pubkey` prints this computer's key (it is created if missing). Hand it over (see "Hand-over"). To authorise the other computer's key run `ss authorize "<key>"`; without administrator rights it exits 1 and the `error:` line contains the command the user must run in an administrator PowerShell.
6. `ss peer test` (after both sides authorised each other) prints JSON `{"name":...,"code":...,"codeSaved":...}`. Check: exit 0, `code` not 0.
7. `ss autostart on`. Check: `status.autostart` is true and `status.problems` is empty.

Other settings: `ss set owned-layout extend|main-only`, `ss set auto-save on|off`, `ss set direct-take on|off`, `ss set hotkey peer <combo>|none`, `ss set hotkey <code> <combo>|none`, `ss set after-take <command>`.

## Mac

(The Mac side adds its steps here: `~/screenctl/screenctl.sh` with the same command names.)

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

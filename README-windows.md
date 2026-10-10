# ScreenSwitch Windows 说明

## 1. 这是什么

Windows 端是一个常驻的托盘程序（`ScreenSwitch.exe`）和一个命令行程序（`ScreenSwitchCli.exe`）。托盘程序通过 DDC/CI 读取主屏（共享的那块显示器）当前的输入源，判断主屏是不是正在显示这台电脑：被切走时把主屏变成本机另一块屏的克隆，切回来时恢复原来的排列。对方电脑可以是 Mac，也可以是另一台 Windows，两台之间通过 SSH 互相代发切换请求。

支持的屏幕组合：

| 这台电脑的屏 | 主屏被切走时 | 主屏切回来时 |
|---|---|---|
| 主屏 + 另一块屏（台式机的副屏，或笔记本自带屏） | 主屏变成另一块屏的克隆 | 恢复扩展，或只用主屏（关闭另一块屏） |
| 只有主屏 | 不调整布局 | 不调整布局 |

一台电脑上三块及以上的屏暂不支持。

哪些情况在真机上测过：

| 情况 | 状态 |
|---|---|
| Windows（主屏 + 副屏，同一块显卡）配 MacBook，显示器只认当前输入口 | 作者日常使用的组合 |
| 只有主屏的 Windows | 只有自动测试 |
| 两台 Windows 互相切换 | 只有自动测试（两个实例共用一块假显示器） |
| 显示器在非当前输入口上也接受 DDC 指令（直接切换） | 只有自动测试 |
| 两块屏分属两块显卡（常见于笔记本：自带屏在核显、外接屏在独显） | 只有自动测试；让出时的克隆可能要退回 `DisplaySwitch /clone`，慢几秒 |

## 2. 适用条件

- Windows 10（1809 或更新）或 Windows 11，64 位，系统自带 .NET Framework 4.x 与 OpenSSH 客户端；编译用系统自带的 `csc.exe`，不用装开发工具。
- 主屏在显示器菜单（OSD）里打开了 DDC/CI，线路能传 DDC（很多扩展坞、转接头、KVM 不传），能通过 VCP 0x60 切换输入源。
- 让对方能请这台电脑代为切换（对方 → Windows 的 SSH）时，当前账户要在 Administrators 组。

## 3. 安装

1. 把主屏切到这台电脑（主屏正显示 Windows 桌面）；有另一块屏时设为扩展排列。在仓库目录运行：
   ```batch
   cd /d <仓库目录>
   .\install.cmd
   ```
   程序装到 `%USERPROFILE%\ScreenSwitch`（即 `C:\Users\<用户名>\ScreenSwitch`），并在"启动"文件夹里加快捷方式，登录后自动运行。已有的 `config.json` 不会被覆盖。

2. 设置：**第一次启动时会自动打开设置向导**（见第 5 节"设置窗口与向导"），按页走完即可；中途关掉不写配置，下次启动再打开。不想用图形界面，可以运行命令行的一问一答：
   ```batch
   %USERPROFILE%\ScreenSwitch\ScreenSwitchCli.exe setup
   ```
   也可以让 AI 助手按仓库根目录的 [AGENTS.md](AGENTS.md) 安装（用第 7 节的命令，不用一问一答）。

   **命令行设置是一问一答的**，直接回车取默认值，写好 `config.json` 后自动重启托盘程序：

   1. 主屏：列出接着的显示器和各自能不能读到 DDC。只有一块能读到时自动选中；另一块屏也应答 DDC 时让你输入序号选（重新运行时回车就是原来那块）。都读不到时会说明原因并停下（显示器菜单里没开 DDC/CI；线路不传 DDC；或者主屏此刻没在显示这台电脑）。
   2. 这台电脑接在主屏的哪个输入口：程序自己读出来。
   3. 这台电脑叫什么（对方的菜单和提示里用），默认是计算机名。
   4. 屏幕组合：只有主屏，还是另有一块屏。
   5. 对方电脑（可以跳过）：名字、是 Windows 还是 Mac、主机名或 IP、用户名。随后配好本机的 SSH 客户端，打印本机的公钥和要在对方电脑上执行的命令，等你在对方电脑上执行完再继续。能连上对方时，自动问到对方接在哪个输入口，以及它已经配好的设备（对方有设备时直接采用）。
   6. 其他设备（游戏机等，可以跳过）：每个设备一个名字和它所接输入口的编码。程序会列出主屏支持的编码供你对照；已有的设备可以改名（回车不改）或删除（输入 `-`）。

   以后要改：在托盘菜单的"设置…"里改；或重新运行 `ScreenSwitchCli.exe setup`；只增删、改名设备用 `ScreenSwitchCli.exe setup devices`。要用 `ScreenSwitchCli.exe`（命令行程序）运行，不是 `ScreenSwitch.exe`。

   屏幕的块数按系统里"已连接"的显示器算，虚拟显示器（某些远程桌面、投屏软件装的）也算一块，超过两块时设置会停下。

3. 首次启动：有另一块屏时，主屏显示这台电脑且为扩展排列，程序会自动保存当前布局（之后拿回主屏都恢复成这样）。

**输入口的编码**是显示器给每个输入口编的数字，软件切换时发的就是它。常见的是 15 = DP 1（不少显示器的 USB-C 也用它）、16 = DP 2、17 = HDMI 1、18 = HDMI 2，但各家显示器不完全一样。每台电脑的编码由程序在主屏显示它时自己读出；游戏机的编码按接口对照上面的数字填，填错了切不过去，在设置窗口的"输入口"页（或 `setup devices`）改掉重来即可。

### 从旧版本升级

旧的 `config.json` 不用改，直接重新运行 `install.cmd` 即可，行为不变：菜单仍是"切给 Mac"；原来 `codes.other` 里的编码变成"设备 1""设备 2"，各有一个菜单项，可以在设置窗口的"输入口"页（或 `ScreenSwitchCli.exe setup devices`）改名或删除。有两处不同：

- **快捷键默认不再注册**。旧配置里的 `hotkeys` 被忽略；要继续用，在"设置 → 快捷键"里重新设。
- "切给 Other"原来会依次尝试几个编码，现在每个设备只切到自己的编码。

两端不必同时升级：一端是旧版本时，新的一端被拒绝后会改用旧写法重试；只是按设备切换和自动询问对方的编码要两端都升级后才有。

## 4. 两台电脑互相授权（SSH）

不配 SSH 也能用：切走、自动跟随都不受影响，只是主屏不在这台电脑上时，拿回要靠显示器按钮（显示器在非当前输入口也接受指令的除外，见第 5 节）。

### 4.1 让这台电脑能请对方切换

`setup` 的第 5 步已经做了这件事。也可以单独运行（**普通、非管理员的 PowerShell**；两个脚本在仓库目录里，`install.cmd` 也会复制一份到安装目录）：

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-client.ps1 -PeerUser <对方的用户名> -PeerHost <对方的主机名或 IP>
```

对方是 Mac 时，用户名是在 Mac 终端里运行 `whoami` 的结果，主机名是 `scutil --get LocalHostName` 的结果加 `.local`。旧的参数名 `-MacUser` `-MacAlias` `-MacIP` 仍然可用。

- 没有 `~/.ssh/id_ed25519` 时自动生成，并打印公钥
- 在 `~/.ssh/config` 写入对方的 Host 块：只走 IPv4（`AddressFamily inet`），首次连接自动记录主机指纹，超时 3 秒；改写前先备份为 `~/.ssh/config.screenswitch.bak`
- 写完会检查一次名字解析，解析失败或得到代理假地址（198.18.x.x）时给出警告
- 重复运行结果一致

把打印出来的公钥交给对方：

- 对方是 Mac：在 Mac 上运行 `~/screenctl/screenctl.sh authorize "<这台电脑的公钥>"`
- 对方是 Windows：在对方的管理员 PowerShell 里运行 `setup-sshd.ps1 -PeerPubKey "<这台电脑的公钥>"`

拿回主屏时，程序会先解析对方的地址（最多 3 秒）：解析成功就用它，并记到 `peer-address.txt`；解析失败就用上次成功的地址。mDNS 在 Wi-Fi 上偶尔会一段时间内没有回应，有了这个兜底，拿回主屏不受影响；对方换了 IP 后也能自动跟上。如果不方便让代理放行 `.local`，可以先在路由器上给对方做 DHCP 固定分配，再加上 `-PeerIP <对方的 IP>` 把名字固定到这个 IP。

验证（应返回退出码 1、输出 `rejected`，说明能连通、认证成功、被对方的 ssh-gate 按规定拒绝，没有副作用）：

```powershell
ssh -4 -o BatchMode=yes <对方的用户名>@<对方的主机名> status; $LASTEXITCODE
```

### 4.2 让对方能请这台电脑切换

1. 先确认家里的网络是**专用网络**（设置 → 网络和 Internet → 以太网/WLAN → 网络配置文件类型），或在管理员 PowerShell 里运行：
   ```powershell
   Set-NetConnectionProfile -Name '<网络名>' -NetworkCategory Private
   ```
   只影响这台电脑对该网络的信任级别，不影响家里其他设备。
2. 在**管理员 PowerShell** 里运行（首次需要对方的公钥，之后可省略 `-PeerPubKey`；旧的参数名 `-MacPubKey` 仍然可用）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup-sshd.ps1 -PeerPubKey "ssh-ed25519 AAAA... comment"
   ```
   脚本会：
   - 安装并启动 OpenSSH 服务器，设为自动启动
   - 在 `C:\ProgramData\ssh\sshd_config` 里设 `PasswordAuthentication no` 和 `KbdInteractiveAuthentication no`，这台电脑的 SSH 从此只允许密钥登录（对所有账户生效）；改前备份，用 `sshd -t` 校验，失败自动还原
   - 把 22 端口的防火墙规则改为只对专用网络开放，并且只接受同一网段（`-RemoteAddress LocalSubnet`）
   - 把对方的公钥加进 `administrators_authorized_keys`，限定为 `ScreenSwitchCli.exe ssh-gate` 强制命令。这个文件对所有管理员账户生效，里面原有的其他公钥会保留，只替换以前写入的 ScreenSwitch 那一行；第一次修改前备份为 `administrators_authorized_keys.screenswitch.bak`
   - 没有任何专用网络时直接退出、不做任何修改
   - 重复运行结果一致；最后输出 `SETUP OK`，日志在 `%USERPROFILE%\ScreenSwitch\setup-sshd.log`

对方的公钥被限定在 `ssh-gate` 上：只能发 `request give peer`、`request give code <n>`、`request take`、`request info` 这几条（以及旧写法 `request give mac`、`request give other`），其他一律输出 `rejected`、退出码 1，拿不到 shell，也不能做端口转发。

在对方电脑上验证：用它的 SSH 执行 `<这台电脑上程序的路径> request info`，应打印一行以 `proto=3` 开头的内容。

## 5. 功能

**托盘菜单**（右键托盘图标）：

```
拿回主屏            （主屏在本机时换成"切给 <对方的名字>"）
切给 Switch         （每个设备一项）
──────────────
扩展屏幕          ✓
只用主屏
──────────────
设置…
退出
```

- 没有状态行：主屏在不在本机看托盘图标（亮 / 灰）。
- 第一项随状态变：在本机时是 `切给 <对方的名字>`（没配置对方就没有这一项），不在时是 `拿回主屏`（一直都有）。之后每个已登记的设备一项 `切给 <设备名>`；切换进行中这些项变灰。
- `扩展屏幕` / `只用主屏` 二选一，当前选中的打勾（对号在文字右边）；主屏在本机时立刻生效，不在时下次拿回生效。只有主屏时这两项不显示。
- 快捷键只有在"设置 → 快捷键"里设了才显示在对应菜单项右边。
- 有需要处理的问题（快捷键被占用、托盘程序没在运行等）时，图标右下角出现感叹号，菜单最上面多一行说明，点它打开设置窗口的对应位置。

**设置窗口与向导**

菜单里的"设置…"打开设置窗口。所有改动立刻生效，没有"保存 / 取消"。五个标签页：

| 标签页 | 内容 |
|---|---|
| 输入口 | 当前主屏和"重新检测"；主屏的输入口表（每个口接的是本机、对方、设备还是没接），"添加输入口"；设备的增删、改名在这里做 |
| 对方电脑 | 名字、系统、地址、用户名；本机的公钥（只显示类型和圆点）和"复制"（复制完整的公钥）；"对方公钥"和"授权"（需要管理员，弹出一次系统确认）；"测试连接"；"不连对方电脑" |
| 屏幕 | 屏幕组合和"重新检测"；拿回主屏后"扩展屏幕 / 只用主屏"（与菜单同一设置）；"自动记住屏幕排列"；"现在记住当前排列" |
| 快捷键 | "切给对方 / 拿回主屏"和每个设备各一行，默认全空；点输入框后按下组合键即录下，"清除"删掉；注册失败时在该行显示原因 |
| 通用 | "开机自动运行"；"界面语言"（跟随系统 / 中文 / English，立即生效）；"重新运行设置向导"；"打开日志"；版本号；折叠的"高级"：先直接切换（`directTake`）、拿回主屏后执行的命令（`afterTake`）、打开配置文件所在的文件夹 |

首次启动向导（主屏还没设置时自动打开）分五页：欢迎 → 主屏（列出显示器和能否用 DDC 控制，自动识别本机所接的口和另一块屏）→ 输入口 → 对方电脑（可以跳过；填信息、互相授权、测试连接）→ 完成（"开机自动运行"，默认勾上）。点"完成"才写入配置。

界面文字（菜单、设置窗口、向导、通知、命令的提示）的语言在"设置 → 通用 → 界面语言"里选，也可以用 `set language auto|zh|en`：默认"跟随系统"（中文系统显示中文，其他显示英文），改了立即生效，不用重启。日志、配置键名、JSON 字段名不翻译。

**自动跟随**

- 占有主屏时每 2 秒读取一次 DDC；读数与当前归属不一致时，0.5 秒后立即复核，连续 2 次一致才切换。未占有时每 0.5 秒读取一次，主屏一切回来就尽快拿回。
- 判断归属：读 VCP 0x60 成功，并且读到的是这台电脑所接输入口的编码，才算主屏在本机。所以在所有输入口都回应 DDC 的显示器上也能正确判断。（配置里没有本机编码时退回"读取成功即占有"，日志里会提醒运行设置。）
- 用显示器按钮切换也会跟随，约 1 到 2 秒。

**切给对方**

- 先通过 SSH 通知对方 `request take`，对方立即恢复它的布局，显示器切过去时直接是对方的桌面，不先出现镜像。通知只是尽力而为：对方不在线、版本不支持、超时，都照常切换，最多多等 5 秒。
- 不知道对方接在哪个输入口时，先向对方询问（`request info`）并记进 `config.json`；问不到就提示运行设置。

**拿回主屏**（主屏不在本机时）

1. 先直接向主屏写本机的编码。有的显示器在非当前输入口上也接受指令，这一步就切回来了。
2. 配置了对方：请对方代发（`request give peer`）。对方接受后，本机立即恢复布局，随后 8 秒内读不到主屏也不让出。
3. 对方没有代发（没配置、不在线、主屏不在它那里）：最多等 3 秒看主屏是否已经切回来；没有就提示"请用显示器上的按钮切换输入源"。

在只认当前输入口的显示器上，第 1 步没有效果，行为与以前一样。程序会记住这一点（连续 3 次等不到、又从没成功过，之后就不再白等 3 秒），重新运行 `setup` 时重新开始试。不想先直接切，把 `config.json` 里的 `directTake` 设为 `false`。

**切给设备**（游戏机等）

- 主屏在本机时：只切到这个设备的编码，让出，再观察 15 秒；主屏自己跳回来（设备没开机）就拿回，并通知"没有检测到 <设备> 的信号"。
- 主屏不在本机时：先直接写该设备的编码，再请对方代发（`request give code <n>`）；都不行就提示按显示器按钮。
- 主屏切到游戏机之后，多数显示器不再接受任何电脑的指令，要用显示器上的按钮切回电脑。

**拿回后的布局**（有另一块屏时；托盘菜单和"设置 → 屏幕"，二选一，选择会记住）

- **双屏扩展**：恢复保存的排列（默认）
- **仅主屏（关闭副屏）**：只点亮主屏
- 占有主屏时切换立即生效；未占有时，下次拿回主屏时生效
- 保护措施：主动切走前先让另一块屏亮起来（直接进入克隆），亮不起来就取消切换并通知；被动让出、启动时对账、退出程序、注销或关机时，都会确保另一块屏处于启用状态
- 自愈：布局切换失败（例如 UAC 提示框弹出期间 Windows 拒绝一切拓扑修改）后每 10 秒自动重试，同一段失败只通知一次；缺屏（显示器被拔掉或关机）时暂不重试，等显示器回来

**自动记住屏幕排列**（有另一块屏时；"设置 → 屏幕"里的开关，默认开）

- 占有主屏、处于双屏扩展且不在切换过程中时，约每 10 秒比对一次当前排列和已保存的布局；发现不同且连续两次比对一致时自动保存，并通知"已自动记住新的屏幕排列"
- "设置 → 屏幕"里的"现在记住当前排列"可随时手动保存

**只有主屏**（`layout` 为 `none`）

主屏被切走时这台电脑没有别的屏，窗口无处可放，所以不做任何布局操作：拿回、让出只是更新归属状态和托盘图标，切换、跟随、代发照常。

## 6. 配置文件

`%USERPROFILE%\ScreenSwitch\config.json`，由向导、设置窗口或 `setup` 写好，一般不用手改。手改后要退出托盘程序再重新运行 `ScreenSwitch.exe`。

```json
{
  "mainEdid": "LHC9059",
  "layout": "second",
  "self": { "name": "台式机", "code": 16 },
  "peer": {
    "name": "MacBook", "os": "mac", "host": "my-mac.local", "user": "alice",
    "code": 15, "program": "", "hotkey": "",
    "keyPath": "", "timeoutSec": 3
  },
  "devices": [
    { "name": "Switch", "code": 17, "hotkey": "" }
  ],
  "directTake": true,
  "uiLanguage": "auto",
  "pollMs": 2000, "debounce": 2, "sshExe": "ssh.exe",
  "release": { "width": 0, "height": 0 },
  "afterTake": { "command": "", "delayMs": 3000 }
}
```

| 字段 | 含义 |
|---|---|
| `mainEdid` | 主屏的标识：主屏设备路径里的一段（厂商码 + 产品码，如 `LHC9059`）。`ScreenSwitchCli.exe diag` 可以看到每块显示器的设备路径 |
| `layout` | `second` = 另有一块屏；`none` = 只有主屏 |
| `self.name` / `self.code` | 这台电脑的名字，和它所接输入口的编码（0 = 未知） |
| `peer.name` / `peer.os` | 对方的名字（菜单和提示里用）和系统（`mac` 或 `windows`） |
| `peer.host` / `peer.user` | 对方的主机名（或 IP）和用户名。任一为空 = 没有配置对方，不发 SSH |
| `peer.code` | 对方所接输入口的编码。0 = 未知，需要时自动向对方询问并写回 |
| `peer.program` | 对方电脑上要调用的程序。留空时按 `peer.os` 推导：Mac 为 `~/screenctl/screenctl.sh`，Windows 为 `C:\Users\<peer.user>\ScreenSwitch\ScreenSwitch.exe`（对方的用户目录不是这个名字时要填） |
| `peer.hotkey` | 全局快捷键，如 `Ctrl+Alt+1`：主屏在本机时切给对方，不在时拿回。默认空 = 不注册 |
| `devices` | 其他设备：名字、编码、各自的快捷键（默认空） |
| `directTake` | 拿回、切给设备时先直接向主屏写编码（默认 `true`） |
| `uiLanguage` | 界面语言：`auto`（跟随系统，默认）、`zh`、`en`；缺失或不认识的值按 `auto` |
| `release.width` / `release.height` | 让出时克隆用的分辨率。0 = 用另一块屏自己的分辨率 |
| `afterTake` | 拿回主屏后执行的命令，见常见问题 |

快捷键写法：`Ctrl`、`Alt`、`Shift`、`Win` 加一个字母、数字或 `F1`–`F24`，用 `+` 连接。

安装目录里的其他文件：

| 文件 | 说明 |
|---|---|
| `prefs.json` | 菜单和设置窗口里的选择（拿回后的布局、自动记住屏幕排列），程序自动维护 |
| `layout-owned.bin` | 双屏扩展布局快照 |
| `peer-address.txt` | 上次成功连上对方时的地址，解析失败时兜底用 |
| `direct-take.txt` | 直接切换在这块显示器上有没有成功过 |
| `state.txt` / `cmd.txt` | 跨机协议的状态文件与命令文件 |
| `screenswitch.log` | 日志 |

## 7. 命令行

`ScreenSwitchCli.exe <命令>`。`status` 到 `autostart` 这几条是给 AI 助手和脚本用的：成功退出码 0，失败退出码 1 并输出一行 `error: <原因>`；它们不经过 SSH 放行，对方电脑不能调用。

| 命令 | 说明 |
|------|------|
| `setup` | 一问一答的设置，写 `config.json` 并重启托盘程序 |
| `setup devices` | 只增删、改名设备 |
| `status` | 以 JSON 打印当前状态：是否设置过、主屏、本机名字和编码、屏幕组合、对方、设备、托盘程序是否在运行、开机自启、需要处理的问题（`problems`） |
| `detect [--main <型号>]` | 检测主屏、本机编码、屏幕组合并写入配置。几块屏都能控制时退出码为 1 并列出候选，用 `--main` 指定 |
| `ports` | 以 JSON 打印主屏的输入口清单和每个口登记的内容（`monitorKnown` 为 false = 读不到清单） |
| `peer set --os <windows\|mac> --host <地址> --user <用户名> [--name <名字>]` | 设置对方电脑，并配好本机的 SSH 客户端 |
| `peer clear` | 清除对方电脑 |
| `peer test` | 向对方取名字和编码（`request info`），JSON 输出；取到编码时写入配置 |
| `pubkey` | 打印本机公钥，没有就先生成 |
| `authorize "<对方公钥>"` | 授权对方。没有管理员权限时不动手，输出要在管理员 PowerShell 里执行的命令 |
| `device add <编码> <名字>` / `device rename <编码> <名字>` / `device remove <编码>` | 设备增删改名（编码为十进制 1–255） |
| `set <项> <值>` | `owned-layout extend\|main-only`、`auto-save on\|off`、`direct-take on\|off`、`language auto\|zh\|en`、`hotkey peer <组合>\|none`、`hotkey <编码> <组合>\|none`、`after-take <命令>` |
| `autostart on\|off` | 开机自动运行 |
| `ui-dump <目录> [zh\|en]` | 把菜单、设置窗口每一页、向导每一页画成 PNG 保存，用来不安装就审阅界面 |
| `diag` | 列出所有显示器的设备路径与状态，再打印当前配置的主屏标识、主屏定位结果与 VCP 0x60 读取结果 |
| `ddc-read` | 读主屏当前的输入口编码（十六进制） |
| `ddc-write <十六进制>` | 写 VCP 0x60。**警告**：写入别的编码会把显示器切走，只有正在显示的设备能用软件切回，否则要按显示器按钮 |
| `layout-show` / `layout-mode` | 只读：当前拓扑（Extend / Clone / MainOnly / Missing）、活动路径、排列签名 |
| `layout-save` | 保存当前布局快照 |
| `layout-release` / `layout-take` / `layout-take-main` | 让出（克隆）/ 拿回（扩展）/ 拿回（仅主屏） |
| `request give peer` | 请托盘程序把主屏切给对方（供对方通过 SSH 调用） |
| `request give code <n>` | 请托盘程序把主屏切到编码为 n（十进制）的设备，n 要在本机的设备列表里 |
| `request give self` | 重新应用本机的布局（只在本机用，不对 SSH 放行） |
| `request take` | 对方即将把主屏切给本机：未占有时托盘程序随即恢复布局并进入 8 秒宽限期 |
| `request info` | 打印一行本机的配置：`proto=3 os=windows code=16 name=<Base64> devices=17:<Base64>,…`（名字用 Base64 传，避免两端编码不同） |

`request give …` 的输出与退出码：`accepted` 0；`not owned` 2（主屏不在本机，无法代切）；`daemon not running` 3（托盘程序没在运行）；`unknown device` 或目标不对 1。旧写法 `request give mac`（= `peer`）、`request give windows`（= `self`）、`request give other`（= 第一个设备）照常接受。

## 8. 常见问题

**Q: 启动后托盘提示"还没有设置"**

A: 向导没走完。从托盘菜单打开"设置…"重新运行向导，或运行 `ScreenSwitchCli.exe setup`。

**Q: 读不到主屏，或者不会自动跟随**

A: 确认显示器菜单里打开了 DDC/CI，电脑直连显示器；运行 `ScreenSwitchCli.exe ddc-read` 测试读取，`ScreenSwitchCli.exe diag` 看 `mainEdid` 是否与主屏的设备路径对得上。换了显示器后重新运行 `setup`。

**Q: 想用快捷键**

A: 在"设置 → 快捷键"里设：点输入框，按下想用的组合键即可（默认一个都没有）。被别的程序占用时注册会失败，那一行会显示原因，换一个组合。

**Q: 克隆时分辨率不合适**

A: `release` 为 0 时用另一块屏自己的分辨率。要指定别的分辨率，在 `config.json` 里填 `"release": { "width": 1920, "height": 1080 }`，重启程序。

**Q: 拿回主屏后，键鼠共享软件（如 Deskflow）的鼠标过不到另一台电脑**

A: 显示器布局变化后，有的常驻程序要重启才恢复。在 `config.json` 里配置拿回主屏后自动执行的命令：
```json
"afterTake": {
  "command": "schtasks /run /tn ScreenSwitch-DeskflowCoreRestart",
  "delayMs": 3000
}
```
`command` 为空（默认）则不执行；布局恢复成功后等 `delayMs` 毫秒执行一次，期间多次拿回只执行一次，结果记在 `screenswitch.log`（`afterTake: ... -> 退出码`）。程序不以管理员身份运行，需要管理员权限的操作（如结束以 SYSTEM 运行的 `deskflow-core`）请先建一个允许普通用户运行的计划任务，再用 `schtasks /run` 触发。

**Q: `request` 命令返回退出码 3**

A: 托盘程序（ScreenSwitch.exe）没在运行。查看 `screenswitch.log` 了解退出原因，重新运行 `ScreenSwitch.exe`。

**Q: 选了"仅主屏"，切给对方时另一块屏会先亮一下**

A: 这是保护措施：主屏切走后，这台电脑只剩另一块屏可用，所以切走前先把它打开（直接进入克隆）。打不开（例如没接好）时切换会被取消并通知"无法恢复副屏，已取消切换"，主屏留在本机。

**Q: 我在"设置 → 显示"里改了排列，拿回主屏后又变回去了**

A: 确认"设置 → 屏幕"里"自动记住屏幕排列"是勾选的，并在双屏扩展状态下改完后等 10 到 20 秒，看到"已自动记住新的屏幕排列"再切换。也可以直接点"现在记住当前排列"。

**Q: 拔掉（或加上）了另一块屏**

A: 重新运行 `ScreenSwitchCli.exe setup`，它会把 `layout` 改成 `none`（或 `second`）。

**Q: 连对方时报 `Connection closed by 198.18.x.x`、`Could not resolve hostname` 或 255**

A: `.local` 名字被本机代理（Mihomo / Clash 等 TUN 模式）的 fake-IP 接管了，SSH 连到了代理而不是对方。首选在代理的 fake-ip-filter 里加上 `+.local`，让 mDNS 名称正常解析。以 Clash Verge 为例：配置由全局扩展脚本生成时，在脚本里生成 `dns['fake-ip-filter']` 的地方加上 `"+.local"`，然后重启 Clash Verge。`.local` 只用于局域网设备，这样改不影响任何网站的分流线路。改不了代理时，在路由器上给对方做 DHCP 固定分配，然后用 `setup-client.ps1 … -PeerIP <IP>` 把名字固定到这个 IP。

`AddressFamily inet` 让 ssh 只走 IPv4：`.local` 名字通常解析出多个 IPv6 地址，对端不在线时会逐个等满超时。`StrictHostKeyChecking accept-new` 让首次连接在 BatchMode 下自动记录主机指纹。

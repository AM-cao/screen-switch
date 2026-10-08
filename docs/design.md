# 屏幕快速切换 设计文档

协议版本：v3。本文是 Windows 端与 Mac 端共同遵守的规范。两端分别实现；改动第 5 节的跨机协议时，先改本文，再同时改两端的代码。

## 1. 背景与范围

### 1.1 设备组合

| 设备 | 说明 |
|---|---|
| 主屏 | 共享的显示器，支持 DDC/CI，能用 VCP 0x60 切换输入源 |
| 两台电脑 | Windows + Mac、两台 Windows 或两台 Mac，各接主屏的一个输入口 |
| 另一块屏（可选） | 每台电脑除主屏外可以再有一块自己的屏：台式机的副屏、笔记本自带屏，或 Mac 的另一块外接屏 |
| 设备 | 游戏机等，接主屏的其他输入口，不会说 DDC；每个有名字和输入口编码，数量不限 |

作者实际使用的设备与实测数据见 [hardware-notes.md](hardware-notes.md)。

不做：三台及以上电脑；一台电脑上三块及以上的屏；多块共享显示器；没有 SSH 的替代通道；Linux；键鼠共享、音频切换；从设备一侧发起切换。

### 1.2 痛点

主屏切换输入源后，不显示的那台电脑仍认为主屏连着，桌面扩展到一块看不见的屏幕上，窗口和鼠标会跑丢。

### 1.3 名词

| 名词 | 含义 |
|---|---|
| 本机 / 对方 | 两台电脑互称。协议与界面不出现 windows、mac |
| 编码 | 主屏 VCP 0x60 的取值，每个输入口一个 |
| 占有 | 主屏正在显示本机 |
| 让出 / 拿回 | 主屏离开 / 回到本机时本机做的布局调整 |

### 1.4 目标状态

| 主屏显示 | 占有的那台电脑 | 另一台电脑 |
|---|---|---|
| 某台电脑 | 主屏 + 另一块屏扩展，或仅主屏（"拿回后的布局"）；只有主屏时不调整 | 有另一块屏：主屏是它的镜像；只有主屏：不调整 |
| 某个设备 | — | 两台都按"另一台电脑"处理 |

任意一台电脑上都能一键进入任意可行的状态。任意一台电脑关机或睡眠时，另一台单独运行不受影响。

## 2. 硬件前提

在作者的显示器上实测得到（详见 hardware-notes.md）：

1. 显示器支持 DDC/CI，VCP 0x60 可读写输入源。
2. **多数显示器只在当前活跃的输入口上响应 DDC/CI**，其他口的读写被忽略。
3. 切走输入源后，电脑仍枚举着主屏，链路不断开，操作系统收不到任何事件。

推论：

- 能读到 VCP 0x60 且值是本机的编码，等价于"主屏在本机"，可以作为轮询信号。
- 在只认活跃口的显示器上，只有占有者能把主屏切走；另一台要拿回，只能请占有者代发。主屏停在设备上时只能按显示器按钮。
- 有的显示器在非活跃口上也接受指令。这时未占有的一方可以直接切换（见 6.2）。

## 3. 目标与非目标

目标：两台电脑各一个常驻小程序，提供切换入口并自动跟随主屏归属；不依赖付费软件；单机可独立工作，双机在线时通过局域网代发指令；装的时候用命令行问答完成设置，用户不必手改配置。

除 Mac 的"仅大屏"模式用 CoreGraphics 未公开的 `CGSConfigureDisplayEnabled` 关闭笔记本屏外，不直接调用未公开 API（Mac 的 DDC 工具 m1ddc 本身依赖 Apple 芯片上未公开的 IOAVService）。

## 4. 配置

两端各自保存：本机的名字和编码、对方的名字/系统/地址/程序路径/编码、设备列表、布局模式。

### 4.1 Windows（`config.json`）

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
    { "name": "Switch", "code": 17, "hotkey": "" },
    { "name": "PS5", "code": 18, "hotkey": "" }
  ],
  "directTake": true,
  "pollMs": 2000, "debounce": 2, "sshExe": "ssh.exe",
  "release": { "width": 0, "height": 0 }
}
```

- `mainEdid`：主屏设备路径里的厂商码 + 产品码。
- `layout`：`second`（有另一块屏，默认）或 `none`（只有主屏）。
- `self.code`：本机所接输入口的编码。0 = 未知，此时退回"读取成功即占有"（见 6.1）。
- `peer.os`：`windows` 或 `mac`，只用于推导 `peer.program` 的默认值和默认名字。
- `peer.program`：对方机器上要调用的程序。留空时按 `peer.os` 推导：mac → `~/screenctl/screenctl.sh`；windows → `C:\Users\<peer.user>\ScreenSwitch\ScreenSwitch.exe`。
- `peer.code`：对方所接输入口的编码。0 = 未知，需要时用 `request info` 向对方询问并写回配置。
- `peer.host` 为空或 `peer.user` 为空（或 `CHANGE_ME`）= 没有配置对方，不发 SSH。
- `hotkey`：全局快捷键，默认空 = 不注册。`peer.hotkey` 随状态在"切给对方"和"拿回"之间切换。
- `release`：让出时克隆用的分辨率。0 = 取快照里另一块屏自己的分辨率。
- "拿回后的布局""自动记住屏幕排列"是托盘菜单里的偏好，存在 `prefs.json`。

旧配置的读取（不要求用户改文件）：

| 旧键 | 读作 |
|---|---|
| `codes.windows` | `self.code` |
| `codes.mac` | `peer.code` |
| `codes.other`（数组） | `devices`，名字依次为"设备 1""设备 2"… |
| `peer.command`（含 ` request give {target}`） | 去掉该后缀后作为 `peer.program` |
| `hotkeys.primary` / `hotkeys.other` | 忽略 |

没有 `peer.name` 时按 `peer.os` 取"Mac"或"Windows"；没有 `peer.os` 时为 `mac`。没有 `self.name` 时取计算机名。

### 4.2 Mac（`config.sh`）

```bash
SELF_NAME='MacBook'
CODE_SELF=15
PEER_NAME='台式机'
PEER_OS=windows                 # windows | mac
PEER_HOST='my-pc.local'
PEER_USER='alice'
PEER_PROG='C:\Users\alice\ScreenSwitch\ScreenSwitch.exe'   # 对方是 Mac 时为 ~/screenctl/screenctl.sh
CODE_PEER=16                    # 0 = 未知
DEVICES=('Switch=17' 'PS5=18')  # 名字=编码
HOTKEY_PEER=''                  # 默认空
HOTKEY_DEVICES=('' '')          # 与 DEVICES 一一对应
DIRECT_TAKE=1
LAYOUT=second                   # second | none
SECOND_ID='…'                   # 另一块屏的 displayplacer id
SECOND_BUILTIN=1                # 1 = 笔记本自带屏；0 = 外接屏
```

另有主屏标识 `MAIN_NAME`、`MAIN_UUID`、`MAIN_ID`，排列 `EXTEND_ARGS`、`MIRROR_ARGS`，拿回后的布局 `OWNED_LAYOUT`（`extend` | `external`），以及轮询、去抖、超时等时序参数，都由设置生成。

旧配置的读取：`CODE_MAC`→`CODE_SELF`，`CODE_WINDOWS`→`CODE_PEER`，`WIN_HOST`/`WIN_USER`/`WIN_EXE`→`PEER_HOST`/`PEER_USER`/`PEER_PROG`，`CODE_OTHER="17 18"`→`DEVICES=('设备 1=17' '设备 2=18')`，`LAPTOP_ID`→`SECOND_ID` 且 `SECOND_BUILTIN=1`，`HOTKEY_MAIN`/`HOTKEY_OTHER` 忽略。没有 `PEER_NAME` 时按 `PEER_OS` 取"Windows"或"Mac"；没有 `PEER_OS` 时为 `windows`。程序改写配置时只写新名字。

`PEER_HOST`、`PEER_USER`、`PEER_PROG` 任一为空 = 没有配置对方。

## 5. 跨机协议 v3（两端必须一致）

### 5.1 目录与文件

| | Windows | Mac |
|---|---|---|
| 安装目录 | `%USERPROFILE%\ScreenSwitch` | `~/screenctl` |
| 可执行 | `ScreenSwitch.exe`（托盘）、`ScreenSwitchCli.exe`（命令行） | `screenctl.sh` |
| 状态文件 | `state.txt` | `state` |
| 命令文件 | `cmd.txt` | `cmd` |
| 日志 | `screenswitch.log` | `screenctl.log` |

状态文件内容一行：`owned=1` 或 `owned=0`，后跟空格和 Unix 时间戳，例如 `owned=1 1758500000`。守护进程每次轮询覆盖写入。

命令文件内容一行：`give peer`、`give code <n>`、`give self`（本机拿回）或 `pre-take`。守护进程每 0.5 秒检查，读到后先删除文件再执行。旧内容 `give windows`、`give mac`、`give other` 按 5.3 的对应关系处理。

### 5.2 命令

发起方通过 SSH 运行对方的程序：`ssh -o BatchMode=yes -o ConnectTimeout=3 <用户>@<主机> "<对方程序> request …"`。

| 命令 | 含义 | 输出 / 退出码 |
|---|---|---|
| `request give peer` | 请接收方把主屏切给它的对方（即发起方） | `accepted` 0；`not owned` 2；`daemon not running` 3 |
| `request give code <n>` | 请接收方把主屏切到编码为 n（十进制，1–255）的设备 | 同上；n 不在接收方的设备列表里：`unknown device` 1 |
| `request take` | 发起方即将把主屏切给接收方，请它预先恢复布局 | 0 已接受或已占有；3 守护进程未运行 |
| `request info` | 询问接收方的配置 | 一行，见 5.4；退出码 0（守护进程没在运行也回答） |

`request give`：读状态文件，时间戳超过 10 秒视为守护进程未运行（3）；`owned=0` → 2，不写命令文件；`owned=1` → 写命令文件，0。

`request take`：时间戳过期 → 3；`owned=1` → 0，不写命令文件；`owned=0` → 写 `pre-take`，0。守护进程读到后按 6.3 立即恢复拿回布局并进入 8 秒宽限期。Mac 端会等布局应用完（最多 3 秒）再返回，这样对方写 DDC 时本机已经就位。调用方不依赖结果：无论退出码（含 1 = 版本不支持、255 = 连接失败）都继续写 DDC，只是失去"预先切换"的效果；调用方最多等 ConnectTimeout + 2 秒。

带任何多余参数的命令一律退出码 1。退出码 255 是 SSH 连接失败。

### 5.3 命令网关与旧写法

两端的 SSH 公钥都限定为强制命令入口 `ssh-gate`。它校验 `SSH_ORIGINAL_COMMAND` 里的程序名（Mac 端按空白切分，所以 Mac 上的程序路径里不能有空格；Windows 端把最后一个 `request` 之前的部分都当作程序路径，可以带空格和引号），只放行 5.2 的几条命令和下面的旧写法，其余输出 `rejected`、退出码 1。"切给接收方自己"（`give self`，以及旧写法里指接收方自己的那个名字）不放行。

旧写法继续接受：Windows 端把 `request give mac` 当作 `give peer`，Mac 端把 `request give windows` 当作 `give peer`；`request give other` 当作切到设备列表里的第一个设备。

发起方的 `request give peer` 被退出码 1 拒绝（对方是 v2 版本，不认识 `peer`）时，用旧写法重试一次：Windows 端发 `give windows`，Mac 端发 `give mac`。

### 5.4 `request info` 的输出

一行 ASCII，空格分隔的 `键=值`：

```
proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1
```

- `code`：接收方自己的编码，未知为 0。
- `name`、设备名：UTF-8 名字的标准 Base64（不换行）。名字只用 Base64 传，避免两端终端编码不同。
- `devices`：`编码:名字` 用逗号分隔，没有设备时为空串。
- 解析方在输出里找以 `proto=` 开头的行，忽略其余行、行尾的 `\r` 和不认识的键。

## 6. 归属与切换

### 6.1 归属判断

读 VCP 0x60 成功，且值的低 8 位等于本机编码，才算主屏在本机。

- Windows 端 `self.code` 为 0 时退回"读取成功即占有"，并在日志里提醒运行设置。
- Mac 端另外要求主屏的最大亮度等于设置时学到的值（`SANITY_MAXLUM`）：m1ddc 在主屏不在本机时仍会返回退出码 0 和垃圾值，睡醒后还可能返回缓存的旧值。

### 6.2 状态机

每台电脑维护 `owned`：

- 占有时按配置间隔（默认 2 秒）读一次；未占有时每 0.5 秒读一次，让主屏切回来的那一刻尽快拿回。读数与当前归属不一致时，下一拍（0.5 秒后）立即复核。
- 连续 2 次读到本机且 `owned=false` → 拿回；连续多次读不到本机且 `owned=true` → 让出（Windows 2 次；Mac 4 次，因为主屏在 Mac 上时 m1ddc 偶尔会返回垃圾值）。
- 本机自己改拓扑后（拿回、切换预设等）的几秒内不因读失败而让出，避免显示器重新同步期间误判（Windows 4 秒；Mac 拿回后 3 秒内不读 DDC）。
- 主动切走时立刻让出并置 `owned=false`。
- 主动切换流程执行期间暂停轮询，这时再点菜单会提示"正在切换中"；流程结束后恢复轮询。
- 显示器断电、系统睡眠都表现为读失败，恢复后自动拿回。
- 启动时按 DDC 读数与当前布局对账：未占有却是扩展 → 让出；占有却是镜像 → 拿回。只有主屏时只以 DDC 读数为准。
- 每次轮询把状态写入状态文件，供命令行和对方查询。

### 6.3 切换流程

设本机为 A、对方为 B。

**切给 B**（A 占有）：

1. B 的编码未知：先 `request info` 取得并写回配置；仍未知则通知"不知道 <B> 接在哪个输入口，请运行设置"。
2. 先向 B 发 `request take`（尽力而为，见 5.2）。
3. 写 VCP 0x60 = B 的编码，等待不再占有（最多 3 秒）后让出。3 秒内仍占有则通知"切换未生效"，不改布局。

**切给设备**（A 占有）：写该设备的编码，最多等 3 秒确认不再占有，让出，再观察 15 秒；主屏自己跳回来（设备没开机，显示器自动回到有信号的口）就拿回并通知"没有检测到 <设备> 的信号"。很多显示器切到无信号的口后不会跳回，所以菜单上提示"请先打开设备"。

**拿回主屏**（A 未占有）。`directTake` / `DIRECT_TAKE` 打开时（默认开）：

1. 向主屏写本机编码，不等结果。
2. 配置了对方：发 `request give peer`。退出码 0 → 立即恢复拿回后的布局并置 `owned=true`，显示器切过来时直接显示本机桌面；随后 8 秒宽限期内读取失败不触发让出，宽限期后仍读不到才让出。通知"已请求 <B> 交还主屏"。
3. 没有配置对方，或第 2 步不是 0：最多等 3 秒，期间每 0.5 秒读一次，连续 `debounce` 次占有即按正常拿回处理，不再提示；否则通知失败原因（B 不可达、B 也不占有主屏、B 未运行），并提示"请用显示器上的按钮切换输入源"。

**切给设备**（A 未占有）：

1. 向主屏写该设备的编码。
2. 配置了对方：发 `request give code <n>`。退出码 0 → 通知"已请求 <B> 切给 <设备>"；1 → 提示 B 那边可能没有配置这个设备。
3. 否则读一次：读到的值等于该编码 → 已切过去，不提示；否则提示按显示器按钮。

在只认活跃口的显示器上，直接写入被忽略，流程退化为请对方代发。为了不让这类显示器上的失败提示每次都晚 3 秒：直接切换从没成功过、且已经连续 3 次等不到结果时，之后不再等（仍然写入）；成功过一次就一直等。重新运行设置时清掉这份记录。`directTake` 关闭时跳过第 1 步和等待。

**收到 `pre-take`**：同样立即恢复拿回后的布局、置 `owned=true` 并进入 8 秒宽限期；已占有时不做事。

## 7. 屏幕组合与布局

### 7.1 让出 = 镜像，不移除

让出时把主屏设为另一块屏的镜像（克隆）。镜像下没有看不见的桌面区域；主屏仍在系统拓扑里，DDC 读取继续可用；不会出现"接不回来"的情况。主屏此时无人观看，分辨率和刷新率无所谓。

拿回 = 恢复"拿回后的布局"：扩展，或仅主屏（关闭另一块屏）。仅主屏时，主动切走也要保证另一块屏最终亮着：Windows 先打开另一块屏再写 DDC，打不开就取消切换；Mac 先写 DDC，确认主屏切走后再打开笔记本屏，打不开就每 10 秒重试并提示合上再打开盖子（避免主屏上先闪一下镜像画面）。未占有、程序退出、注销时保证另一块屏开着。布局切换失败后每 10 秒重试，同一段失败只通知一次。

占有且扩展时，用户改了排列会自动记住（"自动记住屏幕排列"，默认开），以后拿回都恢复成这样。

### 7.2 只有主屏（`layout: none` / `LAYOUT=none`）

主屏被切走时本机没有别的屏，窗口无处可丢：不做任何布局操作。拿回、让出只改归属状态；不需要布局快照；菜单里不显示"拿回后的布局""自动记住屏幕排列""保存布局"。切换、跟随、代发、预先拿回（只置状态和宽限期）照常。

- Windows：`NoLayout` 是不操作硬件的 `ILayout` 实现，`Orchestrator` 不改流程。它的 `Mode` 在拿回后报告扩展、让出后报告克隆，`HasSnapshot` 恒为真。
- Mac：布局操作直接返回成功，当前布局按上次操作记下的状态报告。

### 7.3 Windows

- "拿回"快照：首次在扩展排列下运行时，把 QueryDisplayConfig 的路径与模式数组序列化到 `layout-owned.bin`，之后原样应用。快照里保存每条路径的目标设备路径；适配器 LUID 每次开机都会变，应用前按设备路径在当前路径中找到对应的 LUID 逐条替换（两块屏可以分属两块显卡）。旧快照没有设备路径时统一用第一条路径的 LUID。
- "仅主屏"：只保留主屏一条路径，关闭另一块屏。
- "让出"：另一块屏的源挂两个目标（另一块屏、主屏），源模式为 `release` 分辨率（0 = 快照里另一块屏的源模式），另一块屏目标沿用快照里的模式，主屏目标模式交给系统选。另一块屏为主显示器，位置 (0,0)。
- 应用标志：`SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`。
- 回退：CCD 应用失败时改用 `DisplaySwitch.exe /clone` 与 `/extend`，只负责切换拓扑并校验结果。
- 主屏识别：用 EnumDisplayDevices 枚举逻辑显示器下的物理监视器，DeviceID 含 `mainEdid` 的那一项在激活的监视器中的序号，即 dxva2 物理监视器数组的下标（未激活的项不计数）；每次读取重新枚举，不缓存。

### 7.4 Mac

- "拿回"：应用设置时用 `displayplacer list` 保存的扩展排列（`EXTEND_ARGS`）。"仅大屏"时再用 display-helper 关闭笔记本屏，只在另一块屏是自带屏时提供。
- "让出"：displayplacer 镜像，另一块屏为"优化为"对象，显式指定它当前的分辨率。
- 另一块屏可以是外接屏（`SECOND_BUILTIN=0`）：镜像、扩展都用 displayplacer；用 `display-helper list` 输出的 `uuid=` 按 `MAIN_ID` 找主屏。
- MacBook 合盖：每次布局操作前读盖子状态（`ioreg` 的 `AppleClamshellState`）。合着时自带屏不可用，本次操作按 7.2 处理，也不尝试打开自带屏。开盖后的排列由下面的"未占有但主屏未镜像"检查和轮询纠正。另一块屏是外接屏而此刻没接着时，同样按 7.2 处理。
- 主屏在另一台电脑上停留较久后会从 macOS 的显示器列表里消失，切回时要先等它重新识别完整（display-helper 能找到保存的模式）再应用布局。
- 让出时主屏若已不在显示器列表里，只保证另一块屏开着。主屏回到列表时 macOS 按记忆恢复旧排列，可能是扩展；守护进程未占有时每拍检查主屏（按 UUID 认）是否在镜像组里，不在且持续 4 秒（`UNMIRRORED_FIX_SECS`）仍读不到主屏，就改回镜像。显示器正切回 Mac 时这几秒内就能读到主屏，直接拿回，不先闪镜像。

三块及以上的屏不支持：设置时发现就说明并停止。

## 8. 菜单、提示与快捷键

| 本机状态 | 菜单项 |
|---|---|
| 占有 | `切给 <对方名>`（配置了对方才有），然后每个设备一项 `切给 <设备名>` |
| 未占有 | `拿回主屏`，然后每个设备一项 |

- 提示里一律用对方和设备的名字，不出现"Mac""Windows"。
- 提示走系统通知，不弹模态窗口。
- 快捷键默认不注册；配置了才注册。
- Mac 的菜单栏程序通过 `screenctl.sh menu-info` 取得名字、设备列表、快捷键与布局模式（每行一个 `键=值`，设备为 `device=<编码>\t<名字>\t<快捷键>`），不自己解析 `config.sh`；点菜单时调用 `screenctl.sh give peer`、`give self`、`give code <n>`。守护进程要发的通知写进 `notify.queue`，由菜单栏程序用 UserNotifications 发出（launchd 后台进程用 osascript 发的通知会被系统丢弃）。

## 9. 设置（命令行问答）

Windows 为 `ScreenSwitchCli.exe setup`，Mac 为 `screenctl.sh setup`（`install.sh` 首次安装时调用；`init` 是其中不需要提问的探测部分）。

1. **主屏**：列出本机连接的显示器。只有一块支持 DDC 的外接显示器时自动选中，否则让用户选。读不到 DDC 时说明原因（显示器菜单里没开 DDC/CI；线路不传 DDC；主屏此刻没在显示本机）并停止。
2. **本机编码**：主屏正显示本机时读 0x60 得到。
3. **本机名字**：默认取计算机名。
4. **屏幕组合**：只有主屏 → `none`；另有一块 → `second`；更多 → 说明不支持并停止。Mac 要求开着盖子，否则自带屏不在显示器列表里，会被记成"只有主屏"。
5. **对方**（可跳过）：名字、系统、主机名或 IP、用户名。准备好本机的 SSH 密钥和客户端配置，打印本机公钥和对方要执行的命令。能连通时用 `request info` 取对方的编码和设备列表。
6. **设备**（可跳过）：逐个输入名字和编码。Windows 端先读主屏的 capabilities，列出 0x60 支持的取值并标出标准含义（15 DP 1、16 DP 2、17 HDMI 1、18 HDMI 2），去掉本机和对方已用的。从对方取到设备列表时直接采用。
7. 写配置，重启常驻程序，清掉直接切换的记录。

以后增删设备：`setup devices`（只走第 6 步）。

授权对方：Windows 端在管理员 PowerShell 里运行 `setup-sshd.ps1 -PeerPubKey "<对方的公钥>"`；Mac 端运行 `screenctl.sh authorize "<对方的公钥>"`。两者都把公钥限定为 `ssh-gate` 强制命令，同一把公钥原有的条目被替换，其他条目保留。Windows 端让本机能连对方的 SSH 客户端配置由 `setup-client.ps1 -PeerUser -PeerHost [-PeerIP]` 完成（setup 第 5 步会调用）。

## 10. 实现形态

### 10.1 Windows

C# 程序，用系统自带 .NET Framework 4.x 的 `csc.exe` 编译（C# 5），不装 SDK 或运行库。同一份源码编出托盘程序 `ScreenSwitch.exe`（WinForms `NotifyIcon`）与控制台版 `ScreenSwitchCli.exe`（设置、诊断与 `request` 子命令）。开机自启走"启动"文件夹快捷方式。

| 模块 | 职责 |
|---|---|
| `Config` | 读写 `config.json`，含旧键映射 |
| `Prefs` | 托盘菜单里的偏好（`prefs.json`） |
| `Ddc` | 枚举物理显示器、识别主屏、读写 VCP 0x60、读 capabilities（dxva2） |
| `Layout` / `NoLayout` | 布局快照与 CCD 应用 / 只有主屏时的空实现 |
| `OwnershipTracker` | 去抖后的归属状态 |
| `Orchestrator` | 0.5 秒一拍：读 DDC 驱动状态机、处理命令文件、主动切换流程、自愈重试、自动记住排列 |
| `DirectRecord` | 直接切换在这块显示器上有没有成功过（`direct-take.txt`） |
| `Relay` / `PeerAddress` / `PeerInfo` | 发起 SSH 请求并解释退出码；解析对方地址（失败时用上次成功的）；`request info` 的格式化与解析 |
| `Setup` / `SetupEnv` / `ConsoleIo` | 设置问答 |
| `TrayApp` / `TrayIcons` | 托盘菜单、状态显示、全局快捷键、通知气泡 |
| `Cli` | `request …`、`ssh-gate`、`setup` 与诊断子命令 |
| `StateFile` / `Log` | 状态文件与命令文件读写；追加写日志，超过 1 MB 轮转 |
| `AfterTake` | 拿回主屏后执行用户配置的命令 |

### 10.2 Mac

- `~/screenctl/screenctl.sh`（系统自带 bash 3.2）：子命令 `setup`、`init`、`authorize`、`give <peer|self|设备名|code n>`、`take`、`release`、`status`、`save-layout`、`set`、`request …`、`watch`、`ssh-gate`、`menu-info`。
- `~/Library/LaunchAgents/com.user.screenctl.plist`：登录后运行 `watch`，KeepAlive。守护进程 0.5 秒一拍：检查命令文件；按 6.2 的间隔探测 DDC 并驱动状态机，写状态文件。
- 菜单栏程序 `ScreenSwitch.app`：单文件 Swift，由安装脚本用 Command Line Tools 的 `swiftc` 编译，不需要 Xcode。全局快捷键用 Carbon `RegisterEventHotKey`（无需辅助功能权限）。切换逻辑都在守护进程。
- `~/screenctl/bin/display-helper`：单文件 Swift，开关笔记本屏、在一个配置事务里从镜像直接进入"仅大屏"、列出显示器（含 `uuid=`、`mirror=`）、探测主屏是否已就绪。
- DDC：`m1ddc display <uuid> set|get input`；所有 DDC 访问经文件锁串行化，防止并发读串包。
- 工具：Homebrew 安装的 `m1ddc` 与 `displayplacer`。

## 11. 错误处理

| 情况 | 行为 |
|---|---|
| 未占有且直接切换无效、对方不可达 / 未配置 / 也不占有 | 通知原因和"请用显示器上的按钮切换输入源"，不改布局 |
| 写 DDC 后 3 秒内仍占有 | 通知"切换未生效"，不改布局 |
| 切给设备后主屏跳回 | 拿回，通知"没有检测到 <设备> 的信号" |
| 对方不认识所请求的设备 | 通知"它那边可能没有配置 <设备>" |
| 对方编码未知且问不到 | 通知"不知道 <对方> 接在哪个输入口，请运行设置" |
| 布局应用失败 | 通知原因（同一段失败只通知一次），记日志，每 10 秒重试 |
| 布局快照缺失（Windows） | 通知"请先在双屏状态下保存布局" |
| 守护进程异常 | Windows：托盘程序即守护，异常写日志继续；Mac：launchd 自动拉起 |

## 12. 测试

两端各自的自动测试覆盖：

1. 协议：5.2 的每条命令与旧写法、`ssh-gate` 的放行与拒绝、`request info` 的格式（名字含中文和空格）。
2. 归属：按编码判断；读到别的编码不算占有（模拟在所有输入口都应答的显示器）。
3. 直接切换：6.3 的每条路径（显示器接受 / 忽略直接写入 × 对方接受 / 拒绝 / 未配置），以及"连续 3 次不生效就不再等"。
4. 只有主屏：拿回、让出、预先拿回、启动对账都不调用布局后端。
5. 配置：旧格式读取、新格式读写；设置问答。
6. **配对测试**：起两个实例，共用一块假显示器，把一方的 SSH 调用接到另一方的 `request` 入口，走通"切给对方 → 对方拿回 → 切给设备 → 拿回"。用它验证两台同系统的电脑，因为没有真机可测。

- Windows：`run-tests.cmd` 编译并运行单元测试。状态机、编排、命令行协议、ssh-gate、布局计算（含按路径替换 LUID）、设置问答都通过接口注入假的 DDC、布局、SSH 与时钟来测，不需要显示器和对方。
- Mac：`mac/tests/run-tests.sh` 用假的 m1ddc、displayplacer、ssh、display-helper、ioreg 驱动真实脚本，另测另一块屏为外接屏、合盖；`mac/tests/run-pair-tests.sh` 是两台 Mac 的配对测试。

真机验收：

| 项目 | 做法 |
|---|---|
| 平时的用法 | Windows 两块屏 + MacBook 开盖：切给对方、拿回、切给游戏机、按显示器按钮切换，切换时不出现镜像画面 |
| Windows 只有主屏 | 拔掉副屏，运行设置，再走一遍切换与拿回：没有布局报错 |
| MacBook 合盖 | 合盖后切走、拿回：没有"笔记本屏无法恢复"之类的提示；开盖后布局恢复正常 |
| 设备命名 | 给游戏机起名，菜单出现"切给 <名字>"；另一台电脑能读到这个设备 |
| 设置问答 | 两端各跑一遍，生成的配置与手填的一致 |

没有真机可测、只由自动测试覆盖的：两台同系统的电脑；显示器接受非活跃口的指令；另一块屏是外接屏的 Mac；两块屏分属两块显卡的 Windows。README 的支持情况表里如实标注。

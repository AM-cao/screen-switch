# Mac 端使用指南

## 1. 这是什么

Mac 端由一个守护进程和一个菜单栏程序组成。守护进程通过 DDC/CI 读取主屏（共享的那块显示器）当前的输入源，判断主屏是不是正在显示这台 Mac：被切走时把主屏镜像到本机的另一块屏，切回来时恢复原来的排列。菜单栏程序提供切换菜单、设置窗口、首次启动向导和通知。对方电脑可以是 Windows，也可以是另一台 Mac，两台之间通过 SSH 互相代发切换请求。

安装后在 `~/screenctl/` 生成：`screenctl.sh`（主脚本，也是给脚本和 AI 助手用的命令）、`config.sh`（配置）、`strings.tsv`（中英文文字表）、`state`（状态文件）、`cmd`（命令文件）、`screenctl.log`、`launchd.log`、`launchd/`（两个 LaunchAgent 的模板）、`bin/display-helper`。

菜单栏程序安装在 `~/Applications/ScreenSwitch.app`，日志写到 `~/screenctl/menu.log`。开机自动运行靠 `~/Library/LaunchAgents/` 里的 `com.user.screenctl.plist`（守护进程）和 `com.user.screenswitch-menu.plist`（菜单栏程序）。

界面文字跟随系统语言：系统语言是中文时用中文，否则用英文。

## 2. 安装

也可以让你的 AI 助手按仓库根目录的 [AGENTS.md](../AGENTS.md) 安装，它会用第 3.1 节的命令完成设置，需要你动手的地方会停下来告诉你。

### 2.1 安装 Homebrew 和依赖工具

若未装 Homebrew，执行官方安装命令：

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

然后安装 DDC 控制和屏幕布局工具，以及编译菜单栏程序要用的 Command Line Tools：

```bash
brew install m1ddc displayplacer
```

```bash
xcode-select --install
```

### 2.2 接好显示器

1. 主屏接到 Mac 上（USB-C 或 HDMI 都可以，线路要能传 DDC；很多扩展坞和转接头不传）。
2. 用显示器上的按钮切到 Mac 所接的输入口，主屏应显示 Mac 桌面。在显示器菜单（OSD）里确认 DDC/CI 已打开。
3. 这台 Mac 除了主屏还有另一块屏时，打开"系统设置 → 显示器"，确认是扩展排列（而非镜像），并按需调整位置。

支持的屏幕组合：

| 这台 Mac 的屏 | 主屏被切走时 | 主屏切回来时 |
|---|---|---|
| 主屏 + 笔记本自带屏 | 主屏镜像到自带屏 | 扩展屏幕，或只用主屏（关闭自带屏） |
| 主屏 + 另一块外接屏（如接两块显示器的 Mac mini） | 主屏镜像到那块外接屏 | 扩展屏幕 |
| 只有主屏（Mac mini、Mac Studio） | 不调整布局 | 不调整布局 |

一台 Mac 上三块及以上的屏暂不支持。MacBook 合着盖子使用没有问题：设置时要开着盖子（合盖时自带屏不在显示器列表里），之后每次切换前程序会看盖子是开是合，合着时按"只有主屏"处理。

### 2.3 运行安装脚本

在仓库的 `mac/` 目录执行：

```bash
./install.sh
```

脚本不提问，它会：

- 复制 `screenctl.sh` 和文字表到 `~/screenctl/`。
- 用 `swiftc` 编译菜单栏程序，装到 `~/Applications/ScreenSwitch.app`；编译 `~/screenctl/bin/display-helper`（开关笔记本屏、读显示器的输入口清单）。
- 首次安装时打开"开机自动运行"，然后启动守护进程和菜单栏程序。再次运行时保持你在设置里选的开关。

安装后如弹出"已添加后台项目"通知，需在"系统设置 → 通用 → 登录项与扩展"确保 bash 和 ScreenSwitch 的后台允许开关已打开。

### 2.4 首次启动向导

还没设置过时，菜单栏程序启动后自动打开设置向导。底部"上一步 / 下一步"；中途关掉不会写配置，下次打开时已填的内容还在；最后一页点"完成"才写入。

1. **欢迎**：开始前请用显示器上的按钮把主屏切到这台 Mac。
2. **主屏**：列出接着的显示器，以及能不能用软件控制（DDC/CI）。只有一块能控制时自动选中；有几块时点选。一块都不能控制时会说明可能的原因，改好后点"重新检测"。选好后显示这台 Mac 接在哪个口、主屏切走时桌面放到哪块屏上。MacBook 合着盖子时要求先打开盖子。
3. **输入口**：程序读出显示器的输入口清单（显示器 capabilities 里 VCP 0x60 的取值），一口一行。这台 Mac 的口自动填好；其余每个口选它接的是"对方电脑""设备"（再填名字，如 Switch）还是"没接"。读不出清单时点"添加输入口"手动填编码。
4. **对方电脑**（可以跳过）：名字、系统、地址、用户名；互相授权见 2.5；"测试连接"会问到对方叫什么、接在哪个口。
5. **完成**："开机自动运行"（默认勾上）和设置摘要。

**输入口的编码**是显示器给每个输入口编的数字，软件切换时发的就是它。常见的是 15 = DisplayPort 1（不少显示器的 USB-C 也用它）、16 = DisplayPort 2、17 = HDMI 1、18 = HDMI 2，但各家显示器不完全一样。每台电脑的编码由程序在主屏显示它时自己读出；游戏机等设备的编码按接口对照上面的数字选，选错了切不过去，在设置窗口里改掉即可。

### 2.5 两台电脑互相授权（SSH）

不配 SSH 也能用：切走、自动跟随都不受影响，只是主屏不在这台 Mac 上时，拿回要靠显示器按钮（显示器在非活跃口也接受指令的除外，见第 4 节）。

在设置窗口（或向导）的"对方电脑"页：

1. **这台电脑的公钥**：点"复制"，用你方便的办法（共享剪贴板、发给自己的消息等）带到对方电脑，粘贴到对方设置窗口的"对方公钥"里，点"授权"。对方是 Windows 时授权要管理员确认一次。
2. **对方公钥**：把对方电脑的公钥粘贴进来，点"授权"。它把公钥加进 `~/.ssh/authorized_keys`，并限定为只能调用本机的 `ssh-gate`：对方只能发 `request give peer`、`request give code <n>`、`request take`、`request info` 这几条，拿不到 shell，也不能做端口转发。
3. 打开"系统设置 → 通用 → 共享 → 远程登录"（授权后有按钮直接打开），"允许访问"选"仅这些用户"，只保留当前用户。
4. 点"测试连接"：显示"已连上 <对方>，它接在 <口>"就好了；与"输入口"页标的不一致时会提醒并按对方说的改。

命令行做法：`~/screenctl/screenctl.sh pubkey` 打印本机公钥；`~/screenctl/screenctl.sh authorize "<对方的公钥>"` 授权对方；`~/screenctl/screenctl.sh peer test` 测试连接。

### 2.6 通知权限

通知由菜单栏程序以"屏幕切换"的名义发出。首次启动时系统会询问是否允许，点"允许"；错过了就到"系统设置 → 通知 → 屏幕切换"里打开。没打开时菜单栏图标带小感叹号，菜单最上面有"⚠ 通知没有打开"，点它到设置窗口"通用"页，那里有按钮打开通知设置。

守护进程把要提示的文字写到 `~/screenctl/notify.queue`，由菜单栏程序取走后弹出。菜单栏程序没在运行时才退回 osascript，但 launchd 拉起的后台进程用 osascript 发的通知，新版 macOS 通常会静默丢弃。

### 2.7 菜单栏

菜单栏右上角的显示器图标：亮 = 主屏在本机；灰 = 主屏不在本机；右下角带小感叹号 = 有需要你处理的问题。

主屏不在本机时的菜单：

```
拿回主屏
切给 Switch
──────────────
扩展屏幕          ✓
只用主屏
──────────────
设置…
退出
```

- 主屏在本机时，第一项换成"切给 <对方的名字>"；没有配置对方电脑时不显示这一项。之后每个已登记的设备一项"切给 <设备名>"。
- "扩展屏幕 / 只用主屏"是拿回主屏后的布局：主屏在本机时点选立即生效，不在时下次拿回生效。只有主屏、或另一块屏是外接屏（只能扩展）时不显示。"只用主屏"会关闭笔记本自带屏：切走主屏时先写 DDC，等主屏确实切走后才打开笔记本屏并镜像，主屏上不出现镜像画面；切走后笔记本屏万一打不开，会提示"合上再打开笔记本盖子"并每 10 秒重试。关屏只在当前登录会话有效，万一笔记本屏没亮回来：合上再打开盖子，或注销重新登录，或运行 `~/screenctl/bin/display-helper enable 1`（1 是笔记本屏通常的显示编号，以 `config.sh` 的 `LAPTOP_CGID` 为准）。
- 切换进行中切换项变灰。
- 设了快捷键的项右边显示快捷键；默认没有。
- 有需要处理的问题（快捷键被占用、守护进程没在运行、通知没打开、还没完成设置）时，菜单最上面多一行"⚠ 说明"，点它打开设置窗口对应的位置。

"退出"后菜单栏程序不会自动重启，再打开 `~/Applications/ScreenSwitch.app` 即可。

### 2.8 设置窗口

菜单"设置…"打开。所有改动立即生效，没有"保存 / 取消"：勾选、选择马上生效，文字框在回车或离开时生效。

| 标签页 | 内容 |
|---|---|
| 输入口 | 当前主屏和"重新检测"；输入口表（同向导第 3 步），设备的增删、改名在这里做，菜单随之变化 |
| 对方电脑 | 同 2.5；"不连对方电脑"清空对方的设置 |
| 屏幕 | 屏幕组合和"重新检测"；拿回主屏后"扩展屏幕 / 只用主屏"（与菜单同一设置）；"自动记住屏幕排列"；"现在记住当前排列"。只有主屏时只显示屏幕组合 |
| 快捷键 | "切给对方 / 拿回主屏"（同一个键，按当前状态）和每个设备一行。点按钮后按下组合键即录下（⌃ ⌥ ⇧ ⌘ 至少一个，加一个数字或字母），"清除"删掉；被别的程序占用时在那一行说明 |
| 通用 | 开机自动运行；重新运行设置向导；打开日志；版本号。折叠的"高级"：先直接切换（见第 4 节）、打开配置文件所在的文件夹 |

"自动记住屏幕排列"（默认打开）：主屏在本机且为扩展时，你在"系统设置 → 显示器 → 排列"里改了排列、主显示器或分辨率，约 20 秒后自动记住，以后"拿回"都恢复成这样（会弹通知）。

快捷键不需要辅助功能权限。也可以在"快捷指令" App 里建快捷指令，每个只放一个"运行 Shell 脚本"动作：`~/screenctl/screenctl.sh give peer`（切给对方）、`… give self`（拿回主屏）、`… give Switch`（切给名叫 Switch 的设备）。

### 2.9 配置文件（`~/screenctl/config.sh`）

设置窗口和命令会写好这些项，一般不用手改。手改后执行 `launchctl kickstart -k gui/$(id -u)/com.user.screenctl`；菜单栏程序会自己发现配置变了。

| 项 | 含义 |
|---|---|
| `MAIN_NAME`、`MAIN_UUID`、`MAIN_ID` | 主屏，`detect` 写入 |
| `MAIN_PORTS` | 主屏的输入口清单（capabilities 里 VCP 0x60 的取值），`detect` 读出 |
| `SELF_NAME` | 这台电脑的名字；留空取电脑名 |
| `CODE_SELF` | 这台电脑所接输入口的编码，`detect` 读出 |
| `PEER_NAME`、`PEER_OS` | 对方电脑的名字；`windows` 或 `mac` |
| `PEER_HOST`、`PEER_USER` | 对方的主机名（如 `my-pc.local`）或 IP、用户名 |
| `PEER_PROG` | 对方电脑上本程序的路径，按系统和用户名自动生成。Windows：`C:\Users\<用户名>\ScreenSwitch\ScreenSwitch.exe`（不能有空格）；Mac：`~/screenctl/screenctl.sh` |
| `CODE_PEER` | 对方所接输入口的编码；留空时第一次"切给对方"会自动向它询问 |
| `DEVICES` | 其他设备，每项 `'名字=编码'` |
| `DIRECT_TAKE` | 1 = 拿回时先直接向主屏发切换指令，不行再请对方代发（见第 4 节） |
| `LAYOUT`、`SECOND_ID`、`SECOND_BUILTIN` | 屏幕组合，`detect` 探测：`second` = 另有一块屏，`none` = 只有主屏 |
| `OWNED_LAYOUT` | 拿回后的布局：`extend`（扩展屏幕）或 `external`（只用主屏） |
| `HOTKEY_PEER`、`HOTKEY_DEVICES` | 快捷键，如 `'ctrl+opt+1'`；`HOTKEY_DEVICES` 与 `DEVICES` 一一对应；留空 = 不注册 |

`PEER_HOST`、`PEER_USER`、`PEER_PROG` 任一为空，就当作没有配置对方电脑。旧版本的配置（`WIN_HOST`、`CODE_MAC`、`CODE_OTHER` 等）照常能读，程序下次写配置时会换成新名字。

## 3. 命令

### 3.1 给脚本和 AI 助手用的命令

两端命令名、参数、输出相同（Windows 是 `ScreenSwitchCli.exe <命令>`）。成功时退出码 0，不输出 JSON 的命令最后打印一行"已完成。"（英文系统为 `Done.`）；失败时退出码 1，打印一行 `error: <原因>`。改了配置后自动让守护进程重新读取。这些命令都不经过 `ssh-gate`，对方电脑不能调用。

| 命令 | 作用 |
|---|---|
| `status` | 一行 JSON：`configured`、`mainScreen`、`self{name,code}`、`layout`（`second`\|`none`）、`ownedLayout`（`extend`\|`main-only`）、`autoSave`、`directTake`、`peer{configured,name,os,host,user,code}`、`devices[{name,code,hotkey}]`、`trayRunning`（守护进程在运行）、`owned`（不知道时 `null`）、`autostart`、`problems` |
| `detect [--main <型号>]` | 检测主屏、本机编码、屏幕组合和输入口清单并写入。几块显示器都能控制时退出码 1，并列出候选（每行"  <型号>  <名字>"），带 `--main` 重跑 |
| `ports` | JSON：`{monitorKnown, ports[{code,name,use,device,fromMonitor}]}`，`use` 为 `self`\|`peer`\|`device`\|`none` |
| `peer set --os <windows\|mac> --host <地址> --user <用户名> [--name <名字>]` | 设置对方电脑 |
| `peer clear` | 清除对方电脑 |
| `peer test` | 连对方问它的名字和编码，JSON `{name,code,codeSaved}`；取到编码时写入配置 |
| `pubkey` | 打印本机公钥，没有就先生成 |
| `authorize "<对方公钥>"` | 授权对方；之后还要打开"远程登录" |
| `device add <编码> <名字>` / `device rename <编码> <名字>` / `device remove <编码>` | 设备增删改名；名字取其余所有参数，用空格连接；编码 1–255，不能与本机、对方或已有设备重复 |
| `set owned-layout extend\|main-only` | 拿回后扩展屏幕 / 只用主屏 |
| `set auto-save on\|off` | 自动记住屏幕排列 |
| `set direct-take on\|off` | 先直接切换 |
| `set hotkey peer <组合>\|none` / `set hotkey <编码> <组合>\|none` | 快捷键，如 `ctrl+opt+1`（也认 Windows 的写法 `Ctrl+Alt+1`） |
| `autostart on\|off` | 开机自动运行（两个 LaunchAgent 放进或移出 `~/Library/LaunchAgents`；关掉时正在运行的不停） |

`problems` 里的标识：`not-configured`、`daemon-not-running`、`menu-not-running`、`hotkey-failed:peer`、`hotkey-failed:<编码>`、`notifications-off`。

Mac 另有设置窗口和向导内部用的：`displays`（JSON，所有显示器及能否用软件控制）、`ports --main <型号>`（向导里还没写配置时读某块显示器的清单）、`peer test --os … --host … --user …`（不写配置试连）、`peer code <编码>|none`（标出对方接的口）。

### 3.2 其他命令

| 子命令 | 用途 |
|-------|------|
| `setup [型号]` | 一问一答的设置：检测主屏，问本机名字、对方电脑和其他设备。在主屏显示这台 Mac 时运行。旧配置备份为 `config.sh.bak`。 |
| `setup devices` | 一问一答地增删其他设备。 |
| `give peer` / `give self` / `give <设备名或编码>` | 切给对方 / 拿回主屏 / 切给某个设备。 |
| `take` / `release` | 手动拿回（恢复布局）/ 手动让出（镜像）。 |
| `save-layout` | 现在记住当前排列。 |
| `diag` | 给人看的诊断：主屏归属、守护进程运行状态、当前布局。 |
| `request give peer` | 供对方 SSH 调用：请本机把主屏切给对方。本机占有时接受（退出码 0），未占有返回 2，守护进程停止超过 10 秒返回 3。 |
| `request give code <n>` | 供对方 SSH 调用：请本机把主屏切到编码为 n 的设备。本机的设备列表里没有这个编码时返回 1。 |
| `request take` | 供对方 SSH 调用：对方即将把主屏切给本机。未占有时守护进程立即恢复拿回后的布局并进入 8 秒宽限期，命令等布局应用完再返回（最多 3 秒，输出 `applied`；主屏还没就绪时输出 `pending`）；已占有时返回 0 不做事。 |
| `request info` | 供对方 SSH 调用：回答本机的编码、名字和设备列表。 |
| `menu-info` | 菜单栏程序读取：名字、设备、快捷键、布局模式。 |
| `watch` | 守护进程主体（launchd 调用）。单实例，0.5 秒一拍轮询；还没设置过时等配置出现。 |
| `ssh-gate` | `authorized_keys` 强制命令入口，只放行上面四条 `request`，其他命令退出码 1。 |

旧写法仍然接受：`init`（= `detect`）、`give windows`（= 对方）、`give mac`（= 本机）、`give other`（= 设备列表里的第一个）、`set AUTO_SAVE_LAYOUT 0|1`、`set OWNED_LAYOUT extend|external`。

### 3.3 常用操作

修改配置后重新加载守护进程：

```bash
launchctl kickstart -k gui/$(id -u)/com.user.screenctl
```

更新后重新安装：

```bash
cd /path/to/repo/mac && ./install.sh
```

只用主屏模式下重启守护进程，退出的进程会先把笔记本屏开回来、新进程再关掉，屏幕闪两下。`install.sh` 会先留下 `~/screenctl/.restarting` 标记让它跳过这步；手动 `launchctl kickstart -k` 前也可以先 `touch ~/screenctl/.restarting`。

卸载：

```bash
launchctl bootout gui/$(id -u)/com.user.screenctl; launchctl bootout gui/$(id -u)/com.user.screenswitch-menu
```

```bash
rm -f ~/Library/LaunchAgents/com.user.screenctl.plist ~/Library/LaunchAgents/com.user.screenswitch-menu.plist; rm -rf ~/screenctl ~/Applications/ScreenSwitch.app
```

查看守护进程实时日志（按 Ctrl+C 退出）：

```bash
tail -f ~/screenctl/screenctl.log
```

查看 launchd 启动日志：

```bash
cat ~/screenctl/launchd.log
```

### 3.4 状态文件格式

`~/screenctl/state` 只有一行：`owned=1` 或 `owned=0`（主屏归属），后跟 Unix 时间戳（守护进程最后一次刷新的时刻）。超过 10 秒没刷新视为守护进程没在运行。`diag` 命令输出例：

```
owned=1 1790079743
daemon=running pid=12345 age=2s
layout=extend
```

## 4. 行为说明

- **占有判定**：`m1ddc display <uuid> get input` 的返回值等于 `CODE_SELF`（设置时读出）才算主屏在本机。主屏不在本机时 m1ddc 仍可能返回退出码 0 和 0、-7、-102 之类的垃圾值，所以不能只看退出码；另外还会核对最大亮度（`SANITY_MAXLUM`，见常见问题）。在所有输入口都应答 DDC 的显示器上，读到的是别的口的编码，同样不算占有。
- **轮询周期**：守护进程 0.5 秒一拍；占有时每 4 拍（2 秒）、未占有时每拍（0.5 秒，`POLL_TICKS_UNOWNED`）读一次，主屏一切回来就尽快拿回。
- **去抖**：读数与当前归属不符时下一拍立刻复核。未占有时连续 2 次读到本机才拿回；占有时连续 4 次（2 秒，`RELEASE_DEBOUNCE`）读失败才让出，因为主屏在本机时 DDC 偶尔会返回垃圾值，2 次就让出会误判。拿回后 3 秒内（`SETTLE_SECS`）不读 DDC，显示器重新配置期间读数不可靠。
- **让出与拿回**：让出 = 主屏镜像到本机的另一块屏（那块屏的分辨率保持）；拿回 = 恢复保存的扩展排列，或只用主屏。只有主屏的 Mac、合着盖子的 MacBook、另一块外接屏没接着时，不做任何布局操作，只记归属。
- **先直接切，再请对方代发**（设置窗口"通用 → 高级 → 先直接切换"，`DIRECT_TAKE=1`，默认打开）：点"拿回主屏"时先直接向主屏发一次切换指令。有的显示器在没切到的输入口上也接受指令，这就直接切回来了，不需要对方电脑，主屏停在游戏机上时也能拿回。显示器不理会时照常通过 SSH 请对方代发；对方也帮不上，才提示"请用显示器上的按钮切换输入源"。直接切连着 3 次都没生效、且从没成功过，之后就不再为它多等那 3 秒。
- **预先恢复布局**：两种情况下会在显示器切过来之前就恢复拿回后的布局，显示器切过来时直接显示本机桌面，不再先出现镜像画面：一是"拿回主屏"且对方接受了请求；二是对方切过来之前通过 SSH 发来 `request take`。随后 `TAKE_GRACE_SECS`（默认 8 秒）宽限期内读不到主屏不让出，期满仍读不到才恢复镜像并通知"<对方> 没有交还主屏"。预先恢复布局时主屏可能还没就绪（在对方那里久了会被 macOS 从显示器列表里移除，或正在重新识别），这时只先记为占有，就绪约 1 秒后再应用布局。
- **切给对方前先通知它**（`NOTIFY_PEER_TAKE=1`，默认）："切给对方"会先 SSH 调用对方的 `request take`，让它预先恢复布局，再写 DDC。对方不支持或不在线时只是多等最多 5 秒，切换照常进行。还不知道对方接在哪个输入口时，先用 `request info` 向它询问并记进配置。
- **切给设备**：只切到所选设备的输入口。之后观察 15 秒（`OTHER_OBSERVE`），显示器自己跳回来说明那个口没有信号，这时拿回并通知"没有检测到 <设备> 的信号"。未占有时点"切给 <设备>"，由对方代为切过去。
- **主动切换期间**（写 DDC 后等待、观察期、SSH 最多 10 秒）暂停轮询，再来的请求被忽略并通知"正在切换中"。期间状态文件照常刷新。
- **启动对账**：守护进程启动时，DDC 读数与布局不一致就纠正（未占有但扩展 → 让出；占有但镜像 → 拿回）。
- **未占有时守住镜像**：主屏回到显示器列表（重新插线、打开盖子之后）而 macOS 把它摆成了扩展时，4 秒后（`UNMIRRORED_FIX_SECS`）仍读不到主屏就改回镜像；这 4 秒内读到了就直接拿回，不先闪一下镜像。
- **并发控制**：所有 DDC 访问经 `~/screenctl/.ddc.lock` 文件锁串行化，防止两个进程同时读写导致数据混乱。

## 5. 验收清单

| 项目 | 操作 | 期望 |
|-----|------|------|
| 全新安装 | 删掉 `~/screenctl/config.sh` 后运行 `./install.sh` | 向导自动出现；走完后能切换 |
| 查看状态 | `~/screenctl/screenctl.sh status` | 一行 JSON，`configured` 为 `true`，`trayRunning` 为 `true` |
| 菜单 | 点开菜单栏图标 | 占有时为"切给 <对方>"加各设备，未占有时为"拿回主屏"加各设备；没有状态行；图标随状态变灰或恢复 |
| 登记设备 | 设置窗口"输入口"里把某个口选成设备、填名字，再改名 | 菜单里出现、跟着改名 |
| 布局选项 | 菜单点"只用主屏"，再打开设置窗口"屏幕" | 两边的选中项一致 |
| 开机自动运行 | 设置窗口"通用"里关掉再打开 | `status` 的 `autostart` 跟着变；`~/Library/LaunchAgents` 里两个 plist 移走 / 回来 |
| 语言 | 系统语言分别为中文、英文，重启菜单栏程序 | 菜单、设置窗口、向导、通知的文字随之变 |
| 切给对方（占有时） | 菜单或 `give peer` | 约 1 秒主屏切到对方所接的输入口，本机进入镜像；3 秒内仍读得到则通知"切换未生效：主屏仍在本机"且不改布局 |
| 切给设备（占有时，先打开设备） | 菜单或 `give <设备名>` | 主屏切到该设备的口，本机进入镜像，观察 15 秒后结束，无通知；观察期内跳回则拿回并通知"没有检测到 <设备> 的信号" |
| 拿回主屏（未占有时） | 菜单或 `give self` | 对方接受后本机立即恢复拿回后的布局，显示器切过来时直接是本机桌面；8 秒内主屏没切过来则恢复镜像并通知。对方不在线时提示按显示器按钮 |
| 显示器按钮手动切换 | 用显示器按钮切到其他口，再切回这台 Mac 的口 | 显示器切换输入源约 3 秒黑屏；能读到 DDC 后约 1 秒内切为拿回后的布局 |
| 合着盖子 | 合盖后切给对方、再切回来 | 照常切换；没有"笔记本屏无法恢复"的提示；开盖后布局恢复正常 |
| 对方调用 | 在对方电脑上点"拿回主屏""切给 <设备>" | 本机日志里出现 `request: 已接受 give peer` / `give code <n>` |
| 注销重新登录 | `launchctl print gui/$(id -u)/com.user.screenctl` | 输出中有 `state = running`；注销、登录不会切换主屏 |

自动测试：`mac/tests/run-tests.sh`（约 8 分钟）、`mac/tests/run-pair-tests.sh`（两份程序互相配对，约 1 分钟）和 `mac/tests/run-cli-tests.sh`（第 3.1 节的命令，约半分钟），用假的 m1ddc、displayplacer、ssh 驱动真实脚本，不碰真实显示器。`mac/tests/run-app-tests.sh` 编译菜单栏程序并测菜单项的生成、向导每页的检查等界面背后的逻辑。

界面截图（不安装也能审阅）：`ScreenSwitch --ui-dump <目录> [dark]` 把设置窗口每一页、向导每一页画成 PNG，菜单画成示意图。

## 6. 常见问题

### 通知不显示

在"系统设置 → 通知 → 屏幕切换"里允许通知；专注模式也会拦截通知。确认菜单栏程序在运行（通知靠它弹出）。

**通知进了通知中心却没有弹横幅**：macOS 默认"镜像显示器时进入勿扰"，而 Mac 让出主屏时正处于镜像，"连接不到 <对方>""请用显示器上的按钮切换输入源"这类提示恰好都在这时发出。到"系统设置 → 通知"打开"镜像或共享显示器时允许通知"。

`~/screenctl/menu.log` 记录通知授权结果（`notify auth: granted=…`）和发送失败的原因；`~/Applications/ScreenSwitch.app/Contents/MacOS/ScreenSwitch --self-test` 第一行显示当前授权状态。

### 菜单栏里看不到图标

菜单栏图标太多时会被刘海挡住：按住 ⌘ 拖动其他图标腾出位置。确认程序在运行：`launchctl print gui/$(id -u)/com.user.screenswitch-menu | grep state`；有错误看 `~/screenctl/menu.log`。

### 按了快捷键或菜单没反应

1. 点开菜单栏图标，看最上面有没有"⚠"开头的一行（快捷键没生效、后台程序没在运行等），点它会打开处理的地方。
2. 查看日志：`tail -f ~/screenctl/screenctl.log`，观察是否有错误信息。
3. 检查守护进程状态：`~/screenctl/screenctl.sh status`，看 `trayRunning` 是否为 `true`、`problems` 里有什么。
4. 查看 launchd 日志：`cat ~/screenctl/launchd.log`，看启动时是否有错误。

### 切给游戏机之后主屏一直黑屏

很多显示器切到没有信号的输入口后不会自动跳回。以作者的显示器（P275MV）为例：从 Mac 切到休眠中的 Switch（HDMI 1）或空的 HDMI 2 后，显示器停在黑屏省电，超过 1 分钟没有跳回。此时 Mac 已是镜像，窗口不会跑丢。

这块显示器停在 HDMI 口时，不接受任何一台电脑的 DDC 指令（Mac、Windows 两边都实测被忽略），所以只能用显示器按钮切回来；回到 Mac 的口后 Mac 会自动恢复布局。

- 先打开游戏机等设备，再点"切给 <设备>"。
- 黑屏时用显示器按钮切回 Mac 的口，Mac 会自动拿回。
- 显示器黑屏省电时唤醒游戏机，显示器可能被唤醒后停在 Mac 的口上；再点一次"切给 <设备>"即可。
- 想让游戏机关机后主屏自动回到电脑：如果显示器菜单里有输入源"自动"，玩的时候手动设为"自动"。作者的显示器上，关掉 Switch（可以留在底座上）约 10 秒后会跳到有信号的电脑，由那台电脑自动接管。这一步软件代劳不了：这块屏不能通过 DDC 设置"自动"，经 DDC 切到另一个口还会退出自动模式（写当前口的编码不会）。详见 `docs/hardware-notes.md`。
- 另一种用法：显示器菜单一直保持"自动"，平时只用显示器按钮切换输入源，游戏机关机后总会自动跳回电脑；代价是电脑上的一键切换（经 DDC 切到另一个口）会让显示器退出自动，需要再到显示器菜单里设回来。

### 拿回主屏提示"连接不到 <对方>"

可能原因：
- 对方电脑关机或睡眠。
- 对方没有开 SSH（Windows 的 OpenSSH 服务器，Mac 的"远程登录"），或者没有授权这台 Mac 的公钥。
- mDNS 失效（`.local` 域名无法解析）。

解决方法：在设置窗口"对方电脑"里把地址改为对方的固定 IP 地址，再点"测试连接"。默认脚本仅使用 IPv4（`SSH_ADDRESS_FAMILY=inet`），因为 `.local` 可能解析出多个 IPv6 地址，ssh 会逐个尝试每个地址等待满 3 秒超时。

### Mac 睡醒后以为主屏还在自己这里

旧版本会出现：Mac 睡眠时主屏被对方占去，Mac 醒来仍保持扩展、菜单显示"切给对方"。原因是显示器不回复时 m1ddc 会返回缓存里的旧数据，睡醒后恰好是睡前读到的本机编码。现在读到本机编码后还会核对主屏的最大亮度（`config.sh` 的 `SANITY_MAXLUM`，检测时自动学习），日志里出现"判为缓存旧数据"说明拦下过一次。若 `SANITY_MAXLUM=0`（检测时没读到），在主屏显示 Mac 且为扩展时到设置窗口点"重新检测"。

### 屏幕 ID 变了（displayplacer 的 persistent id 偶尔会变化）

在主屏显示 Mac 且为扩展布局时，到设置窗口"输入口"或"屏幕"点"重新检测"（或运行 `~/screenctl/screenctl.sh detect`）。旧配置会备份为 `config.sh.bak`，对方电脑、设备等设置会保留。

### 改了扩展排列或分辨率

主屏显示 Mac 且为扩展时，直接在系统设置里改即可，约 20 秒后自动记住；也可以在设置窗口"屏幕"里点"现在记住当前排列"，或运行 `~/screenctl/screenctl.sh save-layout`。

### 想看每一次 DDC 读数（调试延迟问题）

编辑 `~/screenctl/config.sh`，改 `LOG_READS=1`，然后 kickstart。看完后改回 `LOG_READS=0` 并 kickstart。

### 手动运行 watch 调试

先关掉守护进程：

```bash
launchctl bootout gui/$(id -u)/com.user.screenctl
```

再直接运行 watch（它是单实例的）：

```bash
~/screenctl/screenctl.sh watch
```

按 Ctrl+C 停止，然后重新加载：

```bash
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.user.screenctl.plist
```

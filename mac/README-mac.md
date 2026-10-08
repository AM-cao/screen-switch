# Mac 端使用指南

## 1. 这是什么

Mac 端由一个守护进程和一个菜单栏程序组成。守护进程通过 DDC/CI 读取主屏（共享的那块显示器）当前的输入源，判断主屏是不是正在显示这台 Mac：被切走时把主屏镜像到本机的另一块屏，切回来时恢复原来的排列。菜单栏程序提供切换入口和提示。对方电脑可以是 Windows，也可以是另一台 Mac，两台之间通过 SSH 互相代发切换请求。

安装后在 `~/screenctl/` 生成：`screenctl.sh`（主脚本）、`config.sh`（配置）、`state`（状态文件）、`cmd`（命令文件）、`screenctl.log`、`launchd.log`。`~/Library/LaunchAgents/com.user.screenctl.plist` 让守护进程在登录后自动运行。

菜单栏程序安装在 `~/Applications/ScreenSwitch.app`，由 `~/Library/LaunchAgents/com.user.screenswitch-menu.plist` 在登录后启动，日志写到 `~/screenctl/menu.log`。

## 2. 安装

### 2.1 安装 Homebrew 和依赖工具

若未装 Homebrew，执行官方安装命令：

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

然后安装 DDC 控制和屏幕布局工具：

```bash
brew install m1ddc displayplacer
```

### 2.2 接好显示器

1. 主屏接到 Mac 上（USB-C 或 HDMI 都可以，线路要能传 DDC；很多扩展坞和转接头不传）。
2. 用显示器上的按钮切到 Mac 所接的输入口，主屏应显示 Mac 桌面。在显示器菜单（OSD）里确认 DDC/CI 已打开。
3. 这台 Mac 除了主屏还有另一块屏时，打开"系统设置 → 显示器"，确认是扩展排列（而非镜像），并按需调整位置。

支持的屏幕组合：

| 这台 Mac 的屏 | 主屏被切走时 | 主屏切回来时 |
|---|---|---|
| 主屏 + 笔记本自带屏 | 主屏镜像到自带屏 | 恢复扩展，或只用主屏（关闭自带屏） |
| 主屏 + 另一块外接屏（如接两块显示器的 Mac mini） | 主屏镜像到那块外接屏 | 恢复扩展 |
| 只有主屏（Mac mini、Mac Studio） | 不调整布局 | 不调整布局 |

一台 Mac 上三块及以上的屏暂不支持。MacBook 合着盖子使用没有问题：设置时要开着盖子，之后每次切换前程序会看盖子是开是合，合着时按"只有主屏"处理。

### 2.3 运行安装脚本

在主屏正显示这台 Mac 时，在仓库的 `mac/` 目录执行：

```bash
./install.sh
```

脚本将：

- 复制 `screenctl.sh` 到 `~/screenctl/`。
- 首次安装时运行设置（`screenctl.sh setup`，见下）。
- 生成并加载守护进程的 launchd 后台项目。
- 用 Command Line Tools 自带的 `swiftc` 编译菜单栏程序，装到 `~/Applications/ScreenSwitch.app` 并设为登录后启动（没有 `swiftc` 时先运行 `xcode-select --install`）。

安装后如弹出"已添加后台项目"通知，需在"系统设置 → 通用 → 登录项与扩展"确保 bash 和 ScreenSwitch 的后台允许开关已打开。

**设置是一问一答的**，直接回车取方括号里的默认值：

1. 自动探测：哪块是主屏（接了不止一块外接显示器时用 `./install.sh "<显示器名>"` 指定）、Mac 接在主屏的哪个输入口、屏幕组合。
2. 这台电脑叫什么（菜单和提示里用）。
3. 对方电脑：主机名或 IP、是 Windows 还是 Mac、叫什么、用户名、对方电脑上本程序的路径。暂时不配就在主机名处填 `-`。
4. SSH：没有密钥时帮你生成，并打印出要在对方电脑上执行的命令。能连上对方时，自动问到对方接在哪个输入口，以及它已经配好的设备。
5. 其他设备（游戏机等）：每个设备一个名字和它所接输入口的编码。

以后要改：重新运行 `~/screenctl/screenctl.sh setup`；只增删设备用 `~/screenctl/screenctl.sh setup devices`。

**输入口的编码**是显示器给每个输入口编的数字，软件切换时发的就是它。常见的是 15 = DP 1（不少显示器的 USB-C 也用它）、16 = DP 2、17 = HDMI 1、18 = HDMI 2，但各家显示器不完全一样。每台电脑的编码由程序在主屏显示它时自己读出；游戏机的编码按接口对照上面的数字填，填错了切不过去，改掉重来即可。

### 2.4 两台电脑互相授权（SSH）

不配 SSH 也能用：切走、自动跟随都不受影响，只是主屏不在这台 Mac 上时，拿回要靠显示器按钮（显示器在非活跃口也接受指令的除外，见第 4 节）。

**让这台 Mac 能请对方切换**：把这台 Mac 的公钥（设置时会打印，也可以 `cat ~/.ssh/id_ed25519.pub`）交给对方。

- 对方是 Windows：在对方的管理员 PowerShell 里运行 `setup-sshd.ps1 -PeerPubKey "<这台 Mac 的公钥>"`（见 Windows 端说明）。
- 对方是 Mac：在对方 Mac 上运行 `~/screenctl/screenctl.sh authorize "<这台 Mac 的公钥>"`。

**让对方能请这台 Mac 切换**：

1. 打开"系统设置 → 通用 → 共享 → 远程登录"，"允许访问"选"仅这些用户"，只保留当前用户。
2. 拿到对方的公钥后运行：

```bash
~/screenctl/screenctl.sh authorize "<对方的公钥>"
```

它把公钥加进 `~/.ssh/authorized_keys`，并限定为只能调用本机的 `ssh-gate`：对方只能发 `request give peer`、`request give code <n>`、`request take`、`request info` 这几条，拿不到 shell，也不能做端口转发。

验证（期望退出码为 0、2 或 3，不应是 255）：

```bash
source ~/screenctl/config.sh; ssh -4 -o BatchMode=yes -o ConnectTimeout=3 -o StrictHostKeyChecking=accept-new "$PEER_USER@$PEER_HOST" "$PEER_PROG request give peer"; echo $?
```

注意：主屏正在对方电脑上时，这条命令返回 0 就表示对方真的把主屏切过来了。

### 2.5 配置通知权限

通知由菜单栏程序以"屏幕切换"的名义发出。首次启动时系统会询问是否允许，点"允许"；错过了就到"系统设置 → 通知 → 屏幕切换"里打开。未授权时菜单里会显示"通知未开启"。

守护进程把要提示的文字写到 `~/screenctl/notify.queue`，由菜单栏程序取走后弹出。菜单栏程序没在运行时才退回 osascript，但 launchd 拉起的后台进程用 osascript 发的通知，新版 macOS 通常会静默丢弃。

### 2.6 菜单栏

菜单栏右上角的显示器图标：正常 = 主屏在本机；变灰 = 主屏不在本机；三角形警告 = 守护进程未运行。

| 本机状态 | 菜单项 |
|---|---|
| 占有主屏 | 切给 <对方的名字>；每个设备一项"切给 <设备名>（请先打开设备）" |
| 未占有 | 拿回主屏；每个设备一项 |
| 守护进程未运行 | 拿回主屏、切给 <对方的名字>、各设备（由脚本直接执行） |

没有配置对方电脑时不显示"切给 <对方的名字>"。

本机另有一块屏时，菜单里还有：

- "拿回后的布局"：**扩展**，或 **仅大屏（关闭笔记本屏）**，主屏在本机时点选立即生效。"仅大屏"只在另一块屏是笔记本自带屏时提供。仅大屏模式下，切走主屏时先写 DDC，等主屏确实切走后才打开笔记本屏并镜像，主屏上不出现镜像画面；切走后笔记本屏万一打不开，会提示"合上再打开笔记本盖子"并每 10 秒重试。关屏只在当前登录会话有效，万一笔记本屏没亮回来：合上再打开盖子，或注销重新登录，或运行 `~/screenctl/bin/display-helper enable 1`（1 是笔记本屏通常的显示编号，以 `config.sh` 的 `LAPTOP_CGID` 为准）。
- "自动记住屏幕排列"（默认勾选）：主屏在本机且为扩展时，你在"系统设置 → 显示器 → 排列"里改了排列、主显示器或分辨率，约 20 秒后自动记住，以后"拿回"都恢复成这样（会弹通知）。
- "立即保存当前排列"：马上记住当前的扩展排列，只在主屏在本机时可点。

菜单底部有"打开日志"和"退出"。退出后不会自动重启，下次登录或执行 `launchctl kickstart gui/$(id -u)/com.user.screenswitch-menu` 再启动。

### 2.7 快捷键（默认没有）

默认不注册任何全局快捷键。想用的话编辑 `~/screenctl/config.sh`：

- `HOTKEY_PEER`：占有时切给对方，未占有时拿回主屏。
- `HOTKEY_DEVICES`：各设备的快捷键，顺序与 `DEVICES` 一一对应。

格式为修饰键 ctrl / opt / shift / cmd 加一个数字或字母，用 `+` 连接，例如 `'ctrl+opt+1'`。改完执行 `launchctl kickstart -k gui/$(id -u)/com.user.screenswitch-menu`。不需要辅助功能权限；快捷键被别的程序占用时，菜单里会显示"已被占用，未生效"。

也可以在"快捷指令" App 里建快捷指令，每个只放一个"运行 Shell 脚本"动作：`~/screenctl/screenctl.sh give peer`（切给对方）、`… give self`（拿回主屏）、`… give Switch`（切给名叫 Switch 的设备）。

### 2.8 配置文件（`~/screenctl/config.sh`）

设置会写好这些项，一般不用手改。改完执行 `launchctl kickstart -k gui/$(id -u)/com.user.screenctl`。

| 项 | 含义 |
|---|---|
| `SELF_NAME` | 这台电脑的名字；留空取电脑名 |
| `CODE_SELF` | 这台电脑所接输入口的编码，设置时读出 |
| `PEER_NAME`、`PEER_OS` | 对方电脑的名字；`windows` 或 `mac` |
| `PEER_HOST`、`PEER_USER` | 对方的主机名（如 `my-pc.local`）或 IP、用户名 |
| `PEER_PROG` | 对方电脑上本程序的路径。Windows：`C:\Users\<用户名>\ScreenSwitch\ScreenSwitch.exe`（不能有空格）；Mac：`~/screenctl/screenctl.sh` |
| `CODE_PEER` | 对方所接输入口的编码；留空时第一次"切给对方"会自动向它询问 |
| `DEVICES` | 其他设备，每项 `'名字=编码'` |
| `DIRECT_TAKE` | 1 = 拿回时先直接向主屏发切换指令，不行再请对方代发（见第 4 节） |
| `LAYOUT`、`SECOND_ID`、`SECOND_BUILTIN` | 屏幕组合，设置时探测：`second` = 另有一块屏，`none` = 只有主屏 |
| `OWNED_LAYOUT` | 拿回后的布局：`extend` 或 `external`（仅大屏） |

`PEER_HOST`、`PEER_USER`、`PEER_PROG` 任一为空，就当作没有配置对方电脑。旧版本的配置（`WIN_HOST`、`CODE_MAC`、`CODE_OTHER` 等）照常能读，程序下次写配置时会换成新名字。

## 3. 日常使用与命令

### 3.1 子命令参考

| 子命令 | 用途 |
|-------|------|
| `setup [显示器名]` | 交互式设置：探测主屏和屏幕组合，填对方电脑和其他设备，生成 `config.sh`。在主屏显示这台 Mac 时运行。旧配置备份为 `config.sh.bak`。 |
| `setup devices` | 只增删其他设备。 |
| `init [显示器名]` | 只做探测，不提问；已有的对方电脑、设备等设置原样保留。屏幕 ID 变了、换了接口时用它。 |
| `authorize "<公钥>"` | 把对方电脑的公钥加进 `~/.ssh/authorized_keys`，限定为只能调用 `ssh-gate`。 |
| `give peer` / `give self` / `give <设备名或编码>` | 切给对方 / 拿回主屏 / 切给某个设备。 |
| `take` / `release` | 手动拿回（恢复布局）/ 手动让出（镜像）。 |
| `save-layout` | 把当前扩展排列保存为"拿回"排列。 |
| `set AUTO_SAVE_LAYOUT <0\|1>` | 开关"自动记住屏幕排列"。 |
| `set OWNED_LAYOUT <extend\|external>` | 拿回后扩展，或仅大屏（关闭笔记本屏）。 |
| `status` | 查看主屏归属（owned=1\|0）、守护进程运行状态、当前布局。 |
| `request give peer` | 供对方 SSH 调用：请本机把主屏切给对方。本机占有时接受（退出码 0），未占有返回 2，守护进程停止超过 10 秒返回 3。 |
| `request give code <n>` | 供对方 SSH 调用：请本机把主屏切到编码为 n 的设备。本机的设备列表里没有这个编码时返回 1。 |
| `request take` | 供对方 SSH 调用：对方即将把主屏切给本机。未占有时守护进程立即恢复拿回后的布局并进入 8 秒宽限期，命令等布局应用完再返回（最多 3 秒，输出 `applied`；主屏还没就绪时输出 `pending`）；已占有时返回 0 不做事。 |
| `request info` | 供对方 SSH 调用：回答本机的编码、名字和设备列表。 |
| `menu-info` | 菜单栏程序读取：名字、设备、快捷键、布局模式。 |
| `watch` | 守护进程主体（launchd 调用）。单实例，0.5 秒一拍轮询。 |
| `ssh-gate` | `authorized_keys` 强制命令入口，只放行上面四条 `request`，其他命令退出码 1。 |

旧写法仍然接受：`give windows`（= 对方）、`give mac`（= 本机）、`give other`（= 设备列表里的第一个）。

### 3.2 常用操作

修改配置后重新加载守护进程：

```bash
launchctl kickstart -k gui/$(id -u)/com.user.screenctl
```

更新脚本后重新安装：

```bash
cd /path/to/repo/mac && ./install.sh
```

仅大屏模式下重启守护进程，退出的进程会先把笔记本屏开回来、新进程再关掉，屏幕闪两下。`install.sh` 会先留下 `~/screenctl/.restarting` 标记让它跳过这步；手动 `launchctl kickstart -k` 前也可以先 `touch ~/screenctl/.restarting`。

卸载：

```bash
launchctl bootout gui/$(id -u)/com.user.screenctl
launchctl bootout gui/$(id -u)/com.user.screenswitch-menu
rm ~/Library/LaunchAgents/com.user.screenctl.plist ~/Library/LaunchAgents/com.user.screenswitch-menu.plist
rm -rf ~/screenctl ~/Applications/ScreenSwitch.app
```

查看守护进程实时日志（按 Ctrl+C 退出）：

```bash
tail -f ~/screenctl/screenctl.log
```

查看 launchd 启动日志：

```bash
cat ~/screenctl/launchd.log
```

### 3.3 状态文件格式

`status` 命令输出例：

```
owned=1 1790079743
daemon=running pid=12345 age=2s
layout=extend
```

- 首行：`owned=1` 或 `owned=0`（主屏归属），后跟 Unix 时间戳（最后一次轮询的时刻）。
- 第二行：`daemon=running` 或 `daemon=stopped`；若在运行则显示 pid 和 age（距离最后轮询的秒数）。
- 第三行：`layout=extend` 或 `layout=mirror`（当前屏幕布局）。
- 可能的第四行：`busy=<pid>`（主动切换进行中）。

## 4. 行为说明

- **占有判定**：`m1ddc display <uuid> get input` 的返回值等于 `CODE_SELF`（设置时读出）才算主屏在本机。主屏不在本机时 m1ddc 仍可能返回退出码 0 和 0、-7、-102 之类的垃圾值，所以不能只看退出码；另外还会核对最大亮度（`SANITY_MAXLUM`，见常见问题）。在所有输入口都应答 DDC 的显示器上，读到的是别的口的编码，同样不算占有。
- **轮询周期**：守护进程 0.5 秒一拍；占有时每 4 拍（2 秒）、未占有时每拍（0.5 秒，`POLL_TICKS_UNOWNED`）读一次，主屏一切回来就尽快拿回。
- **去抖**：读数与当前归属不符时下一拍立刻复核。未占有时连续 2 次读到本机才拿回；占有时连续 4 次（2 秒，`RELEASE_DEBOUNCE`）读失败才让出，因为主屏在本机时 DDC 偶尔会返回垃圾值，2 次就让出会误判。拿回后 3 秒内（`SETTLE_SECS`）不读 DDC，显示器重新配置期间读数不可靠。
- **让出与拿回**：让出 = 主屏镜像到本机的另一块屏（那块屏的分辨率保持）；拿回 = 恢复保存的扩展排列，或只用主屏。只有主屏的 Mac、合着盖子的 MacBook、另一块外接屏没接着时，不做任何布局操作，只记归属。
- **先直接切，再请对方代发**（`DIRECT_TAKE=1`，默认）：点"拿回主屏"时先直接向主屏发一次切换指令。有的显示器在没切到的输入口上也接受指令，这就直接切回来了，不需要对方电脑，主屏停在游戏机上时也能拿回。显示器不理会时照常通过 SSH 请对方代发；对方也帮不上，才提示"请用显示器上的按钮切换输入源"。直接切连着 3 次都没生效、且从没成功过，之后就不再为它多等那 3 秒。
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
| 查看状态 | `~/screenctl/screenctl.sh status` | 首行显示 `owned=1` 或 `owned=0` |
| 菜单 | 点开菜单栏图标 | 占有时为"切给 <对方>"加各设备，未占有时为"拿回主屏"加各设备；图标随状态变灰或恢复 |
| 切给对方（占有时） | 菜单或 `give peer` | 约 1 秒主屏切到对方所接的输入口，本机进入镜像；3 秒内仍读得到则通知"切换未生效：主屏仍在本机"且不改布局 |
| 切给设备（占有时，先打开设备） | 菜单或 `give <设备名>` | 主屏切到该设备的口，本机进入镜像，观察 15 秒后结束，无通知；观察期内跳回则拿回并通知"没有检测到 <设备> 的信号" |
| 拿回主屏（未占有时） | 菜单或 `give self` | 对方接受后本机立即恢复拿回后的布局，显示器切过来时直接是本机桌面；8 秒内主屏没切过来则恢复镜像并通知。对方不在线时提示按显示器按钮 |
| 显示器按钮手动切换 | 用显示器按钮切到其他口，再切回这台 Mac 的口 | 显示器切换输入源约 3 秒黑屏；能读到 DDC 后约 1 秒内切为拿回后的布局 |
| 合着盖子 | 合盖后切给对方、再切回来 | 照常切换；没有"笔记本屏无法恢复"的提示；开盖后布局恢复正常 |
| 对方调用 | 在对方电脑上点"拿回主屏""切给 <设备>" | 本机日志里出现 `request: 已接受 give peer` / `give code <n>` |
| 注销重新登录 | `launchctl print gui/$(id -u)/com.user.screenctl` | 输出中有 `state = running`；注销、登录不会切换主屏 |

自动测试：`mac/tests/run-tests.sh`（约 8 分钟）和 `mac/tests/run-pair-tests.sh`（两份程序互相配对，约 1 分钟），用假的 m1ddc、displayplacer、ssh 驱动真实脚本，不碰真实显示器。

## 6. 常见问题

### 通知不显示

在"系统设置 → 通知 → 屏幕切换"里允许通知；专注模式也会拦截通知。确认菜单栏程序在运行（通知靠它弹出）。

**通知进了通知中心却没有弹横幅**：macOS 默认"镜像显示器时进入勿扰"，而 Mac 让出主屏时正处于镜像，"连接不到 <对方>""请用显示器上的按钮切换输入源"这类提示恰好都在这时发出。到"系统设置 → 通知"打开"镜像或共享显示器时允许通知"。

`~/screenctl/menu.log` 记录通知授权结果（`notify auth: granted=…`）和发送失败的原因；`~/Applications/ScreenSwitch.app/Contents/MacOS/ScreenSwitch --self-test` 第一行显示当前授权状态。

### 菜单栏里看不到图标

菜单栏图标太多时会被刘海挡住：按住 ⌘ 拖动其他图标腾出位置。确认程序在运行：`launchctl print gui/$(id -u)/com.user.screenswitch-menu | grep state`；有错误看 `~/screenctl/menu.log`。

### 按了快捷键或菜单没反应

1. 点开菜单栏图标，看最上面的状态行和有没有"快捷键已被占用"的提示。
2. 查看日志：`tail -f ~/screenctl/screenctl.log`，观察是否有错误信息。
3. 检查守护进程状态：`~/screenctl/screenctl.sh status`，看是否 `daemon=running`。
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

解决方法：在 `~/screenctl/config.sh` 把 `PEER_HOST` 改为对方的固定 IP 地址。默认脚本仅使用 IPv4（`SSH_ADDRESS_FAMILY=inet`），因为 `.local` 可能解析出多个 IPv6 地址，ssh 会逐个尝试每个地址等待满 3 秒超时。

### Mac 睡醒后以为主屏还在自己这里

旧版本会出现：Mac 睡眠时主屏被对方占去，Mac 醒来仍保持扩展、菜单显示"切给对方"。原因是显示器不回复时 m1ddc 会返回缓存里的旧数据，睡醒后恰好是睡前读到的本机编码。现在读到本机编码后还会核对主屏的最大亮度（`config.sh` 的 `SANITY_MAXLUM`，init 自动学习），日志里出现"判为缓存旧数据"说明拦下过一次。若 `SANITY_MAXLUM=0`（init 时没读到），在主屏显示 Mac 且为扩展时重新运行 init。

### 屏幕 ID 变了（displayplacer 的 persistent id 偶尔会变化）

在主屏显示 Mac 且为扩展布局时重新运行：

```bash
~/screenctl/screenctl.sh init
```

旧配置会备份为 `config.sh.bak`，对方电脑、设备等设置会保留。之后执行：

```bash
launchctl kickstart -k gui/$(id -u)/com.user.screenctl
```

### 改了扩展排列或分辨率

主屏显示 Mac 且为扩展时，直接在系统设置里改即可，约 20 秒后自动记住；也可以点菜单"立即保存当前排列"，或运行 `~/screenctl/screenctl.sh save-layout`。

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

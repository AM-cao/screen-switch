#!/bin/bash
# 安装或更新 Mac 端。用法：./install.sh
#   1. 复制 screenctl.sh 和文字表到 ~/screenctl
#   2. 编译菜单栏程序 ScreenSwitch.app 到 ~/Applications，编译 ~/screenctl/bin/display-helper
#   3. 生成两个 LaunchAgent（守护进程、菜单栏程序）的 plist 放在 ~/screenctl/launchd；
#      开机自动运行时（首次安装默认开）复制到 ~/Library/LaunchAgents；然后（重新）加载
# 不提问。还没设置过时，菜单栏程序会打开设置向导；也可以用命令设置（见仓库根目录的 AGENTS.md）。
# 重复运行是安全的：已有的 config.sh 不会被覆盖，开机自动运行的开关保持原样。
set -e

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
DEST="$HOME/screenctl"
DOMAIN="gui/$(id -u)"
AGENTS="$HOME/Library/LaunchAgents"
APP="$HOME/Applications/ScreenSwitch.app"
LABELS="com.user.screenctl com.user.screenswitch-menu"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

command -v m1ddc >/dev/null && command -v displayplacer >/dev/null \
    || { echo "请先运行 / Run first: brew install m1ddc displayplacer"; exit 1; }
command -v swiftc >/dev/null \
    || { echo "未找到 swiftc，请先安装 Command Line Tools / swiftc not found, install Command Line Tools first: xcode-select --install"; exit 1; }

# $1 = label：卸载正在运行的 agent，等它退出
unload_agent() {
    launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || return 0
    launchctl bootout "$DOMAIN/$1" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || return 0
        sleep 0.5
    done
}

# 首次安装：还没有 ~/screenctl/launchd（旧版本装过的，看 ~/Library/LaunchAgents 里有没有）
first_install=1
[ -d "$DEST/launchd" ] && first_install=0
[ -f "$AGENTS/com.user.screenctl.plist" ] && first_install=0

# ---- 守护进程脚本与文字表
mkdir -p "$DEST" "$DEST/launchd" "$DEST/bin"
install -m 755 "$HERE/screenctl.sh" "$DEST/screenctl.sh"
install -m 644 "$REPO/i18n/strings.tsv" "$DEST/strings.tsv"
echo "→ $DEST/screenctl.sh"

# ---- 编译
BUILD=$(mktemp -d)
echo "编译菜单栏程序（首次约一分钟）/ Building the menu bar app…"
swiftc -swift-version 5 -O -module-cache-path "$BUILD/cache" "$HERE"/app/ScreenSwitch/*.swift -o "$BUILD/ScreenSwitch"
# 开关笔记本屏、读显示器输入口清单的小工具
swiftc -O -module-cache-path "$BUILD/cache" "$HERE/app/display-helper.swift" -o "$BUILD/display-helper"

# ---- LaunchAgent 模板
for l in $LABELS; do
    sed "s#__HOME__#$HOME#g" "$HERE/$l.plist" > "$DEST/launchd/$l.plist"
    plutil -lint "$DEST/launchd/$l.plist" >/dev/null
done

# 重启标记：只用主屏模式下旧守护进程退出时不再先开笔记本屏（新进程马上接手），屏幕不闪
touch "$DEST/.restarting"
for l in $LABELS; do unload_agent "$l"; done

install -m 755 "$BUILD/display-helper" "$DEST/bin/display-helper"
codesign --force --sign - "$DEST/bin/display-helper" >/dev/null 2>&1 || true
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/en.lproj" "$APP/Contents/Resources/zh-Hans.lproj"
cp "$BUILD/ScreenSwitch" "$APP/Contents/MacOS/ScreenSwitch"
cp "$HERE/app/Info.plist" "$APP/Contents/Info.plist"
cp "$REPO/i18n/strings.tsv" "$APP/Contents/Resources/strings.tsv"
# 程序名（通知、系统设置里显示的）跟随系统语言
printf '"CFBundleDisplayName" = "ScreenSwitch";\n"CFBundleName" = "ScreenSwitch";\n' > "$APP/Contents/Resources/en.lproj/InfoPlist.strings"
printf '"CFBundleDisplayName" = "屏幕切换";\n"CFBundleName" = "屏幕切换";\n' > "$APP/Contents/Resources/zh-Hans.lproj/InfoPlist.strings"
codesign --force --sign - "$APP" >/dev/null 2>&1 || true
# 登记到 LaunchServices：launchd 直接运行可执行文件不会自动登记，未登记的应用发不了通知
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" || true
rm -rf "$BUILD"
echo "→ $APP"

# ---- 开机自动运行：首次安装默认开；之后保持用户的选择（设置窗口"通用"或 screenctl.sh autostart on|off）
if [ $first_install -eq 1 ] || [ -f "$AGENTS/com.user.screenctl.plist" ]; then
    mkdir -p "$AGENTS"
    for l in $LABELS; do cp -f "$DEST/launchd/$l.plist" "$AGENTS/$l.plist"; done
fi
for l in $LABELS; do
    if [ -f "$AGENTS/$l.plist" ]; then plist="$AGENTS/$l.plist"; else plist="$DEST/launchd/$l.plist"; fi
    launchctl bootstrap "$DOMAIN" "$plist"
done
echo "已启动守护进程和菜单栏程序（菜单栏右上角的显示器图标）/ Started the daemon and the menu bar app."

if [ ! -f "$DEST/config.sh" ]; then
    echo
    echo "还没有设置过：菜单栏程序会打开设置向导。开始前请用显示器上的按钮把主屏切到这台 Mac。"
    echo "Not set up yet: the menu bar app opens the setup wizard. First switch the monitor to this Mac with its buttons."
fi
sleep 2
"$DEST/screenctl.sh" status

#!/bin/bash
# 安装或更新 Mac 端。用法：./install.sh [显示器名]（显示器名只在首次安装、且接了不止一块外接显示器时需要）
#   1. 复制 screenctl.sh 到 ~/screenctl，首次安装时运行 setup（探测主屏，再问几个问题）
#   2. 安装并（重新）加载守护进程 launchd agent
#   3. 编译菜单栏程序 ScreenSwitch.app 到 ~/Applications 并设为登录后启动；编译 ~/screenctl/bin/display-helper
# 重复运行是安全的，已有的 config.sh 不会被覆盖。
set -e

HERE=$(cd "$(dirname "$0")" && pwd)
DEST="$HOME/screenctl"
DOMAIN="gui/$(id -u)"
AGENTS="$HOME/Library/LaunchAgents"
APP="$HOME/Applications/ScreenSwitch.app"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

command -v m1ddc >/dev/null && command -v displayplacer >/dev/null \
    || { echo "请先运行：brew install m1ddc displayplacer"; exit 1; }

# $1 = label：卸载正在运行的 agent，等它退出
unload_agent() {
    launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || return 0
    launchctl bootout "$DOMAIN/$1" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || return 0
        sleep 0.5
    done
}

# $1 = label：从模板生成 plist 并加载
load_agent() {
    mkdir -p "$AGENTS"
    sed "s#__HOME__#$HOME#g" "$HERE/$1.plist" > "$AGENTS/$1.plist"
    plutil -lint "$AGENTS/$1.plist" >/dev/null
    launchctl bootstrap "$DOMAIN" "$AGENTS/$1.plist"
}

# ---- 守护进程
mkdir -p "$DEST"
install -m 755 "$HERE/screenctl.sh" "$DEST/screenctl.sh"
echo "已复制 $DEST/screenctl.sh"

if [ ! -f "$DEST/config.sh" ]; then
    echo "首次安装，运行设置（主屏须正显示这台 Mac；有另一块屏时须为扩展排列）…"
    "$DEST/screenctl.sh" setup "$@"
fi

# 重启标记：仅大屏模式下旧守护进程退出时不再先开笔记本屏（新进程马上接手），屏幕不闪
touch "$DEST/.restarting"
unload_agent com.user.screenctl
load_agent com.user.screenctl
echo "已加载守护进程 com.user.screenctl"

# ---- 菜单栏程序
if command -v swiftc >/dev/null; then
    BUILD=$(mktemp -d)
    echo "编译菜单栏程序（首次约半分钟）…"
    swiftc -swift-version 5 -O -module-cache-path "$BUILD/cache" \
        "$HERE/app/ScreenSwitchMenu.swift" -o "$BUILD/ScreenSwitch"
    # 开关笔记本屏的小工具（"仅大屏"模式用）
    mkdir -p "$DEST/bin"
    swiftc -O -module-cache-path "$BUILD/cache" "$HERE/app/display-helper.swift" -o "$DEST/bin/display-helper"
    codesign --force --sign - "$DEST/bin/display-helper" >/dev/null 2>&1 || true
    unload_agent com.user.screenswitch-menu
    rm -rf "$APP"
    mkdir -p "$APP/Contents/MacOS"
    cp "$BUILD/ScreenSwitch" "$APP/Contents/MacOS/ScreenSwitch"
    cp "$HERE/app/Info.plist" "$APP/Contents/Info.plist"
    codesign --force --sign - "$APP" >/dev/null 2>&1 || true
    # 登记到 LaunchServices：launchd 直接运行可执行文件不会自动登记，未登记的应用发不了通知
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" || true
    rm -rf "$BUILD"
    load_agent com.user.screenswitch-menu
    echo "已安装 $APP，菜单栏右上角出现显示器图标"
else
    echo "未找到 swiftc，跳过菜单栏程序。安装 Command Line Tools 后重新运行本脚本：xcode-select --install"
fi

sleep 3
"$DEST/screenctl.sh" status

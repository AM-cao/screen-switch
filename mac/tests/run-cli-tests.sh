#!/bin/bash
# 给 Agent 和设置窗口用的命令（docs/settings-ui-design.md 第 7 节）：status、detect、ports、peer、pubkey、authorize、
# device、set、autostart，以及中英文。用假的 m1ddc / displayplacer / ssh / display-helper / ioreg，不碰真实显示器。
# 用法：mac/tests/run-cli-tests.sh   约半分钟；全部通过时退出码 0。
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=$HERE/../screenctl.sh
FAKE=$HERE/fake
ROOT=$(mktemp -d "${TMPDIR:-/tmp}/screenctl-cli-test.XXXXXX")
trap 'rm -rf "$ROOT"' EXIT
export M1DDC=$FAKE/m1ddc DISPLAYPLACER=$FAKE/displayplacer SSH_BIN=$FAKE/ssh DISPLAY_HELPER=$FAKE/display-helper IOREG=$FAKE/ioreg
export FAKE_SRC=$SRC SCREENSWITCH_LANG=zh
S="/bin/bash $SRC"
fresh() {
  export FAKE_DIR=$ROOT/$1; mkdir -p $FAKE_DIR/home $FAKE_DIR/fakehome
  export SCREENCTL_DIR=$FAKE_DIR/home SCREENCTL_NOTIFY_FILE=$FAKE_DIR/notify.txt LAUNCH_AGENTS_DIR=$FAKE_DIR/LaunchAgents
  export HOME=$FAKE_DIR/fakehome
  export FAKE_NOSIGNAL="" FAKE_SSH_RC=0 FAKE_SSH_SLEEP=0 FAKE_SSH_OUT=""
  echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; : > $SCREENCTL_NOTIFY_FILE
}
pass=0; fail=0
ok()  { echo "  PASS $*"; pass=$((pass+1)); }
bad() { echo "  FAIL $*"; fail=$((fail+1)); }
C() { cat $SCREENCTL_DIR/config.sh; }
# json <JSON> <python 表达式，j 为解析结果>：打印表达式的值
json() { python3 -c 'import json,sys; j=json.loads(sys.argv[1]); print(eval(sys.argv[2]))' "$1" "$2" 2>&1; }
# run <命令…>：OUT = 标准输出加标准错误，RC = 退出码
run() { OUT=$("$@" 2>&1); RC=$?; }
expect_done() { [ $RC -eq 0 ] && [ "$OUT" = "已完成。" ] && ok "$1" || bad "$1: rc=$RC [$OUT]"; }
expect_error() { [ $RC -eq 1 ] && [ "$(printf '%s\n' "$OUT" | wc -l | tr -d ' ')" = 1 ] && case "$OUT" in "error: "*"$2"*) true ;; *) false ;; esac \
  && ok "$1" || bad "$1: rc=$RC [$OUT]"; }

echo "== 还没设置过"
fresh blank
run $S status
[ $RC -eq 0 ] && [ "$(json "$OUT" 'j["configured"], j["self"]["code"], j["mainScreen"], j["problems"], j["owned"], j["trayRunning"]')" = "(False, None, '', ['not-configured'], None, False)" ] \
  && ok "status：configured=false，本机编码 null，problems=[not-configured]" || bad "status blank: rc=$RC $OUT"
[ "$(json "$OUT" 'sorted(j.keys())')" = "['autoSave', 'autostart', 'configured', 'devices', 'directTake', 'layout', 'mainScreen', 'owned', 'ownedLayout', 'peer', 'problems', 'self', 'trayRunning']" ] \
  && [ "$(json "$OUT" 'sorted(j["peer"].keys()), sorted(j["self"].keys())')" = "(['code', 'configured', 'host', 'name', 'os', 'user'], ['code', 'name'])" ] \
  && ok "status 的字段与规范一致" || bad "status keys: $(json "$OUT" 'sorted(j.keys())')"
[ "$(printf '%s\n' "$OUT" | wc -l | tr -d ' ')" = 1 ] && ok "status 只有一行" || bad "status lines"
run $S displays
[ "$(json "$OUT" '[(d["name"], d["controllable"], d["code"], d["builtin"]) for d in j["displays"]], j["lidClosed"]')" = "([('P275MV', True, 15, False), ('笔记本自带屏', False, None, True)], False)" ] \
  && ok "displays：主屏能控制、读到 15；自带屏不能控制" || bad "displays: $OUT"
run $S ports --main P275MV
[ "$(json "$OUT" 'j["monitorKnown"], [(p["code"], p["name"], p["use"], p["fromMonitor"]) for p in j["ports"]]')" \
  = "(True, [(15, 'DisplayPort 1', 'self', True), (16, 'DisplayPort 2', 'none', True), (17, 'HDMI 1', 'none', True), (18, 'HDMI 2', 'none', True)])" ] \
  && ok "ports --main（向导，还没写配置）：读出显示器的四个口，本机是 15" || bad "ports --main: $OUT"
[ ! -f $SCREENCTL_DIR/config.sh ] && ok "displays、ports --main 不写配置" || bad "config written before detect"
run $S give self
expect_error "没设置过时切换：error" "还没有设置过"

echo "== detect"
touch $FAKE_DIR/lid_closed
run $S detect
expect_error "合着盖子：error，要求开盖" "打开盖子"
rm -f $FAKE_DIR/lid_closed
run $S detect
expect_done "detect：只有一块能控制，直接用它"
C | grep -q "^MAIN_NAME='P275MV'" && C | grep -q "^CODE_SELF=15" && C | grep -q "^MAIN_PORTS='15 16 17 18'" \
  && ok "写入主屏、本机编码和输入口清单" || bad "detect config: $(C | grep '^MAIN_NAME\|^CODE_SELF\|^MAIN_PORTS' | tr '\n' ' ')"
run $S status
[ "$(json "$OUT" 'j["configured"], j["mainScreen"], j["self"]["code"], j["layout"], j["ownedLayout"], j["autoSave"], j["directTake"], j["peer"]["configured"], j["devices"]')" \
  = "(True, 'P275MV', 15, 'second', 'extend', True, True, False, [])" ] && ok "status：设置过，主屏 P275MV，本机 15，扩展" || bad "status after detect: $OUT"
json "$OUT" 'j["problems"]' | grep -q "daemon-not-running" && ok "problems 里有 daemon-not-running" || bad "problems: $(json "$OUT" 'j["problems"]')"
run $S ports
[ "$(json "$OUT" 'j["monitorKnown"], [(p["code"], p["use"]) for p in j["ports"]]')" = "(True, [(15, 'self'), (16, 'none'), (17, 'none'), (18, 'none')])" ] \
  && ok "ports：清单来自配置" || bad "ports: $OUT"
touch $FAKE_DIR/caps_fail
run $S detect --main P275MV
expect_done "detect --main：读不出清单也成功"
C | grep -q "^MAIN_PORTS='15 16 17 18'" && ok "同一块显示器读不出清单时沿用上次的" || bad "caps kept: $(C | grep '^MAIN_PORTS')"
rm -f $FAKE_DIR/caps_fail
run $S detect --main NOPE
expect_error "--main 写了不存在的显示器：error" "NOPE"
echo ext > $FAKE_DIR/topology; touch $FAKE_DIR/all_inputs
run $S detect
[ $RC -eq 1 ] && [ "$(printf '%s\n' "$OUT" | head -1 | cut -c1-7)" = "error: " ] && printf '%s\n' "$OUT" | grep -qx "  P275MV  P275MV" && printf '%s\n' "$OUT" | grep -qx "  SIDE24  SIDE 24" \
  && ok "两块都能控制：退出码 1，列出候选（型号  名字）" || bad "many: rc=$RC [$OUT]"
run $S detect --main SIDE24
expect_done "带 --main 重跑"
C | grep -q "^MAIN_NAME='SIDE24'" && C | grep -q "^MAIN_PORTS=''" && ok "换了显示器：清单作废（这块读不出）" || bad "switch monitor: $(C | grep '^MAIN_NAME\|^MAIN_PORTS' | tr '\n' ' ')"
rm -f $FAKE_DIR/topology $FAKE_DIR/all_inputs
run $S detect --main P275MV
expect_done "换回 P275MV"
echo 1 > $SCREENCTL_DIR/.direct_fail
run $S detect
[ ! -f $SCREENCTL_DIR/.direct_fail ] && ok "detect 清掉"直接切换从未成功"的记录" || bad ".direct_fail kept"

echo "== 对方电脑"
run $S peer set --os windows --host pc.local --user bob
expect_done "peer set"
C | grep -q "^PEER_NAME='Windows'" && C | grep -q "^PEER_HOST='pc.local'" && C | grep -q "^PEER_USER='bob'" && C | grep -qF "PEER_PROG='C:\\Users\\bob\\ScreenSwitch\\ScreenSwitch.exe'" \
  && ok "名字默认 Windows，程序路径按用户名生成" || bad "peer set: $(C | grep '^PEER_' | tr '\n' ' ')"
run $S peer set --os mac --host mini.local --user amy --name '书房 mini'
C | grep -q "^PEER_NAME='书房 mini'" && C | grep -q "^PEER_OS=mac" && C | grep -q "^PEER_PROG='~/screenctl/screenctl.sh'" \
  && ok "改成 Mac：名字、程序路径跟着变" || bad "peer set mac: $(C | grep '^PEER_' | tr '\n' ' ')"
run $S peer set --os linux --host x --user y
expect_error "系统写错：error" "peer set"
run $S peer set --os mac --host mini.local
expect_error "缺用户名：error" "peer set"
run $S status
[ "$(json "$OUT" 'j["peer"]')" = "{'configured': True, 'name': '书房 mini', 'os': 'mac', 'host': 'mini.local', 'user': 'amy', 'code': None}" ] \
  && ok "status.peer" || bad "status peer: $(json "$OUT" 'j["peer"]')"
export FAKE_SSH_OUT="proto=3 os=mac code=16 name=$(printf %s mini | base64) devices="
run $S peer test
[ $RC -eq 0 ] && [ "$(json "$OUT" 'j')" = "{'name': 'mini', 'code': 16, 'codeSaved': True}" ] && C | grep -q "^CODE_PEER='16'" \
  && ok "peer test：JSON，取到编码写入配置" || bad "peer test: rc=$RC [$OUT] $(C | grep '^CODE_PEER')"
export FAKE_SSH_OUT="proto=3 os=windows code=17 name=$(printf %s PC | base64) devices="
cp $SCREENCTL_DIR/config.sh $FAKE_DIR/before
run $S peer test --os windows --host other.local --user carl
[ $RC -eq 0 ] && [ "$(json "$OUT" 'j')" = "{'name': 'PC', 'code': 17, 'codeSaved': False}" ] && cmp -s $SCREENCTL_DIR/config.sh $FAKE_DIR/before \
  && grep -q "carl@other.local.*ScreenSwitch.exe request info" $FAKE_DIR/ssh.log \
  && ok "peer test --os --host --user（向导）：按给的地址试连，不写配置" || bad "peer test temp: rc=$RC [$OUT] $(tail -1 $FAKE_DIR/ssh.log)"
export FAKE_SSH_OUT="Permission denied (publickey)." FAKE_SSH_RC=255
run $S peer test
expect_error "peer test 连不上：error 带上 ssh 的原因" "Permission denied"
export FAKE_SSH_OUT="" FAKE_SSH_RC=0
run $S ports
[ "$(json "$OUT" '[(p["code"], p["use"]) for p in j["ports"]]')" = "[(15, 'self'), (16, 'peer'), (17, 'none'), (18, 'none')]" ] && ok "ports：16 是对方" || bad "ports peer: $OUT"
run $S peer code 15
expect_error "peer code 不能是本机的口" "这台电脑"
run $S peer code 18
expect_done "peer code 18"
C | grep -q "^CODE_PEER='18'" && ok "对方改到 18" || bad "peer code: $(C | grep '^CODE_PEER')"
run $S peer code 16

echo "== 设备"
run $S device add 17 Switch 2
expect_done "device add（名字取其余参数，用空格连接）"
C | grep -q "'Switch 2=17'" && ok "写入 'Switch 2=17'" || bad "device add: $(C | sed -n '/^DEVICES=(/,/^)/p' | tr '\n' ' ')"
run $S device add 17 PS5
expect_error "编码重复：error" "17"
run $S device add 15 PS5
expect_error "本机的口：error" "这台电脑"
run $S device add 16 PS5
expect_error "对方的口：error" "书房 mini"
run $S device add 300 PS5
expect_error "编码超出 1–255：error" "300"
run $S device add 18 "Switch 2"
expect_error "名字重复：error" "Switch 2"
run $S device add 18 "客厅 PS5"
expect_done "添加第二个设备"
run $S device rename 17 NS2
expect_done "device rename"
C | grep -q "'NS2=17'" && ! C | grep -q "Switch 2" && ok "改名" || bad "rename: $(C | sed -n '/^DEVICES=(/,/^)/p' | tr '\n' ' ')"
run $S device rename 19 X
expect_error "改名不存在的设备：error" "19"
run $S ports
[ "$(json "$OUT" '[(p["code"], p["use"], p["device"]) for p in j["ports"]]')" = "[(15, 'self', ''), (16, 'peer', ''), (17, 'device', 'NS2'), (18, 'device', '客厅 PS5')]" ] \
  && ok "ports：设备的口带名字" || bad "ports devices: $OUT"
run $S device add 27 Dock
run $S ports
[ "$(json "$OUT" '[(p["code"], p["name"], p["fromMonitor"]) for p in j["ports"] if p["code"] == 27]')" = "[(27, '', False)]" ] \
  && ok "手动添加的口：没有标准名，fromMonitor=false" || bad "manual port: $OUT"

echo "== 快捷键"
run $S set hotkey peer Ctrl+Alt+1
expect_done "set hotkey peer（Windows 的写法 Ctrl+Alt 也认）"
C | grep -q "^HOTKEY_PEER='ctrl+opt+1'" && ok "规范成 ctrl+opt+1" || bad "hotkey peer: $(C | grep '^HOTKEY_PEER')"
run $S set hotkey 18 cmd+shift+P
expect_done "set hotkey <编码>"
run $S set hotkey 27 opt+ctrl+1
expect_error "同一个组合给了别的动作：error" "ctrl+opt+1"
run $S set hotkey 17 x
expect_error "没有修饰键：error" "x"
run $S set hotkey 99 ctrl+9
expect_error "没有这个设备：error" "99"
run $S status
[ "$(json "$OUT" '[(d["name"], d["code"], d["hotkey"]) for d in j["devices"]]')" = "[('NS2', 17, ''), ('客厅 PS5', 18, 'shift+cmd+p'), ('Dock', 27, '')]" ] \
  && ok "status.devices 带快捷键" || bad "status devices: $(json "$OUT" 'j["devices"]')"
run $S device remove 17
expect_done "device remove"
sed -n '/^HOTKEY_DEVICES=(/,/^)/p' $SCREENCTL_DIR/config.sh | grep -q "'shift+cmd+p'" && run $S status \
  && [ "$(json "$OUT" '[(d["code"], d["hotkey"]) for d in j["devices"]]')" = "[(18, 'shift+cmd+p'), (27, '')]" ] \
  && ok "删掉设备后，其余设备的快捷键仍对得上" || bad "hotkeys after remove: $(json "$OUT" 'j["devices"]')"
run $S set hotkey peer none
C | grep -q "^HOTKEY_PEER=''" && ok "set hotkey peer none 清除" || bad "hotkey none"
$S menu-info | grep -qx "device=18	客厅 PS5	shift+cmd+p" && ok "menu-info 给菜单栏程序的设备和快捷键" || bad "menu-info: $($S menu-info | tr '\n' '|')"

echo "== 其他设置"
run $S set auto-save off
expect_done "set auto-save off"
run $S set direct-take off
expect_done "set direct-take off"
run $S set owned-layout main-only
expect_done "set owned-layout main-only"
C | grep -q "^AUTO_SAVE_LAYOUT=0" && C | grep -q "^DIRECT_TAKE=0" && C | grep -q "^OWNED_LAYOUT='external'" && ok "写进配置（只用主屏 = OWNED_LAYOUT=external）" || bad "set: $(C | grep '^AUTO_SAVE\|^DIRECT_TAKE=\|^OWNED' | tr '\n' ' ')"
run $S status
[ "$(json "$OUT" 'j["autoSave"], j["directTake"], j["ownedLayout"]')" = "(False, False, 'main-only')" ] && ok "status 跟着变" || bad "status set: $OUT"
run $S set owned-layout sideways
expect_error "取值写错：error" "set"
run $S set after-take "echo hi"
expect_error "Mac 没有 after-take：error" "set"
run $S set AUTO_SAVE_LAYOUT 1
C | grep -q "^AUTO_SAVE_LAYOUT=1" && ok "旧写法 set AUTO_SAVE_LAYOUT 1 照旧可用" || bad "old set"

echo "== 公钥与授权"
run $S pubkey
[ $RC -eq 0 ] && case "$OUT" in "ssh-ed25519 "*) true ;; *) false ;; esac && [ -f $HOME/.ssh/id_ed25519 ] && ok "pubkey：没有就生成（在假的家目录里）" || bad "pubkey: rc=$RC [$OUT]"
k1=$OUT; run $S pubkey; [ "$OUT" = "$k1" ] && ok "再运行打印同一把" || bad "pubkey changed"
run $S authorize "ssh-ed25519 AAAAPEERKEY bob@pc"
expect_done "authorize"
grep -q "ssh-gate\",no-port-forwarding.* ssh-ed25519 AAAAPEERKEY bob@pc$" $HOME/.ssh/authorized_keys && ok "写进 authorized_keys，限定为 ssh-gate" || bad "authorize: $(cat $HOME/.ssh/authorized_keys)"
run $S authorize "hello"
expect_error "不是公钥：error" "公钥"

echo "== 开机自动运行"
mkdir -p $SCREENCTL_DIR/launchd
for l in com.user.screenctl com.user.screenswitch-menu; do echo "<plist>$l</plist>" > $SCREENCTL_DIR/launchd/$l.plist; done
run $S autostart on
expect_done "autostart on"
[ -f $LAUNCH_AGENTS_DIR/com.user.screenctl.plist ] && [ -f $LAUNCH_AGENTS_DIR/com.user.screenswitch-menu.plist ] && ok "两个 LaunchAgent 放进去" || bad "autostart on: $(ls $LAUNCH_AGENTS_DIR)"
run $S status; [ "$(json "$OUT" 'j["autostart"]')" = True ] && ok "status.autostart=true" || bad "autostart status on"
run $S autostart off
expect_done "autostart off"
[ -z "$(ls $LAUNCH_AGENTS_DIR)" ] && ok "两个都移走" || bad "autostart off: $(ls $LAUNCH_AGENTS_DIR)"
run $S status; [ "$(json "$OUT" 'j["autostart"]')" = False ] && ok "status.autostart=false" || bad "autostart status off"
run $S autostart maybe
expect_error "取值写错：error" "autostart"

echo "== 清除对方电脑"
run $S peer clear
expect_done "peer clear"
run $S status
[ "$(json "$OUT" 'j["peer"]')" = "{'configured': False, 'name': '', 'os': '', 'host': '', 'user': '', 'code': None}" ] && ok "对方的设置都清空" || bad "peer clear: $(json "$OUT" 'j["peer"]')"
run $S peer test
expect_error "没有对方时 peer test：error" "对方电脑"

echo "== 英文"
export SCREENSWITCH_LANG=en
run $S set auto-save on
[ "$OUT" = "Done." ] && ok "英文：Done." || bad "en done: [$OUT]"
run $S device add 15 X
expect_en() { [ $RC -eq 1 ] && [ "$OUT" = "$2" ] && ok "$1" || bad "$1: [$OUT]"; }
expect_en "英文：error 的原因也是英文" "error: Input 15 is the one this computer uses"
run $S displays
[ "$(json "$OUT" '[d["name"] for d in j["displays"]]')" = "['P275MV', 'Built-in screen']" ] && ok "英文：自带屏的名字" || bad "en displays: $OUT"
export SCREENSWITCH_LANG=zh

echo "== 状态与问题"
echo "owned=1 $(date +%s)" > $SCREENCTL_DIR/state; echo $$ > $SCREENCTL_DIR/watch.pid
printf 'hotkey-failed:peer\nnotifications-off\n' > $SCREENCTL_DIR/menu.problems; echo $$ > $SCREENCTL_DIR/menu.pid
run $S status
[ "$(json "$OUT" 'j["trayRunning"], j["owned"], j["problems"]')" = "(True, True, ['hotkey-failed:peer', 'notifications-off'])" ] \
  && ok "守护进程在跑：trayRunning、owned；菜单栏程序报的问题" || bad "status running: $OUT"
rm -f $SCREENCTL_DIR/menu.pid
run $S status
[ "$(json "$OUT" 'j["problems"]')" = "['menu-not-running']" ] && ok "菜单栏程序没在运行：menu-not-running，不再用它留下的问题" || bad "menu down: $(json "$OUT" 'j["problems"]')"
rm -f $SCREENCTL_DIR/state $SCREENCTL_DIR/watch.pid $SCREENCTL_DIR/menu.problems

echo; echo "PASS=$pass FAIL=$fail"
[ $fail -eq 0 ]

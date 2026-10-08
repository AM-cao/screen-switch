#!/bin/bash
# 配对测试（设计文档第 12 节）：在同一台机器上起两份 screenctl.sh，当作两台 Mac——
# A 是开着盖子的 MacBook（主屏 + 自带屏），B 是只接主屏的 Mac mini——共用一块假显示器，
# 一方的 ssh 直接交给另一方的 ssh-gate 执行。用来验证两台同系统的电脑能互相切换（没有真机可测）。
# 用法：mac/tests/run-pair-tests.sh   约 1 分钟；全部通过时退出码 0。
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=$HERE/../screenctl.sh
FAKE=$HERE/fake
ROOT=$(mktemp -d "${TMPDIR:-/tmp}/screenctl-pair.XXXXXX")
MON=$ROOT/mon
mkdir -p $MON $ROOT/a/home $ROOT/b/home
trap 'kill $PA $PB 2>/dev/null; rm -rf "$ROOT"' EXIT
pass=0; fail=0
ok()  { echo "  PASS $*"; pass=$((pass+1)); }
bad() { echo "  FAIL $*"; fail=$((fail+1)); }
wait_for() { local t=$1; shift; local i; for ((i=0;i<t*10;i++)); do eval "$@" && { echo "$((i/10)).$((i%10))"; return 0; }; sleep 0.1; done; return 1; }

# 每台"电脑"一份环境：自己的安装目录、屏幕状态、所接的输入口；显示器（FAKE_MON）共用；ssh 连到对方
write_env() {   # $1 = a | b，$2 = 本机所接输入口的编码，$3 = 对方
  cat > $ROOT/$1.env <<EOF
export FAKE_DIR=$ROOT/$1 FAKE_MON=$MON FAKE_SELF=$2 FAKE_NOSIGNAL="" FAKE_SSH_RC=0 FAKE_SSH_SLEEP=0 FAKE_SSH_OUT=""
export SCREENCTL_DIR=$ROOT/$1/home SCREENCTL_NOTIFY_FILE=$ROOT/$1/notify.txt
export M1DDC=$FAKE/m1ddc DISPLAYPLACER=$FAKE/displayplacer SSH_BIN=$FAKE/ssh DISPLAY_HELPER=$FAKE/display-helper IOREG=$FAKE/ioreg
export FAKE_SRC=$SRC FAKE_PEER_ENV=$ROOT/$3.env
EOF
}
write_env a 15 b
write_env b 16 a
A() { ( . $ROOT/a.env; /bin/bash $SRC "$@" ); }
B() { ( . $ROOT/b.env; /bin/bash $SRC "$@" ); }
mon() { cat $MON/input; }
ownedA() { cut -d' ' -f1 $ROOT/a/home/state 2>/dev/null; }
ownedB() { cut -d' ' -f1 $ROOT/b/home/state 2>/dev/null; }
layA() { cat $ROOT/a/layout; }
notifiedA() { grep -qF "$1" $ROOT/a/notify.txt 2>/dev/null; }
notifiedB() { grep -qF "$1" $ROOT/b/notify.txt 2>/dev/null; }

echo "== 两台各自设置"
echo extend > $ROOT/a/layout; echo extend > $ROOT/b/layout; echo solo > $ROOT/b/topology
: > $ROOT/a/notify.txt; : > $ROOT/b/notify.txt
echo 16 > $MON/input; B init >/dev/null && grep -q "^CODE_SELF=16" $ROOT/b/home/config.sh && grep -q "^LAYOUT=none" $ROOT/b/home/config.sh \
  && ok "B（mini）：读出自己的编码 16，只有主屏" || bad "B init: $(grep '^CODE_SELF\|^LAYOUT' $ROOT/b/home/config.sh | tr '\n' ' ')"
echo 15 > $MON/input; A init >/dev/null && grep -q "^CODE_SELF=15" $ROOT/a/home/config.sh && grep -q "^LAYOUT=second" $ROOT/a/home/config.sh \
  && ok "A（MacBook）：读出自己的编码 15，另有自带屏" || bad "A init: $(grep '^CODE_SELF\|^LAYOUT' $ROOT/a/home/config.sh | tr '\n' ' ')"
# 两边都不填对方的编码：第一次切换时互相询问
cat >> $ROOT/a/home/config.sh <<CFG
SELF_NAME='MacBook'
PEER_NAME='mini'
PEER_OS=mac
PEER_HOST='mini.local'
PEER_USER='u'
PEER_PROG='~/screenctl/screenctl.sh'
DEVICES=('Switch=17')
OTHER_OBSERVE=4
SSH_KEY='$ROOT/nokey'
CFG
cat >> $ROOT/b/home/config.sh <<CFG
SELF_NAME='mini'
PEER_NAME='MacBook'
PEER_OS=mac
PEER_HOST='macbook.local'
PEER_USER='u'
PEER_PROG='~/screenctl/screenctl.sh'
OTHER_OBSERVE=4
SSH_KEY='$ROOT/nokey'
CFG
( . $ROOT/a.env; exec /bin/bash $SRC watch ) >/dev/null 2>&1 & PA=$!
( . $ROOT/b.env; exec /bin/bash $SRC watch ) >/dev/null 2>&1 & PB=$!
wait_for 6 '[ "$(ownedA)" = owned=1 ] && [ "$(ownedB)" = owned=0 ]' >/dev/null && ok "启动后：主屏在 A，B 未占有" || bad "start: A=$(ownedA) B=$(ownedB)"

echo "== A 切给 B"
A give peer >/dev/null
t=$(wait_for 12 '[ "$(mon)" = 16 ] && [ "$(ownedA)" = owned=0 ] && [ "$(ownedB)" = owned=1 ]') && ok "主屏到了 B：A 让出、B 占有 ${t}s" || bad "A→B: mon=$(mon) A=$(ownedA) B=$(ownedB)"
[ "$(layA)" = mirror ] && ok "A 把主屏镜像到自带屏" || bad "A layout: $(layA)"
grep -q "^CODE_PEER='16'" $ROOT/a/home/config.sh && ok "A 用 request info 问到 B 的编码并记下" || bad "A CODE_PEER: $(grep '^CODE_PEER' $ROOT/a/home/config.sh)"
grep -q "request: 已接受 take" $ROOT/b/home/screenctl.log && ok "B 在主屏切过来之前收到了 request take" || bad "B got no request take"
grep -q "request give" $ROOT/a/ssh.log && bad "A 切走时不该请对方代发" || ok "A 占有时自己写 DDC，不用对方代发"
sleep 5   # 让 B 的宽限期和稳定期过去
[ "$(ownedB)" = owned=1 ] && ok "之后 B 保持占有" || bad "B lost ownership: $(ownedB)"

echo "== A 拿回（请 B 代发）"
: > $ROOT/a/notify.txt
A give self >/dev/null
t=$(wait_for 12 '[ "$(mon)" = 15 ] && [ "$(ownedA)" = owned=1 ] && [ "$(ownedB)" = owned=0 ]') && ok "主屏回到 A：A 占有、B 让出 ${t}s" || bad "A take back: mon=$(mon) A=$(ownedA) B=$(ownedB)"
[ "$(layA)" = extend ] && ok "A 恢复扩展" || bad "A layout after take: $(layA)"
notifiedA "已请求 mini 交还主屏" && ok "A 的提示里用的是对方的名字 mini" || bad "A notify: $(cat $ROOT/a/notify.txt)"
grep -q "~/screenctl/screenctl.sh request give peer" $ROOT/a/ssh.log && ok "A 发给 B 的是 request give peer（两台 Mac 之间不出现 windows）" || bad "A ssh: $(tail -2 $ROOT/a/ssh.log)"
grep -q "^CODE_PEER='15'" $ROOT/b/home/config.sh && ok "B 同样问到并记下 A 的编码" || bad "B CODE_PEER: $(grep '^CODE_PEER' $ROOT/b/home/config.sh)"
sleep 5

echo "== B 请 A 切给游戏机"
( . $ROOT/b.env; /bin/bash $SRC give Switch ) >/dev/null 2>&1 && bad "B 还没有这个设备却接受了" || ok "B 的设备列表里没有 Switch：拒绝这个名字"
echo "DEVICES=('Switch=17')" >> $ROOT/b/home/config.sh; echo reload > $ROOT/b/home/cmd; sleep 1.5
: > $ROOT/b/notify.txt
B give Switch >/dev/null
t=$(wait_for 10 '[ "$(mon)" = 17 ] && [ "$(ownedA)" = owned=0 ]') && ok "B 未占有时点"切给 Switch"：A 代为切过去 ${t}s" || bad "B→device: mon=$(mon) A=$(ownedA)"
notifiedB "已请求 MacBook 切给 Switch" && ok "B 的提示：已请求 MacBook 切给 Switch" || bad "B notify: $(cat $ROOT/b/notify.txt)"
grep -q "request give code 17" $ROOT/b/ssh.log && ok "B 发的是 request give code 17" || bad "B ssh: $(tail -1 $ROOT/b/ssh.log)"
sleep 6   # 等 A 的观察期结束

echo "== 主屏在游戏机上时 A 拿回：两台都不占有"
: > $ROOT/a/notify.txt
A give self >/dev/null
wait_for 10 'notifiedA "也不占有主屏"' >/dev/null
notifiedA "mini 也不占有主屏" && notifiedA "请用显示器上的按钮切换输入源" && [ "$(mon)" = 17 ] && ok "提示按显示器按钮，主屏不动" || bad "both unowned: $(cat $ROOT/a/notify.txt) mon=$(mon)"

echo "== 对方没有配置的设备"
echo 16 > $MON/input; wait_for 8 '[ "$(ownedB)" = owned=1 ]' >/dev/null || bad "setup: B not owned"
echo "DEVICES=('Switch=17' 'PS5=18')" >> $ROOT/a/home/config.sh; echo reload > $ROOT/a/home/cmd; sleep 1.5
: > $ROOT/a/notify.txt
A give PS5 >/dev/null
wait_for 10 '[ -s $ROOT/a/notify.txt ]' >/dev/null
notifiedA "它那边可能没有配置 PS5" && [ "$(mon)" = 16 ] && ok "B 不认识这个设备：A 得到明确的提示，主屏不动" || bad "unknown device: $(cat $ROOT/a/notify.txt) mon=$(mon)"

echo "== 显示器在非活跃口也接受指令：A 直接拿回，B 自己发现并让出"
touch $MON/all_inputs
: > $ROOT/a/notify.txt; : > $ROOT/a/order.log
A give self >/dev/null
t=$(wait_for 12 '[ "$(mon)" = 15 ] && [ "$(ownedA)" = owned=1 ] && [ "$(ownedB)" = owned=0 ]') && ok "A 占有、B 让出 ${t}s" || bad "direct pair: mon=$(mon) A=$(ownedA) B=$(ownedB)"
head -1 $ROOT/a/order.log | grep -qx "ddc set 15" && ok "A 先直接写了自己的编码" || bad "A order: $(tr '\n' ';' < $ROOT/a/order.log)"
notifiedA "按钮" && bad "直接切换成功却提示按按钮: $(cat $ROOT/a/notify.txt)" || ok "没有提示按显示器按钮"

echo; echo "PASS=$pass FAIL=$fail"
[ $fail -eq 0 ]

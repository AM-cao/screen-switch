#!/bin/bash
# 用假的 m1ddc / displayplacer / ssh / display-helper / ioreg 测试 screenctl.sh 的状态机、跨机协议与各流程，不碰真实显示器。
# 用法：mac/tests/run-tests.sh   约 7 分钟；全部通过时退出码 0。两台电脑互相配对的测试在 run-pair-tests.sh。
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=$HERE/../screenctl.sh
FAKE=$HERE/fake
ROOT=$(mktemp -d "${TMPDIR:-/tmp}/screenctl-test.XXXXXX")
trap 'kill $DPID 2>/dev/null; rm -rf "$ROOT"' EXIT
export M1DDC=$FAKE/m1ddc DISPLAYPLACER=$FAKE/displayplacer SSH_BIN=$FAKE/ssh DISPLAY_HELPER=$FAKE/display-helper IOREG=$FAKE/ioreg
export FAKE_SRC=$SRC SCREENSWITCH_LANG=zh
S="/bin/bash $SRC"
# 换一套全新的假环境（新的显示器状态、新的安装目录）：$1 = 场景名
fresh() {
  kill $DPID 2>/dev/null; wait $DPID 2>/dev/null
  export FAKE_DIR=$ROOT/$1; mkdir -p $FAKE_DIR/home
  export SCREENCTL_DIR=$FAKE_DIR/home SCREENCTL_NOTIFY_FILE=$FAKE_DIR/notify.txt
  export FAKE_NOSIGNAL="" FAKE_SSH_RC=0 FAKE_SSH_SLEEP=0 FAKE_SSH_OUT=""
  echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; : > $SCREENCTL_NOTIFY_FILE
}
fresh main
pass=0; fail=0
ok()  { echo "  PASS $*"; pass=$((pass+1)); }
bad() { echo "  FAIL $*"; fail=$((fail+1)); }
expect_rc() { local want=$1; shift; "$@" >/dev/null 2>&1; local rc=$?; [ $rc -eq $want ] && ok "rc=$rc: ${*:3}" || bad "rc=$rc want $want: ${*:3}"; }
owned() { cut -d' ' -f1 $SCREENCTL_DIR/state 2>/dev/null; }
age() { echo $(( $(date +%s) - $(cut -d' ' -f2 $SCREENCTL_DIR/state 2>/dev/null || date +%s) )); }
wait_for() { # 秒 条件…
  local t=$1; shift; local i; for ((i=0;i<t*10;i++)); do eval "$@" && { echo "$((i/10)).$((i%10))"; return 0; }; sleep 0.1; done; return 1; }
notified() { grep -qF "$1" $SCREENCTL_NOTIFY_FILE 2>/dev/null; }
start_daemon() { $S watch >/dev/null 2>&1 & DPID=$!; sleep 0.3; }
stop_daemon() { kill $DPID 2>/dev/null; wait $DPID 2>/dev/null; }

echo "== init"
echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout
$S init >/dev/null && ok "init" || bad "init"
grep -q "MIRROR_ARGS" $SCREENCTL_DIR/config.sh && grep -q "id:11111111-2222-3333-4444-555555555555+AAAAAAAA" $SCREENCTL_DIR/config.sh && ok "mirror args" || bad "mirror args"
grep -q "^CODE_SELF=15" $SCREENCTL_DIR/config.sh && ok "CODE_SELF=15（init 读出本机编码）" || bad "CODE_SELF"
grep -q "^SANITY_MAXLUM=100" $SCREENCTL_DIR/config.sh && ok "SANITY_MAXLUM=100（init 学到最大亮度）" || bad "SANITY_MAXLUM"
grep -q "^MAIN_NAME='P275MV'" $SCREENCTL_DIR/config.sh && ok "init 不带名字时选出唯一的外接显示器" || bad "auto main name"
grep -q "^PEER_HOST=''" $SCREENCTL_DIR/config.sh && grep -q "^DEVICES=($" $SCREENCTL_DIR/config.sh && ! grep -q "^WIN_HOST\|^CODE_MAC\|^CODE_OTHER" $SCREENCTL_DIR/config.sh \
  && ok "新配置不带任何对方电脑和设备，只写新名字" || bad "fresh config: $(grep '^PEER_HOST\|^WIN_\|^CODE_' $SCREENCTL_DIR/config.sh | tr '\n' ' ')"
grep -q "^LAYOUT=second" $SCREENCTL_DIR/config.sh && grep -q "^SECOND_BUILTIN=1" $SCREENCTL_DIR/config.sh && grep -q "^SECOND_ID='11111111-" $SCREENCTL_DIR/config.sh \
  && ok "init 认出另一块屏是笔记本自带屏" || bad "layout detect: $(grep '^LAYOUT\|^SECOND' $SCREENCTL_DIR/config.sh | tr '\n' ' ')"

echo "== 没配对方电脑"
echo 16 > $FAKE_DIR/input; : > $SCREENCTL_NOTIFY_FILE; : > $FAKE_DIR/order.log
$S give self >/dev/null
notified "没有配置对方电脑" && notified "请用显示器上的按钮切换输入源" && [ ! -s $FAKE_DIR/ssh.log ] && ok "拿回：提示按显示器按钮，不发 ssh" || bad "unconfigured relay: $(cat $SCREENCTL_NOTIFY_FILE)"
grep -qx "ddc set 15" $FAKE_DIR/order.log && ok "拿回时先直接向主屏写了本机编码" || bad "no direct write: $(tr '\n' ';' < $FAKE_DIR/order.log)"
echo 15 > $FAKE_DIR/input; : > $SCREENCTL_NOTIFY_FILE

# 测试加速：观察期 6 秒
echo "OTHER_OBSERVE=6" >> $SCREENCTL_DIR/config.sh
# 测试用的对方电脑与设备（init 不带默认值）。SSH_KEY 指向不存在的文件，ssh 参数里就不会带上真实的密钥
cat >> $SCREENCTL_DIR/config.sh <<CFG
SELF_NAME='MacBook'
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
CODE_PEER=16
DEVICES=('Switch=17' 'PS5=18')
SSH_KEY='$FAKE_DIR/nokey'
CFG

echo "== 未运行守护进程"
expect_rc 3 $S request give peer
expect_rc 3 $S request give windows
expect_rc 3 $S request give code 17
expect_rc 1 $S request give foo
expect_rc 1 $S request give code 99
expect_rc 1 $S request take windows
expect_rc 3 $S request take
out=$($S request info); rc=$?
[ $rc -eq 0 ] && [ "$out" = "proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1" ] && ok "request info：守护进程没在运行也回答，格式与规范 5.4 的例子一致" || bad "info: rc=$rc [$out]"
$S diag | head -1 | grep -q "owned=1" && ok "status 直接读取 owned=1" || bad "status direct"

echo "== 启动对账：占有但镜像 → 拿回"
echo mirror > $FAKE_DIR/layout
start_daemon
t=$(wait_for 5 '[ "$(cat $FAKE_DIR/layout)" = extend ]') && ok "reconcile take ${t}s" || bad "reconcile take"
wait_for 2 '[ "$(owned)" = owned=1 ]' >/dev/null && ok "state owned=1" || bad "state"
$S diag | head -1 | grep -q "owned=1" && ok "status owned=1" || bad "status"
expect_rc 1 $S request give foo
expect_rc 0 $S request give peer
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "request give peer → mirror ${t}s" || bad "give peer"
[ "$(cat $FAKE_DIR/input)" = 16 ] && ok "DDC=16" || bad "DDC"
grep -q 'ScreenSwitch.exe request take' $FAKE_DIR/ssh.log && bad "对方请求的切换不该再回发 request take: $(cat $FAKE_DIR/ssh.log)" || ok "对方用 request give 请求的切换：不回发 request take（它已预先拿回）"
grep -q "自己请求的切换，不回发 request take" $SCREENCTL_DIR/screenctl.log && ok "日志：对方自己请求的切换，不回发 request take" || bad "no skip log"
wait_for 2 '[ "$(owned)" = owned=0 ]' >/dev/null && ok "owned=0" || bad "owned"
expect_rc 2 $S request give peer
[ -f $SCREENCTL_DIR/cmd ] && bad "cmd written when unowned" || ok "no cmd when unowned"

echo "== 按钮跟随"
sleep 3   # 启动对账拿回后的 3 秒稳定期（SETTLE_SECS）内不读 DDC；流程快了之后这里要先等它过去
s=$(date +%s); echo 15 > $FAKE_DIR/input
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/layout)" = extend ]') && ok "button→Mac take ${t}s" || bad "take follow"
[ "${t%%.*}" -le 1 ] && ok "未占有时每 0.5 秒轮询：${t}s 内拿回（设计 4.3）" || bad "unowned poll too slow: ${t}s"
echo 16 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "button→Win release ${t}s（拿回后 3 秒稳定期 + 连续 4 次失败）" || bad "release follow"
wait_for 4 '[ "$(owned)" = owned=0 ]' >/dev/null || bad "setup: release not finished"   # 等让出写完状态再切回来，否则下面会读到旧的 owned=1
echo 15 > $FAKE_DIR/input; wait_for 6 '[ "$(owned)" = owned=1 ] && [ "$(cat $FAKE_DIR/layout)" = extend ]' >/dev/null || bad "setup: not re-taken"

echo "== 占有时的垃圾读数与拿回后的稳定期"
echo 3 > $FAKE_DIR/glitch; sleep 2
[ "$(cat $FAKE_DIR/glitch)" = 3 ] && [ "$(owned)" = owned=1 ] && ok "拿回后 SETTLE_SECS 内不读 DDC（垃圾读数未被消费）" || bad "settle: glitch=$(cat $FAKE_DIR/glitch) $(owned)"
sleep 2; echo 2 > $FAKE_DIR/glitch; sleep 4
[ "$(cat $FAKE_DIR/glitch)" = 0 ] && [ "$(owned)" = owned=1 ] && [ "$(cat $FAKE_DIR/layout)" = extend ] && ok "连续 2 次垃圾读数不让出（RELEASE_DEBOUNCE=4）" || bad "glitch2: glitch=$(cat $FAKE_DIR/glitch) $(owned) $(cat $FAKE_DIR/layout)"
echo 6 > $FAKE_DIR/glitch
t=$(wait_for 8 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "连续 6 次读失败视为真的切走，${t}s 后让出" || bad "glitch6 no release"
rm -f $FAKE_DIR/glitch
t=$(wait_for 6 '[ "$(owned)" = owned=1 ]') && ok "读数恢复后拿回 ${t}s" || bad "re-take after glitch"

echo "== 切给设备：那个口没有信号，显示器自己跳回来"
: > $SCREENCTL_NOTIFY_FILE
export FAKE_NOSIGNAL="17" FAKE_JUMP=2
stop_daemon; start_daemon; sleep 1   # 让守护进程继承 FAKE_NOSIGNAL（假 m1ddc 在其子进程里）
$S give Switch >/dev/null; sleep 1.5
$S give self >/dev/null   # 流程进行中再来一个请求：应被忽略并通知
maxage=0; for i in $(seq 1 40); do a=$(age); [ $a -gt $maxage ] && maxage=$a; rc=$($S request give self >/dev/null 2>&1; echo $?); [ $rc -eq 3 ] && bad "request 在流程中返回 3"; sleep 0.5; notified "没有检测到" && break; done
notified "没有检测到 Switch 的信号" && ok "通知：没有检测到 Switch 的信号（用的是设备的名字）" || bad "no-signal notify: $(cat $SCREENCTL_NOTIFY_FILE)"
echo "    流程中状态文件最大年龄 ${maxage}s"; [ $maxage -le 2 ] && ok "state 持续刷新" || bad "state stale"
notified "正在切换中" && ok "流程中的新请求被忽略并通知" || bad "dup notify"
[ "$(cat $FAKE_DIR/layout)" = extend ] && [ "$(cat $FAKE_DIR/input)" = 15 ] && ok "最终拿回 extend" || bad "final state"
grep -q "ddc set 18" $FAKE_DIR/order.log && bad "切给 Switch 却写了别的设备的编码" || ok "只写 Switch 的编码，不再挨个试别的口"
sleep 1

echo "== 切给设备：有信号"
: > $SCREENCTL_NOTIFY_FILE
stop_daemon; export FAKE_NOSIGNAL="" ; start_daemon; sleep 1
$S give code 17 >/dev/null
t=$(wait_for 5 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "切给 Switch（编码 17）→ mirror ${t}s" || bad "device 17"
sleep 7
[ "$(cat $FAKE_DIR/input)" = 17 ] && [ "$(owned)" = owned=0 ] && ok "停在 17，owned=0" || bad "device 17 final $(cat $FAKE_DIR/input) $(owned)"
[ -s $SCREENCTL_NOTIFY_FILE ] && bad "unexpected notify: $(cat $SCREENCTL_NOTIFY_FILE)" || ok "无多余通知"
expect_rc 1 $S give 没有这个设备

echo "== 未占有：拿回时对方的各种回答"
for rc in 0 2 3 255 1; do
  : > $SCREENCTL_NOTIFY_FILE
  stop_daemon; export FAKE_SSH_RC=$rc; start_daemon; sleep 1.5
  $S give self >/dev/null
  wait_for 8 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null
  echo "    rc=$rc → $(cat $SCREENCTL_NOTIFY_FILE)"
  [ $rc -eq 2 ] && { notified "Windows 也不占有主屏" && notified "请用显示器上的按钮切换输入源" && ok "对方也不占有：提示按显示器按钮" || bad "rc2 notify"; }
  [ $rc -eq 0 ] && { notified "已请求 Windows 交还主屏" && ok "对方接受：通知里用对方的名字" || bad "rc0 notify"; }
done
grep -q 'tester@win-pc.local C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe request give peer' $FAKE_DIR/ssh.log && ok "ssh 命令行：<对方程序> request give peer" || bad "ssh cmdline: $(tail -2 $FAKE_DIR/ssh.log)"
tail -1 $FAKE_DIR/ssh.log | grep -q 'ScreenSwitch.exe request give mac' && ok "对方不认识 give peer（退出码 1）时用旧写法 give mac 重试" || bad "no legacy retry: $(tail -1 $FAKE_DIR/ssh.log)"
grep -q "BatchMode=yes" $FAKE_DIR/ssh.log && grep -q "ConnectTimeout=3" $FAKE_DIR/ssh.log && grep -q "AddressFamily=inet" $FAKE_DIR/ssh.log && ok "ssh 选项" || bad "ssh opts"
echo "== 中继超时"
: > $SCREENCTL_NOTIFY_FILE
stop_daemon; export FAKE_SSH_RC=0 FAKE_SSH_SLEEP=30; echo "SSH_TOTAL_TIMEOUT=2" >> $SCREENCTL_DIR/config.sh; start_daemon; sleep 1.5
$S give self >/dev/null; wait_for 10 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null
grep -q "连接不到 Windows" $SCREENCTL_NOTIFY_FILE && ok "超时按 255 处理" || bad "timeout: $(cat $SCREENCTL_NOTIFY_FILE)"
export FAKE_SSH_SLEEP=0
echo "== 直接切从没生效过：连着 3 次之后不再白等"
n=$(cat $SCREENCTL_DIR/.direct_fail 2>/dev/null); [ "${n:-0}" -ge 3 ] && ok "已记下直接切连续没生效（$n 次）" || bad "direct_fail count: [$n]"
stop_daemon; export FAKE_SSH_RC=255; start_daemon; sleep 1.5
: > $SCREENCTL_NOTIFY_FILE; s0=$(date +%s)
$S give self >/dev/null; t=$(wait_for 6 '[ -s $SCREENCTL_NOTIFY_FILE ]')
[ "${t%%.*}" -le 1 ] 2>/dev/null && ok "对方连不上时提示立刻出来（${t}s），不再多等 3 秒" || bad "still waiting: ${t}s"
export FAKE_SSH_RC=0
echo "== 未占有时切给对方 / 已占有时拿回"
: > $SCREENCTL_NOTIFY_FILE; $S give peer >/dev/null; wait_for 4 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null; echo "    $(cat $SCREENCTL_NOTIFY_FILE)"
notified "主屏不在本机，无法切给 Windows" && ok "未占有时切给对方：提示主屏不在本机" || bad "unowned give peer"
echo 15 > $FAKE_DIR/input; wait_for 6 '[ "$(owned)" = owned=1 ]' >/dev/null
: > $SCREENCTL_NOTIFY_FILE; $S give self >/dev/null; wait_for 3 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null; echo "    $(cat $SCREENCTL_NOTIFY_FILE)"
notified "主屏已在本机" && ok "已占有时拿回：提示主屏已在本机" || bad "owned give self"

echo "== 切换未生效（写 DDC 后仍读得到）"
: > $SCREENCTL_NOTIFY_FILE
cat > $FAKE_DIR/m1ddc-stuck <<'X'
#!/bin/bash
[ "$3" = set ] && { echo "$5"; exit 0; }
exec $FAKE_M1DDC "$@"
X
chmod +x $FAKE_DIR/m1ddc-stuck
stop_daemon; FAKE_M1DDC=$FAKE/m1ddc M1DDC=$FAKE_DIR/m1ddc-stuck start_daemon; sleep 1
$S give peer >/dev/null; wait_for 6 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null
grep -q "切换未生效" $SCREENCTL_NOTIFY_FILE && [ "$(cat $FAKE_DIR/layout)" = extend ] && ok "未生效 → 通知且不改布局" || bad "stuck: $(cat $SCREENCTL_NOTIFY_FILE)"
grep -q 'ScreenSwitch.exe request take' $FAKE_DIR/ssh.log && ok "本机自己点“切给对方”：先 SSH 通知它 request take" || bad "no request take in ssh.log: $(tail -3 $FAKE_DIR/ssh.log)"
grep -q "毫秒后仍读到本机，再写一次 (16)" $SCREENCTL_DIR/screenctl.log && [ "$(grep -c "再写一次" $SCREENCTL_DIR/screenctl.log)" = 1 ] && ok "写后约 1.5 秒仍读到本机：再写一次，只补一次" || bad "no rewrite: $(grep -c 再写一次 $SCREENCTL_DIR/screenctl.log)"

echo "== 单实例 / 守护进程停止后 request=3"
$S watch >/dev/null 2>&1 & W2=$!; sleep 2; if kill -0 $W2 2>/dev/null; then kill $W2; bad "second watch 仍在运行"; else ok "第二个 watch 立即退出"; fi
kill -0 $DPID && ok "第一个仍在运行" || bad "first died"
stop_daemon; [ -f $SCREENCTL_DIR/watch.pid ] && bad "pid 残留" || ok "pid 清理"
echo "    等 11 秒…"; sleep 11
expect_rc 3 $S request give windows

echo "== 睡醒后的缓存旧数据：主屏在 DP，输入源却读出 15"
echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; rm -f $SCREENCTL_DIR/state; start_daemon   # 删旧状态文件，等新守护进程真的对账完
wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
echo 16 > $FAKE_DIR/input; touch $FAKE_DIR/stale
t=$(wait_for 8 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "假 15 被最大亮度核对拦下，${t}s 内让出" || bad "stale 15 仍被当成占有"
grep -q "判为缓存旧数据" $SCREENCTL_DIR/screenctl.log && ok "日志记录了被拦下的假 15" || bad "no stale log"
rm -f $FAKE_DIR/stale; stop_daemon

echo "== 保存排列：手动与自动"
echo "AUTO_SAVE_POLLS=1" >> $SCREENCTL_DIR/config.sh
echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; rm -f $FAKE_DIR/extend_line; : > $SCREENCTL_NOTIFY_FILE; start_daemon
wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
NEWL='displayplacer "id:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE res:2560x1440 hz:160 color_depth:8 enabled:true scaling:on origin:(0,0) degree:0" "id:11111111-2222-3333-4444-555555555555 res:1470x956 hz:60 color_depth:8 enabled:true scaling:on origin:(2560,0) degree:0"'
echo "$NEWL" > $FAKE_DIR/extend_line
t=$(wait_for 10 'grep -q "origin:(2560,0)" $SCREENCTL_DIR/config.sh') && ok "自动记住新排列 ${t}s" || bad "auto save"
wait_for 3 'notified "已自动记住新的屏幕排列"' >/dev/null && ok "自动保存有通知" || bad "auto save notify"
grep -q "^AUTO_SAVE_LAYOUT=1" $SCREENCTL_DIR/config.sh && grep -q "^PEER_HOST='win-pc.local'" $SCREENCTL_DIR/config.sh && grep -q "'PS5=18'" $SCREENCTL_DIR/config.sh && ok "写回配置保留其他项（对方电脑、设备）" || bad "config preserved"
$S set AUTO_SAVE_LAYOUT 0 >/dev/null; wait_for 4 'grep -q "^AUTO_SAVE_LAYOUT=0" $SCREENCTL_DIR/config.sh' >/dev/null && ok "set AUTO_SAVE_LAYOUT 0" || bad "set 0"
sed 's/origin:(2560,0)/origin:(-1470,0)/' $FAKE_DIR/extend_line > $FAKE_DIR/x && mv $FAKE_DIR/x $FAKE_DIR/extend_line
sleep 6; grep -q "origin:(-1470,0)" $SCREENCTL_DIR/config.sh && bad "关闭后仍自动保存" || ok "关闭后不再自动保存"
: > $SCREENCTL_NOTIFY_FILE; $S save-layout >/dev/null
t=$(wait_for 4 'grep -q "origin:(-1470,0)" $SCREENCTL_DIR/config.sh') && ok "手动保存 ${t}s" || bad "manual save"
wait_for 3 'notified "已记住当前屏幕排列"' >/dev/null && ok "手动保存有通知" || bad "manual notify"
expect_rc 1 $S set FOO 1
$S give peer >/dev/null; wait_for 5 '[ "$(cat $FAKE_DIR/layout)" = mirror ]' >/dev/null
: > $SCREENCTL_NOTIFY_FILE; $S save-layout >/dev/null; wait_for 3 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null
notified "无法保存排列：当前是镜像" && ok "镜像时拒绝保存" || bad "mirror save: $(cat $SCREENCTL_NOTIFY_FILE)"
stop_daemon; $S set AUTO_SAVE_LAYOUT 1 >/dev/null

echo "== 屏幕休眠时 displayplacer 给出 enabled:false：不保存，坏配置也不套用"
echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; start_daemon
wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
echo 'displayplacer "id:11111111-2222-3333-4444-555555555555 enabled:false" "id:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE enabled:false"' > $FAKE_DIR/extend_line
sleep 6; grep -q "enabled:false" $SCREENCTL_DIR/config.sh && bad "休眠时的排列被自动保存" || ok "休眠时的排列不自动保存"
stop_daemon
cp $SCREENCTL_DIR/config.sh $FAKE_DIR/config.good
sed -i '' "s/^    'id:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE res:[^']*'/    'id:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE enabled:false'/" $SCREENCTL_DIR/config.sh
grep -q "EEEEEEEEEEEE enabled:false'" $SCREENCTL_DIR/config.sh || bad "测试没改出坏配置"
echo 16 > $FAKE_DIR/input; echo mirror > $FAKE_DIR/layout; rm -f $FAKE_DIR/extend_line $FAKE_DIR/dp.log; start_daemon
echo 15 > $FAKE_DIR/input
wait_for 8 'grep -q "排列不完整" $SCREENCTL_DIR/screenctl.log' >/dev/null && ok "坏配置拒绝套用并记日志" || bad "bad config applied"
grep -q "enabled:false" $FAKE_DIR/dp.log 2>/dev/null && bad "坏排列传给了 displayplacer" || ok "坏排列没传给 displayplacer"
stop_daemon; cp $FAKE_DIR/config.good $SCREENCTL_DIR/config.sh

echo "== 仅大屏（关闭笔记本屏）"
lay() { cat $FAKE_DIR/layout; }
laptop() { [ -f $FAKE_DIR/laptop_off ] && echo off || echo on; }
dpn() { cat $FAKE_DIR/dp.log 2>/dev/null | wc -l | tr -d ' '; }   # displayplacer 被调用（非 list）的次数
echo 15 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout; rm -f $FAKE_DIR/laptop_off $FAKE_DIR/helper_fail; : > $SCREENCTL_NOTIFY_FILE; start_daemon
wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
$S set OWNED_LAYOUT external >/dev/null
t=$(wait_for 6 '[ "$(laptop)" = off ]') && ok "占有时选仅大屏，立即关闭笔记本屏 ${t}s" || bad "external apply"
grep -q "^OWNED_LAYOUT='external'" $SCREENCTL_DIR/config.sh && grep -q "^LAPTOP_CGID=1" $SCREENCTL_DIR/config.sh && ok "配置记住偏好与显示编号" || bad "config external"
$S diag | grep -q "layout=external" && ok "status 显示 layout=external" || bad "status external"
: > $FAKE_DIR/order.log
$S give peer >/dev/null
t=$(wait_for 8 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ]') && ok "切给对方：切走后打开笔记本屏并镜像 ${t}s" || bad "give peer from external: $(lay) $(laptop)"
[ "$(cat $FAKE_DIR/input)" = 16 ] && ok "DDC=16" || bad "DDC not 16"
head -1 $FAKE_DIR/order.log | grep -qx "ddc set 16" && ok "仅大屏切走：DDC 写入早于任何布局调用（主屏上不出现镜像）" || bad "order: $(tr '\n' ';' < $FAKE_DIR/order.log)"
grep -q "enable-mirror 1 3" $FAKE_DIR/helper.log && ok "切走后用合并操作直接进镜像（少闪一次）" || bad "enable-mirror not used"
wait_for 5 '[ "$(owned)" = owned=0 ]' >/dev/null   # 等 give 流程结束再切回，否则会被判成"切换未生效"
n=$(dpn); echo 15 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(laptop)" = off ]') && ok "拿回时按偏好关闭笔记本屏 ${t}s" || bad "take external"
grep -q "solo 1 3 2560 1440 160 hidpi" $FAKE_DIR/helper.log && [ "$(dpn)" = "$n" ] && ok "拿回时一步从镜像进入仅大屏（solo 带保存的主屏模式，未调用 displayplacer）" || bad "solo not used: $(tail -1 $FAKE_DIR/helper.log) dp=$n→$(dpn)"
echo 16 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ]') && ok "被动让出：开笔记本屏并镜像 ${t}s" || bad "passive release: $(lay) $(laptop)"
touch $FAKE_DIR/solo_fail; n=$(dpn)
echo 15 > $FAKE_DIR/input; wait_for 8 '[ "$(laptop)" = off ]' >/dev/null
[ "$(laptop)" = off ] && [ "$(dpn)" -gt "$n" ] && tail -1 $FAKE_DIR/helper.log | grep -q "disable 1" && ok "solo 失败时退回 扩展→关笔记本屏" || bad "solo fallback: laptop=$(laptop) dp=$n→$(dpn) $(tail -1 $FAKE_DIR/helper.log)"
rm -f $FAKE_DIR/solo_fail
curmode() { $S diag | sed -n 's/^layout=//p'; }
nolayout() { ! grep -qv '^ddc set' $FAKE_DIR/order.log; }   # order.log 里只有 DDC 写入，没有任何布局调用
touch $FAKE_DIR/ddc_set_fail; : > $SCREENCTL_NOTIFY_FILE; : > $FAKE_DIR/order.log
$S give peer >/dev/null; wait_for 6 'notified "写入 DDC 失败"' >/dev/null
sleep 1; notified "写入 DDC 失败" && nolayout && [ "$(curmode)" = external ] && [ "$(owned)" = owned=1 ] && ok "仅大屏下 DDC 写失败：通知，布局不动、不重配" || bad "ddc fail: $(cat $SCREENCTL_NOTIFY_FILE) order=$(tr '\n' ';' < $FAKE_DIR/order.log) $(curmode) $(owned)"
rm -f $FAKE_DIR/ddc_set_fail
touch $FAKE_DIR/ddc_stuck; : > $SCREENCTL_NOTIFY_FILE; : > $FAKE_DIR/order.log
$S give peer >/dev/null; wait_for 8 'notified "切换未生效"' >/dev/null
sleep 1; notified "切换未生效" && nolayout && [ "$(curmode)" = external ] && [ "$(owned)" = owned=1 ] && ok "仅大屏下切换未生效：通知，布局不动、不重配" || bad "stuck external: $(cat $SCREENCTL_NOTIFY_FILE) order=$(tr '\n' ';' < $FAKE_DIR/order.log) $(curmode) $(owned)"
grep -q "give: 布局仍为 external，不动" $SCREENCTL_DIR/screenctl.log && ok "日志：布局仍为 external，不动" || bad "no keep-layout log"
rm -f $FAKE_DIR/ddc_stuck
# 切走后笔记本屏打不开：主屏已不显示本机，必须通知并退避重试到打开为止（替代原来的"开不回来就取消切换"）
touch $FAKE_DIR/helper_fail; : > $SCREENCTL_NOTIFY_FILE; : > $FAKE_DIR/order.log
$S give peer >/dev/null; wait_for 15 'notified "合上再打开笔记本盖子"' >/dev/null
notified "合上再打开笔记本盖子" && [ "$(cat $FAKE_DIR/input)" = 16 ] && [ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "切走后笔记本屏打不开：DDC 已切走，通知提示合上再打开盖子，归属留待重试" || bad "enable fail after switch: $(cat $SCREENCTL_NOTIFY_FILE) input=$(cat $FAKE_DIR/input) $(laptop) $(owned)"
n1=$(grep -c '^helper enable-mirror' $FAKE_DIR/order.log)
sleep 5; [ "$(grep -c '^helper enable-mirror' $FAKE_DIR/order.log)" = "$n1" ] && ok "退避期内不重试（已尝试 $n1 次）" || bad "retried within backoff"
t=$(wait_for 20 '[ "$(grep -c "^helper enable-mirror" $FAKE_DIR/order.log)" -gt '"$n1"' ]') && ok "退避后重试打开笔记本屏 ${t}s" || bad "no retry"
[ "$(grep -c "合上再打开笔记本盖子" $SCREENCTL_NOTIFY_FILE)" = 1 ] && ok "同一段失败只通知一次" || bad "notified $(grep -c "合上再打开笔记本盖子" $SCREENCTL_NOTIFY_FILE) times"
rm -f $FAKE_DIR/helper_fail
t=$(wait_for 25 '[ "$(laptop)" = on ] && [ "$(lay)" = mirror ] && [ "$(owned)" = owned=0 ]') && ok "笔记本屏恢复后完成让出（镜像，owned=0）${t}s" || bad "no recovery: $(laptop) $(lay) $(owned)"
echo 15 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(laptop)" = off ]' >/dev/null || bad "setup: not re-taken"
# 切走的同时主屏从拓扑里消失（absent）：至少保证笔记本屏打开，算让出完成
touch $FAKE_DIR/absent_on_switch; : > $FAKE_DIR/order.log
sleep 3; $S give peer >/dev/null
t=$(wait_for 8 '[ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]') && ok "切走后主屏 absent：打开笔记本屏，owned=0 ${t}s" || bad "absent after switch: $(laptop) $(owned)"
head -1 $FAKE_DIR/order.log | grep -qx "ddc set 16" && ok "absent 路径同样先写 DDC" || bad "absent order: $(tr '\n' ';' < $FAKE_DIR/order.log)"
rm -f $FAKE_DIR/absent_on_switch $FAKE_DIR/absent; echo 15 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(laptop)" = off ]') && ok "主屏回来后按偏好拿回仅大屏 ${t}s" || bad "re-take after absent: $(laptop) $(owned)"
$S set OWNED_LAYOUT extend >/dev/null
t=$(wait_for 6 '[ "$(laptop)" = on ] && [ "$(lay)" = extend ]') && ok "改回扩展，笔记本屏打开 ${t}s" || bad "back to extend"
$S set OWNED_LAYOUT external >/dev/null; wait_for 6 '[ "$(laptop)" = off ]' >/dev/null
stop_daemon
[ "$(laptop)" = on ] && ok "守护进程退出时打开笔记本屏" || bad "cleanup did not enable laptop"
echo extend > $FAKE_DIR/layout; start_daemon
t=$(wait_for 6 '[ "$(laptop)" = off ]') && ok "启动对账：占有、扩展、偏好仅大屏 → 关闭笔记本屏 ${t}s" || bad "reconcile external"
touch $SCREENCTL_DIR/.restarting; stop_daemon
[ "$(laptop)" = off ] && ok "带重启标记退出：笔记本屏保持关闭" || bad "restart marker ignored"
start_daemon; sleep 2
[ ! -f $SCREENCTL_DIR/.restarting ] && [ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "重启后清除标记，布局不动" || bad "after restart: marker=$([ -f $SCREENCTL_DIR/.restarting ] && echo yes || echo no) laptop=$(laptop) $(owned)"
stop_daemon; $S set OWNED_LAYOUT extend >/dev/null; rm -f $FAKE_DIR/laptop_off

echo "== 主动拿回：对方接受后预先恢复布局（设计 4.3）"
$S set OWNED_LAYOUT external >/dev/null
echo "TAKE_GRACE_SECS=3" >> $SCREENCTL_DIR/config.sh   # 测试加速：宽限 3 秒
echo 16 > $FAKE_DIR/input; echo mirror > $FAKE_DIR/layout; rm -f $FAKE_DIR/laptop_off; : > $SCREENCTL_NOTIFY_FILE
export FAKE_SSH_RC=0; start_daemon; sleep 1.5
$S give self >/dev/null
t=$(wait_for 4 '[ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ]') && ok "对端接受后立即进入仅大屏 ${t}s（显示器切过来时直接是本机桌面）" || bad "pre-take: laptop=$(laptop) $(owned)"
sleep 1.5; [ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "宽限期内读不到主屏也不让出" || bad "released during grace: $(laptop) $(owned)"
t=$(wait_for 6 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ]') && ok "宽限期满仍读不到：让出 ${t}s" || bad "grace release: $(lay) $(laptop)"
notified "Windows 没有交还主屏" && ok "宽限期满有通知" || bad "no grace notify: $(cat $SCREENCTL_NOTIFY_FILE)"
: > $SCREENCTL_NOTIFY_FILE
$S give self >/dev/null
wait_for 4 '[ "$(laptop)" = off ]' >/dev/null; echo 15 > $FAKE_DIR/input
sleep 5; [ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "宽限期内主屏切回：保持仅大屏，不再重配" || bad "success case: laptop=$(laptop) $(owned)"
notified "没有交还" && bad "success case notified grace failure" || ok "成功时无宽限期通知"
grep -q "预先拿回生效" $SCREENCTL_DIR/screenctl.log && ok "日志记录预先拿回生效" || bad "no pre-take log"

echo "== 对方发来 request take（它切过来之前先通知）"
echo 16 > $FAKE_DIR/input
wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup: not released"
expect_rc 0 $S request take
t=$(wait_for 4 '[ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ]') && ok "request take → 立即进入仅大屏 ${t}s（显示器还没切过来）" || bad "request take: $(laptop) $(owned)"
grep -q "pre-take: 已预先拿回.*对端 request take" $SCREENCTL_DIR/screenctl.log && ok "日志：对端 request take 触发预先拿回" || bad "no pre-take log for request take"
echo 15 > $FAKE_DIR/input; sleep 5
[ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "宽限期内主屏切来，保持仅大屏" || bad "after request take: $(laptop) $(owned)"
expect_rc 0 $S request take
sleep 1; [ -f $SCREENCTL_DIR/cmd ] && bad "已占有时 request take 不应写命令文件" || ok "已占有时 request take 返回 0 且不写命令文件"
expect_rc 1 $S request take now

echo "== 预先拿回时主屏不在系统拓扑里（absent）"
# 让出后主屏在 Windows 上久了会从 Mac 的拓扑里消失；这时 pre-take 不能算布局已就位（联调 19:10 停在镜像的问题）
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup: not released"
touch $FAKE_DIR/absent; : > $SCREENCTL_NOTIFY_FILE
expect_rc 0 $S request take
t=$(wait_for 4 '[ "$(owned)" = owned=1 ]') && ok "absent 时 pre-take：记为占有 ${t}s" || bad "pre-take absent: $(owned)"
grep -q "待主屏就绪后再应用" $SCREENCTL_DIR/screenctl.log && ok "日志：待主屏就绪后再应用" || bad "no pending log"
sleep 1.5; [ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && ok "主屏没回来前不动布局、不关笔记本屏" || bad "touched layout while absent: $(lay) $(laptop)"
rm -f $FAKE_DIR/absent; echo 15 > $FAKE_DIR/input   # 显示器切过来：主屏回到拓扑，macOS 恢复成镜像
t=$(wait_for 5 '[ "$(laptop)" = off ] && [ "$(lay)" != mirror ]') && ok "主屏回到拓扑后应用仅大屏 ${t}s" || bad "pending take not applied: $(lay) $(laptop)"
grep -q "主屏已就绪，应用待拿回的布局" $SCREENCTL_DIR/screenctl.log && ok "日志：主屏已就绪，应用待拿回的布局" || bad "no ready log"
sleep 5; [ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "之后稳定，不来回闪" || bad "flapped: $(laptop) $(owned)"
notified "没有交还" && bad "unexpected grace notify" || ok "无宽限期通知"
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup2: not released"
touch $FAKE_DIR/absent
$S give self >/dev/null
t=$(wait_for 4 '[ "$(owned)" = owned=1 ]') && ok "本机拿回被接受、absent：记为占有 ${t}s" || bad "give self absent: $(owned)"
sleep 1; [ "$(lay)" = mirror ] || bad "give self absent touched layout: $(lay)"
rm -f $FAKE_DIR/absent; echo 15 > $FAKE_DIR/input
t=$(wait_for 5 '[ "$(laptop)" = off ] && [ "$(lay)" != mirror ]') && ok "主屏回来后应用仅大屏 ${t}s（本机拿回的路径）" || bad "pending take (give self) not applied: $(lay) $(laptop)"
sleep 5
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup3: not released"
touch $FAKE_DIR/absent; : > $SCREENCTL_NOTIFY_FILE
expect_rc 0 $S request take
wait_for 4 '[ "$(owned)" = owned=1 ]' >/dev/null
t=$(wait_for 10 '[ "$(owned)" = owned=0 ]') && ok "主屏一直不回来：宽限期满让出 ${t}s" || bad "no release when absent forever"
notified "Windows 没有交还主屏" && ok "宽限期满有通知（absent）" || bad "no grace notify (absent): $(cat $SCREENCTL_NOTIFY_FILE)"
[ "$(laptop)" = on ] && [ "$(lay)" = mirror ] && ok "让出后笔记本屏开着、仍是镜像" || bad "after release: $(laptop) $(lay)"
rm -f $FAKE_DIR/absent

echo "== 主屏正在重新枚举（模式列表不全）时的预先拿回；request take 等布局应用完才返回"
# 联调 19:53：Windows 拿到 request take 的返回值就写 DDC，显示器切换让主屏重新枚举，这时配置必定失败
echo 15 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=1 ]' >/dev/null
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup4: not released"
touch $FAKE_DIR/degraded; : > $SCREENCTL_NOTIFY_FILE
out=$($S request take); rc=$?
[ $rc -eq 0 ] && [ "$out" = pending ] && ok "主屏没就绪：request take 立即返回 0 / pending" || bad "degraded request take: rc=$rc out=$out"
grep -q "主屏还没就绪（主屏模式列表里还没有" $SCREENCTL_DIR/screenctl.log && ok "日志：主屏还没就绪（模式列表）" || bad "no not-ready log"
sleep 1; [ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=1 ] && ok "没就绪前不动布局，归属已记为占有" || bad "degraded: $(lay) $(laptop) $(owned)"
rm -f $FAKE_DIR/degraded; echo 15 > $FAKE_DIR/input
t=$(wait_for 5 '[ "$(laptop)" = off ] && [ "$(lay)" != mirror ]') && ok "主屏就绪后应用仅大屏 ${t}s" || bad "pending take after degraded: $(lay) $(laptop)"
sleep 5
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(lay)" = mirror ] && [ "$(laptop)" = on ] && [ "$(owned)" = owned=0 ]' >/dev/null || bad "setup5: not released"
out=$($S request take); rc=$?
[ $rc -eq 0 ] && [ "$out" = applied ] && [ "$(laptop)" = off ] && [ "$(lay)" != mirror ] && ok "主屏就绪：request take 等布局应用完才返回（applied，返回时已是仅大屏）" || bad "blocking request take: rc=$rc out=$out $(lay) $(laptop)"
echo 15 > $FAKE_DIR/input; sleep 5
[ "$(laptop)" = off ] && [ "$(owned)" = owned=1 ] && ok "之后稳定" || bad "unstable after blocking take"
stop_daemon; $S set OWNED_LAYOUT extend >/dev/null; rm -f $FAKE_DIR/laptop_off

echo "== 启动对账：未占有但扩展 → 让出"
echo 18 > $FAKE_DIR/input; echo extend > $FAKE_DIR/layout
start_daemon
t=$(wait_for 5 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "reconcile release ${t}s" || bad "reconcile release"

echo "== 拔掉 Mac 的线再插回：macOS 恢复成扩展，未占有时改回镜像"
echo 15 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(cat $FAKE_DIR/layout)" = extend ]' >/dev/null || bad "setup: not owned"
touch $FAKE_DIR/absent; echo 16 > $FAKE_DIR/input   # 拔线：主屏从拓扑消失，显示器切到 Windows
t=$(wait_for 8 '[ "$(owned)" = owned=0 ]') && ok "拔线后让出（主屏 absent，只开笔记本屏）${t}s" || bad "unplug release: $(owned)"
[ "$(cat $FAKE_DIR/layout)" = extend ] || bad "setup: fake layout should still be extend"
: > $FAKE_DIR/order.log; rm -f $FAKE_DIR/absent   # 插回：主屏回到拓扑，macOS 恢复成扩展
t=$(wait_for 10 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "插回后主屏在线却是扩展：改回镜像 ${t}s" || bad "stray extend not fixed: $(cat $FAKE_DIR/layout)"
[ "${t%%.*}" -ge 3 ] 2>/dev/null && ok "等了 ${t}s（UNMIRRORED_FIX_SECS=4）才改，给显示器切回本机留时间" || bad "fixed too early: ${t}s"
grep -q "未占有但布局为 extend，改回镜像" $SCREENCTL_DIR/screenctl.log && ok "日志：未占有但布局为 extend，改回镜像" || bad "no fix log"
sleep 3; [ "$(owned)" = owned=0 ] && [ "$(cat $FAKE_DIR/layout)" = mirror ] && ok "之后保持镜像、owned=0" || bad "after fix: $(owned) $(cat $FAKE_DIR/layout)"
# 主屏回到拓扑是因为显示器正切回本机：几秒内读到主屏，直接拿回，中间不闪镜像
touch $FAKE_DIR/absent; echo extend > $FAKE_DIR/layout; sleep 1
: > $FAKE_DIR/order.log; rm -f $FAKE_DIR/absent; sleep 1.5; echo 15 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(owned)" = owned=1 ]') && ok "主屏回来后 1.5 秒读到本机：拿回 ${t}s" || bad "take after return: $(owned)"
sleep 1; [ ! -s $FAKE_DIR/order.log ] && [ "$(cat $FAKE_DIR/layout)" = extend ] && ok "拿回前没有先改成镜像（无布局调用）" || bad "flashed mirror: $(tr '\n' ';' < $FAKE_DIR/order.log)"
# 读 DDC 卡住很久（真机：读取跨过一整夜睡眠）时，计时不能从 poll 开头的过期时刻算起（真机 09:07 只等了 1 秒）
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(owned)" = owned=0 ] && [ "$(cat $FAKE_DIR/layout)" = mirror ]' >/dev/null || bad "setup: not released"
touch $FAKE_DIR/absent; echo extend > $FAKE_DIR/layout; sleep 1; touch $FAKE_DIR/slow_read
wait_for 3 '[ -f $FAKE_DIR/slow_reading ]' >/dev/null || bad "setup: slow read not started"
rm -f $FAKE_DIR/absent   # 主屏在这次慢读期间回到拓扑；慢读约 4 秒后结束，之后还应再等 4 秒
t=$(wait_for 14 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "慢读之后改回镜像 ${t}s" || bad "not fixed after slow read"
[ "${t%%.*}" -ge 6 ] 2>/dev/null && ok "慢读结束后仍等满约 4 秒（${t}s ≥ 6s）" || bad "timer used stale time: ${t}s"
stop_daemon

echo "== ssh-gate"
start_daemon; echo 15 > $FAKE_DIR/input; wait_for 6 '[ "$(owned)" = owned=1 ]' >/dev/null
# 下面检查的是命令文件的内容：守护进程 0.1 秒就会把它取走并真的去切换，所以先停掉它，状态文件手工保持新鲜（owned=1）
stop_daemon
fresh_state() { printf 'owned=1 %s\n' "$(date +%s)" > $SCREENCTL_DIR/state; }
# 命令, 期望退出码, 期望输出。每次调用后删掉命令文件
gate() {
  local out rc
  fresh_state
  out=$(SSH_ORIGINAL_COMMAND="$1" $S ssh-gate 2>&1); rc=$?; rm -f $SCREENCTL_DIR/cmd
  [ $rc -eq $2 ] && [ "$out" = "$3" ] && ok "gate [$1] → $rc $out" || bad "gate [$1] → rc=$rc out=$out（期望 $2 $3）"
}
gate '~/screenctl/screenctl.sh request give peer' 0 accepted
gate '~/screenctl/screenctl.sh request give code 17' 0 accepted
gate '~/screenctl/screenctl.sh request give windows' 0 accepted     # 旧写法：对方
gate '~/screenctl/screenctl.sh request give other' 0 accepted       # 旧写法：第一个设备
gate '~/screenctl/screenctl.sh request give code 99' 1 'unknown device'
gate '~/screenctl/screenctl.sh request give self' 1 rejected
gate '~/screenctl/screenctl.sh request give mac' 1 rejected
gate '~/screenctl/screenctl.sh request give code 17 now' 1 rejected
gate '~/screenctl/screenctl.sh request give code abc' 1 rejected
gate '~/screenctl/screenctl.sh request info x' 1 rejected
gate 'rm -rf ~' 1 rejected
gate '~/screenctl/screenctl.sh request give peer; id' 1 rejected
gate '/bin/sh -c id screenctl.sh' 1 rejected
gate '' 1 rejected
gate '~/screenctl/screenctl.sh request info' 0 'proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1'
fresh_state; out=$(SSH_ORIGINAL_COMMAND='~/screenctl/screenctl.sh request take' $S ssh-gate 2>&1); rc=$?
[ $rc -eq 0 ] && [ "$out" = "already owned" ] && ok "ssh-gate 放行 request take（已占有 → 0，不动）" || bad "gate take owned: rc=$rc $out"
out=$(SSH_ORIGINAL_COMMAND='~/screenctl/screenctl.sh request take now' $S ssh-gate 2>&1); rc=$?
[ $rc -eq 1 ] && [ "$out" = rejected ] && ok "ssh-gate 拒绝带多余参数的 request take" || bad "gate take extra: rc=$rc $out"
# 命令文件里写的是规范写法，并带 request- 前缀（对方请求的切换不回发 request take）
fresh_state; SSH_ORIGINAL_COMMAND='~/screenctl/screenctl.sh request give other' $S ssh-gate >/dev/null 2>&1
c=$(cat $SCREENCTL_DIR/cmd 2>/dev/null); rm -f $SCREENCTL_DIR/cmd
[ "$c" = "request-give code 17" ] && ok "旧写法 give other 写成 request-give code 17（设备列表里的第一个）" || bad "cmd for other: [$c]"
start_daemon; wait_for 6 '[ "$(owned)" = owned=1 ]' >/dev/null
echo 16 > $FAKE_DIR/input; wait_for 10 '[ "$(owned)" = owned=0 ]' >/dev/null
out=$(SSH_ORIGINAL_COMMAND='~/screenctl/screenctl.sh request take' $S ssh-gate 2>&1); rc=$?
[ $rc -eq 0 ] && [ "$out" = applied ] && ok "ssh-gate 放行 request take（未占有 → 0，等布局应用完返回 applied）" || bad "gate take unowned: rc=$rc $out"
stop_daemon

echo "== menu-info：菜单栏程序读到的名字、设备和快捷键"
mi=$($S menu-info)
printf '%s\n' "$mi" | grep -qx "peer=Windows" && printf '%s\n' "$mi" | grep -qx "layout=second" && printf '%s\n' "$mi" | grep -qx "hotkey_peer=" \
  && printf '%s\n' "$mi" | grep -q "^device=17	Switch	$" && printf '%s\n' "$mi" | grep -q "^device=18	PS5	$" \
  && ok "menu-info：对方名字、两个设备、默认没有快捷键" || bad "menu-info: $(printf '%s' "$mi" | tr '\n' '|')"

echo "== 旧版配置（只有旧名字）照常可用"
fresh legacy
cat > $SCREENCTL_DIR/config.sh <<'CFG'
MAIN_NAME='P275MV'
MAIN_UUID='AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
MAIN_ID='AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
LAPTOP_ID='11111111-2222-3333-4444-555555555555'
CODE_WINDOWS=16
CODE_MAC=15
CODE_OTHER='17 18'
SANITY_MAXLUM=100
EXTEND_ARGS=(
    'id:11111111-2222-3333-4444-555555555555 res:1470x956 hz:60 color_depth:8 enabled:true scaling:on origin:(0,0) degree:0'
    'id:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE res:1920x1080 hz:160 color_depth:8 enabled:true scaling:on origin:(-1920,0) degree:0'
)
MIRROR_ARGS=(
    'id:11111111-2222-3333-4444-555555555555+AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE res:1470x956 hz:60 color_depth:8 scaling:on origin:(0,0) degree:0'
)
WIN_HOST='old-pc.local'
WIN_USER='olduser'
WIN_EXE='C:\Users\olduser\ScreenSwitch\ScreenSwitch.exe'
HOTKEY_MAIN='ctrl+opt+1'
HOTKEY_OTHER='ctrl+opt+2'
AUTO_SAVE_POLLS=1
CFG
echo "SSH_KEY='$FAKE_DIR/nokey'" >> $SCREENCTL_DIR/config.sh
mi=$($S menu-info)
printf '%s\n' "$mi" | grep -qx "peer=Windows" && printf '%s\n' "$mi" | grep -q "^device=17	设备 1	$" && printf '%s\n' "$mi" | grep -q "^device=18	设备 2	$" \
  && ok "旧配置：对方叫 Windows，Other 的两个编码变成"设备 1""设备 2"" || bad "legacy menu-info: $(printf '%s' "$mi" | tr '\n' '|')"
printf '%s\n' "$mi" | grep -qx "hotkey_peer=" && ok "旧配置里的快捷键不再读取" || bad "legacy hotkeys still read"
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null || bad "legacy: not owned"
$S give windows >/dev/null   # 旧写法
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/layout)" = mirror ] && [ "$(cat $FAKE_DIR/input)" = 16 ]') && ok "旧配置 + 旧写法 give windows：切给对方 ${t}s" || bad "legacy give windows"
grep -q 'olduser@old-pc.local C:\\Users\\olduser\\ScreenSwitch\\ScreenSwitch.exe request take' $FAKE_DIR/ssh.log && ok "旧配置里的 WIN_HOST / WIN_USER / WIN_EXE 仍在用" || bad "legacy ssh: $(cat $FAKE_DIR/ssh.log)"
wait_for 6 '[ "$(owned)" = owned=0 ]' >/dev/null || bad "legacy: release not finished"   # 等让出真正完成，再把主屏切回来
echo 15 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(cat $FAKE_DIR/layout)" = extend ]' >/dev/null || bad "legacy: not re-taken"
$S save-layout >/dev/null; wait_for 6 'grep -q "^PEER_HOST=" $SCREENCTL_DIR/config.sh' >/dev/null
grep -q "^PEER_HOST='old-pc.local'" $SCREENCTL_DIR/config.sh && grep -q "^CODE_SELF=15" $SCREENCTL_DIR/config.sh && grep -q "^CODE_PEER='16'" $SCREENCTL_DIR/config.sh \
  && grep -q "'设备 1=17'" $SCREENCTL_DIR/config.sh && grep -q "^SECOND_ID='11111111-" $SCREENCTL_DIR/config.sh && ! grep -q "^WIN_HOST\|^CODE_MAC\|^LAPTOP_ID\|^HOTKEY_MAIN" $SCREENCTL_DIR/config.sh \
  && ok "程序重写配置后换成新名字，内容不变" || bad "legacy rewrite: $(grep '^PEER_\|^CODE_\|^WIN_\|^SECOND\|设备' $SCREENCTL_DIR/config.sh | tr '\n' ' ')"
stop_daemon

echo "== 还不知道对方接在哪个口：切给对方时先问它"
fresh askcode
$S init >/dev/null
cat >> $SCREENCTL_DIR/config.sh <<CFG
PEER_NAME='台式机'
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
SSH_KEY='$FAKE_DIR/nokey'
CFG
export FAKE_SSH_RC=255
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
$S give peer >/dev/null; wait_for 8 '[ -s $SCREENCTL_NOTIFY_FILE ]' >/dev/null
notified "不知道 台式机 接在主屏的哪个输入口" && [ "$(cat $FAKE_DIR/input)" = 15 ] && ok "问不到时提示运行设置，不切换" || bad "unknown peer code: $(cat $SCREENCTL_NOTIFY_FILE) input=$(cat $FAKE_DIR/input)"
stop_daemon; : > $SCREENCTL_NOTIFY_FILE
export FAKE_SSH_RC=0 FAKE_SSH_OUT="proto=3 os=windows code=16 name=$(printf %s 台式机 | base64) devices="
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
$S give peer >/dev/null
t=$(wait_for 8 '[ "$(cat $FAKE_DIR/input)" = 16 ] && [ "$(owned)" = owned=0 ]') && ok "用 request info 问到对方的编码后切过去 ${t}s" || bad "ask code: input=$(cat $FAKE_DIR/input) $(owned) $(cat $SCREENCTL_NOTIFY_FILE)"
grep -q "^CODE_PEER='16'" $SCREENCTL_DIR/config.sh && ok "问到的编码写回配置" || bad "CODE_PEER not saved: $(grep '^CODE_PEER' $SCREENCTL_DIR/config.sh)"
grep -q 'ScreenSwitch.exe request info' $FAKE_DIR/ssh.log && ok "发出的是 request info" || bad "no request info in ssh.log"
stop_daemon

echo "== 显示器在非活跃口也接受指令：直接切换，不经过对方电脑"
fresh direct
$S init >/dev/null
echo "DEVICES=('Switch=17')" >> $SCREENCTL_DIR/config.sh
touch $FAKE_DIR/all_inputs
echo 16 > $FAKE_DIR/input
start_daemon
t=$(wait_for 6 '[ "$(owned)" = owned=0 ] && [ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "读得到但编码不是本机的：不算占有，让出 ${t}s" || bad "all_inputs not-owned: $(owned) $(cat $FAKE_DIR/layout)"
: > $SCREENCTL_NOTIFY_FILE
$S give self >/dev/null
t=$(wait_for 8 '[ "$(owned)" = owned=1 ] && [ "$(cat $FAKE_DIR/layout)" = extend ]') && ok "没配对方电脑也能一键拿回 ${t}s" || bad "direct take: $(owned) input=$(cat $FAKE_DIR/input)"
sleep 1; [ ! -s $SCREENCTL_NOTIFY_FILE ] && [ ! -s $FAKE_DIR/ssh.log ] && ok "直接切换成功：不提示按按钮，不发 ssh" || bad "direct take notify: $(cat $SCREENCTL_NOTIFY_FILE)"
sleep 3   # 过了拿回后的稳定期再切走
echo 17 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=0 ]' >/dev/null || bad "setup: not released"
: > $SCREENCTL_NOTIFY_FILE
$S give self >/dev/null
t=$(wait_for 8 '[ "$(owned)" = owned=1 ]') && ok "主屏在游戏机上时也能直接拿回 ${t}s" || bad "direct take from device"
sleep 4
echo 16 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=0 ]' >/dev/null || bad "setup: not released 2"
: > $SCREENCTL_NOTIFY_FILE
$S give Switch >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/input)" = 17 ]') && ok "未占有时直接把主屏切给 Switch ${t}s" || bad "direct device: input=$(cat $FAKE_DIR/input)"
sleep 1.5; [ ! -s $SCREENCTL_NOTIFY_FILE ] && ok "直接切给设备成功：没有提示" || bad "direct device notify: $(cat $SCREENCTL_NOTIFY_FILE)"
stop_daemon
cat >> $SCREENCTL_DIR/config.sh <<CFG
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
CODE_PEER=16
SSH_KEY='$FAKE_DIR/nokey'
CFG
export FAKE_SSH_RC=2
echo 16 > $FAKE_DIR/input; : > $SCREENCTL_NOTIFY_FILE
start_daemon; wait_for 6 '[ "$(owned)" = owned=0 ]' >/dev/null
$S give self >/dev/null
t=$(wait_for 10 '[ "$(owned)" = owned=1 ]') && ok "对方回答"我也不占有"，但直接切换已生效：拿回成功 ${t}s" || bad "direct + rc2: $(owned)"
sleep 1; notified "按钮" && bad "direct ok but hinted: $(cat $SCREENCTL_NOTIFY_FILE)" || ok "直接切换已生效时不提示按按钮"
sleep 4
echo 17 > $FAKE_DIR/input; wait_for 8 '[ "$(owned)" = owned=0 ]' >/dev/null
: > $SCREENCTL_NOTIFY_FILE
$S give peer >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/input)" = 16 ]') && ok "主屏在游戏机上、本机未占有：直接切给对方 ${t}s" || bad "direct to peer: input=$(cat $FAKE_DIR/input) $(cat $SCREENCTL_NOTIFY_FILE)"
stop_daemon; export FAKE_SSH_RC=0

echo "== 只有主屏（Mac mini 只接一块显示器）：不调整布局"
fresh solo
echo solo > $FAKE_DIR/topology
$S init >/dev/null && ok "init（只有主屏）" || bad "init solo"
grep -q "^LAYOUT=none" $SCREENCTL_DIR/config.sh && ok "init 认出只有主屏：LAYOUT=none" || bad "LAYOUT: $(grep '^LAYOUT' $SCREENCTL_DIR/config.sh)"
cat >> $SCREENCTL_DIR/config.sh <<CFG
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
CODE_PEER=16
TAKE_GRACE_SECS=3
SSH_KEY='$FAKE_DIR/nokey'
CFG
nolayout() { ! grep -qv '^ddc set' $FAKE_DIR/order.log 2>/dev/null; }   # order.log 里只有 DDC 写入，没有任何布局调用
: > $FAKE_DIR/order.log
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null && ok "启动：占有" || bad "solo start: $(owned)"
$S diag | grep -qx "layout=none" && ok "status 显示 layout=none" || bad "status: $($S diag | tr '\n' '|')"
$S give peer >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/input)" = 16 ] && [ "$(owned)" = owned=0 ]') && ok "切给对方：只写 DDC，归属变为未占有 ${t}s" || bad "solo give: input=$(cat $FAKE_DIR/input) $(owned)"
out=$($S request take); rc=$?
[ $rc -eq 0 ] && [ "$out" = applied ] && ok "对方发来 request take：立即记为占有（applied）" || bad "solo request take: rc=$rc out=$out"
echo 15 > $FAKE_DIR/input; sleep 2
[ "$(owned)" = owned=1 ] && ok "主屏切回来，保持占有" || bad "solo after take: $(owned)"
sleep 3
echo 16 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(owned)" = owned=0 ]') && ok "用显示器按钮切走：跟随 ${t}s" || bad "solo follow away"
echo 15 > $FAKE_DIR/input
t=$(wait_for 6 '[ "$(owned)" = owned=1 ]') && ok "切回来：跟随 ${t}s" || bad "solo follow back"
sleep 6   # 给"未占有时的布局检查""自动记住排列"留出时间，确认它们什么都不做
nolayout && ok "全程没有任何布局调用" || bad "solo layout calls: $(tr '\n' ';' < $FAKE_DIR/order.log)"
notified "布局" && bad "solo layout notice: $(cat $SCREENCTL_NOTIFY_FILE)" || ok "没有任何布局方面的提示"
stop_daemon
echo 16 > $FAKE_DIR/input; : > $FAKE_DIR/order.log
start_daemon; sleep 2
[ "$(owned)" = owned=0 ] && nolayout && ok "启动对账（未占有）：只记归属，不动布局" || bad "solo reconcile: $(owned) $(tr '\n' ';' < $FAKE_DIR/order.log)"
stop_daemon

echo "== 另一块屏是外接屏（Mac mini 接两块显示器）"
fresh ext
echo ext > $FAKE_DIR/topology
$S init >/dev/null 2>&1 && bad "两块外接屏时不指定名字也通过了" || ok "两块外接显示器：要求指定哪块是共享的"
$S init P275MV >/dev/null && ok "init P275MV" || bad "init ext"
grep -q "^LAYOUT=second" $SCREENCTL_DIR/config.sh && grep -q "^SECOND_BUILTIN=0" $SCREENCTL_DIR/config.sh && grep -q "^SECOND_ID='EEEEEEEE-" $SCREENCTL_DIR/config.sh \
  && ok "init 认出另一块屏是外接屏" || bad "ext detect: $(grep '^LAYOUT\|^SECOND' $SCREENCTL_DIR/config.sh | tr '\n' ' ')"
grep -q "id:EEEEEEEE-FFFF-0000-1111-222222222222+AAAAAAAA" $SCREENCTL_DIR/config.sh && ok "镜像目标是那块外接屏" || bad "ext mirror args"
cat >> $SCREENCTL_DIR/config.sh <<CFG
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
CODE_PEER=16
SSH_KEY='$FAKE_DIR/nokey'
CFG
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
: > $FAKE_DIR/order.log
$S give peer >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/layout)" = mirror ] && [ "$(owned)" = owned=0 ]') && ok "切给对方：主屏镜像到外接的另一块屏 ${t}s" || bad "ext release: $(cat $FAKE_DIR/layout) $(owned)"
grep -q '^helper' $FAKE_DIR/order.log && bad "外接屏不该去开关：$(grep '^helper' $FAKE_DIR/order.log | tr '\n' ';')" || ok "没有去开关那块外接屏"
echo 15 > $FAKE_DIR/input
t=$(wait_for 8 '[ "$(cat $FAKE_DIR/layout)" = extend ] && [ "$(owned)" = owned=1 ]') && ok "切回来：恢复扩展 ${t}s" || bad "ext take: $(cat $FAKE_DIR/layout) $(owned)"
: > $SCREENCTL_NOTIFY_FILE
out=$($S set owned-layout main-only 2>&1); rc=$?
[ $rc -eq 1 ] && case "$out" in "error: "*"不支持"*) true ;; *) false ;; esac && grep -q "^OWNED_LAYOUT='extend'" $SCREENCTL_DIR/config.sh \
  && ok "另一块屏是外接屏时不提供"只用主屏"：退出码 1，error: 开头" || bad "ext external: rc=$rc [$out] $(grep '^OWNED_LAYOUT' $SCREENCTL_DIR/config.sh)"
$S menu-info | grep -qx "second_builtin=0" && ok "menu-info 报告 second_builtin=0（菜单据此隐藏"仅大屏"）" || bad "menu-info ext"
sleep 3
touch $FAKE_DIR/second_unplugged; : > $FAKE_DIR/order.log; : > $SCREENCTL_NOTIFY_FILE
$S give peer >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/input)" = 16 ] && [ "$(owned)" = owned=0 ]') && ok "另一块屏没接着时切走：照常切换 ${t}s" || bad "ext unplugged give"
sleep 1; ! grep -qv '^ddc set' $FAKE_DIR/order.log && [ ! -s $SCREENCTL_NOTIFY_FILE ] && ok "另一块屏没接着：不做布局操作，也不报错" || bad "ext unplugged: $(tr '\n' ';' < $FAKE_DIR/order.log) $(cat $SCREENCTL_NOTIFY_FILE)"
stop_daemon

echo "== MacBook 合着盖子"
fresh lid
touch $FAKE_DIR/lid_closed
$S init >/dev/null 2>&1 && bad "合着盖子时 init 通过了" || ok "合着盖子时设置会停下，提示打开盖子"
rm -f $FAKE_DIR/lid_closed
$S init >/dev/null || bad "init lid"
cat >> $SCREENCTL_DIR/config.sh <<CFG
PEER_HOST='win-pc.local'
PEER_USER='tester'
PEER_PROG='C:\\Users\\tester\\ScreenSwitch\\ScreenSwitch.exe'
CODE_PEER=16
SSH_KEY='$FAKE_DIR/nokey'
CFG
start_daemon; wait_for 5 '[ "$(owned)" = owned=1 ]' >/dev/null
touch $FAKE_DIR/lid_closed; : > $FAKE_DIR/order.log; : > $SCREENCTL_NOTIFY_FILE
$S give peer >/dev/null
t=$(wait_for 6 '[ "$(cat $FAKE_DIR/input)" = 16 ] && [ "$(owned)" = owned=0 ]') && ok "合着盖子切给对方：照常切换 ${t}s" || bad "lid give: input=$(cat $FAKE_DIR/input) $(owned)"
sleep 6
! grep -qv '^ddc set' $FAKE_DIR/order.log && ok "合着盖子：不做布局操作，不去开笔记本屏" || bad "lid layout calls: $(tr '\n' ';' < $FAKE_DIR/order.log)"
notified "合上再打开" && bad "lid: 提示了合上再打开盖子" || ok "没有"笔记本屏无法恢复"的提示"
$S diag | grep -qx "layout=none" && ok "status 显示 layout=none（盖子合着）" || bad "lid status: $($S diag | tr '\n' '|')"
rm -f $FAKE_DIR/lid_closed   # 打开盖子：自带屏回来，系统把它摆成扩展，主屏还在对方那里
t=$(wait_for 12 '[ "$(cat $FAKE_DIR/layout)" = mirror ]') && ok "开盖后发现主屏在线却不是镜像：改回镜像 ${t}s" || bad "lid open not re-mirrored: $(cat $FAKE_DIR/layout)"
touch $FAKE_DIR/lid_closed; : > $FAKE_DIR/order.log
echo 15 > $FAKE_DIR/input
t=$(wait_for 6 '[ "$(owned)" = owned=1 ]') && ok "合着盖子时主屏切回来：记为占有 ${t}s" || bad "lid take: $(owned)"
sleep 1; [ ! -s $FAKE_DIR/order.log ] && ok "合着盖子拿回：没有布局调用" || bad "lid take calls: $(tr '\n' ';' < $FAKE_DIR/order.log)"
stop_daemon

echo "== 设置问答（setup）"
fresh setup
export FAKE_SSH_RC=0 FAKE_SSH_OUT="proto=3 os=mac code=16 name=$(printf %s mini | base64) devices=17:$(printf %s Switch | base64)"
# 依次回答：本机名字、对方主机、系统、对方名字、用户名、程序路径（回车取默认）、生成密钥（家目录换成了假的，不碰真实的 ~/.ssh）、
# 设备：不删除（回车）、添加"客厅 PS5"=18、编码写错一次（abc）、与本机重复一次（15）、结束
printf '%s\n' '书房 Mac' 'mini.local' 'mac' 'mini' 'bob' '' 'y' '' '客厅 PS5' '18' '坏设备' 'abc' '重复' '15' '' \
  | HOME=$FAKE_DIR/fakehome $S setup > $FAKE_DIR/setup.out 2>&1 && ok "setup 跑完" || bad "setup failed: $(tail -3 $FAKE_DIR/setup.out)"
C=$SCREENCTL_DIR/config.sh
grep -q "^SELF_NAME='书房 Mac'" $C && grep -q "^PEER_NAME='mini'" $C && grep -q "^PEER_OS=mac" $C && grep -q "^PEER_HOST='mini.local'" $C && grep -q "^PEER_USER='bob'" $C \
  && ok "本机与对方的名字、系统、地址、用户名写进配置" || bad "setup peer: $(grep '^SELF_NAME\|^PEER_' $C | tr '\n' ' ')"
grep -q "^PEER_PROG='~/screenctl/screenctl.sh'" $C && ok "对方是 Mac：程序路径默认 ~/screenctl/screenctl.sh" || bad "setup prog: $(grep '^PEER_PROG' $C)"
grep -q "^CODE_PEER='16'" $C && ok "对方的编码由 request info 取得" || bad "setup CODE_PEER: $(grep '^CODE_PEER' $C)"
grep -q "'Switch=17'" $C && grep -q "'客厅 PS5=18'" $C && ok "设备：从对方读到 Switch，另外添加了"客厅 PS5"" || bad "setup devices: $(sed -n '/^DEVICES=(/,/^)/p' $C | tr '\n' ' ')"
grep -q "坏设备\|重复" $C && bad "写错的设备也被添加了" || ok "编码不是数字、或与电脑重复的设备没有添加"
[ -f $FAKE_DIR/fakehome/.ssh/id_ed25519.pub ] && grep -q "~/screenctl/screenctl.sh authorize \"ssh-ed25519 " $FAKE_DIR/setup.out && ok "生成了密钥；对方是 Mac：提示在对方运行 authorize 并带上本机公钥" || bad "setup ssh hint: $(grep -n authorize $FAKE_DIR/setup.out)"
out=$($S request info)
[ "$out" = "proto=3 os=mac code=15 name=$(printf %s '书房 Mac' | base64) devices=17:$(printf %s Switch | base64),18:$(printf %s '客厅 PS5' | base64)" ] && ok "request info 里带着中文和空格的名字" || bad "info after setup: $out"
printf '%s\n' '1' 'NS2' '17' '' | $S setup devices > $FAKE_DIR/setup2.out 2>&1
! grep -q "'Switch=17'" $C && grep -q "'NS2=17'" $C && grep -q "'客厅 PS5=18'" $C && ok "setup devices：删掉 Switch、添加 NS2，其余不变" || bad "setup devices: $(sed -n '/^DEVICES=(/,/^)/p' $C | tr '\n' ' ')"
grep -q "^PEER_HOST='mini.local'" $C && ok "setup devices 不动别的设置" || bad "setup devices clobbered peer"
fresh setup2
$S setup < /dev/null > $FAKE_DIR/setup.out 2>&1 && grep -q "^PEER_HOST=''" $SCREENCTL_DIR/config.sh && grep -q "^CODE_SELF=15" $SCREENCTL_DIR/config.sh \
  && ok "没有输入时（非交互）setup 全部取默认值：不配对方" || bad "non-interactive setup: $(tail -3 $FAKE_DIR/setup.out)"

echo "== authorize：把对方的公钥限定为 ssh-gate"
export AUTHORIZED_KEYS=$FAKE_DIR/ak
echo "ssh-ed25519 AAAAOTHERKEY someone@else" > $AUTHORIZED_KEYS
$S authorize "ssh-ed25519 AAAATESTKEY peer@pc" >/dev/null && ok "authorize" || bad "authorize failed"
grep -q "^command=\"$SCREENCTL_DIR/screenctl.sh ssh-gate\",no-port-forwarding,no-agent-forwarding,no-pty,no-X11-forwarding ssh-ed25519 AAAATESTKEY peer@pc$" $AUTHORIZED_KEYS \
  && ok "写入的是带强制命令和限制的一行" || bad "authorize line: $(cat $AUTHORIZED_KEYS)"
grep -q "AAAAOTHERKEY" $AUTHORIZED_KEYS && ok "别的公钥保留" || bad "authorize dropped other keys"
$S authorize "ssh-ed25519 AAAATESTKEY peer@pc" >/dev/null
[ "$(grep -c AAAATESTKEY $AUTHORIZED_KEYS)" = 1 ] && ok "重复运行不会写两行" || bad "authorize duplicated"
expect_rc 1 $S authorize "不是公钥"
unset AUTHORIZED_KEYS

echo; echo "PASS=$pass FAIL=$fail"
[ $fail -eq 0 ]

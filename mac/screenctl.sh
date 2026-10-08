#!/bin/bash
# screenctl.sh — 屏幕快速切换 Mac 端
# 规范：docs/design.md（归属状态机、菜单语义、跨机协议、Mac 侧）
# 须兼容 macOS 自带的 bash 3.2：不用关联数组、不开 set -u。
#
# 子命令：
#   setup [显示器名]          交互式设置：探测主屏和屏幕组合，填对方电脑和其他设备，生成 config.sh
#   setup devices             只增删其他设备
#   init [显示器名]           setup 里不需要提问的探测部分（主屏显示本机时运行）
#   authorize <公钥>          把对方电脑的公钥加进 ~/.ssh/authorized_keys，限定为 ssh-gate 强制命令
#   give peer | self | <设备名或编码>   切给对方 / 拿回 / 切给某个设备；守护进程在跑时交给它执行
#   take | release            手动拿回 / 让出
#   status                    输出 owned=1|0 与诊断信息
#   request give peer         供对方 SSH 调用：请本机把主屏切给对方。0 已接受 / 2 未占有 / 3 守护进程未运行
#   request give code <n>     供对方 SSH 调用：请本机把主屏切到编码为 n 的设备；设备列表里没有时 1
#   request take              供对方 SSH 调用：对方即将把主屏切给本机，本机预先恢复布局
#   request info              供对方 SSH 调用：本机的编码、名字、设备列表
#   menu-info                 菜单栏程序读取：名字、设备、快捷键、布局模式
#   watch                     守护进程（launchd 调用）
#   ssh-gate                  authorized_keys 强制命令入口，只放行上面几条 request

SCREENCTL_DIR="${SCREENCTL_DIR:-$HOME/screenctl}"
export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

CONFIG="$SCREENCTL_DIR/config.sh"
STATE="$SCREENCTL_DIR/state"
CMD="$SCREENCTL_DIR/cmd"
LOG="$SCREENCTL_DIR/screenctl.log"
PIDFILE="$SCREENCTL_DIR/watch.pid"
BUSY="$SCREENCTL_DIR/busy"            # 主动切换进行中，内容为执行者 pid
DDC_LOCK="$SCREENCTL_DIR/.ddc.lock"   # 同一时刻只允许一个进程访问 DDC，并发读会串包
WATCH_LOCK="$SCREENCTL_DIR/.watch.lock"

# 测试时可用环境变量替换外部工具
M1DDC="${M1DDC:-m1ddc}"
DISPLAYPLACER="${DISPLAYPLACER:-displayplacer}"
SSH_BIN="${SSH_BIN:-ssh}"
DISPLAY_HELPER="${DISPLAY_HELPER:-$SCREENCTL_DIR/bin/display-helper}"
IOREG="${IOREG:-ioreg}"

# ---- 默认配置，config.sh 覆盖 ----
MAIN_NAME=""         # 主屏（共享显示器）在 m1ddc display list 里的名字；留空时 init 自动选唯一的一块外接显示器
MAIN_UUID=""
MAIN_ID=""
SELF_NAME=""         # 本机的名字；留空时取电脑名
CODE_SELF=""         # 本机所接输入口的编码（VCP 0x60，十进制），init 读出
PEER_NAME=""         # 对方电脑的名字，用在菜单和提示里；留空时按 PEER_OS 取 Windows 或 Mac
PEER_OS=""           # windows | mac；留空 = windows
PEER_HOST=""         # 对方的主机名（如 my-pc.local）或局域网 IP。PEER_HOST、PEER_USER、PEER_PROG 都填好才走 SSH
PEER_USER=""         # 对方的用户名（对方是 Windows 时须在 Administrators 组）
PEER_PROG=""         # 对方机器上的程序：Windows 为 'C:\Users\<用户名>\ScreenSwitch\ScreenSwitch.exe'（路径里不能有空格），Mac 为 '~/screenctl/screenctl.sh'
CODE_PEER=""         # 对方所接输入口的编码；留空 = 未知，切给对方时先向它询问
DEVICES=()           # 其他设备（游戏机等），每项 '名字=编码'
HOTKEY_PEER=""       # 菜单栏程序的全局快捷键：占有时切给对方，未占有时拿回；留空 = 不注册
HOTKEY_DEVICES=()    # 各设备的快捷键，与 DEVICES 一一对应；留空 = 不注册
DIRECT_TAKE=1        # 1 = 拿回时先直接向主屏发切换指令（有的显示器在非活跃口也接受），不行再请对方代发
DIRECT_WAIT_SECS=3   # 对方没帮上忙时，等几秒看直接发的指令有没有生效
LAYOUT=""            # second = 本机另有一块屏；none = 只有主屏，不调整布局；留空 = second
SECOND_ID=""         # 另一块屏的 displayplacer id
SECOND_BUILTIN=""    # 1 = 另一块屏是笔记本自带屏；0 = 外接屏；留空 = 1
EXTEND_ARGS=()
MIRROR_ARGS=()
SSH_KEY="$HOME/.ssh/id_ed25519"
SSH_CONNECT_TIMEOUT=3
SSH_TOTAL_TIMEOUT=10
SSH_ADDRESS_FAMILY=inet   # .local 常解析出多个 IPv6 地址，ssh 会逐个等满超时；只走 IPv4
POLL_TICKS=4
POLL_TICKS_UNOWNED=1 # 未占有时每几拍读一次（1 = 每 0.5 秒），主屏切回来时尽快拿回（设计 4.3）
TAKE_GRACE_SECS=8    # 主动拿回：对方接受请求后立即恢复布局，这段秒数内读不到主屏不让出（设计 4.3）；0 = 关闭
NOTIFY_PEER_TAKE=1   # 切给对方前先 SSH 通知它 request take，让它预先恢复布局（设计 4.5）；对方不支持或不在线只是多等几秒
PRETAKE_WAIT_SECS=3  # request take 最多等守护进程把布局应用完几秒再返回：对端拿到返回值才写 DDC（对端等待上限 5 秒减去 SSH 开销）
DEBOUNCE=2
RELEASE_DEBOUNCE=4   # 占有时连续几次读失败才让出（0.5 秒一次）：垃圾读数（99、90、最大亮度 55…）通常只持续 1–2 次，2 次会误让出
SETTLE_SECS=3        # 拿回主屏后几秒内不读 DDC：显示器重新配置、链路重训期间读数不可靠
UNMIRRORED_FIX_SECS=4  # 未占有时主屏在线却不是镜像（例如拔线重插后 macOS 恢复成扩展），持续几秒仍读不到主屏就改回镜像；0 = 关闭
SWITCH_WAIT=3
OTHER_OBSERVE=15
STALE_SECS=10
LAYOUT_RETRY_SECS=10
LOG_READS=0          # 1 = 记录每一次 DDC 读取，排查延迟用
SANITY_MAXLUM=0      # 主屏"最大亮度"的真实值（init 学习）；>0 时读到本机编码后还须读到它才算占有
OWNED_LAYOUT=extend  # 拿回后的布局：extend = 扩展；external = 仅大屏（关闭笔记本屏，只在另一块屏是笔记本自带屏时可用）
LAPTOP_CGID=1        # 笔记本屏的系统显示编号（CGDirectDisplayID），关屏后要靠它开回来；init 与关屏时学习
AUTO_SAVE_LAYOUT=1   # 1 = 占有且扩展时自动记住用户改过的屏幕排列
AUTO_SAVE_POLLS=5    # 每 5 次轮询（约 10 秒）比对一次排列，连续两次一致才保存
# 旧版配置里的名字：只读不写，由 normalize_config 映射到上面的新名字
LAPTOP_ID=""; CODE_WINDOWS=""; CODE_MAC=""; CODE_OTHER=""; WIN_HOST=""; WIN_USER=""; WIN_EXE=""

# 旧名字 → 新名字（设计文档 4.2）；新名字已有值时以新名字为准。旧的 HOTKEY_MAIN / HOTKEY_OTHER 不再读取
normalize_config() {
    local c i
    [ -n "$CODE_SELF" ] || CODE_SELF=${CODE_MAC:-15}
    [ -n "$CODE_PEER" ] || CODE_PEER=$CODE_WINDOWS
    [ -n "$PEER_HOST" ] || PEER_HOST=$WIN_HOST
    [ -n "$PEER_USER" ] || PEER_USER=$WIN_USER
    [ -n "$PEER_PROG" ] || PEER_PROG=$WIN_EXE
    [ -n "$PEER_OS" ] || PEER_OS=windows
    if [ -z "$PEER_NAME" ]; then
        if [ "$PEER_OS" = mac ]; then PEER_NAME=Mac; else PEER_NAME=Windows; fi
    fi
    if [ ${#DEVICES[@]} -eq 0 ] && [ -n "$CODE_OTHER" ]; then
        i=1
        for c in $CODE_OTHER; do DEVICES+=("设备 $i=$c"); i=$((i + 1)); done
    fi
    [ -n "$SECOND_ID" ] || SECOND_ID=$LAPTOP_ID
    [ -n "$SECOND_BUILTIN" ] || SECOND_BUILTIN=1
    [ -n "$LAYOUT" ] || LAYOUT=second
}

load_config() {
    [ -f "$CONFIG" ] && . "$CONFIG"
    normalize_config
}
load_config

# 软件切不回来时提示用户的话（主屏停在别的设备上时，只有显示器按钮能切回来）
HINT_BUTTON="请用显示器上的按钮切换输入源"

# ---- 运行时状态 ----
MODE=cli            # daemon | direct | cli
OWNED=0
OKS=0
FAILS=0
NEXT_POLL=0
LAST_READ=""
LAYOUT_ERR=""
LAYOUT_FAIL_AT=0
LAYOUT_FAIL_NOTIFIED=0
STALE_LOGGED=0
PENDING_SIG=""
SAVE_COUNTDOWN=0
GRACE_UNTIL=0       # 预先拿回后的宽限期截止时刻（Unix 秒）；0 = 不在宽限期
LAYOUT_AT=0         # 最近一次拿回完成的时刻（Unix 秒），SETTLE_SECS 内不读 DDC
TAKE_PENDING=0      # 1 = 拿回时主屏不在系统拓扑里，布局还没应用，等主屏回来再应用
PENDING_SEEN=0      # 待拿回期间连续几拍看到主屏回到拓扑
UNMIRRORED_SINCE=0  # 未占有时从何时起看到主屏在线却不是镜像（Unix 秒）；0 = 没看到

# ---------------------------------------------------------------- 基础

log() {
    [ -d "$SCREENCTL_DIR" ] || mkdir -p "$SCREENCTL_DIR"
    local size
    size=$(stat -f %z "$LOG" 2>/dev/null || echo 0)
    [ "$size" -gt 1048576 ] && mv -f "$LOG" "$LOG.1"
    printf '%s [%s %s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$MODE" "$$" "$*" >> "$LOG"
}

# 菜单栏程序在运行时交给它弹通知：launchd 拉起的后台进程用 osascript 发的通知，
# 新版 macOS 会静默丢弃（通知设置里连条目都不会出现）。
notify() {
    local pid
    log "notify: $1"
    if [ -n "${SCREENCTL_NOTIFY_FILE:-}" ]; then
        printf '%s\n' "$1" >> "$SCREENCTL_NOTIFY_FILE"
        return 0
    fi
    pid=$(cat "$SCREENCTL_DIR/menu.pid" 2>/dev/null)
    if is_int "$pid" && kill -0 "$pid" 2>/dev/null; then
        printf '%s\n' "$1" >> "$SCREENCTL_DIR/notify.queue"
        return 0
    fi
    osascript - "$1" >/dev/null 2>&1 <<'EOF'
on run argv
    display notification (item 1 of argv) with title "屏幕切换"
end run
EOF
}

die() {
    echo "错误：$*" >&2
    exit 1
}

need_config() {
    [ -n "$MAIN_UUID" ] || die "缺少配置，请在主屏显示本机时运行：$0 setup"
    [ "$LAYOUT" = none ] && return 0
    [ -n "$MAIN_ID" ] && [ ${#MIRROR_ARGS[@]} -gt 0 ] \
        || die "缺少配置，请在主屏显示本机且为扩展时运行：$0 setup"
}

# 本机的名字：配置里没有就取电脑名
self_name() {
    local n=$SELF_NAME
    [ -n "$n" ] || n=$(scutil --get ComputerName 2>/dev/null)
    [ -n "$n" ] || n=Mac
    printf '%s' "$n"
}

# PEER_HOST、PEER_USER、PEER_PROG 都填了才走 SSH
peer_configured() {
    [ -n "$PEER_HOST" ] && [ -n "$PEER_USER" ] && [ -n "$PEER_PROG" ]
}

# 设备（DEVICES 每项 '名字=编码'；名字里可以有 =，取最后一个 = 之后的为编码）
device_name_by_code() {
    local e
    for e in "${DEVICES[@]}"; do
        [ "${e##*=}" = "$1" ] && { printf '%s' "${e%=*}"; return 0; }
    done
    return 1
}

# 名字或编码 → 编码；设备列表里没有返回 1
device_code_of() {
    local e
    for e in "${DEVICES[@]}"; do
        if [ "${e%=*}" = "$1" ] || [ "${e##*=}" = "$1" ]; then
            printf '%s' "${e##*=}"
            return 0
        fi
    done
    return 1
}

# 名字经 Base64 在两台电脑之间传（设计文档 5.4）：两端终端编码不同，只传 ASCII
b64() { printf '%s' "$1" | base64 | tr -d '\n'; }
unb64() { printf '%s' "$1" | base64 -D 2>/dev/null; }

is_int() {
    local v=${1#-}
    case "$v" in ''|*[!0-9]*) return 1 ;; esac
    return 0
}

daemon_alive() {
    local pid ts
    pid=$(cat "$PIDFILE" 2>/dev/null)
    is_int "$pid" && kill -0 "$pid" 2>/dev/null || return 1
    # 刚启动、还在对账的守护进程也算在运行（pid 文件 15 秒内写入）
    [ $(( $(date +%s) - $(stat -f %m "$PIDFILE" 2>/dev/null || echo 0) )) -le 15 ] && return 0
    read -r _ ts 2>/dev/null < "$STATE"
    is_int "$ts" && [ $(( $(date +%s) - ts )) -le "$STALE_SECS" ]
}

write_cmd() {
    printf '%s\n' "$1" > "$CMD.tmp" && mv -f "$CMD.tmp" "$CMD"
}

write_state() {
    [ "$MODE" = daemon ] || return 0
    printf 'owned=%d %d\n' "$OWNED" "$(date +%s)" > "$STATE.tmp" && mv -f "$STATE.tmp" "$STATE"
}

# ---------------------------------------------------------------- DDC

# 读 VCP 0x60，主屏正显示本机时返回 0。
# 实测：主屏在别的口上时 m1ddc 照样返回 rc=0，值为 0、-7、-102 之类的垃圾，
# 所以"读取成功"定义为 rc=0 且值等于本机编码。
ddc_read() {
    local out rc
    out=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" get input 2>/dev/null)
    rc=$?
    LAST_READ="rc=$rc val=$(printf '%s' "$out" | head -1 | cut -c1-40)"
    if [ "$LOG_READS" = 1 ]; then
        # 诊断：顺带读亮度与最大亮度。若是缓存的旧回复，三个值会"串"成同一份数据
        local lum maxl
        lum=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" get luminance 2>/dev/null | head -1 | cut -c1-20)
        maxl=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" max luminance 2>/dev/null | head -1 | cut -c1-20)
        log "ddc: $LAST_READ lum=$lum max=$maxl"
    fi
    [ $rc -eq 0 ] && is_int "$out" && [ "$out" -eq $((CODE_SELF)) ] || return 1
    ddc_sanity
}

# 读一次输入源的原始值，不判断归属；读不到时不输出。用来核对"直接写入"有没有把主屏切到某个编码
ddc_raw_input() {
    local out
    out=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" get input 2>/dev/null | head -1)
    is_int "$out" && printf '%s' "$out"
}

# 显示器不回复时，m1ddc 返回缓存里与请求无关的数据（实测输入源与亮度读出同一个数），
# Mac 睡醒后这份数据可能恰好是睡前最后一次真实回复的 15。再读一次最大亮度核对：
# 缓存数据给不出正确的最大亮度；而且每次都会把缓存刷成亮度回复，下次不会再"剩下 15"。
ddc_sanity() {
    [ "${SANITY_MAXLUM:-0}" -gt 0 ] 2>/dev/null || return 0
    local m
    m=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" max luminance 2>/dev/null | head -1)
    if is_int "$m" && [ "$m" -eq "$SANITY_MAXLUM" ]; then
        STALE_LOGGED=0
        return 0
    fi
    LAST_READ="$LAST_READ maxlum=$m≠$SANITY_MAXLUM"
    if [ $STALE_LOGGED -eq 0 ]; then
        STALE_LOGGED=1
        log "read: 输入源读到 $CODE_SELF 但最大亮度=$m（应为 $SANITY_MAXLUM），判为缓存旧数据，不算占有"
    fi
    return 1
}

ddc_write() {
    lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$MAIN_UUID" set input "$(($1))" >/dev/null 2>&1
}

# 连读 3 次，至少 2 次成功视为占有（用于启动对账和无守护进程时）
ddc_owned_now() {
    local i oks=0
    for i in 1 2 3; do
        ddc_read && oks=$((oks + 1))
        [ $i -lt 3 ] && sleep 0.3
    done
    [ $oks -ge 2 ]
}

# ---------------------------------------------------------------- 布局

# 盖子是否合着（只有 MacBook 有这个状态；读不到当作开着）
lid_closed() {
    "$IOREG" -r -k AppleClamshellState -d 1 2>/dev/null | grep -q '"AppleClamshellState" = Yes'
}

# 另一块屏此刻在不在线：按 uuid（= displayplacer id）在 display-helper 里找；没有 helper 时问 displayplacer
second_online() {
    if [ -x "$DISPLAY_HELPER" ]; then
        "$DISPLAY_HELPER" list 2>/dev/null | awk -v u="uuid=$SECOND_ID" 'toupper($0) ~ toupper(u) {f=1} END {exit !f}'
    else
        "$DISPLAYPLACER" list 2>/dev/null | grep -q "^Persistent screen id: $SECOND_ID\$"
    fi
}

# 这一次不做布局操作（设计文档 7.2）：本机只有主屏（LAYOUT=none）；或另一块屏此刻用不了——
# 笔记本合着盖子，或外接的那块没接着。这时主屏被切走也没有窗口会跑丢
no_layout() {
    [ "$LAYOUT" = none ] && return 0
    if [ "$SECOND_BUILTIN" = 1 ]; then lid_closed; else ! second_online; fi
}

# 占有时应处于的布局：extend，或 external（仅大屏，只在另一块屏是笔记本自带屏时可用）
want_layout() {
    if [ "$OWNED_LAYOUT" = external ] && [ "$SECOND_BUILTIN" = 1 ]; then echo external; else echo extend; fi
}

# 输出 none（不做布局操作）/ mirror / extend / external（主屏在、另一块屏关闭）/ absent（主屏不在系统拓扑里）
layout_mode() {
    local line
    no_layout && { echo none; return; }
    line=$("$DISPLAYPLACER" list 2>/dev/null | tail -1)
    case "$line" in
        *"+$MAIN_ID"*|*"$MAIN_ID+"*) echo mirror ;;
        *"$MAIN_ID"*)
            case "$line" in *"$SECOND_ID"*) echo extend ;; *) echo external ;; esac ;;
        *) echo absent ;;
    esac
}

laptop_enabled() {
    "$DISPLAYPLACER" list 2>/dev/null | grep -q "^Persistent screen id: $SECOND_ID\$"
}

# 主屏的系统显示编号：按 uuid（= MAIN_ID）找，另接的显示器不算；旧版 display-helper 不输出 uuid，取第一块外接屏
main_cgid() {
    "$DISPLAY_HELPER" list 2>/dev/null | awk -v u="uuid=$MAIN_ID" '
        / uuid=/ { has_uuid = 1 }
        toupper($0) ~ toupper(u) { print $1; found = 1; exit }
        /builtin=0/ && first == "" { first = $1 }
        END { if (!found && !has_uuid && first != "") print first }'
}

# $1 = enable | disable，成功返回 0，失败时 LAYOUT_ERR 给出原因。
# 关闭前从 display-helper 读出笔记本屏的显示编号并记住，关闭后它就不在在线列表里了。
laptop_set() {
    local id i
    # 另一块屏是外接屏时不去开关它：打开无事可做，关闭不支持
    if [ "$SECOND_BUILTIN" != 1 ]; then
        [ "$1" = enable ] && return 0
        LAYOUT_ERR="另一块屏是外接屏，不支持关闭"
        return 1
    fi
    if [ "$1" = enable ]; then
        laptop_enabled && return 0
        "$DISPLAY_HELPER" enable "${LAPTOP_CGID:-1}" >> "$LOG" 2>&1
        for i in 1 2 3 4 5 6 7 8 9 10; do
            laptop_enabled && { log "layout: 已打开笔记本屏"; return 0; }
            sleep 0.5
        done
        LAYOUT_ERR="笔记本屏无法恢复，请合上再打开笔记本盖子"
        return 1
    fi
    [ -x "$DISPLAY_HELPER" ] || { LAYOUT_ERR="缺少 $DISPLAY_HELPER，请重新运行 install.sh"; return 1; }
    id=$("$DISPLAY_HELPER" list 2>/dev/null | awk '/builtin=1/ {print $1; exit}')
    is_int "$id" || { LAYOUT_ERR="找不到笔记本屏的显示编号"; return 1; }
    remember_laptop_cgid "$id"
    "$DISPLAY_HELPER" disable "$id" >> "$LOG" 2>&1 || { LAYOUT_ERR="关闭笔记本屏失败"; return 1; }
    for i in 1 2 3 4 5 6 7 8 9 10; do
        laptop_enabled || { log "layout: 已关闭笔记本屏"; return 0; }
        sleep 0.5
    done
    LAYOUT_ERR="关闭笔记本屏后它仍在"
    return 1
}

# 记住笔记本屏的显示编号：关闭后它就不在在线列表里，开回来要靠它
remember_laptop_cgid() {
    [ "$1" != "$LAPTOP_CGID" ] || return 0
    load_config; LAPTOP_CGID=$1; write_config
    log "layout: 记住笔记本屏显示编号 $1"
}

# 从保存的扩展排列里解析主屏模式 → MM_W MM_H MM_HZ MM_SCALING（hidpi|lowdpi）；解析不出返回 1，MM_W 为空
main_mode_spec() {
    local a v res
    MM_W=""; MM_H=""; MM_HZ=""; MM_SCALING=lowdpi
    a=""
    for v in "${EXTEND_ARGS[@]}"; do case "$v" in "id:$MAIN_ID "*) a=$v ;; esac; done
    [ -n "$a" ] || return 1
    res=$(printf '%s\n' "$a" | grep -o 'res:[0-9]*x[0-9]*' | cut -c5-)
    MM_HZ=$(printf '%s\n' "$a" | grep -o 'hz:[0-9]*' | cut -c4-)
    case "$a" in *"scaling:on"*) MM_SCALING=hidpi ;; esac
    MM_W=${res%x*}; MM_H=${res#*x}
    is_int "$MM_W" && is_int "$MM_H" && is_int "$MM_HZ" || { MM_W=""; return 1; }
}

# 主屏是否已完整枚举、可以应用布局。显示器切换输入源时主屏会重新枚举：几秒内它还在拓扑里，
# 但没有 EDID，模式列表不全、displayplacer 按持久 id 也找不到它（实测 19:53），这时配置必定失败，白等 3 秒。
# 判据：在拓扑里，且 display-helper 能在主屏上找到保存的扩展模式；没法探测时当作就绪。$1 可传入已知的 layout_mode。
main_ready() {
    local mode ext
    MAIN_READY_ERR=""
    mode=${1:-$(layout_mode)}
    [ "$mode" != absent ] || { MAIN_READY_ERR="主屏不在系统拓扑中"; return 1; }
    [ -x "$DISPLAY_HELPER" ] && main_mode_spec || return 0
    ext=$(main_cgid)
    is_int "$ext" || { MAIN_READY_ERR="display-helper 看不到主屏"; return 1; }
    "$DISPLAY_HELPER" mode "$ext" "$MM_W" "$MM_H" "$MM_HZ" "$MM_SCALING" >/dev/null 2>&1 \
        || { MAIN_READY_ERR="主屏模式列表里还没有 ${MM_W}x${MM_H}@${MM_HZ}，正在重新枚举"; return 1; }
}

# 镜像 → 仅大屏：display-helper 在同一个配置事务里取消镜像、把主屏设为保存的扩展模式、关闭笔记本屏，
# 不经过扩展状态，显示器只重配一次（原来是 镜像→扩展→关笔记本屏，重配两次，用户看到镜像画面再闪两下）
laptop_solo() {
    local lap ext i
    [ -x "$DISPLAY_HELPER" ] || return 1
    lap=$("$DISPLAY_HELPER" list 2>/dev/null | awk '/builtin=1/ {print $1; exit}')
    ext=$(main_cgid)
    is_int "$lap" && is_int "$ext" || return 1
    remember_laptop_cgid "$lap"
    # 主屏的模式取自保存的扩展排列；解析不出就让系统自选
    if main_mode_spec; then
        "$DISPLAY_HELPER" solo "$lap" "$ext" "$MM_W" "$MM_H" "$MM_HZ" "$MM_SCALING" >> "$LOG" 2>&1 || return 1
    else
        "$DISPLAY_HELPER" solo "$lap" "$ext" >> "$LOG" 2>&1 || return 1
    fi
    for i in 1 2 3 4 5 6; do
        [ "$(layout_mode)" = external ] && { log "layout: 已从镜像直接进入仅大屏"; return 0; }
        sleep 0.5
    done
    return 1
}

# 仅大屏 → 镜像：让 display-helper 打开笔记本屏后立刻设镜像，不经过扩展状态，屏幕少闪一次
laptop_mirror_on() {
    local ext i
    [ "$SECOND_BUILTIN" = 1 ] || return 1
    ext=$(main_cgid)
    is_int "$ext" || return 1
    "$DISPLAY_HELPER" enable-mirror "${LAPTOP_CGID:-1}" "$ext" >> "$LOG" 2>&1 || return 1
    for i in 1 2 3 4 5 6; do
        [ "$(layout_mode)" = mirror ] && { log "layout: 已打开笔记本屏并镜像"; return 0; }
        sleep 0.5
    done
    return 1
}

# $1 = extend | external | mirror；应用并校验。返回 0 成功；2 = 主屏不在系统拓扑里，什么都没做（只保证笔记本屏开着，
# 镜像时笔记本屏开不了返回 1）；1 失败，LAYOUT_ERR 给出原因。进入镜像或扩展前一律先打开笔记本屏。
# 不做布局操作时（只有主屏、合着盖子等，见 no_layout）直接算成功。
apply_layout() {
    local want=$1 mode i
    mode=$(layout_mode)
    [ "$mode" = none ] && return 0
    [ "$mode" = "$want" ] && return 0
    if [ "$mode" = absent ]; then
        # 让出时主屏已不显示本机：笔记本屏开不了就是失败，交给 layout_failed 通知并退避重试
        if ! laptop_set enable && [ "$want" = mirror ]; then
            return 1
        fi
        log "layout: 主屏不在系统拓扑中，跳过 $want"
        return 2
    fi
    if [ "$want" != mirror ] && ! main_ready "$mode"; then
        log "layout: 主屏还没就绪（$MAIN_READY_ERR），跳过 $want"
        return 2
    fi
    if [ "$want" = mirror ] && [ "$mode" = external ] && laptop_mirror_on; then
        return 0
    fi
    if [ "$want" = external ] && [ "$mode" = mirror ]; then
        laptop_solo && return 0
        log "layout: 未能直接从镜像进入仅大屏，改走 扩展→关笔记本屏"
    fi
    if [ "$want" != mirror ] && [ ${#EXTEND_ARGS[@]} -eq 0 ]; then
        LAYOUT_ERR="未保存扩展排列，请在扩展状态下运行 screenctl.sh init"
        return 1
    fi
    laptop_set enable || return 1
    case "$want" in
        mirror) "$DISPLAYPLACER" "${MIRROR_ARGS[@]}" >> "$LOG" 2>&1 ;;
        *)      "$DISPLAYPLACER" "${EXTEND_ARGS[@]}" >> "$LOG" 2>&1 ;;
    esac
    for i in 1 2 3 4 5 6; do
        mode=$(layout_mode)
        [ "$mode" = mirror ] && [ "$want" = mirror ] && return 0
        [ "$mode" = extend ] && [ "$want" != mirror ] && break
        sleep 0.5
    done
    if [ "$want" = external ] && [ "$mode" = extend ]; then
        laptop_set disable && return 0
        return 1
    fi
    [ "$mode" = "$want" ] && return 0
    LAYOUT_ERR="displayplacer 执行后布局仍为 $mode"
    return 1
}

layout_failed() {
    LAYOUT_FAIL_AT=$(date +%s)
    log "layout: 失败 $LAYOUT_ERR"
    if [ $LAYOUT_FAIL_NOTIFIED -eq 0 ]; then
        LAYOUT_FAIL_NOTIFIED=1
        notify "$1：$LAYOUT_ERR"
    fi
}

# 拿回 = 扩展。布局失败时状态不变（设计文档第 11 节），由轮询退避重试。
# 拿回 = OWNED_LAYOUT（扩展或仅大屏）。布局失败时状态不变（设计文档第 11 节），由轮询退避重试；
# 只是关不掉笔记本屏时按扩展算拿回成功，并提示一次。
do_take() {
    local want rc
    want=$(want_layout)
    apply_layout "$want"
    rc=$?
    if [ $rc -eq 0 ]; then
        OWNED=1
        TAKE_PENDING=0
        PENDING_SEEN=0
        LAYOUT_FAIL_AT=0
        LAYOUT_FAIL_NOTIFIED=0
        LAYOUT_AT=$(date +%s)
        write_state
        log "take: 布局 $want，owned=1"
        return 0
    fi
    if [ $rc -eq 2 ]; then
        # 主屏还没就绪（不在拓扑里，或正在重新枚举；预先拿回时常见）：布局没法应用，
        # 先记为占有并留下"待拿回"标记，守护进程每拍探测，主屏就绪后再应用。什么都没重配，不设稳定期
        OWNED=1
        TAKE_PENDING=1
        PENDING_SEEN=0
        write_state
        log "take: 主屏还没就绪，owned=1，待主屏就绪后再应用 $want"
        return 0
    fi
    if [ "$want" = external ] && [ "$(layout_mode)" = extend ]; then
        OWNED=1
        TAKE_PENDING=0
        PENDING_SEEN=0
        LAYOUT_AT=$(date +%s)
        write_state
        log "take: 关闭笔记本屏失败（$LAYOUT_ERR），按扩展拿回"
        notify "无法关闭笔记本屏，已按扩展显示：$LAYOUT_ERR"
        return 0
    fi
    layout_failed "拿回主屏时布局切换失败"
    return 1
}

# 让出 = 主屏镜像到另一块屏（只有主屏时什么都不用做）
do_release() {
    local rc
    apply_layout mirror
    rc=$?
    # 主屏不在拓扑里（2）：没有看不见的桌面，笔记本屏已开着，也算让出完成
    if [ $rc -eq 0 ] || [ $rc -eq 2 ]; then
        OWNED=0
        TAKE_PENDING=0
        PENDING_SEEN=0
        LAYOUT_FAIL_AT=0
        LAYOUT_FAIL_NOTIFIED=0
        write_state
        log "release: 已让出，owned=0"
        return 0
    fi
    layout_failed "让出主屏时布局切换失败"
    return 1
}

# ---------------------------------------------------------------- 主动切换流程

# 流程中的一拍：刷新状态文件（否则对端 request 会因时间戳过期拿到 3），
# 并丢弃期间到达的命令——重复触发不排队。
flow_sleep() {
    write_state
    if [ "$MODE" = daemon ] && [ -f "$CMD" ]; then
        local c
        c=$(head -1 "$CMD" 2>/dev/null)
        rm -f "$CMD"
        log "busy: 忽略 [$c]"
        notify "正在切换中，已忽略新的请求"
    fi
    sleep 0.5
}

# 写 DDC 后最多约 SWITCH_WAIT 秒内等到连续 DEBOUNCE 次读取失败
wait_read_fail() {
    local fails=0 i n=$((SWITCH_WAIT * 2))
    for ((i = 0; i < n; i++)); do
        if ddc_read; then
            fails=0
        else
            fails=$((fails + 1))
            [ $fails -ge $DEBOUNCE ] && return 0
        fi
        flow_sleep
    done
    log "wait: 读取未转为失败（$LAST_READ）"
    return 1
}

# Other 切走后观察：读取重新成功说明该口无信号、显示器已跳回本机
observe_jump_back() {
    local oks=0 end=$(( $(date +%s) + OTHER_OBSERVE ))
    while [ "$(date +%s)" -lt "$end" ]; do
        flow_sleep
        if ddc_read; then
            oks=$((oks + 1))
            [ $oks -ge $DEBOUNCE ] && return 0
        else
            oks=0
        fi
    done
    return 1
}

# 切走没生效：写 DDC 之前没动过布局，一般不用重配；只有当前布局确实不是拿回布局时才 do_take
give_aborted() {
    local want mode
    want=$(want_layout)
    mode=$(layout_mode)
    if [ "$mode" = "$want" ] || [ "$mode" = none ]; then
        log "give: 布局仍为 $mode，不动"
        return 0
    fi
    log "give: 布局为 $mode，恢复 $want"
    do_take
}

# 先写 DDC、等主屏确实切走，再让出（仅大屏时这时才打开笔记本屏并镜像）：主屏直接从本机桌面变黑、
# 再变成对方画面，不出现镜像。代价是切走后笔记本屏若打不开，本机一时没有屏幕——do_release 失败时
# 由 layout_failed 通知（提示合上再打开盖子）并按 LAYOUT_RETRY_SECS 退避重试，直到笔记本屏打开
give_to_code() {
    local code=$1 name=$2
    # 切给对方时先通知它预先恢复布局：它切布局的时间正好与下面的显示器切换重叠
    [ "$3" = peer ] && notify_peer_take
    log "give: 写入 $name ($code)"
    if ! ddc_write "$code"; then
        notify "写入 DDC 失败，切换未生效"
        give_aborted
        return 1
    fi
    if ! wait_read_fail; then
        notify "切换未生效：主屏仍在本机"
        give_aborted
        return 1
    fi
    do_release
    return 0
}

# 切给对方（占有时）。还不知道对方接在哪个口时先向它询问并记下（设计文档 6.3）
give_to_peer() {
    if [ -z "$CODE_PEER" ] && peer_info && [ -n "$INFO_CODE" ] && [ "$INFO_CODE" != 0 ]; then
        save_peer_code "$INFO_CODE"
    fi
    if [ -z "$CODE_PEER" ]; then
        notify "不知道 $PEER_NAME 接在主屏的哪个输入口，请运行设置：screenctl.sh setup"
        return 1
    fi
    give_to_code "$CODE_PEER" "$PEER_NAME" peer
}

# 切给某个设备（占有时）：只写它的编码，确认切走后让出；之后主屏自己跳回来，说明那个口没有信号
give_to_device() {
    local code=$1 name
    name=$(device_name_by_code "$code") || name="设备 $code"
    give_to_code "$code" "$name" || return 1
    if observe_jump_back; then
        log "device: $name（$code）无信号，显示器已跳回"
        do_take
        notify "没有检测到 $name 的信号"
        return 0
    fi
    log "device: $name（$code）有信号，切换完成"
    return 0
}

# 等主屏出现在本机：最多 $1 秒，连续 DEBOUNCE 次读到本机编码返回 0
wait_owned() {
    local oks=0 i n=$(( $1 * 2 ))
    for ((i = 0; i < n; i++)); do
        if ddc_read; then
            oks=$((oks + 1))
            [ $oks -ge $DEBOUNCE ] && return 0
        else
            oks=0
        fi
        flow_sleep
    done
    return 1
}

# "直接切"有没有必要等结果：成功过就一直等；从没成功过、且已经连着 3 次没生效，就不再等——
# 只认活跃口的显示器永远等不到，白白让提示晚出现几秒。init / setup 会清掉这份记录（可能换了显示器）
direct_worth_waiting() {
    local n
    [ -f "$SCREENCTL_DIR/.direct_ok" ] && return 0
    n=$(cat "$SCREENCTL_DIR/.direct_fail" 2>/dev/null)
    is_int "$n" || n=0
    [ "$n" -lt 3 ]
}

direct_result() {   # $1 = ok | fail
    local n
    if [ "$1" = ok ]; then
        : > "$SCREENCTL_DIR/.direct_ok"
        rm -f "$SCREENCTL_DIR/.direct_fail"
    elif [ ! -f "$SCREENCTL_DIR/.direct_ok" ]; then
        n=$(cat "$SCREENCTL_DIR/.direct_fail" 2>/dev/null)
        is_int "$n" || n=0
        echo $((n + 1)) > "$SCREENCTL_DIR/.direct_fail"
    fi
}

# 拿回主屏（未占有时，设计文档 6.3）：先直接向主屏写本机编码——显示器在非活跃口也听指令时这就够了；
# 再照常请对方代发。对方没帮上忙时等几秒看直接写的有没有生效，都不行才提示按显示器按钮
take_back() {
    local rc=-1
    if [ "$DIRECT_TAKE" = 1 ]; then
        log "take: 直接向主屏写本机编码 $CODE_SELF"
        ddc_write "$CODE_SELF"
    fi
    if peer_configured; then
        relay_give peer
        rc=$?
        if [ $rc -eq 0 ]; then
            notify "已请求 $PEER_NAME 交还主屏"
            pre_take "$PEER_NAME 已接受 give peer"
            return 0
        fi
    else
        log "take: 没有配置对方电脑，不发 ssh"
    fi
    if [ "$DIRECT_TAKE" = 1 ] && direct_worth_waiting; then
        if wait_owned "${DIRECT_WAIT_SECS:-3}"; then
            log "take: 直接切换生效"
            direct_result ok
            do_take
            return 0
        fi
        direct_result fail
    fi
    case $rc in
        -1) notify "没有配置对方电脑（config.sh 的 PEER_HOST、PEER_USER、PEER_PROG），无法请它交还主屏。$HINT_BUTTON" ;;
        2) notify "$PEER_NAME 也不占有主屏。主屏在其他设备上，$HINT_BUTTON" ;;
        3) notify "$PEER_NAME 端守护程序未运行。$HINT_BUTTON" ;;
        255) notify "连接不到 $PEER_NAME。$HINT_BUTTON" ;;
        *) notify "$PEER_NAME 拒绝了请求（退出码 $rc）。$HINT_BUTTON" ;;
    esac
    return 1
}

# 直接写入之后读一次：主屏在非活跃口也应答时，读到的就是它现在所在的口
switched_to() {
    local v
    flow_sleep
    v=$(ddc_raw_input)
    [ -n "$v" ] && [ "$v" -eq "$1" ] 2>/dev/null
}

# 未占有时切给某个设备（设计文档 6.3）：先直接写它的编码，再请对方代发
ask_device() {
    local code=$1 name rc=-1
    name=$(device_name_by_code "$code") || name="设备 $code"
    if [ "$DIRECT_TAKE" = 1 ]; then
        log "device: 直接向主屏写 $name 的编码 $code"
        ddc_write "$code"
    fi
    if peer_configured; then
        relay_give code "$code"
        rc=$?
        if [ $rc -eq 0 ]; then
            notify "已请求 $PEER_NAME 切给 $name"
            return 0
        fi
    fi
    if [ "$DIRECT_TAKE" = 1 ] && switched_to "$code"; then
        log "device: 直接切换生效"
        return 0
    fi
    case $rc in
        -1) notify "没有配置对方电脑，无法请它切给 $name。$HINT_BUTTON" ;;
        1) notify "$PEER_NAME 拒绝了请求：它那边可能没有配置 $name（编码 $code）" ;;
        2) notify "主屏不在任何一台电脑上，无法代为切换。$HINT_BUTTON" ;;
        3) notify "$PEER_NAME 端守护程序未运行，无法代为切给 $name" ;;
        255) notify "连接不到 $PEER_NAME，无法代为切给 $name" ;;
        *) notify "$PEER_NAME 拒绝了请求（退出码 $rc）" ;;
    esac
    return 1
}

# 未占有时"切给对方"：主屏在别的设备上。显示器在非活跃口也听指令时可以直接切过去
give_peer_unowned() {
    if [ "$DIRECT_TAKE" = 1 ] && [ -n "$CODE_PEER" ]; then
        log "give: 未占有，直接向主屏写 $PEER_NAME 的编码 $CODE_PEER"
        ddc_write "$CODE_PEER"
        switched_to "$CODE_PEER" && { log "give: 直接切换生效"; return 0; }
    fi
    notify "主屏不在本机，无法切给 $PEER_NAME。$HINT_BUTTON"
    return 1
}

# 对方即将把主屏切给本机（本机拿回的请求被接受，或对方发来 request take）：立即恢复布局并进入宽限期，
# 显示器切过来时直接显示本机桌面，不再先出现镜像；宽限期内读不到主屏不让出，期满仍读不到才让出（设计 4.3）。
# 只在守护进程里做：直接模式下没人负责宽限期满后开回笔记本屏。
pre_take() {
    PRETAKE_RESULT=failed   # applied | pending | failed，request take 据此决定何时返回
    [ "$MODE" = daemon ] && [ "${TAKE_GRACE_SECS:-0}" -gt 0 ] 2>/dev/null || return 1
    if [ $OWNED -eq 1 ]; then
        log "pre-take: 已占有，不动（$1）"
        PRETAKE_RESULT=applied
        return 0
    fi
    do_take || return 1
    GRACE_UNTIL=$(( $(date +%s) + TAKE_GRACE_SECS ))
    if [ $TAKE_PENDING -eq 1 ]; then PRETAKE_RESULT=pending; else PRETAKE_RESULT=applied; fi
    log "pre-take: 已预先拿回（$PRETAKE_RESULT），宽限 $TAKE_GRACE_SECS 秒（$1）"
}

# 在后台 SSH 到对方电脑执行 "$PEER_PROG <参数…>"，期间本进程继续刷新状态文件；$1 = 最多等待秒数。
# 返回对方的退出码，超时或没有配置对方时 255；对方的输出留在 .relay.out 并记入日志
ssh_peer() {
    local total=$1 rc pid waited=0 limit timed_out=0 line
    shift
    if ! peer_configured; then
        log "relay: 没有配置对方电脑（PEER_HOST / PEER_USER / PEER_PROG），跳过：$*"
        return 255
    fi
    local -a args
    args=(-o BatchMode=yes -o ConnectTimeout="$SSH_CONNECT_TIMEOUT" -o StrictHostKeyChecking=accept-new
          -o AddressFamily="$SSH_ADDRESS_FAMILY")
    [ -n "$SSH_KEY" ] && [ -f "$SSH_KEY" ] && args+=(-i "$SSH_KEY")
    log "relay: ssh $PEER_USER@$PEER_HOST $*"
    "$SSH_BIN" "${args[@]}" "$PEER_USER@$PEER_HOST" "$PEER_PROG $*" \
        > "$SCREENCTL_DIR/.relay.out" 2>&1 < /dev/null &
    pid=$!
    limit=$((total * 2))
    while kill -0 "$pid" 2>/dev/null; do
        if [ $waited -ge $limit ]; then
            kill "$pid" 2>/dev/null
            timed_out=1
            break
        fi
        flow_sleep
        waited=$((waited + 1))
    done
    wait "$pid"
    rc=$?
    [ $timed_out -eq 1 ] && rc=255
    # 中文 Windows 的 cmd 报错是 GBK，不是合法 UTF-8 时先转码再记日志
    if ! iconv -f UTF-8 -t UTF-8 "$SCREENCTL_DIR/.relay.out" >/dev/null 2>&1; then
        iconv -f GBK -t UTF-8 "$SCREENCTL_DIR/.relay.out" > "$SCREENCTL_DIR/.relay.utf8" 2>/dev/null \
            && mv -f "$SCREENCTL_DIR/.relay.utf8" "$SCREENCTL_DIR/.relay.out"
    fi
    while IFS= read -r line; do
        log "relay: $line"
    done < "$SCREENCTL_DIR/.relay.out"
    log "relay: 退出码 $rc"
    return $rc
}

# 切给对方前先通知它（request take）：它立即恢复自己的布局，显示器切过去时直接是它的桌面。
# 尽力而为：对方不支持（退出码 1）、不在线（255）、没有配置都照常切换，最多多等 SSH_CONNECT_TIMEOUT+2 秒
notify_peer_take() {
    local rc
    [ "${NOTIFY_PEER_TAKE:-1}" = 1 ] && peer_configured || return 0
    ssh_peer $((SSH_CONNECT_TIMEOUT + 2)) request take
    rc=$?
    [ $rc -eq 0 ] || log "give: $PEER_NAME 未接受 request take（退出码 $rc），照常切换"
    return 0
}

# 请对方把主屏切走：参数为 peer（切给本机）或 code <n>（切给某个设备）。返回对方的退出码。
# 对方是旧版本、不认识 peer（退出码 1）时用旧写法 give mac 重试一次（设计文档 5.3）
relay_give() {
    local rc
    ssh_peer "$SSH_TOTAL_TIMEOUT" request give "$@"
    rc=$?
    if [ $rc -eq 1 ] && [ "$1" = peer ]; then
        log "relay: 对方不认识 give peer，改用旧写法 give mac 重试"
        ssh_peer "$SSH_TOTAL_TIMEOUT" request give mac
        rc=$?
    fi
    return $rc
}

# 向对方询问它的编码、名字和设备列表（request info，设计文档 5.4）。
# 成功返回 0 并设置 INFO_CODE（未知为空）、INFO_NAME、INFO_DEVICES（每项 '名字=编码'）
peer_info() {
    local line kv item c n old_ifs
    INFO_CODE=""; INFO_NAME=""; INFO_DEVICES=()
    ssh_peer $((SSH_CONNECT_TIMEOUT + 2)) request info || return 1
    line=$(grep -m1 '^proto=' "$SCREENCTL_DIR/.relay.out" 2>/dev/null | tr -d '\r')
    [ -n "$line" ] || return 1
    for kv in $line; do
        case "$kv" in
            code=*) INFO_CODE=${kv#code=} ;;
            name=*) INFO_NAME=$(unb64 "${kv#name=}") ;;
            devices=*)
                old_ifs=$IFS; IFS=,
                for item in ${kv#devices=}; do
                    IFS=$old_ifs
                    c=${item%%:*}; n=$(unb64 "${item#*:}")
                    is_int "$c" && [ -n "$n" ] && INFO_DEVICES+=("$n=$c")
                done
                IFS=$old_ifs ;;
        esac
    done
    is_int "$INFO_CODE" && [ "$INFO_CODE" -gt 0 ] || INFO_CODE=""
    return 0
}

# 把问到的对方编码写回配置
save_peer_code() {
    load_config
    CODE_PEER=$1
    write_config
    log "peer: 记下 $PEER_NAME 的编码 $1"
}

# 菜单语义（设计 4.4 与 v3 5.2）；$1 = peer | self | code，$2 = 设备编码。调用方负责 busy 标记
run_give() {
    log "give $* 开始（owned=$OWNED）"
    case "$OWNED:$1" in
        1:peer) give_to_peer ;;
        1:code) give_to_device "$2" ;;
        1:self) notify "主屏已在本机" ;;
        0:self) take_back ;;
        0:code) ask_device "$2" ;;
        0:peer) give_peer_unowned ;;
    esac
    log "give $* 结束（owned=$OWNED）"
}

busy_begin() {
    printf '%s\n' "$$" > "$BUSY"
}

busy_end() {
    rm -f "$BUSY"
    # 流程结束后重新开始去抖计数，下一拍立即轮询
    OKS=0
    FAILS=0
    NEXT_POLL=0
}

busy_by_other() {
    local pid
    pid=$(cat "$BUSY" 2>/dev/null)
    is_int "$pid" && [ "$pid" != "$$" ] && kill -0 "$pid" 2>/dev/null
}

# ---------------------------------------------------------------- 守护进程

# 主屏在线且不在镜像组里（扩展或仅大屏）。用 display-helper（约 40 毫秒），不用 displayplacer list（约 1.2 秒），
# 未占有时每拍都能查；按 UUID（= MAIN_ID）认主屏，另接的显示器不算。旧版 display-helper 不输出这两项，一律返回 1（不检查）
main_unmirrored() {
    [ -x "$DISPLAY_HELPER" ] && [ -n "$MAIN_ID" ] || return 1
    "$DISPLAY_HELPER" list 2>/dev/null | awk -v u="uuid=$MAIN_ID" 'toupper($0) ~ toupper(u) && / mirror=0/ {f=1} END {exit !f}'
}

# 未占有时布局应为镜像。让出时主屏若已不在拓扑里（拔掉 Mac 的线、在别的口上久了），只开了笔记本屏；
# 主屏回到拓扑时 macOS 按记忆恢复旧排列，可能是扩展，本机桌面有一块落在看不见的主屏上。
# 持续 UNMIRRORED_FIX_SECS 秒仍读不到主屏、且主屏已枚举完整才改回镜像：显示器正切回本机时几秒内就能读到，
# 那时直接拿回，不先闪一下镜像
check_unowned_layout() {
    # 现取时间，不用 poll 开头的：那之后的 DDC 读取可能卡很久（实测睡眠期间卡了一整夜），计时会从过期的时刻算起
    local now
    now=$(date +%s)
    if [ "${UNMIRRORED_FIX_SECS:-0}" -le 0 ] 2>/dev/null || ! main_unmirrored || no_layout; then
        UNMIRRORED_SINCE=0
        return 0
    fi
    if [ $UNMIRRORED_SINCE -eq 0 ]; then
        UNMIRRORED_SINCE=$now
        log "poll: 未占有但主屏在线且不是镜像，${UNMIRRORED_FIX_SECS} 秒后仍读不到主屏就改回镜像"
        return 0
    fi
    [ $(( now - UNMIRRORED_SINCE )) -ge "$UNMIRRORED_FIX_SECS" ] || return 0
    if ! main_ready; then
        log "poll: 主屏还没就绪（$MAIN_READY_ERR），稍后再改回镜像"
        UNMIRRORED_SINCE=$now
        return 0
    fi
    UNMIRRORED_SINCE=0
    log "poll: 未占有但布局为 $(layout_mode)，改回镜像"
    do_release
}

poll() {
    local now mode
    now=$(date +%s)
    # 待拿回：预先拿回时主屏还没就绪（不在拓扑里，或正在重新枚举）。每拍探测一次，主屏连续 2 拍就绪
    # （给系统约 1 秒把它加完整）就应用拿回布局；布局失败按 LAYOUT_RETRY_SECS 退避。期间照常读 DDC，宽限期与让出逻辑不变
    if [ $TAKE_PENDING -eq 1 ] && [ $OWNED -eq 1 ]; then
        if main_ready; then
            PENDING_SEEN=$((PENDING_SEEN + 1))
            if [ $PENDING_SEEN -ge 2 ] \
                && { [ $LAYOUT_FAIL_AT -eq 0 ] || [ $(( now - LAYOUT_FAIL_AT )) -ge $LAYOUT_RETRY_SECS ]; }; then
                log "poll: 主屏已就绪，应用待拿回的布局"
                do_take
                NEXT_POLL=1
                return
            fi
        else
            PENDING_SEEN=0
        fi
    fi
    # 刚拿回主屏的几秒内不读 DDC：显示器重新配置、链路重训期间读数常是垃圾（实测 99、90、最大亮度 55），
    # 读了只会把主屏误判成不在本机而让出，几秒后又拿回，屏幕来回闪
    if [ $LAYOUT_AT -gt 0 ] && [ $(( now - LAYOUT_AT )) -lt "${SETTLE_SECS:-0}" ]; then
        NEXT_POLL=1
        return
    fi
    if ddc_read; then
        [ $OKS -eq 0 ] && log "read: 成功（$LAST_READ）"
        OKS=$((OKS + 1))
        FAILS=0
        if [ $GRACE_UNTIL -gt 0 ]; then
            GRACE_UNTIL=0
            log "poll: 主屏已切回，预先拿回生效"
        fi
    else
        [ $FAILS -eq 0 ] && log "read: 失败（$LAST_READ）"
        FAILS=$((FAILS + 1))
        OKS=0
    fi
    if [ $LAYOUT_FAIL_AT -gt 0 ] && [ $(( now - LAYOUT_FAIL_AT )) -lt $LAYOUT_RETRY_SECS ]; then
        NEXT_POLL=$POLL_TICKS
        return
    fi
    [ $OWNED -eq 1 ] && UNMIRRORED_SINCE=0
    if [ $OWNED -eq 0 ] && [ $OKS -ge $DEBOUNCE ]; then
        UNMIRRORED_SINCE=0
        log "poll: take（$LAST_READ）"
        do_take
    elif [ $OWNED -eq 0 ]; then
        # 读到过主屏（正在切回本机）就不改镜像，等拿回
        if [ $OKS -gt 0 ]; then UNMIRRORED_SINCE=0; else check_unowned_layout; fi
    elif [ $OWNED -eq 1 ] && [ $FAILS -ge "${RELEASE_DEBOUNCE:-$DEBOUNCE}" ]; then
        if [ $GRACE_UNTIL -gt 0 ] && [ $now -lt $GRACE_UNTIL ]; then
            :   # 预先拿回的宽限期：显示器还没切过来，不让出
        else
            if [ $GRACE_UNTIL -gt 0 ]; then
                GRACE_UNTIL=0
                log "poll: 宽限期满仍读不到主屏，让出"
                notify "$PEER_NAME 没有交还主屏。$HINT_BUTTON"
            fi
            log "poll: release（$LAST_READ）"
            do_release
        fi
    fi
    # 下次读取：占有时按 POLL_TICKS，未占有时按 POLL_TICKS_UNOWNED（默认每拍，主屏一切回来就尽快拿回）；
    # 读数与当前归属不符、或还在等主屏回到拓扑时，下一拍立即复核
    if [ $TAKE_PENDING -eq 1 ]; then
        NEXT_POLL=1
    elif [ $OWNED -eq 0 ]; then
        NEXT_POLL=${POLL_TICKS_UNOWNED:-$POLL_TICKS}
        [ $OKS -gt 0 ] && NEXT_POLL=1
    else
        NEXT_POLL=$POLL_TICKS
        [ $FAILS -gt 0 ] && NEXT_POLL=1
    fi
}

# 启动对账：未占有但扩展 → 让出；占有但镜像 → 拿回；一致则不动
reconcile() {
    local owned=0 mode want
    ddc_owned_now && owned=1
    mode=$(layout_mode)
    if [ "$mode" = none ]; then
        # 只有主屏（或合着盖子）：没有布局可对，归属只看 DDC
        OWNED=$owned
        log "startup: DDC owned=$owned，不调整布局（$LAST_READ）"
        write_state
        return
    fi
    want=$(want_layout)
    case "$mode" in extend|external) OWNED=1 ;; *) OWNED=0 ;; esac
    log "startup: DDC owned=$owned，布局 $mode，偏好 $want（$LAST_READ）"
    if [ $owned -eq 0 ] && [ $OWNED -eq 1 ]; then
        log "startup reconcile: release"
        do_release
    elif [ $owned -eq 1 ] && { [ "$mode" = mirror ] || { [ "$mode" != "$want" ] && [ "$mode" != absent ]; }; }; then
        log "startup reconcile: take（$want）"
        do_take
    elif [ $owned -eq 0 ]; then
        laptop_set enable || notify "$LAYOUT_ERR"
    fi
    write_state
}

check_cmd() {
    [ -f "$CMD" ] || return 0
    local c
    c=$(head -1 "$CMD" 2>/dev/null)
    rm -f "$CMD"
    log "cmd: [$c]"
    case "$c" in
        "give "*)
            # 内容为 give peer / give self / give code <n>；旧版的 give windows|mac|other 由 canon_target 换算
            if canon_target ${c#give }; then
                busy_begin
                run_give $CANON
                busy_end
            else
                log "cmd: 不认识的目标，忽略"
            fi
            ;;
        take)
            busy_begin; do_take; busy_end ;;
        pre-take)
            busy_begin
            pre_take "对端 request take"
            # 结果交给正在等待的 request take（带时间戳，避免读到上一次的）
            printf '%s %s\n' "${PRETAKE_RESULT:-failed}" "$(date +%s)" > "$SCREENCTL_DIR/pretake.result.tmp" \
                && mv -f "$SCREENCTL_DIR/pretake.result.tmp" "$SCREENCTL_DIR/pretake.result"
            busy_end ;;
        save-layout)
            do_save_layout manual ;;
        "set "*)
            do_set $(printf '%s' "${c#set }") ;;
        reload)
            load_config; log "reload: 已重新读取配置" ;;
        release)
            busy_begin; do_release; busy_end ;;
        *)
            log "cmd: 无法识别，忽略" ;;
    esac
}

watch_cleanup() {
    # 守护进程不在了就没人负责开回笔记本屏，退出前先开。install.sh 重启守护进程前会留下 .restarting 标记
    # （30 秒内有效），这时跳过：新进程马上接手，屏幕不用先开再关闪两下
    if [ "$(layout_mode)" = external ]; then
        if [ -f "$SCREENCTL_DIR/.restarting" ] \
            && [ $(( $(date +%s) - $(stat -f %m "$SCREENCTL_DIR/.restarting" 2>/dev/null || echo 0) )) -le 30 ]; then
            log "watch 退出：正在重启，笔记本屏保持关闭"
        else
            laptop_set enable
        fi
    fi
    [ "$(cat "$PIDFILE" 2>/dev/null)" = "$$" ] && rm -f "$PIDFILE"
    [ "$(cat "$BUSY" 2>/dev/null)" = "$$" ] && rm -f "$BUSY"
    log "watch 退出"
}

# 单实例：在短锁内原子地"检查并写入" pid 文件。不长期持锁，
# 以免锁 fd 被子进程继承后在守护进程退出后仍不释放。
claim_pidfile() {
    lockf -k -t 3 "$WATCH_LOCK" /bin/bash -c '
        p=$(cat "$1" 2>/dev/null)
        if [ -n "$p" ] && [ "$p" != "$2" ] && kill -0 "$p" 2>/dev/null; then
            # pid 被别的程序复用（例如重启后）才算过期；ps 不可用时保守地当作仍在运行
            c=$(ps -p "$p" -o command= 2>/dev/null)
            case "$c" in ""|*"screenctl.sh watch"*) exit 1 ;; esac
        fi
        printf "%s\n" "$2" > "$1"' _ "$PIDFILE" "$$"
}

cmd_watch() {
    MODE=daemon
    need_config
    mkdir -p "$SCREENCTL_DIR"
    claim_pidfile || { echo "watch 已在运行（pid $(cat "$PIDFILE")）" >&2; exit 0; }
    rm -f "$SCREENCTL_DIR/.restarting"
    trap watch_cleanup EXIT
    trap 'exit 0' TERM INT HUP
    log "watch 启动"
    # 上次直接模式的流程还没结束就等它结束
    while busy_by_other; do sleep 0.5; done
    rm -f "$BUSY" "$CMD"
    reconcile
    NEXT_POLL=$POLL_TICKS
    while :; do
        check_cmd
        if [ $NEXT_POLL -le 1 ]; then
            poll
            SAVE_COUNTDOWN=$((SAVE_COUNTDOWN - 1))
            if [ $SAVE_COUNTDOWN -le 0 ]; then
                SAVE_COUNTDOWN=$AUTO_SAVE_POLLS
                auto_save_check
            fi
        else
            NEXT_POLL=$((NEXT_POLL - 1))
        fi
        write_state
        sleep 0.5
    done
}

# ---------------------------------------------------------------- 其他子命令

# 把目标换算成规范写法，放进 CANON：peer | self | code <n>。认不出返回 1。
# 旧写法（设计文档 5.1、5.3）：windows = 对方，mac = 本机，other = 设备列表里的第一个
canon_target() {
    local c
    CANON=""
    case "$1" in
        peer|windows) [ $# -eq 1 ] && CANON=peer ;;
        self|mac) [ $# -eq 1 ] && CANON=self ;;
        other)
            [ $# -eq 1 ] && [ ${#DEVICES[@]} -gt 0 ] && CANON="code ${DEVICES[0]##*=}" ;;
        code)
            [ $# -eq 2 ] && is_int "$2" && c=$(device_code_of "$2") && CANON="code $c" ;;
        *)
            # 本机命令行还可以直接写设备的名字或编码
            [ $# -eq 1 ] && c=$(device_code_of "$1") && CANON="code $c" ;;
    esac
    [ -n "$CANON" ]
}

# 供对方 SSH 调用（设计文档 5.2）：request give peer | give code <n> 请本机切走主屏；
# request take 通知本机"我马上把主屏切给你"；request info 回答本机的编码、名字和设备列表
cmd_request() {
    local kv ts e devs=""
    if [ "$1" = info ] && [ $# -eq 1 ]; then
        for e in "${DEVICES[@]}"; do devs="$devs${devs:+,}${e##*=}:$(b64 "${e%=*}")"; done
        echo "proto=3 os=mac code=${CODE_SELF:-0} name=$(b64 "$(self_name)") devices=$devs"
        exit 0
    fi
    case "$1" in
        give)
            shift
            if ! canon_target "$@"; then
                log "request: 不认识的目标 [$*] → 1"
                if [ "$1" = code ]; then echo "unknown device"; else echo "invalid target"; fi
                exit 1
            fi
            set -- give $CANON ;;
        take) [ $# -eq 1 ] || { echo "usage: request take"; exit 1; } ;;
        *) echo "usage: request give peer | request give code <n> | request take | request info"; exit 1 ;;
    esac
    if ! read -r kv ts 2>/dev/null < "$STATE" || ! is_int "$ts" \
        || [ $(( $(date +%s) - ts )) -gt "$STALE_SECS" ]; then
        log "request: $* → 3（守护进程未运行）"
        echo "daemon not running"
        exit 3
    fi
    if [ "$1" = take ]; then
        # 对端即将把主屏切给本机：守护进程立即恢复布局，显示器切过来时直接显示本机桌面
        if [ "$kv" = "owned=1" ]; then
            log "request: take → 0（已占有，不动）"
            echo "already owned"
            exit 0
        fi
        # 让守护进程预先拿回，并等它把布局应用完再返回（最多 PRETAKE_WAIT_SECS 秒）：对端拿到返回值才写 DDC，
        # 显示器切过来时本机已经就位，不会撞上链路重训。主屏还没就绪（pending）或失败时立即返回，不让对端白等
        local t0 rs ts2 i
        t0=$(date +%s)
        rm -f "$SCREENCTL_DIR/pretake.result"
        write_cmd "pre-take"
        log "request: 已接受 take"
        for i in $(seq 1 $(( ${PRETAKE_WAIT_SECS:-3} * 4 ))); do
            rs=""; ts2=""
            read -r rs ts2 2>/dev/null < "$SCREENCTL_DIR/pretake.result"
            if [ -n "$rs" ] && is_int "$ts2" && [ "$ts2" -ge "$t0" ]; then
                log "request: take → 0（$rs）"
                echo "$rs"
                exit 0
            fi
            sleep 0.25
        done
        log "request: take → 0（等了 ${PRETAKE_WAIT_SECS:-3} 秒布局还没应用完，守护进程继续）"
        echo "accepted"
        exit 0
    fi
    shift
    if [ "$kv" != "owned=1" ]; then
        log "request: give $* → 2（未占有）"
        echo "not owned"
        exit 2
    fi
    write_cmd "give $*"
    log "request: 已接受 give $*"
    echo "accepted"
    exit 0
}

# authorized_keys 里的强制命令（设计文档 5.3）：只放行 request give peer、give code <n>、take、info，
# 以及旧写法 give windows、give other；其余一律退出码 1。"切给本机自己"不放行
cmd_ssh_gate() {
    local -a w
    read -r -a w <<< "${SSH_ORIGINAL_COMMAND:-}"
    if [ ${#w[@]} -ge 3 ] && [ "$(basename -- "${w[0]}")" = screenctl.sh ] && [ "${w[1]}" = request ]; then
        if [ ${#w[@]} -eq 3 ] && [ "${w[2]}" = take ]; then
            cmd_request take
        elif [ ${#w[@]} -eq 3 ] && [ "${w[2]}" = info ]; then
            cmd_request info
        elif [ ${#w[@]} -eq 4 ] && [ "${w[2]}" = give ]; then
            case "${w[3]}" in
                peer|windows|other) cmd_request give "${w[3]}" ;;
            esac
        elif [ ${#w[@]} -eq 5 ] && [ "${w[2]}" = give ] && [ "${w[3]}" = code ]; then
            case "${w[4]}" in
                [0-9]|[0-9][0-9]|[0-9][0-9][0-9]) cmd_request give code "${w[4]}" ;;
            esac
        fi
    fi
    log "ssh-gate: 拒绝 [${SSH_ORIGINAL_COMMAND:-}]"
    echo "rejected"
    exit 1
}

cmd_give() {
    canon_target "$@" || die "用法：$0 give peer | self | <设备名或编码>"
    need_config
    if daemon_alive; then
        write_cmd "give $CANON"
        echo "已交给守护进程：give $CANON"
        return 0
    fi
    # 守护进程未运行：本进程直接执行，不写状态文件（对端 request 仍应得到 3）
    MODE=direct
    if busy_by_other; then
        notify "正在切换中，已忽略新的请求"
        return 0
    fi
    busy_begin
    trap 'rm -f "$BUSY"' EXIT
    ddc_owned_now && OWNED=1 || OWNED=0
    run_give $CANON
}

cmd_take_release() {
    need_config
    if daemon_alive; then
        write_cmd "$1"
        echo "已交给守护进程：$1（之后守护进程仍按 DDC 读数跟随）"
        return 0
    fi
    MODE=direct
    if [ "$1" = take ]; then do_take; else do_release; fi || die "$LAYOUT_ERR"
    echo "完成：$(layout_mode)"
}

cmd_status() {
    need_config
    local kv ts pid
    if daemon_alive; then
        read -r kv ts < "$STATE"
        pid=$(cat "$PIDFILE")
        echo "$kv $ts"
        echo "daemon=running pid=$pid age=$(( $(date +%s) - ts ))s"
    else
        ddc_owned_now && OWNED=1 || OWNED=0
        echo "owned=$OWNED $(date +%s)"
        echo "daemon=stopped（以上为本次直接读取 DDC 的结果，$LAST_READ）"
    fi
    echo "layout=$(layout_mode)"
    busy_by_other && echo "busy=$(cat "$BUSY")"
    return 0
}

# ---------------------------------------------------------------- init

sq() {
    printf "'%s'" "$1"
}

# 按当前变量重写 config.sh（init、setup、保存排列、菜单开关共用）。只写新名字（设计文档 4.2）
write_config() {
    local v
    {
        echo "# screenctl 配置：由 screenctl.sh 生成或更新于 $(date '+%Y-%m-%d %H:%M:%S')，可手改。"
        echo "# 改完执行 launchctl kickstart -k gui/\$(id -u)/com.user.screenctl 让守护进程重新加载。"
        echo
        echo "# 主屏（共享显示器）：m1ddc 用 uuid，displayplacer 用 persistent id"
        echo "MAIN_NAME=$(sq "$MAIN_NAME")"
        echo "MAIN_UUID=$(sq "$MAIN_UUID")"
        echo "MAIN_ID=$(sq "$MAIN_ID")"
        echo
        echo "# 本机：名字（留空取电脑名）和所接输入口的编码（VCP 0x60，十进制）"
        echo "SELF_NAME=$(sq "$SELF_NAME")"
        echo "CODE_SELF=$CODE_SELF"
        echo "# 读到本机编码后再核对主屏最大亮度，防止睡醒后把缓存旧数据当成占有；0 = 不核对"
        echo "SANITY_MAXLUM=$SANITY_MAXLUM"
        echo
        echo "# 对方电脑。PEER_HOST、PEER_USER、PEER_PROG 都填了才走 SSH，否则拿回只能靠直接切换或显示器按钮"
        echo "PEER_NAME=$(sq "$PEER_NAME")"
        echo "PEER_OS=$PEER_OS   # windows | mac"
        echo "PEER_HOST=$(sq "$PEER_HOST")"
        echo "PEER_USER=$(sq "$PEER_USER")"
        echo "PEER_PROG=$(sq "$PEER_PROG")"
        echo "CODE_PEER=$(sq "$CODE_PEER")   # 对方所接输入口的编码；留空 = 切给对方时先向它询问"
        echo "SSH_KEY=$(sq "$SSH_KEY")"
        echo "SSH_CONNECT_TIMEOUT=$SSH_CONNECT_TIMEOUT"
        echo "SSH_TOTAL_TIMEOUT=$SSH_TOTAL_TIMEOUT"
        echo "SSH_ADDRESS_FAMILY=$SSH_ADDRESS_FAMILY   # inet=只走 IPv4；any=系统默认"
        echo
        echo "# 其他设备（游戏机等），每项 '名字=编码'。增删用 screenctl.sh setup devices"
        echo "DEVICES=("
        for v in "${DEVICES[@]}"; do echo "    $(sq "$v")"; done
        echo ")"
        echo "DIRECT_TAKE=$DIRECT_TAKE   # 1 = 拿回时先直接向主屏发切换指令，不行再请对方代发"
        echo "DIRECT_WAIT_SECS=$DIRECT_WAIT_SECS"
        echo
        echo "# 屏幕组合：second = 本机另有一块屏（SECOND_ID；SECOND_BUILTIN=1 是笔记本自带屏，0 是外接屏）；none = 只有主屏，不调整布局"
        echo "LAYOUT=$LAYOUT"
        echo "SECOND_ID=$(sq "$SECOND_ID")"
        echo "SECOND_BUILTIN=$SECOND_BUILTIN"
        echo "# 拿回：扩展排列（displayplacer list 最后一行的参数）。占有且扩展时改了排列会自动更新（AUTO_SAVE_LAYOUT）"
        echo "EXTEND_ARGS=("
        for v in "${EXTEND_ARGS[@]}"; do echo "    $(sq "$v")"; done
        echo ")"
        echo "# 拿回后的布局：extend = 扩展；external = 仅大屏（关闭笔记本屏，只在另一块屏是笔记本自带屏时可用）"
        echo "OWNED_LAYOUT=$(sq "$OWNED_LAYOUT")"
        echo "LAPTOP_CGID=$LAPTOP_CGID   # 笔记本屏的系统显示编号，自动学习"
        echo "# 让出：镜像，另一块屏为\"优化为\"对象并显式指定其分辨率"
        echo "MIRROR_ARGS=("
        for v in "${MIRROR_ARGS[@]}"; do echo "    $(sq "$v")"; done
        echo ")"
        echo
        echo "# 时序：0.5 秒一拍，占有时每 POLL_TICKS 拍、未占有时每 POLL_TICKS_UNOWNED 拍读一次 DDC，连续 DEBOUNCE 次才改变归属"
        echo "POLL_TICKS=$POLL_TICKS"
        echo "POLL_TICKS_UNOWNED=$POLL_TICKS_UNOWNED   # 1 = 未占有时每 0.5 秒读一次，主屏切回来时尽快拿回"
        echo "TAKE_GRACE_SECS=$TAKE_GRACE_SECS   # 主动拿回：对方接受后立即恢复布局，这段秒数内读不到主屏不让出；0 = 关闭"
        echo "NOTIFY_PEER_TAKE=$NOTIFY_PEER_TAKE   # 1 = 切给对方前先 SSH 通知它 request take"
        echo "PRETAKE_WAIT_SECS=$PRETAKE_WAIT_SECS   # request take 最多等守护进程应用完布局几秒再返回"
        echo "DEBOUNCE=$DEBOUNCE"
        echo "RELEASE_DEBOUNCE=$RELEASE_DEBOUNCE   # 占有时连续几次读失败才让出（0.5 秒一次），防垃圾读数误让出"
        echo "SETTLE_SECS=$SETTLE_SECS   # 拿回主屏后几秒内不读 DDC"
        echo "UNMIRRORED_FIX_SECS=$UNMIRRORED_FIX_SECS   # 未占有时主屏在线却不是镜像，持续几秒就改回镜像；0 = 关闭"
        echo "SWITCH_WAIT=$SWITCH_WAIT"
        echo "OTHER_OBSERVE=$OTHER_OBSERVE"
        echo "STALE_SECS=$STALE_SECS"
        echo "LAYOUT_RETRY_SECS=$LAYOUT_RETRY_SECS"
        echo "LOG_READS=$LOG_READS   # 1 = 记录每一次 DDC 读取"
        echo "AUTO_SAVE_LAYOUT=$AUTO_SAVE_LAYOUT   # 1 = 占有且扩展时自动记住改过的屏幕排列"
        echo "AUTO_SAVE_POLLS=$AUTO_SAVE_POLLS"
        echo
        echo "# 菜单栏程序的全局快捷键，默认都不设。格式：修饰键 ctrl/opt/shift/cmd 加一个数字或字母，用 + 连接，如 'ctrl+opt+1'。"
        echo "# HOTKEY_PEER：占有时切给对方，未占有时拿回主屏；HOTKEY_DEVICES 与 DEVICES 一一对应。"
        echo "# 改完执行 launchctl kickstart -k gui/\$(id -u)/com.user.screenswitch-menu 让菜单栏程序重新读取。"
        echo "HOTKEY_PEER=$(sq "$HOTKEY_PEER")"
        echo "HOTKEY_DEVICES=("
        for v in "${HOTKEY_DEVICES[@]}"; do echo "    $(sq "$v")"; done
        echo ")"
    } > "$CONFIG.tmp" && mv -f "$CONFIG.tmp" "$CONFIG"
}

# 读当前扩展排列：成功时设置 CUR_EXT / CUR_MIR（镜像时另一块屏为"优化为"对象，显式写入其分辨率）；
# 镜像、缺屏时返回 1，原因在 CUR_ERR
read_current_layout() {
    local last a v res hz depth scaling
    CUR_ERR=""
    last=$("$DISPLAYPLACER" list 2>/dev/null | tail -1)
    case "$last" in
        *"+$MAIN_ID"*|*"$MAIN_ID+"*) CUR_ERR="当前是镜像"; return 1 ;;
        *"$MAIN_ID"*) ;;
        *) CUR_ERR="当前拓扑里没有主屏"; return 1 ;;
    esac
    CUR_EXT=()
    while IFS= read -r a; do
        CUR_EXT+=("$a")
    done < <(printf '%s\n' "$last" | grep -o '"[^"]*"' | sed 's/^"//; s/"$//')
    a=""
    for v in "${CUR_EXT[@]}"; do
        case "$v" in "id:$SECOND_ID "*) a=$v ;; esac
    done
    [ -n "$a" ] || { CUR_ERR="当前排列里没有另一块屏"; return 1; }
    res=$(printf '%s\n' "$a" | grep -o 'res:[0-9]*x[0-9]*')
    hz=$(printf '%s\n' "$a" | grep -o 'hz:[0-9]*')
    depth=$(printf '%s\n' "$a" | grep -o 'color_depth:[0-9]*')
    scaling=$(printf '%s\n' "$a" | grep -o 'scaling:o[nf]*')
    CUR_MIR=("id:$SECOND_ID+$MAIN_ID $res $hz $depth $scaling origin:(0,0) degree:0")
}

# 排列签名：参数排序后拼接，用于比对是否变化
layout_sig() {
    printf '%s\n' "$@" | sort | tr '\n' '|'
}

# 保存当前排列为"拿回"排列。$1 = auto | manual。先读回 config.sh，保留用户手改的其他项
do_save_layout() {
    load_config
    if ! read_current_layout; then
        log "layout: 未保存排列（$1）：$CUR_ERR"
        [ "$1" = manual ] && notify "无法保存排列：$CUR_ERR"
        return 1
    fi
    EXTEND_ARGS=("${CUR_EXT[@]}")
    MIRROR_ARGS=("${CUR_MIR[@]}")
    write_config
    PENDING_SIG=""
    log "layout: 已保存排列（$1）$(layout_sig "${EXTEND_ARGS[@]}")"
    if [ "$1" = auto ]; then notify "已自动记住新的屏幕排列"; else notify "已保存当前屏幕排列"; fi
}

# 菜单开关：只允许改白名单里的项
do_set() {
    case "$1=$2" in
        AUTO_SAVE_LAYOUT=0|AUTO_SAVE_LAYOUT=1|OWNED_LAYOUT=extend|OWNED_LAYOUT=external) ;;
        *) log "set: 拒绝 [$1=$2]"; return 1 ;;
    esac
    if [ "$1=$2" = OWNED_LAYOUT=external ] && [ "$SECOND_BUILTIN" != 1 ]; then
        log "set: 另一块屏是外接屏，不支持仅大屏"
        notify "另一块屏是外接屏，不支持"仅大屏""
        return 1
    fi
    load_config
    case "$1" in
        AUTO_SAVE_LAYOUT) AUTO_SAVE_LAYOUT=$2 ;;
        OWNED_LAYOUT) OWNED_LAYOUT=$2 ;;
    esac
    write_config
    PENDING_SIG=""
    log "set: $1=$2"
    # 守护进程里改了拿回后的布局、且主屏正在本机：立即按新偏好生效
    if [ "$1" = OWNED_LAYOUT ] && [ "$MODE" = daemon ] && [ $OWNED -eq 1 ]; then
        busy_begin; do_take; busy_end
    fi
}

# 守护进程定期调用：占有且扩展、排列与已保存的不同且连续两次一致时自动保存
auto_save_check() {
    [ "$AUTO_SAVE_LAYOUT" = 1 ] && [ $OWNED -eq 1 ] && [ $LAYOUT_FAIL_AT -eq 0 ] || { PENDING_SIG=""; return 0; }
    no_layout && { PENDING_SIG=""; return 0; }
    read_current_layout || { PENDING_SIG=""; return 0; }
    local cur saved
    cur=$(layout_sig "${CUR_EXT[@]}")
    saved=$(layout_sig "${EXTEND_ARGS[@]}")
    if [ "$cur" = "$saved" ]; then
        PENDING_SIG=""
    elif [ "$cur" = "$PENDING_SIG" ]; then
        do_save_layout auto
    else
        PENDING_SIG=$cur
        log "layout: 检测到排列变化，下次比对一致后保存"
    fi
}

# 探测主屏、本机编码和屏幕组合，写入 config.sh。不提问；对方电脑、设备等已有的设置原样保留
cmd_init() {
    local name=${1:-$MAIN_NAME} line uuid list builtin main others n v i names
    local -a vals
    mkdir -p "$SCREENCTL_DIR"
    # m1ddc 只支持 Apple 芯片（用 sysctl 判断，Rosetta 下的终端也不会误判）
    [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ] || die "只支持 Apple 芯片的 Mac（m1ddc 不支持 Intel Mac）"
    command -v "$M1DDC" >/dev/null && command -v "$DISPLAYPLACER" >/dev/null \
        || die "请先运行：brew install m1ddc displayplacer"
    # 合着盖子时自带屏不在显示器列表里，会被记成"没有另一块屏"。开着盖子设置一次，之后合盖使用由守护进程自动处理
    lid_closed && die "笔记本合着盖子。请打开盖子再运行设置；设置好以后合盖使用不受影响"

    # 没指定名字时：m1ddc 列出的外接显示器（笔记本内建屏显示为 (null)）只有一块就用它
    if [ -z "$name" ]; then
        names=$("$M1DDC" display list 2>/dev/null | sed -n 's/^\[[0-9]*\] \(.*\) ([0-9A-Fa-f-]\{36\})$/\1/p' | grep -vx '(null)')
        case "$(printf '%s\n' "$names" | grep -c .)" in
            1) name=$names; echo "主屏（共享显示器）：$name" ;;
            0) die "m1ddc 没有找到外接显示器。请确认共享显示器接在 Mac 上、正在显示 Mac、显示器菜单里已打开 DDC/CI，且线路（扩展坞、转接头）支持 DDC" ;;
            *) die "找到多块外接显示器，请指定哪块是共享的：$0 setup <名字>（可选：$(printf '%s\n' "$names" | paste -sd '/' -)）" ;;
        esac
    fi

    line=$("$M1DDC" display list 2>/dev/null | grep -F " $name (" | head -1)
    uuid=$(printf '%s\n' "$line" | sed -n 's/.*(\([0-9A-Fa-f-]\{36\}\)).*/\1/p')
    [ -n "$uuid" ] || die "m1ddc display list 中找不到 $name。请用 m1ddc display list 查看显示器的名字，并确认共享显示器正在显示 Mac"

    list=$("$DISPLAYPLACER" list 2>/dev/null)
    builtin=$(printf '%s\n' "$list" | awk '/^Persistent screen id:/{id=$4} /^Type: MacBook built in screen/{print id; exit}')
    if printf '%s\n' "$list" | grep -q "^Persistent screen id: $uuid\$"; then
        main=$uuid
    else
        main=$(printf '%s\n' "$list" | awk -v l="$builtin" '/^Persistent screen id:/{if ($4 != l) print $4}')
        [ "$(printf '%s\n' "$main" | grep -c .)" -eq 1 ] \
            || die "无法确定主屏的 displayplacer id，请手工填写 config.sh 的 MAIN_ID"
    fi

    # 屏幕组合（设计文档 7）：除主屏外，另一块屏是笔记本自带屏、一块外接屏，或者没有
    others=$(printf '%s\n' "$list" | awk -v m="$main" -v b="$builtin" '/^Persistent screen id:/{if ($4 != m && $4 != b) print $4}')
    n=$(printf '%s\n' "$others" | grep -c .)
    if [ -n "$builtin" ]; then
        [ "$n" -eq 0 ] || die "这台 Mac 除了主屏和自带屏还接着别的显示器。一台电脑上三块及以上的屏暂不支持"
        LAYOUT=second; SECOND_ID=$builtin; SECOND_BUILTIN=1
    elif [ "$n" -eq 0 ]; then
        LAYOUT=none; SECOND_ID=""; SECOND_BUILTIN=0
    elif [ "$n" -eq 1 ]; then
        LAYOUT=second; SECOND_ID=$others; SECOND_BUILTIN=0
    else
        die "这台 Mac 接了三块及以上的显示器，暂不支持"
    fi

    MAIN_NAME=$name
    MAIN_UUID=$uuid
    MAIN_ID=$main
    if [ "$LAYOUT" = none ]; then
        EXTEND_ARGS=(); MIRROR_ARGS=()
    elif read_current_layout; then
        EXTEND_ARGS=("${CUR_EXT[@]}")
        MIRROR_ARGS=("${CUR_MIR[@]}")
    elif [ "$CUR_ERR" = "当前是镜像" ] && [ ${#EXTEND_ARGS[@]} -gt 0 ] && [ ${#MIRROR_ARGS[@]} -gt 0 ]; then
        echo "注意：当前是镜像，沿用 config.sh 里已有的扩展排列"
    else
        die "$CUR_ERR。请让主屏显示 Mac、切到扩展后再运行设置"
    fi
    [ "$SECOND_BUILTIN" = 1 ] || OWNED_LAYOUT=extend

    # 主屏正显示 Mac 时读到的值就是 Mac 所接输入口的编码
    vals=()
    for i in 1 2 3; do
        v=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$uuid" get input 2>/dev/null)
        is_int "$v" && [ "$v" -gt 0 ] && vals+=("$v")
        sleep 0.3
    done
    v=""
    if [ ${#vals[@]} -ge 2 ]; then
        v=$(printf '%s\n' "${vals[@]}" | sort | uniq -c | sort -rn | awk '$1 >= 2 {print $2; exit}')
    fi
    if [ -n "$v" ]; then
        [ "$v" != "$((CODE_SELF))" ] && echo "本机编码由 $CODE_SELF 更新为 $v"
        CODE_SELF=$v
    else
        echo "警告：未读到稳定的输入源编码（主屏可能不在本机），沿用 CODE_SELF=$CODE_SELF"
    fi

    # 学习最大亮度，供 ddc_sanity 核对；读不出稳定正值就关闭核对
    vals=()
    for i in 1 2 3; do
        v=$(lockf -k -t 3 "$DDC_LOCK" "$M1DDC" display "$uuid" max luminance 2>/dev/null | head -1)
        is_int "$v" && [ "$v" -gt 0 ] && vals+=("$v")
        sleep 0.3
    done
    v=""
    [ ${#vals[@]} -ge 2 ] && v=$(printf '%s\n' "${vals[@]}" | sort | uniq -c | sort -rn | awk '$1 >= 2 {print $2; exit}')
    if [ -n "$v" ]; then
        SANITY_MAXLUM=$v
    elif [ "${SANITY_MAXLUM:-0}" -gt 0 ]; then
        echo "警告：未读到稳定的最大亮度（主屏可能不在本机），沿用 SANITY_MAXLUM=$SANITY_MAXLUM"
    else
        echo "警告：未读到稳定的最大亮度，关闭占有核对（SANITY_MAXLUM=0）"
    fi

    if [ "$SECOND_BUILTIN" = 1 ] && [ -x "$DISPLAY_HELPER" ]; then
        v=$("$DISPLAY_HELPER" list 2>/dev/null | awk '/builtin=1/ {print $1; exit}')
        is_int "$v" && LAPTOP_CGID=$v
    fi

    [ -f "$CONFIG" ] && cp -f "$CONFIG" "$CONFIG.bak"
    write_config
    rm -f "$SCREENCTL_DIR/.direct_ok" "$SCREENCTL_DIR/.direct_fail"

    echo "已写入 $CONFIG"
    echo "  主屏 $MAIN_NAME  uuid=$MAIN_UUID"
    echo "  本机编码 CODE_SELF=$CODE_SELF  最大亮度核对 SANITY_MAXLUM=$SANITY_MAXLUM"
    if [ "$LAYOUT" = none ]; then
        echo "  屏幕组合：只有主屏，不调整布局"
    else
        if [ "$SECOND_BUILTIN" = 1 ]; then echo "  屏幕组合：主屏 + 笔记本自带屏（id=$SECOND_ID）"
        else echo "  屏幕组合：主屏 + 另一块外接屏（id=$SECOND_ID）"; fi
        echo "  扩展：displayplacer$(printf ' "%s"' "${EXTEND_ARGS[@]}")"
        echo "  镜像：displayplacer \"${MIRROR_ARGS[0]}\""
    fi
}

# ---------------------------------------------------------------- 设置问答

# 提问：$1 = 问题，$2 = 默认值。回答放进 ANSWER，直接回车取默认值；输入读完了（非交互）也取默认值
ask() {
    local a=""
    if [ -n "$2" ]; then printf '%s [%s]：' "$1" "$2"; else printf '%s：' "$1"; fi
    IFS= read -r a || true
    [ -n "$a" ] || a=$2
    ANSWER=$a
}

# 名字和地址写进 config.sh 时用单引号包着，里面不能有单引号、制表符和换行
clean_value() {
    printf '%s' "$1" | tr -d "'\t\r\n"
}

# 增删其他设备。编码必须是数字，不能与两台电脑的编码或已有设备重复
setup_devices() {
    local i e name code
    local -a keep keep_hk
    echo
    echo "== 其他设备（游戏机等）=="
    if [ ${#DEVICES[@]} -gt 0 ]; then
        echo "现在的设备："
        i=1
        for e in "${DEVICES[@]}"; do echo "  $i. ${e%=*}（编码 ${e##*=}）"; i=$((i + 1)); done
        ask "要删除哪几个（输入编号，空格分隔；改名请先删除再添加；都保留就直接回车）" ""
        if [ -n "$ANSWER" ]; then
            keep=(); keep_hk=(); i=1
            for e in "${DEVICES[@]}"; do
                case " $ANSWER " in
                    *" $i "*) echo "  已删除：${e%=*}" ;;
                    *) keep+=("$e"); keep_hk+=("${HOTKEY_DEVICES[$((i - 1))]}") ;;
                esac
                i=$((i + 1))
            done
            DEVICES=("${keep[@]}"); HOTKEY_DEVICES=("${keep_hk[@]}")
        fi
    fi
    echo "主屏的每个输入口有一个编码。常见的是：15 = DP 1（不少显示器的 USB-C 也用它）、16 = DP 2、17 = HDMI 1、18 = HDMI 2。"
    if [ -n "$CODE_PEER" ]; then echo "本机用的是 $CODE_SELF，$PEER_NAME 用的是 $CODE_PEER。"; else echo "本机用的是 $CODE_SELF。"; fi
    while :; do
        ask "要添加的设备的名字（不添加就直接回车）" ""
        name=$(clean_value "$ANSWER")
        [ -n "$name" ] || break
        ask "$name 接的输入口编码" ""
        code=$ANSWER
        if ! is_int "$code" || [ "$code" -lt 1 ] || [ "$code" -gt 255 ]; then
            echo "  编码要是 1 到 255 之间的数字，没有添加"
        elif [ "$code" = "$CODE_SELF" ] || [ "$code" = "$CODE_PEER" ]; then
            echo "  编码 $code 是电脑在用的口，没有添加"
        elif device_name_by_code "$code" >/dev/null; then
            echo "  编码 $code 已经有设备了，没有添加"
        else
            DEVICES+=("$name=$code"); HOTKEY_DEVICES+=("")
            echo "  已添加：$name（编码 $code）"
        fi
    done
}

# 设置写完之后：让守护进程重新读配置
setup_done() {
    echo
    echo "已写入 $CONFIG"
    if daemon_alive; then
        write_cmd reload
        echo "守护进程已重新读取配置。"
    fi
    echo "菜单栏程序下次点开菜单时会更新。"
}

# 交互式设置（设计文档 9）：setup [显示器名] 走全部步骤；setup devices 只增删其他设备
cmd_setup() {
    local os auto_name prog pub e n
    if [ "$1" = devices ]; then
        need_config
        setup_devices
        write_config
        setup_done
        return 0
    fi
    cmd_init "$@"

    echo
    echo "== 本机 =="
    ask "这台电脑在菜单和提示里叫什么" "$(self_name)"
    SELF_NAME=$(clean_value "$ANSWER")

    echo
    echo "== 对方电脑 =="
    echo "填了对方电脑，两台才能互相代为切换。没有，或者暂时不配：主机名填 - 。"
    ask "对方电脑的主机名（如 my-pc.local）或局域网 IP" "${PEER_HOST:--}"
    PEER_HOST=$(clean_value "$ANSWER")
    [ "$PEER_HOST" = - ] && PEER_HOST=""
    if [ -n "$PEER_HOST" ]; then
        auto_name=0
        { [ "$PEER_NAME" = Windows ] || [ "$PEER_NAME" = Mac ]; } && auto_name=1
        ask "对方是 Windows 还是 Mac（输入 windows 或 mac）" "$PEER_OS"
        case "$ANSWER" in mac|Mac|MAC|m|M) os=mac ;; *) os=windows ;; esac
        [ "$os" = "$PEER_OS" ] || PEER_PROG=""
        PEER_OS=$os
        if [ $auto_name -eq 1 ]; then
            if [ "$PEER_OS" = mac ]; then PEER_NAME=Mac; else PEER_NAME=Windows; fi
        fi
        ask "对方电脑在菜单和提示里叫什么" "$PEER_NAME"
        PEER_NAME=$(clean_value "$ANSWER")
        ask "对方电脑上的用户名" "$PEER_USER"
        PEER_USER=$(clean_value "$ANSWER")
        prog=$PEER_PROG
        if [ -z "$prog" ]; then
            if [ "$PEER_OS" = mac ]; then prog='~/screenctl/screenctl.sh'
            else prog="C:\\Users\\$PEER_USER\\ScreenSwitch\\ScreenSwitch.exe"; fi
        fi
        ask "对方电脑上本程序的路径" "$prog"
        PEER_PROG=$(clean_value "$ANSWER")

        echo
        echo "== SSH =="
        if [ ! -f "$SSH_KEY.pub" ]; then
            ask "本机还没有 SSH 密钥（$SSH_KEY），现在生成吗（y / n）" "y"
            case "$ANSWER" in
                y|Y|yes)
                    mkdir -p "$(dirname "$SSH_KEY")"
                    ssh-keygen -t ed25519 -N "" -C "screenctl@$(scutil --get LocalHostName 2>/dev/null)" -f "$SSH_KEY" >/dev/null \
                        && echo "  已生成" ;;
            esac
        fi
        if [ -f "$SSH_KEY.pub" ]; then
            pub=$(cat "$SSH_KEY.pub")
            echo "本机的公钥："
            echo "  $pub"
            if [ "$PEER_OS" = windows ]; then
                echo "在 $PEER_NAME 上用管理员 PowerShell、在仓库目录里运行："
                echo "  powershell -ExecutionPolicy Bypass -File .\\setup-sshd.ps1 -PeerPubKey \"$pub\""
            else
                echo "在 $PEER_NAME 上运行："
                echo "  ~/screenctl/screenctl.sh authorize \"$pub\""
            fi
        fi
        echo "拿到 $PEER_NAME 的公钥后，在本机运行：$0 authorize \"<它的公钥>\""
        echo "并在 系统设置 → 通用 → 共享 里打开"远程登录"。"

        echo
        echo "正在向 $PEER_NAME 询问它的输入口编码和设备列表…"
        if peer_info; then
            if [ -n "$INFO_CODE" ]; then
                CODE_PEER=$INFO_CODE
                echo "  $PEER_NAME 接在编码 $CODE_PEER 的输入口"
            fi
            if [ ${#INFO_DEVICES[@]} -gt 0 ] && [ ${#DEVICES[@]} -eq 0 ]; then
                DEVICES=(); HOTKEY_DEVICES=()
                for e in "${INFO_DEVICES[@]}"; do
                    n=$(clean_value "${e%=*}")
                    DEVICES+=("$n=${e##*=}"); HOTKEY_DEVICES+=("")
                    echo "  从 $PEER_NAME 读到设备：$n（编码 ${e##*=}）"
                done
            fi
        else
            echo "  现在连不上 $PEER_NAME（SSH 还没配好也没关系）。"
            ask "  $PEER_NAME 接在主屏的哪个输入口（编码；不知道就直接回车，以后切换时会自动询问）" "$CODE_PEER"
            if is_int "$ANSWER" && [ "$ANSWER" -ge 1 ] && [ "$ANSWER" -le 255 ]; then CODE_PEER=$ANSWER; fi
        fi
    fi

    setup_devices
    write_config
    setup_done
    if [ -n "$PEER_HOST" ] || [ "$LAYOUT" != "" ]; then
        echo "如果守护进程正在运行，执行一次让它按新设置重新启动：launchctl kickstart -k gui/\$(id -u)/com.user.screenctl"
    fi
}

# 把对方电脑的公钥加进 authorized_keys，限定为只能调用本机的 ssh-gate（拿不到 shell、不能端口转发）。
# 这把公钥原有的条目会被替换
cmd_authorize() {
    local key=$1 ak=${AUTHORIZED_KEYS:-$HOME/.ssh/authorized_keys} blob tmp
    case "$key" in
        ssh-*|ecdsa-*|sk-*) ;;
        *) die "用法：$0 authorize \"<对方电脑的公钥，形如 ssh-ed25519 AAAA… 注释>\"" ;;
    esac
    blob=$(printf '%s\n' "$key" | awk '{print $2}')
    [ -n "$blob" ] || die "公钥格式不对：要有类型和内容两段"
    mkdir -p "$(dirname "$ak")" && chmod 700 "$(dirname "$ak")"
    [ -f "$ak" ] || : > "$ak"
    tmp="$ak.screenctl.$$"
    grep -vF " $blob" "$ak" > "$tmp"
    printf 'command="%s/screenctl.sh ssh-gate",no-port-forwarding,no-agent-forwarding,no-pty,no-X11-forwarding %s\n' \
        "$SCREENCTL_DIR" "$key" >> "$tmp"
    chmod 600 "$tmp" && mv -f "$tmp" "$ak" || die "写入 $ak 失败"
    echo "已把这把公钥加进 $ak，它只能调用 $SCREENCTL_DIR/screenctl.sh ssh-gate。"
}

# 菜单栏程序读取（设计文档 8）：每行一个 键=值；设备为 device=<编码><Tab><名字><Tab><快捷键>
cmd_menu_info() {
    local i=0 e
    echo "self=$(self_name)"
    if peer_configured; then echo "peer=$PEER_NAME"; else echo "peer="; fi
    echo "layout=$LAYOUT"
    echo "second_builtin=$SECOND_BUILTIN"
    echo "owned_layout=$OWNED_LAYOUT"
    echo "auto_save=$AUTO_SAVE_LAYOUT"
    echo "hotkey_peer=$HOTKEY_PEER"
    for e in "${DEVICES[@]}"; do
        printf 'device=%s\t%s\t%s\n' "${e##*=}" "${e%=*}" "${HOTKEY_DEVICES[$i]}"
        i=$((i + 1))
    done
}

cmd_save_layout() {
    need_config
    if daemon_alive; then
        write_cmd "save-layout"
        echo "已交给守护进程：save-layout"
        return 0
    fi
    MODE=direct
    do_save_layout manual || die "$CUR_ERR"
    echo "已保存：displayplacer$(printf ' "%s"' "${EXTEND_ARGS[@]}")"
}

cmd_set() {
    need_config
    case "$1=$2" in
        AUTO_SAVE_LAYOUT=0|AUTO_SAVE_LAYOUT=1|OWNED_LAYOUT=extend|OWNED_LAYOUT=external) ;;
        *) die "用法：$0 set AUTO_SAVE_LAYOUT <0|1> | set OWNED_LAYOUT <extend|external>" ;;
    esac
    if daemon_alive; then
        write_cmd "set $1 $2"
        echo "已交给守护进程：set $1 $2"
        return 0
    fi
    MODE=direct
    do_set "$1" "$2"
    echo "已设置 $1=$2"
}

usage() {
    cat <<EOF
用法：$0 <子命令>
  setup [显示器名]           交互式设置：探测主屏和屏幕组合，填对方电脑和其他设备（主屏显示本机时运行）
  setup devices              只增删其他设备
  init [显示器名]            只做探测，不提问
  authorize <公钥>           把对方电脑的公钥加进 authorized_keys，限定为 ssh-gate
  give peer | self | <设备名或编码>   切给对方 / 拿回 / 切给某个设备
  take | release             手动拿回 / 让出
  status                     查看归属与守护进程
  save-layout                把当前扩展排列保存为"拿回"排列
  set AUTO_SAVE_LAYOUT <0|1> 开关"自动记住屏幕排列"
  set OWNED_LAYOUT <extend|external>  拿回后扩展，或仅大屏（关闭笔记本屏）
  request give peer | request give code <n> | request take | request info   供对方 SSH 调用
  menu-info                  菜单栏程序读取
  watch                      守护进程
EOF
}

case "${1:-}" in
    init) shift; cmd_init "$@" ;;
    setup) shift; cmd_setup "$@" ;;
    authorize) cmd_authorize "${2:-}" ;;
    give) shift; cmd_give "$@" ;;
    take|release) cmd_take_release "$1" ;;
    status) cmd_status ;;
    save-layout) cmd_save_layout ;;
    set) cmd_set "${2:-}" "${3:-}" ;;
    request) shift; cmd_request "$@" ;;
    menu-info) cmd_menu_info ;;
    watch) cmd_watch ;;
    ssh-gate) cmd_ssh_gate ;;
    -h|--help|help) usage ;;
    *) usage; exit 1 ;;
esac

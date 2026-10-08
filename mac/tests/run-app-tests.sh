#!/bin/bash
# 编译菜单栏程序，测界面背后的逻辑（菜单项的生成、向导每页的检查、快捷键写法、文字表），中英文各跑一遍。
# 用法：mac/tests/run-app-tests.sh   约一分钟（主要是编译）；全部通过时退出码 0。
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
BUILD=$(mktemp -d "${TMPDIR:-/tmp}/screenswitch-app-test.XXXXXX")
trap 'rm -rf "$BUILD"' EXIT
swiftc -swift-version 5 -O -module-cache-path "$BUILD/cache" "$REPO"/mac/app/ScreenSwitch/*.swift -o "$BUILD/ScreenSwitch" || exit 1
rc=0
for lang in zh en; do
    echo "== $lang"
    SCREENSWITCH_LANG=$lang SCREENSWITCH_STRINGS="$REPO/i18n/strings.tsv" SCREENCTL_DIR="$BUILD/home" "$BUILD/ScreenSwitch" --run-tests || rc=1
done
exit $rc

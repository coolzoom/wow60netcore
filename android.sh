#!/usr/bin/env bash
# 安卓一键脚本：1 安装环境  2 编译  3 打包  4 测试
#
#   ./android.sh            显示菜单，输入数字（可多个，如 "1 2 3 4"）
#   ./android.sh 1 2 3 4    不经菜单直接按顺序执行
#
# 环境（SDK、NDK，必要时还有 JDK 和私有 .NET SDK）全部装在本项目的 android-sdk/ 和 android-ndk/ 下，
# 两个目录已被 .gitignore 忽略，删除即可完全卸载，不会改动系统。
#
# 可选环境变量：
#   WOW_DATA=<目录>     游戏 Data 目录（默认 ../Data），测试时推送到设备
#   SKIP_DATA=1         测试时不推送游戏数据（应用会进入程序化场景）
#   TEST_MODE=world     测试启动模式：world | glue | procedural
#   TEST_SECONDS=45     启动后等待多少秒再截图
#   ANDROID_SERIAL=<序列号>  连了多台设备时指定测试设备（adb devices 查看）
#   ANDROID_KEYSTORE / ANDROID_KEY_ALIAS / ANDROID_KEY_PASS   打包时用自己的签名（默认用调试签名）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
SDK="$ROOT/android-sdk"
NDK="$ROOT/android-ndk"
PROJECT="$ROOT/src/Client.Android/Client.Android.csproj"
NATIVE="$ROOT/src/Client.Android/native"
DIST="$ROOT/dist"
APP_ID="org.netcoreclient.wow"
ACTIVITY="$APP_ID/$APP_ID.MainActivity"

NDK_RELEASE="r28c"
PLATFORM="android-36"
BUILD_TOOLS="36.0.0"
CMAKE_VERSION="3.31.6"
MIN_API=24
ABIS=(arm64-v8a x86_64)
# cimgui 版本必须与 ImGui.NET 1.90.8.1 一致（ImGui.NET-nativebuild v1.90.8 的子模块）
CIMGUI_COMMIT="7c16d31cdb9d2db3038b324fe967ffa76b02c8c4"
AVD_NAME="netcore_wow"
REMOTE_FILES="/sdcard/Android/data/$APP_ID/files"

# 先取到变量里：macOS 自带的 bash 3.2 解析 case "$(...)" 会报语法错误
UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"
case "$UNAME_S" in
    Darwin) HOST_OS=mac; NDK_HOST=darwin ;;
    Linux) HOST_OS=linux; NDK_HOST=linux ;;
    *) echo "只支持 macOS 和 Linux" >&2; exit 1 ;;
esac
case "$UNAME_M" in
    arm64|aarch64) HOST_ARCH=arm64; EMULATOR_ABI=arm64-v8a ;;
    *) HOST_ARCH=x64; EMULATOR_ABI=x86_64 ;;
esac
SYSTEM_IMAGE="system-images;$PLATFORM;google_apis;$EMULATOR_ABI"

info() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m警告:\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m错误:\033[0m %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "缺少命令 $1"; }

# ---------------------------------------------------------------- 环境探测

find_java() {
    local candidate version
    for candidate in "${JAVA_HOME:-}" "$SDK/jdk/current" \
        "$( [ "$HOST_OS" = mac ] && /usr/libexec/java_home -v 17+ 2>/dev/null || true)" \
        "$(command -v java >/dev/null && java -XshowSettings:properties -version 2>&1 | awk -F'= ' '/java.home/ {print $2}' || true)"; do
        [ -n "$candidate" ] && [ -x "$candidate/bin/java" ] || continue
        version="$("$candidate/bin/java" -version 2>&1 | awk -F'"' '/version/ {split($2, v, "."); print v[1]}')"
        if [ "${version:-0}" -ge 17 ]; then
            export JAVA_HOME="$candidate"
            return 0
        fi
    done
    return 1
}

find_dotnet() {
    if [ -x "$SDK/dotnet/dotnet" ]; then
        export DOTNET_ROOT="$SDK/dotnet"
        DOTNET="$SDK/dotnet/dotnet"
    else
        DOTNET="$(command -v dotnet || true)"
    fi
    [ -n "$DOTNET" ]
}

has_android_workload() { "$DOTNET" workload list 2>/dev/null | grep -qE '^ *android '; }

sdkmanager() { "$SDK/cmdline-tools/latest/bin/sdkmanager" --sdk_root="$SDK" "$@"; }
avdmanager() { "$SDK/cmdline-tools/latest/bin/avdmanager" "$@"; }
adb() { "$SDK/platform-tools/adb" "$@"; }

require_env() {
    find_java || die "未找到 JDK 17+，请先执行 1 安装环境"
    find_dotnet || die "未找到 dotnet，请先执行 1 安装环境"
    has_android_workload || die "$DOTNET 没有 android workload，请先执行 1 安装环境"
    [ -x "$SDK/cmdline-tools/latest/bin/sdkmanager" ] || die "Android SDK 未安装，请先执行 1 安装环境"
    [ -f "$NDK/build/cmake/android.toolchain.cmake" ] || die "Android NDK 未安装，请先执行 1 安装环境"
    PROPS=("-p:AndroidSdkDirectory=$SDK" "-p:AndroidNdkDirectory=$NDK" "-p:JavaSdkDirectory=$JAVA_HOME")
}

# ---------------------------------------------------------------- 1 安装环境

install_jdk() {
    find_java && { info "JDK: $JAVA_HOME"; return; }
    local os arch url
    os=$([ "$HOST_OS" = mac ] && echo macOS || echo linux)
    arch=$([ "$HOST_ARCH" = arm64 ] && echo aarch64 || echo x64)
    url="https://aka.ms/download-jdk/microsoft-jdk-17-$os-$arch.tar.gz"
    info "下载 Microsoft OpenJDK 17 到 android-sdk/jdk"
    rm -rf "$SDK/jdk" && mkdir -p "$SDK/jdk"
    curl -fL --progress-bar "$url" | tar -xz -C "$SDK/jdk"
    local home
    home="$(find "$SDK/jdk" -maxdepth 4 -type f -path '*/bin/java' | head -1)"
    ln -sfn "$(dirname "$(dirname "$home")")" "$SDK/jdk/current"
    find_java || die "JDK 安装失败"
    info "JDK: $JAVA_HOME"
}

install_sdk() {
    if [ ! -x "$SDK/cmdline-tools/latest/bin/sdkmanager" ]; then
        local zip
        zip="$(curl -fsSL https://dl.google.com/android/repository/repository2-3.xml |
            grep -oE "commandlinetools-$HOST_OS-[0-9]+_latest\.zip" | sort -t- -k3 -n | tail -1)"
        [ -n "$zip" ] || zip="commandlinetools-$HOST_OS-13114758_latest.zip"
        info "下载 Android SDK 命令行工具 $zip"
        local tmp
        tmp="$(mktemp -d)"
        curl -fL --progress-bar -o "$tmp/tools.zip" "https://dl.google.com/android/repository/$zip"
        unzip -q "$tmp/tools.zip" -d "$tmp"
        mkdir -p "$SDK/cmdline-tools"
        rm -rf "$SDK/cmdline-tools/latest"
        mv "$tmp/cmdline-tools" "$SDK/cmdline-tools/latest"
        rm -rf "$tmp"
    fi
    info "接受 SDK 许可"
    set +o pipefail
    yes | sdkmanager --licenses >/dev/null
    set -o pipefail
    info "安装 platform-tools、${PLATFORM}、build-tools ${BUILD_TOOLS}、cmake $CMAKE_VERSION"
    sdkmanager "platform-tools" "platforms;$PLATFORM" "build-tools;$BUILD_TOOLS" "cmake;$CMAKE_VERSION"
}

install_ndk() {
    if [ -f "$NDK/build/cmake/android.toolchain.cmake" ]; then
        info "NDK: $NDK ($(awk -F' = ' '/Pkg.Revision/ {print $2}' "$NDK/source.properties"))"
        return
    fi
    local tmp
    tmp="$(mktemp -d)"
    info "下载 Android NDK $NDK_RELEASE 到 android-ndk/"
    curl -fL --progress-bar -o "$tmp/ndk.zip" "https://dl.google.com/android/repository/android-ndk-$NDK_RELEASE-$NDK_HOST.zip"
    unzip -q "$tmp/ndk.zip" -d "$tmp"
    rm -rf "$NDK"
    mv "$tmp/android-ndk-$NDK_RELEASE" "$NDK"
    rm -rf "$tmp"
}

install_workload() {
    find_dotnet || true
    if [ -n "${DOTNET:-}" ] && has_android_workload; then
        info ".NET android workload 已安装（${DOTNET}）"
        return
    fi
    local system_root=""
    [ -n "${DOTNET:-}" ] && system_root="$(dirname "$(readlink -f "$DOTNET" 2>/dev/null || echo "$DOTNET")")"
    if [ -n "$system_root" ] && [ -w "$system_root" ] && [ -w "$system_root/packs" ]; then
        info "为系统 .NET 安装 android workload"
        "$DOTNET" workload install android
        return
    fi
    # 系统 .NET 归 root 所有时不用 sudo：在 android-sdk/dotnet 装一份私有 .NET SDK 带 workload
    local version="10.0"
    [ -n "${DOTNET:-}" ] && version="$("$DOTNET" --version 2>/dev/null || echo 10.0)"
    info "系统 .NET 目录不可写，安装私有 .NET SDK $version 到 android-sdk/dotnet"
    local installer
    installer="$(mktemp)"
    curl -fsSL -o "$installer" https://dot.net/v1/dotnet-install.sh
    if ! bash "$installer" --version "$version" --install-dir "$SDK/dotnet" --no-path; then
        bash "$installer" --channel 10.0 --install-dir "$SDK/dotnet" --no-path
    fi
    rm -f "$installer"
    find_dotnet
    info "安装 android workload（私有 .NET）"
    "$DOTNET" workload install android
}

build_cimgui() {
    local cmake="$SDK/cmake/$CMAKE_VERSION/bin/cmake"
    local ninja="$SDK/cmake/$CMAKE_VERSION/bin/ninja"
    local src="$SDK/src/cimgui"
    [ -x "$cmake" ] || die "cmake 未安装，请先执行 1 安装环境"
    if [ ! -f "$src/imgui/imgui.h" ]; then
        need git
        info "获取 cimgui 源码（${CIMGUI_COMMIT}）"
        rm -rf "$src" && mkdir -p "$src"
        git -C "$src" init -q
        git -C "$src" remote add origin https://github.com/cimgui/cimgui.git
        git -C "$src" fetch -q --depth 1 origin "$CIMGUI_COMMIT"
        git -C "$src" checkout -q FETCH_HEAD
        git -C "$src" submodule update -q --init --depth 1
    fi
    local strip
    strip="$(find "$NDK/toolchains/llvm/prebuilt" -maxdepth 3 -name llvm-strip | head -1)"
    for abi in "${ABIS[@]}"; do
        info "用 NDK 编译 libcimgui.so ($abi)"
        local out="$SDK/src/cimgui-build/$abi"
        "$cmake" -S "$src" -B "$out" -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" \
            -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
            -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$MIN_API" -DCMAKE_BUILD_TYPE=Release >/dev/null
        "$cmake" --build "$out" >/dev/null
        local lib
        lib="$(find "$out" -maxdepth 1 -name '*cimgui.so' | head -1)"
        [ -n "$lib" ] || die "没有找到 $abi 的 cimgui 编译产物"
        mkdir -p "$NATIVE/$abi"
        # 安卓只解包 lib*.so，cimgui 的 CMake 去掉了 lib 前缀，这里补回来
        cp "$lib" "$NATIVE/$abi/libcimgui.so"
        [ -n "$strip" ] && "$strip" --strip-unneeded "$NATIVE/$abi/libcimgui.so"
    done
}

step_install() {
    need curl
    need unzip
    mkdir -p "$SDK"
    install_jdk
    install_sdk
    install_ndk
    install_workload
    build_cimgui
    info "环境安装完成：SDK=$SDK  NDK=$NDK  JDK=$JAVA_HOME  dotnet=$DOTNET"
}

# ---------------------------------------------------------------- 2 编译

ensure_cimgui() {
    for abi in "${ABIS[@]}"; do
        [ -f "$NATIVE/$abi/libcimgui.so" ] || { build_cimgui; return; }
    done
}

step_build() {
    require_env
    ensure_cimgui
    info "编译桌面版和测试（确认共享代码没坏）"
    "$DOTNET" build "$ROOT/NetCoreClient.slnx" -c Debug -v quiet -nologo
    info "编译安卓项目 (Debug)"
    "$DOTNET" build "$PROJECT" -c Debug -v quiet -nologo "${PROPS[@]}"
    info "编译完成：$(find "$ROOT/src/Client.Android/bin/Debug" -name '*-Signed.apk' | head -1)"
}

# ---------------------------------------------------------------- 3 打包

step_package() {
    require_env
    ensure_cimgui
    local signing=()
    if [ -n "${ANDROID_KEYSTORE:-}" ]; then
        signing=(-p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$ANDROID_KEYSTORE"
            "-p:AndroidSigningKeyAlias=${ANDROID_KEY_ALIAS:?需要 ANDROID_KEY_ALIAS}"
            "-p:AndroidSigningKeyPass=${ANDROID_KEY_PASS:?需要 ANDROID_KEY_PASS}"
            "-p:AndroidSigningStorePass=${ANDROID_KEY_PASS}")
    else
        warn "未设置 ANDROID_KEYSTORE，使用调试签名（可以侧载安装，不能上架应用商店）"
    fi
    info "打包 Release APK（arm64-v8a + x86_64）"
    # ${a[@]+...}: macOS 自带 bash 3.2 在 set -u 下展开空数组会报错
    "$DOTNET" publish "$PROJECT" -c Release -v quiet -nologo "${PROPS[@]}" ${signing[@]+"${signing[@]}"}
    local apk
    apk="$(find "$ROOT/src/Client.Android/bin/Release" -name '*-Signed.apk' -path '*publish*' | head -1)"
    [ -n "$apk" ] || apk="$(find "$ROOT/src/Client.Android/bin/Release" -name '*-Signed.apk' | head -1)"
    [ -n "$apk" ] || die "没有找到打包产物"
    mkdir -p "$DIST"
    cp "$apk" "$DIST/WoWNetCore.apk"
    info "APK：$DIST/WoWNetCore.apk ($(du -h "$DIST/WoWNetCore.apk" | cut -f1))"
}

# ---------------------------------------------------------------- 4 测试

device_count() { adb devices | awk 'NR > 1 && $2 == "device"' | wc -l | tr -d ' '; }

# 多台设备时：优先 ANDROID_SERIAL；交互运行时让用户选，否则用第一台
select_device() {
    [ -n "${ANDROID_SERIAL:-}" ] && return
    local serials=() serial index=1 choice
    while read -r serial; do serials+=("$serial"); done < <(adb devices | awk 'NR > 1 && $2 == "device" {print $1}')
    [ ${#serials[@]} -gt 1 ] || return 0
    echo "检测到多台设备："
    for serial in "${serials[@]}"; do
        printf '  %d) %s  %s\n' "$index" "$serial" "$(adb -s "$serial" shell getprop ro.product.model | tr -d '\r')"
        index=$((index + 1))
    done
    choice=1
    if [ -t 0 ]; then
        read -r -p "选择测试设备 [1]：" choice || true
        choice="${choice:-1}"
    fi
    [ "$choice" -ge 1 ] 2>/dev/null && [ "$choice" -le ${#serials[@]} ] || die "无效设备编号：$choice"
    export ANDROID_SERIAL="${serials[$((choice - 1))]}"
}

start_emulator() {
    info "没有已连接的设备，准备模拟器 ${AVD_NAME}（${SYSTEM_IMAGE}）"
    sdkmanager "emulator" "$SYSTEM_IMAGE"
    export ANDROID_SDK_ROOT="$SDK" ANDROID_HOME="$SDK" ANDROID_AVD_HOME="$SDK/avd"
    mkdir -p "$ANDROID_AVD_HOME"
    if [ ! -d "$ANDROID_AVD_HOME/$AVD_NAME.avd" ]; then
        echo no | avdmanager create avd -n "$AVD_NAME" -k "$SYSTEM_IMAGE" -d pixel_6 >/dev/null
        # 游戏数据 5GB+，扩大数据分区；开启硬件键盘便于调试
        printf 'disk.dataPartition.size=24G\nhw.keyboard=yes\nhw.ramSize=6144\nhw.gpu.enabled=yes\nhw.gpu.mode=host\n' \
            >> "$ANDROID_AVD_HOME/$AVD_NAME.avd/config.ini"
    fi
    info "启动模拟器（日志：$DIST/emulator.log）"
    mkdir -p "$DIST"
    nohup "$SDK/emulator/emulator" -avd "$AVD_NAME" -no-snapshot-save -no-boot-anim -gpu host >"$DIST/emulator.log" 2>&1 &
    adb wait-for-device
    local waited=0
    until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
        sleep 3
        waited=$((waited + 3))
        [ $waited -lt 300 ] || die "模拟器 5 分钟内没有启动完成，见 $DIST/emulator.log"
    done
    info "模拟器已启动"
}

push_data() {
    [ "${SKIP_DATA:-0}" = 1 ] && { warn "SKIP_DATA=1，不推送游戏数据"; return; }
    local data="${WOW_DATA:-$ROOT/../Data}"
    if [ ! -d "$data" ]; then
        warn "找不到游戏 Data 目录 ($data)，应用会进入程序化场景；用 WOW_DATA=<目录> 指定"
        return
    fi
    data="$(cd "$data" && pwd)"
    adb shell mkdir -p "$REMOTE_FILES/Data"
    local file name size remote_size
    for file in "$data"/*.MPQ "$data"/*.mpq; do
        [ -f "$file" ] || continue
        name="$(basename "$file")"
        size="$(wc -c <"$file" | tr -d ' ')"
        remote_size="$(adb shell stat -c %s "$REMOTE_FILES/Data/$name" 2>/dev/null | tr -d '\r' || true)"
        if [ "$size" = "$remote_size" ]; then
            continue
        fi
        info "推送 $name ($(du -h "$file" | cut -f1))"
        adb push "$file" "$REMOTE_FILES/Data/$name" >/dev/null
    done
    # 以 root 运行 adb 的模拟器（如 MuMu）推上去的文件归 root，应用读不了，改回应用自己的 uid
    if [ "$(adb shell id -u | tr -d '\r')" = 0 ]; then
        adb shell chown -R "$(adb shell stat -c %u:%g "$REMOTE_FILES" | tr -d '\r')" "$REMOTE_FILES/Data"
    fi
    info "游戏数据已在设备 $REMOTE_FILES/Data"
}

step_test() {
    require_env
    info "运行单元测试"
    "$DOTNET" test "$ROOT/NetCoreClient.slnx" -v quiet -nologo

    [ -f "$DIST/WoWNetCore.apk" ] || step_package
    [ -x "$SDK/platform-tools/adb" ] || die "adb 不存在，请先执行 1 安装环境"
    adb start-server >/dev/null
    [ "$(device_count)" -gt 0 ] || start_emulator
    select_device
    info "设备 ${ANDROID_SERIAL:-}：$(adb shell getprop ro.product.model | tr -d '\r')，Android $(adb shell getprop ro.build.version.release | tr -d '\r')，ABI $(adb shell getprop ro.product.cpu.abi | tr -d '\r')"

    info "安装 APK"
    adb install -r "$DIST/WoWNetCore.apk" >/dev/null ||
        die "安装失败。小米/MIUI 需在开发者选项打开「USB 安装」并在手机上确认；签名不同时先卸载旧版：adb uninstall $APP_ID"
    push_data

    local mode="${TEST_MODE:-world}" seconds="${TEST_SECONDS:-45}"
    info "启动应用（mode=${mode}），等待 $seconds 秒"
    adb shell am force-stop "$APP_ID"
    # 锁屏时 Activity 拿不到 Surface：先点亮屏幕并尝试解除锁屏（有密码时需手动解锁）
    adb shell input keyevent KEYCODE_WAKEUP || true
    adb shell wm dismiss-keyguard 2>/dev/null || true
    adb logcat -c
    adb shell am start -n "$ACTIVITY" --es mode "$mode" >/dev/null
    sleep "$seconds"

    mkdir -p "$DIST"
    adb exec-out screencap -p >"$DIST/android-test.png"
    adb logcat -d -v brief DOTNET:V monodroid:V SDL:V SDL/APP:V AndroidRuntime:E libc:F '*:S' >"$DIST/android-test.log" || true
    local pid
    pid="$(adb shell pidof "$APP_ID" | tr -d '\r' || true)"
    if [ -z "$pid" ] || grep -qE 'FATAL EXCEPTION|Unhandled Exception|Fatal signal' "$DIST/android-test.log"; then
        grep -E 'FATAL|Unhandled|Exception|Fatal signal|error' "$DIST/android-test.log" | head -20 || true
        die "测试失败：应用已退出或崩溃，完整日志 $DIST/android-test.log"
    fi
    grep -q 'OpenGL:' "$DIST/android-test.log" ||
        die "测试失败：应用在运行但没有创建 OpenGL ES 画面（手机锁屏/息屏？请解锁后重试），日志 $DIST/android-test.log"
    grep -E 'OpenGL:|Game data|No Data|GlueXML' "$DIST/android-test.log" | sed 's/^/    /' || true
    info "测试通过：应用运行中 (pid $pid)；截图 $DIST/android-test.png，日志 $DIST/android-test.log"
}

# ---------------------------------------------------------------- 菜单

run_step() {
    case "$1" in
        1) step_install ;;
        2) step_build ;;
        3) step_package ;;
        4) step_test ;;
        *) die "无效选项：$1（可选 1 2 3 4）" ;;
    esac
}

PROPS=()
steps=(${@+"$@"})
if [ ${#steps[@]} -eq 0 ]; then
    cat <<EOF
安卓一键脚本（项目：${ROOT}）
  1) 安装环境  JDK、Android SDK、NDK、.NET android workload，并用 NDK 编译 cimgui
  2) 编译      桌面版 + 安卓 Debug
  3) 打包      Release APK -> dist/WoWNetCore.apk
  4) 测试      单元测试 + 安装到设备/模拟器 + 推送游戏数据 + 启动截图
EOF
    read -r -p "请选择（可多选，空格分隔，如 1 2 3 4）：" -a steps || true
fi
[ ${#steps[@]} -gt 0 ] || die "没有选择任何步骤"
for step in "${steps[@]}"; do
    run_step "$step"
done

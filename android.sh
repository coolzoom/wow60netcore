#!/usr/bin/env bash
# One-stop Android script: 1 set up  2 build  3 package  4 test
#
#   ./android.sh            show the menu and enter numbers (several at once, e.g. "1 2 3 4")
#   ./android.sh 1 2 3 4    run those steps in order without the menu
#
# Everything it installs (SDK, NDK, and if needed a JDK and a private .NET SDK) goes under this project's
# android-sdk/ and android-ndk/. Both are in .gitignore; deleting them uninstalls everything, the system is untouched.
#
# Optional environment variables:
#   WOW_DATA=<dir>        game Data directory (default ../Data), pushed to the device when testing
#   SKIP_DATA=1           don't push game data when testing (the app then starts the procedural scene)
#   TEST_MODE=glue        launch mode when testing: glue (full client: login screens to the world) | world | procedural
#   TEST_REALMLIST=host[:port]       logon server for glue mode, also written to the device's realmlist.wtf
#                                    (default: REALMLIST in android.config, else 127.0.0.1)
#   TEST_LOGIN=account:password      log in with this account right away in glue mode (empty: type it on the device)
#                                    (default: LOGIN in android.config)
#   TEST_PORTS="3724 8085"     server ports forwarded from the device to this machine with adb reverse, so
#                              127.0.0.1 on the device reaches the realmd/mangosd running here
#                              (default: only when TEST_REALMLIST is 127.0.0.1/localhost; empty: no forwarding)
#   TEST_SECONDS=45       seconds to wait after launch before the screenshot
#   ANDROID_SERIAL=<serial>    device to test on when several are connected (see adb devices)
#   ANDROID_KEYSTORE / ANDROID_KEY_ALIAS / ANDROID_KEY_PASS   your own signing key for packaging (default: debug key)
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
# The cimgui version must match ImGui.NET 1.90.8.1 (the submodule of ImGui.NET-nativebuild v1.90.8)
CIMGUI_COMMIT="7c16d31cdb9d2db3038b324fe967ffa76b02c8c4"
AVD_NAME="netcore_wow"
REMOTE_FILES="/sdcard/Android/data/$APP_ID/files"

# Read into variables first: macOS's bash 3.2 fails to parse case "$(...)"
UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"
case "$UNAME_S" in
    Darwin) HOST_OS=mac; NDK_HOST=darwin ;;
    Linux) HOST_OS=linux; NDK_HOST=linux ;;
    *) echo "Only macOS and Linux are supported" >&2; exit 1 ;;
esac
case "$UNAME_M" in
    arm64|aarch64) HOST_ARCH=arm64; EMULATOR_ABI=arm64-v8a ;;
    *) HOST_ARCH=x64; EMULATOR_ABI=x86_64 ;;
esac
SYSTEM_IMAGE="system-images;$PLATFORM;google_apis;$EMULATOR_ABI"

info() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWarning:\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "Missing command: $1"; }

# ---------------------------------------------------------------- Environment detection

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
    find_java || die "No JDK 17+ found; run step 1 (set up) first"
    find_dotnet || die "No dotnet found; run step 1 (set up) first"
    has_android_workload || die "$DOTNET has no android workload; run step 1 (set up) first"
    [ -x "$SDK/cmdline-tools/latest/bin/sdkmanager" ] || die "Android SDK is not installed; run step 1 (set up) first"
    [ -f "$NDK/build/cmake/android.toolchain.cmake" ] || die "Android NDK is not installed; run step 1 (set up) first"
    PROPS=("-p:AndroidSdkDirectory=$SDK" "-p:AndroidNdkDirectory=$NDK" "-p:JavaSdkDirectory=$JAVA_HOME")
}

# ---------------------------------------------------------------- 1 Set up

install_jdk() {
    find_java && { info "JDK: $JAVA_HOME"; return; }
    local os arch url
    os=$([ "$HOST_OS" = mac ] && echo macOS || echo linux)
    arch=$([ "$HOST_ARCH" = arm64 ] && echo aarch64 || echo x64)
    url="https://aka.ms/download-jdk/microsoft-jdk-17-$os-$arch.tar.gz"
    info "Downloading Microsoft OpenJDK 17 to android-sdk/jdk"
    rm -rf "$SDK/jdk" && mkdir -p "$SDK/jdk"
    curl -fL --progress-bar "$url" | tar -xz -C "$SDK/jdk"
    local home
    home="$(find "$SDK/jdk" -maxdepth 4 -type f -path '*/bin/java' | head -1)"
    ln -sfn "$(dirname "$(dirname "$home")")" "$SDK/jdk/current"
    find_java || die "JDK installation failed"
    info "JDK: $JAVA_HOME"
}

install_sdk() {
    if [ ! -x "$SDK/cmdline-tools/latest/bin/sdkmanager" ]; then
        local zip
        zip="$(curl -fsSL https://dl.google.com/android/repository/repository2-3.xml |
            grep -oE "commandlinetools-$HOST_OS-[0-9]+_latest\.zip" | sort -t- -k3 -n | tail -1)"
        [ -n "$zip" ] || zip="commandlinetools-$HOST_OS-13114758_latest.zip"
        info "Downloading Android SDK command-line tools $zip"
        local tmp
        tmp="$(mktemp -d)"
        curl -fL --progress-bar -o "$tmp/tools.zip" "https://dl.google.com/android/repository/$zip"
        unzip -q "$tmp/tools.zip" -d "$tmp"
        mkdir -p "$SDK/cmdline-tools"
        rm -rf "$SDK/cmdline-tools/latest"
        mv "$tmp/cmdline-tools" "$SDK/cmdline-tools/latest"
        rm -rf "$tmp"
    fi
    info "Accepting SDK licenses"
    set +o pipefail
    yes | sdkmanager --licenses >/dev/null
    set -o pipefail
    info "Installing platform-tools, ${PLATFORM}, build-tools ${BUILD_TOOLS}, cmake $CMAKE_VERSION"
    sdkmanager "platform-tools" "platforms;$PLATFORM" "build-tools;$BUILD_TOOLS" "cmake;$CMAKE_VERSION"
}

install_ndk() {
    if [ -f "$NDK/build/cmake/android.toolchain.cmake" ]; then
        info "NDK: $NDK ($(awk -F' = ' '/Pkg.Revision/ {print $2}' "$NDK/source.properties"))"
        return
    fi
    local tmp
    tmp="$(mktemp -d)"
    info "Downloading Android NDK $NDK_RELEASE to android-ndk/"
    curl -fL --progress-bar -o "$tmp/ndk.zip" "https://dl.google.com/android/repository/android-ndk-$NDK_RELEASE-$NDK_HOST.zip"
    unzip -q "$tmp/ndk.zip" -d "$tmp"
    rm -rf "$NDK"
    mv "$tmp/android-ndk-$NDK_RELEASE" "$NDK"
    rm -rf "$tmp"
}

install_workload() {
    find_dotnet || true
    if [ -n "${DOTNET:-}" ] && has_android_workload; then
        info ".NET android workload is already installed (${DOTNET})"
        return
    fi
    local system_root=""
    [ -n "${DOTNET:-}" ] && system_root="$(dirname "$(readlink -f "$DOTNET" 2>/dev/null || echo "$DOTNET")")"
    if [ -n "$system_root" ] && [ -w "$system_root" ] && [ -w "$system_root/packs" ]; then
        info "Installing the android workload for the system .NET"
        "$DOTNET" workload install android
        return
    fi
    # When the system .NET is owned by root, avoid sudo: install a private .NET SDK with the workload in android-sdk/dotnet
    local version="10.0"
    [ -n "${DOTNET:-}" ] && version="$("$DOTNET" --version 2>/dev/null || echo 10.0)"
    info "The system .NET directory is not writable; installing a private .NET SDK $version to android-sdk/dotnet"
    local installer
    installer="$(mktemp)"
    curl -fsSL -o "$installer" https://dot.net/v1/dotnet-install.sh
    if ! bash "$installer" --version "$version" --install-dir "$SDK/dotnet" --no-path; then
        bash "$installer" --channel 10.0 --install-dir "$SDK/dotnet" --no-path
    fi
    rm -f "$installer"
    find_dotnet
    info "Installing the android workload (private .NET)"
    "$DOTNET" workload install android
}

build_cimgui() {
    local cmake="$SDK/cmake/$CMAKE_VERSION/bin/cmake"
    local ninja="$SDK/cmake/$CMAKE_VERSION/bin/ninja"
    local src="$SDK/src/cimgui"
    [ -x "$cmake" ] || die "cmake is not installed; run step 1 (set up) first"
    if [ ! -f "$src/imgui/imgui.h" ]; then
        need git
        info "Fetching cimgui sources (${CIMGUI_COMMIT})"
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
        info "Building libcimgui.so with the NDK ($abi)"
        local out="$SDK/src/cimgui-build/$abi"
        "$cmake" -S "$src" -B "$out" -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" \
            -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
            -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$MIN_API" -DCMAKE_BUILD_TYPE=Release >/dev/null
        "$cmake" --build "$out" >/dev/null
        local lib
        lib="$(find "$out" -maxdepth 1 -name '*cimgui.so' | head -1)"
        [ -n "$lib" ] || die "No cimgui build output found for $abi"
        mkdir -p "$NATIVE/$abi"
        # Android only extracts lib*.so; cimgui's CMake drops the lib prefix, so add it back
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
    info "Setup complete: SDK=$SDK  NDK=$NDK  JDK=$JAVA_HOME  dotnet=$DOTNET"
}

# ---------------------------------------------------------------- 2 Build

ensure_cimgui() {
    for abi in "${ABIS[@]}"; do
        [ -f "$NATIVE/$abi/libcimgui.so" ] || { build_cimgui; return; }
    done
}

step_build() {
    require_env
    ensure_cimgui
    info "Building the desktop client and tests (checks the shared code)"
    "$DOTNET" build "$ROOT/NetCoreClient.slnx" -c Debug -v quiet -nologo
    info "Building the Android project (Debug)"
    # Embedded assemblies make the Debug APK installable on its own (no Fast Deployment), with full exception messages.
    "$DOTNET" build "$PROJECT" -c Debug -v quiet -nologo "${PROPS[@]}" -p:EmbedAssembliesIntoApk=true
    info "Build complete: $(find "$ROOT/src/Client.Android/bin/Debug" -name '*-Signed.apk' | head -1)"
}

# ---------------------------------------------------------------- 3 Package

step_package() {
    require_env
    ensure_cimgui
    local signing=()
    if [ -n "${ANDROID_KEYSTORE:-}" ]; then
        signing=(-p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$ANDROID_KEYSTORE"
            "-p:AndroidSigningKeyAlias=${ANDROID_KEY_ALIAS:?ANDROID_KEY_ALIAS is required}"
            "-p:AndroidSigningKeyPass=${ANDROID_KEY_PASS:?ANDROID_KEY_PASS is required}"
            "-p:AndroidSigningStorePass=${ANDROID_KEY_PASS}")
    else
        warn "ANDROID_KEYSTORE is not set; using the debug key (fine for sideloading, not for app stores)"
    fi
    info "Packaging the Release APK (arm64-v8a + x86_64)"
    # ${a[@]+...}: macOS's bash 3.2 fails on expanding an empty array under set -u
    "$DOTNET" publish "$PROJECT" -c Release -v quiet -nologo "${PROPS[@]}" ${signing[@]+"${signing[@]}"}
    local apk
    apk="$(find "$ROOT/src/Client.Android/bin/Release" -name '*-Signed.apk' -path '*publish*' | head -1)"
    [ -n "$apk" ] || apk="$(find "$ROOT/src/Client.Android/bin/Release" -name '*-Signed.apk' | head -1)"
    [ -n "$apk" ] || die "No packaged APK found"
    mkdir -p "$DIST"
    cp "$apk" "$DIST/WoWNetCore.apk"
    info "APK: $DIST/WoWNetCore.apk ($(du -h "$DIST/WoWNetCore.apk" | cut -f1))"
}

# ---------------------------------------------------------------- 4 Test

device_count() { adb devices | awk 'NR > 1 && $2 == "device"' | wc -l | tr -d ' '; }

# With several devices: ANDROID_SERIAL wins; otherwise ask when interactive, else use the first one
select_device() {
    [ -n "${ANDROID_SERIAL:-}" ] && return
    local serials=() serial index=1 choice
    while read -r serial; do serials+=("$serial"); done < <(adb devices | awk 'NR > 1 && $2 == "device" {print $1}')
    [ ${#serials[@]} -gt 1 ] || return 0
    echo "Several devices are connected:"
    for serial in "${serials[@]}"; do
        printf '  %d) %s  %s\n' "$index" "$serial" "$(adb -s "$serial" shell getprop ro.product.model | tr -d '\r')"
        index=$((index + 1))
    done
    choice=1
    if [ -t 0 ]; then
        read -r -p "Device to test on [1]: " choice || true
        choice="${choice:-1}"
    fi
    [ "$choice" -ge 1 ] 2>/dev/null && [ "$choice" -le ${#serials[@]} ] || die "Invalid device number: $choice"
    export ANDROID_SERIAL="${serials[$((choice - 1))]}"
}

start_emulator() {
    info "No device connected; preparing emulator ${AVD_NAME} (${SYSTEM_IMAGE})"
    sdkmanager "emulator" "$SYSTEM_IMAGE"
    export ANDROID_SDK_ROOT="$SDK" ANDROID_HOME="$SDK" ANDROID_AVD_HOME="$SDK/avd"
    mkdir -p "$ANDROID_AVD_HOME"
    if [ ! -d "$ANDROID_AVD_HOME/$AVD_NAME.avd" ]; then
        echo no | avdmanager create avd -n "$AVD_NAME" -k "$SYSTEM_IMAGE" -d pixel_6 >/dev/null
        # Game data is 5 GB+: enlarge the data partition; enable the hardware keyboard for debugging
        printf 'disk.dataPartition.size=24G\nhw.keyboard=yes\nhw.ramSize=6144\nhw.gpu.enabled=yes\nhw.gpu.mode=host\n' \
            >> "$ANDROID_AVD_HOME/$AVD_NAME.avd/config.ini"
    fi
    info "Starting the emulator (log: $DIST/emulator.log)"
    mkdir -p "$DIST"
    nohup "$SDK/emulator/emulator" -avd "$AVD_NAME" -no-snapshot-save -no-boot-anim -gpu host >"$DIST/emulator.log" 2>&1 &
    adb wait-for-device
    local waited=0
    until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
        sleep 3
        waited=$((waited + 3))
        [ $waited -lt 300 ] || die "The emulator did not finish booting within 5 minutes; see $DIST/emulator.log"
    done
    info "Emulator started"
}

push_data() {
    [ "${SKIP_DATA:-0}" = 1 ] && { warn "SKIP_DATA=1: not pushing game data"; return; }
    local data="${WOW_DATA:-$ROOT/../Data}"
    if [ ! -d "$data" ]; then
        warn "Game Data directory not found ($data); the app will start the procedural scene. Set WOW_DATA=<dir>"
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
        info "Pushing $name ($(du -h "$file" | cut -f1))"
        adb push "$file" "$REMOTE_FILES/Data/$name" >/dev/null
    done
    # Emulators whose adb runs as root (e.g. MuMu) leave pushed files owned by root, unreadable by the app: give them back to the app's uid
    if [ "$(adb shell id -u | tr -d '\r')" = 0 ]; then
        adb shell chown -R "$(adb shell stat -c %u:%g "$REMOTE_FILES" | tr -d '\r')" "$REMOTE_FILES/Data"
    fi
    info "Game data is on the device at $REMOTE_FILES/Data"
}

# Server and account come from android.config (copy android.config.example; it is in .gitignore).
# TEST_REALMLIST / TEST_LOGIN in the environment override it.
REALMLIST="" LOGIN=""
[ ! -f "$ROOT/android.config" ] || eval "$(tr -d '\r' <"$ROOT/android.config")"
REALMLIST="${TEST_REALMLIST:-${REALMLIST:-127.0.0.1}}"
LOGIN="${TEST_LOGIN-$LOGIN}"

# The full client logs in to REALMLIST. For a server on this machine, adb reverse makes 127.0.0.1 on the device
# reach its realmd/mangosd, which also covers the world server address realmd hands out when it is 127.0.0.1.
# realmlist.wtf next to Data/ lets the client find the server when started from the launcher too.
setup_server() {
    local ports="" port
    case "$REALMLIST" in 127.0.0.1*|localhost*) ports="3724 8085" ;; esac
    ports="${TEST_PORTS-$ports}"
    for port in $ports; do
        adb reverse "tcp:$port" "tcp:$port" >/dev/null || warn "adb reverse tcp:$port failed"
    done
    mkdir -p "$DIST"
    printf 'set realmlist %s\r\n' "$REALMLIST" >"$DIST/realmlist.wtf"
    adb shell mkdir -p "$REMOTE_FILES"
    adb push "$DIST/realmlist.wtf" "$REMOTE_FILES/realmlist.wtf" >/dev/null
    info "Logon server: $REALMLIST (forwarded ports: ${ports:-none}${LOGIN:+, auto login as ${LOGIN%%:*}})"
}

step_test() {
    require_env
    info "Running unit tests"
    "$DOTNET" test "$ROOT/NetCoreClient.slnx" -v quiet -nologo

    [ -f "$DIST/WoWNetCore.apk" ] || step_package
    [ -x "$SDK/platform-tools/adb" ] || die "adb is missing; run step 1 (set up) first"
    adb start-server >/dev/null
    [ "$(device_count)" -gt 0 ] || start_emulator
    select_device
    info "Device ${ANDROID_SERIAL:-}: $(adb shell getprop ro.product.model | tr -d '\r'), Android $(adb shell getprop ro.build.version.release | tr -d '\r'), ABI $(adb shell getprop ro.product.cpu.abi | tr -d '\r')"

    info "Installing the APK"
    adb install -r "$DIST/WoWNetCore.apk" >/dev/null ||
        die "Install failed. On Xiaomi/MIUI enable \"Install via USB\" in Developer options and confirm on the phone; if the signature differs, uninstall the old version first: adb uninstall $APP_ID"
    push_data

    local mode="${TEST_MODE:-glue}" seconds="${TEST_SECONDS:-45}" extras=()
    if [ "$mode" = glue ]; then
        setup_server
        extras=(--es realmlist "$REALMLIST")
        [ -z "$LOGIN" ] || extras+=(--es login "$LOGIN")
    fi
    info "Launching the app (mode=${mode}), waiting $seconds seconds"
    adb shell am force-stop "$APP_ID"
    # While the screen is locked the Activity gets no Surface: wake the screen and try to dismiss the keyguard (unlock by hand if there is a PIN)
    # MIUI and similar block key injection unless "USB debugging (Security settings)" is on; then wake the screen by hand
    if ! adb shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1; then
        info "Could not wake the screen over adb (on Xiaomi enable \"USB debugging (Security settings)\" in Developer options); make sure the screen is on and unlocked"
    fi
    adb shell wm dismiss-keyguard 2>/dev/null || true
    adb logcat -c
    adb shell am start -n "$ACTIVITY" --es mode "$mode" ${extras[@]+"${extras[@]}"} >/dev/null
    sleep "$seconds"

    mkdir -p "$DIST"
    adb exec-out screencap -p >"$DIST/android-test.png"
    adb logcat -d -v brief DOTNET:V monodroid:V SDL:V SDL/APP:V AndroidRuntime:E libc:F '*:S' >"$DIST/android-test.log" || true
    local pid
    pid="$(adb shell pidof "$APP_ID" | tr -d '\r' || true)"
    if [ -z "$pid" ] || grep -qE 'FATAL EXCEPTION|Unhandled Exception|Fatal signal' "$DIST/android-test.log"; then
        grep -E 'FATAL|Unhandled|Exception|Fatal signal|error' "$DIST/android-test.log" | head -20 || true
        die "Test failed: the app exited or crashed; full log at $DIST/android-test.log"
    fi
    grep -q 'OpenGL:' "$DIST/android-test.log" ||
        die "Test failed: the app is running but created no OpenGL ES surface (screen locked or off? unlock and retry); log at $DIST/android-test.log"
    grep -E 'OpenGL:|Game data|No Data|GlueXML|FrameXML' "$DIST/android-test.log" | sed 's/^/    /' || true
    info "Test passed: the app is running (pid $pid); screenshot $DIST/android-test.png, log $DIST/android-test.log"
}

# ---------------------------------------------------------------- Menu

run_step() {
    case "$1" in
        1) step_install ;;
        2) step_build ;;
        3) step_package ;;
        4) step_test ;;
        *) die "Invalid option: $1 (choose from 1 2 3 4)" ;;
    esac
}

PROPS=()
steps=(${@+"$@"})
if [ ${#steps[@]} -eq 0 ]; then
    cat <<EOF
Android script (project: ${ROOT})
  1) Set up    JDK, Android SDK, NDK, .NET android workload; build cimgui with the NDK
  2) Build     desktop + Android Debug
  3) Package   Release APK -> dist/WoWNetCore.apk
  4) Test      unit tests + install on device/emulator + push game data + launch the full client + screenshot
EOF
    read -r -p "Choose steps (several allowed, separated by spaces, e.g. 1 2 3 4): " -a steps || true
fi
[ ${#steps[@]} -gt 0 ] || die "No steps chosen"
for step in "${steps[@]}"; do
    run_step "$step"
done

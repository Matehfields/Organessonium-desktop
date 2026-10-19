#!/usr/bin/env bash
set -euo pipefail

# Run inside the portable UCRT64 environment, not Git Bash or a system-wide shell.
[[ ${MSYSTEM:-} == UCRT64 ]] || { echo 'UCRT64 is required.' >&2; exit 1; }
org_work=$(pwd)
org_script=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
org_sources="$org_work/sources"
org_prefix="$org_work/prefix"
org_jobs=${ORG_MEDIA_JOBS:-4}
mkdir -p "$org_sources" "$org_prefix" "$org_work/compiled"
export PKG_CONFIG_PATH="$org_prefix/lib/pkgconfig:/ucrt64/lib/pkgconfig"
export CMAKE_PREFIX_PATH="$org_prefix;/ucrt64"
export GIT_TERMINAL_PROMPT=0
# The core source is an archive. Do not accidentally report the enclosing app
# repository's Git revision as the FFmpeg revision.
export revision=N-126168-gb16b5f2a01

checkout_source() {
    local name=$1 repo=$2 commit=$3
    if [[ ! -d "$org_sources/$name/.git" ]]; then
        git init -q "$org_sources/$name"
        git -C "$org_sources/$name" -c protocol.file.allow=never fetch --depth 1 "$repo" "$commit"
        git -C "$org_sources/$name" checkout -q --detach FETCH_HEAD
        git -C "$org_sources/$name" -c protocol.file.allow=never submodule update --init --recursive --depth 1
    fi
    [[ $(git -C "$org_sources/$name" rev-parse HEAD) == "$commit" ]] || { echo "Source identity mismatch: $name" >&2; exit 1; }
}

build_cmake() {
    local name=$1; shift
    cmake -S "$org_sources/$name" -B "$org_work/compiled/$name" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$(cygpath -am "$org_prefix")" \
        -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "$@"
    cmake --build "$org_work/compiled/$name" --parallel "$org_jobs"
    cmake --install "$org_work/compiled/$name"
}

# These revisions and compatibility adaptations are from the MIT BtbN recipes
# already used by Organessonium. No moving branch is used as a source identity.
checkout_source davs2 https://github.com/pkuvcl/davs2.git b41cf117452e2d73d827f02d3e30aa20f1c721ac
checkout_source uavs3d https://github.com/uavs3/uavs3d.git 0e20d2c291853f196c68922a264bcd8471d75b68
checkout_source xavs2 https://github.com/pkuvcl/xavs2.git eae1e8b9d12468059bdd7dee893508e470fa83d8
checkout_source vvenc https://github.com/fraunhoferhhi/vvenc.git 0f2e874451d6b194615e5dfefdc96796a7da00f4
checkout_source lcevcdec https://github.com/v-novaltd/LCEVCdec.git a254bd474649e5dcd8182689ac414420bfe8d8c3
checkout_source avisynth https://github.com/AviSynth/AviSynthPlus.git de1d1fc0d69e6d308235491b24b8e9b26ac023ff
checkout_source openapv https://github.com/AcademySoftwareFoundation/openapv.git d625af974550427e638574db61c270fe7f8c5a73

# Upstream version generators count the pinned history through origin/master.
# Keep that local ref pinned too; do not resolve a moving remote branch.
for org_entry in \
    'davs2 https://github.com/pkuvcl/davs2.git b41cf117452e2d73d827f02d3e30aa20f1c721ac' \
    'xavs2 https://github.com/pkuvcl/xavs2.git eae1e8b9d12468059bdd7dee893508e470fa83d8' \
    'uavs3d https://github.com/uavs3/uavs3d.git 0e20d2c291853f196c68922a264bcd8471d75b68'; do
    read -r org_name org_repo org_commit <<< "$org_entry"
    if [[ $(git -C "$org_sources/$org_name" rev-parse --is-shallow-repository) == true ]]; then
        git -C "$org_sources/$org_name" fetch --unshallow "$org_repo" "$org_commit"
    fi
    git -C "$org_sources/$org_name" update-ref refs/remotes/origin/master "$org_commit"
done

if ! pkg-config --exists 'davs2 >= 1.6.0'; then
    (
        cd "$org_sources/davs2/build/linux"
        sed -i -e 's/EGIB/bss/g' -e 's/naidnePF/bss/g' configure
        ./configure --disable-cli --enable-pic --prefix="$(cygpath -am "$org_prefix")" --host=x86_64-w64-mingw32 --cross-prefix=
        make -j"$org_jobs"
        make install
    )
fi
if [[ ! -f "$org_prefix/lib/pkgconfig/xavs2.pc" ]] || grep -Eq '^Version:.*\.$' "$org_prefix/lib/pkgconfig/xavs2.pc"; then
    (
        cd "$org_sources/xavs2/build/linux"
        sed -i -e 's/EGIB/bss/g' -e 's/naidnePF/bss/g' configure
        ./configure --disable-cli --enable-static --enable-pic --disable-avs --disable-swscale --disable-lavf --disable-ffms \
            --disable-gpac --disable-lsmash --extra-asflags=-w-macro-params-legacy --extra-cflags=-Wno-error=incompatible-pointer-types \
            --prefix="$(cygpath -am "$org_prefix")" --host=x86_64-w64-mingw32 --cross-prefix=
        make -j"$org_jobs"
        make install
    )
fi
[[ -f "$org_prefix/lib/pkgconfig/uavs3d.pc" ]] || build_cmake uavs3d -DCOMPILE_10BIT=1
[[ -f "$org_prefix/lib/pkgconfig/libvvenc.pc" || -f "$org_prefix/lib/pkgconfig/vvenc.pc" ]] || \
    build_cmake vvenc -DVVENC_LIBRARY_ONLY=ON -DVVENC_ENABLE_WERROR=OFF -DVVENC_ENABLE_LINK_TIME_OPT=OFF -DEXTRALIBS=-lstdc++
[[ -f "$org_prefix/lib/pkgconfig/lcevc_dec.pc" ]] || build_cmake lcevcdec \
    -DVN_SDK_EXECUTABLES=OFF -DVN_SDK_SAMPLE_SOURCE=OFF -DVN_SDK_TRACING=OFF -DVN_SDK_METRICS=OFF \
    -DVN_SDK_SYSTEM_INSTALL=ON -DVN_SDK_PIPELINE_LEGACY=OFF -DVN_SDK_PIPELINE_VULKAN=OFF '-DPC_LIBS_PRIVATE=Libs.private: -lstdc++'
if [[ ! -f "$org_prefix/include/avisynth/avisynth_c.h" ]]; then
    git -C "$org_sources/avisynth" -c user.name='Organessonium build' -c user.email='build@example.invalid' \
        tag -a ffbuild -m 'FFbuild Version' 2>/dev/null || true
    build_cmake avisynth -DHEADERS_ONLY=ON
fi
if [[ ! -f "$org_prefix/lib/pkgconfig/oapv.pc" ]]; then
    # Disable only the upstream sample apps, never the library/codec.
    cp "$org_script/openapv-app-CMakeLists.txt" "$org_sources/openapv/app/CMakeLists.txt"
    build_cmake openapv -DOAPV_APP_STATIC_BUILD=ON -DENABLE_TESTS=OFF
    printf '\nLibs.private: -lm\nCflags.private: -DOAPV_STATIC_DEFINE\n' >> "$org_prefix/lib/pkgconfig/oapv.pc"
    cat "$org_script/openapv-compat.h" >> "$org_prefix/include/oapv/oapv.h"
fi

org_core="$org_sources/FFmpeg-b16b5f2a01f3c4f8c9a7769d7a35e8b193946d3e"
[[ -f "$org_core/configure" ]] || { echo 'Extract the verified FFmpeg source archive into sources/ first.' >&2; exit 1; }
mkdir -p "$org_work/compiled/ffmpeg"
cd "$org_work/compiled/ffmpeg"
mapfile -t org_config < "$org_script/ffmpeg-config.txt"
"$org_core/configure" "${org_config[@]}" \
    --prefix=/usr/local/organessonium-media --enable-static --disable-shared \
    --extra-cflags="-DLIBTWOLAME_STATIC -I$(cygpath -am "$org_prefix/include")" \
    --extra-ldflags="-pthread -L$(cygpath -am "$org_prefix/lib") -Wl,-Map,%" \
    --extra-version=organessonium-b16b5f2a01
make -j"$org_jobs" STRIP=strip ffmpeg.exe ffprobe.exe
./ffmpeg.exe -version
printf 'Controlled FFmpeg compiled at %s\n' "$(cygpath -am "$org_work/compiled/ffmpeg/ffmpeg.exe")"

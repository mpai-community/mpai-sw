#!/bin/bash
# PACKAGE MPAI-MAS FOR A LINUX SERVER. Run from the repository root, on the build
# machine (Git Bash on Windows, or Linux), with the .NET 10 SDK and the Models folder:
#
#   MAS/Service/linux/package.sh <folder> [install-root]
#   MPAI_GPU=1 MAS/Service/linux/package.sh <folder> [install-root]
#
# With MPAI_GPU=1 the Service carries ONNX Runtime for CUDA, so that the ONNX models run
# on an NVIDIA GPU where the server has one (with CUDA 13 and cuDNN 9), and on the CPU
# otherwise; without it they always run on the CPU. The language model (Ollama) uses the
# GPU either way; speech recognition (whisper.cpp) does if built with CUDA - once, on a
# build machine, by build-whisper.sh, whose folder MPAI_WHISPER_DIR names here; otherwise
# setup.sh builds it on the server (the CPU, or CUDA where the toolkit is there).
#
# <folder> receives everything the server needs, laid out as it will be installed
# under install-root (default /opt/mpai): the MAS Service and the browser client's
# host, published for linux-x64; the six Apps, their L3s and the schemas; the avatar
# and the client's workflow; the model files the Apps use - and nothing else from
# Models; the configuration, the settings, the systemd units, setup.sh and README.md.
# Copy <folder> to install-root on the server, then run setup.sh there.
set -e
out="${1:?the folder to package into}"
root="${2:-/opt/mpai}"
here="MAS/Service/linux"
[ -f "$here/package.sh" ] || { echo "Run from the repository root."; exit 1; }
mkdir -p "$out"

gpu=false; [ "${MPAI_GPU:-0}" = 1 ] && gpu=true
# MPAI_CUDA=12 (with MPAI_GPU=1): ONNX Runtime 1.26.0, the CUDA 12.8 build, for a server whose
# driver is older than 580 (550, say); without it, 1.27.0 and CUDA 13 (driver 580 or later).
cuda="${MPAI_CUDA:-13}"; ort=1.27.0; [ "$cuda" = 12 ] && ort=1.26.0
echo "== the MAS Service and the browser client's host, for linux-x64 (ONNX Runtime $ort: $([ $gpu = true ] && echo "CUDA $cuda, CPU fallback" || echo CPU))"
rm -rf "$out/service"
dotnet publish MAS/Service/src/MasService.csproj -c Release -r linux-x64 --self-contained false -o "$out/service" -v quiet -nologo -p:MpaiOnnxGpu=$gpu -p:MpaiOrtVersion=$ort
# setup.sh reads which CUDA this Service was built for.
if [ $gpu = true ]; then echo "$cuda" > "$out/cuda.version"; else rm -f "$out/cuda.version"; fi
dotnet publish UserAgent/Clients/Browser/Host/RcaWeb.Host.csproj -c Release -r linux-x64 --self-contained false -o "$out/client" -v quiet -nologo

echo "== the Apps, their L3s, the schemas; the avatar and the client's workflow"
mkdir -p "$out/Apps" "$out/UserAgent"
for app in MAD AMQ MAT MPD MAC ACR; do cp -r "Apps/$app" "$out/Apps/"; done
rm -rf "$out/AMDs" "$out/schemas"
cp -r AIMs/AMDs "$out/AMDs"
cp -r schemas "$out/schemas"
cp -r UserAgent/Assets UserAgent/Orchestration "$out/UserAgent/"
# What the Service answers before anyone connects (WarmUp.cs): a spoken question and a picture.
mkdir -p "$out/warmup"
cp Test/Data/question.wav "$out/warmup/question.wav"
cp Test/Data/red.jpg "$out/warmup/picture.jpg"

# MPAI_NO_MODELS=1: a package of the software without the model files (6.8 GB) - to be
# published; the Models folder is then copied to <install-root>/Models separately.
if [ "${MPAI_NO_MODELS:-0}" = 1 ]; then
    echo "== the models are left out (MPAI_NO_MODELS=1): copy the repository's Models folder to $root/Models"
else
    echo "== the models the Apps use"
    grep -o '"/opt/mpai/Models/[^"]*"' "$here/aim-settings.json" | tr -d '"' | sed 's#^/opt/mpai/##' | sort -u | while read -r model; do
        mkdir -p "$out/$(dirname "$model")"
        [ -f "$out/$model" ] && [ "$(stat -c %s "$out/$model")" = "$(stat -c %s "$model")" ] || cp "$model" "$out/$model"
    done
    du -sh "$out/Models"
fi

if [ -n "${MPAI_WHISPER_DIR:-}" ]; then
    echo "== whisper.cpp, built beforehand for CUDA (build-whisper.sh)"
    for f in whisper-cli whisper-server whisper.build whisper.arch; do
        [ -f "$MPAI_WHISPER_DIR/bin/$f" ] || { echo "No $MPAI_WHISPER_DIR/bin/$f: run build-whisper.sh first."; exit 1; }
    done
    mkdir -p "$out/bin"
    cp "$MPAI_WHISPER_DIR"/bin/whisper-cli "$MPAI_WHISPER_DIR"/bin/whisper-server "$MPAI_WHISPER_DIR"/bin/whisper.build "$MPAI_WHISPER_DIR"/bin/whisper.arch "$out/bin/"
    chmod +x "$out/bin/whisper-cli" "$out/bin/whisper-server"
fi

echo "== the configuration, for $root"
for f in mas-server.json aim-settings.json; do sed "s#/opt/mpai#$root#g" "$here/$f" > "$out/$f"; done
mkdir -p "$out/systemd"
for f in "$here"/systemd/*.service; do sed "s#/opt/mpai#$root#g" "$f" > "$out/systemd/$(basename "$f")"; done
cp "$here/setup.sh" "$here/build-whisper.sh" "$here/run.sh" "$here/pod-boot.sh" "$here/README.md" "$out/"
chmod +x "$out/setup.sh" "$out/build-whisper.sh" "$out/run.sh" "$out/pod-boot.sh" "$out/service/MasService" "$out/client/RcaWeb.Host" 2> /dev/null || true
echo "Packaged in $out, for $root. Copy it there on the server, and run setup.sh."

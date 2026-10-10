#!/bin/bash
# SET UP MPAI-MAS ON A LINUX SERVER, in the folder package.sh made, once it is in
# its install root. Run as the user that will run the Services (not root):
#
#   ./setup.sh
#
# It checks the system's packages, then puts in <root>/bin the programs the AIMs call
# - Piper 1.2.0, whisper.cpp 1.9.3 built here - installs Ollama with llama3.2:3b in
# <root>/ollama, writes the SHA-256 of whisper-cli into aim-settings.json, and makes a
# self-signed certificate in <root>/tls if none is there. What is already in place is
# left as it is.
set -e
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"

echo "== the system's packages"
# A whisper.cpp built beforehand with CUDA (build-whisper.sh, brought by package.sh) is not
# built again here, and needs no compiler on this server.
prebuilt=""; [ "$(cat bin/whisper.build 2> /dev/null)" = cuda-prebuilt ] && prebuilt=1
missing=""
for c in espeak-ng zstd curl openssl; do command -v "$c" > /dev/null || missing="$missing $c"; done
[ -n "$prebuilt" ] || for c in g++ cmake; do command -v "$c" > /dev/null || missing="$missing $c"; done
dotnet --list-runtimes 2> /dev/null | grep -q "Microsoft.AspNetCore.App 10\." || missing="$missing aspnetcore-runtime-10.0"
if [ -n "$missing" ]; then
    echo "Missing:$missing. As root (Ubuntu):"
    echo "  apt-get install -y espeak-ng zstd g++ cmake curl openssl aspnetcore-runtime-10.0"
    exit 1
fi

echo "== the GPU"
# An NVIDIA GPU is used only if its driver is installed: Ollama then runs the language
# model on it; whisper.cpp does if built with CUDA (below); the ONNX models do if the
# Service was packaged with MPAI_GPU=1 and CUDA 13 and cuDNN 9 are installed. A cloud
# image usually comes without the driver - and then everything runs on the CPU, slowly,
# with no error anywhere.
gpu=""
if command -v nvidia-smi > /dev/null && nvidia-smi > /dev/null 2>&1; then
    gpu=$(nvidia-smi --query-gpu=name,memory.total --format=csv,noheader | head -1)
    echo "NVIDIA GPU: $gpu"
elif lspci 2> /dev/null | grep -qi nvidia || [ -d /proc/driver/nvidia ]; then
    echo "WARNING: an NVIDIA GPU is present but its driver is not working - everything will run on the CPU."
    echo "  As root (Ubuntu): apt-get install -y ubuntu-drivers-common && ubuntu-drivers install && reboot"
else
    echo "No NVIDIA GPU: everything runs on the CPU."
fi
nvcc=$(command -v nvcc || ls /usr/local/cuda/bin/nvcc 2> /dev/null || true)
if [ -n "$gpu" ]; then
    if [ -n "$prebuilt" ]; then echo "whisper.cpp: built beforehand for CUDA ($(cat bin/whisper.arch 2> /dev/null || echo '?')): no toolkit needed here"
    elif [ -n "$nvcc" ]; then echo "CUDA toolkit: $nvcc"
    else echo "No CUDA toolkit (nvcc): whisper.cpp will be built for the CPU. Better: build it once with build-whisper.sh on a build machine and package it (README). Or, as root (Ubuntu): apt-get install -y nvidia-cuda-toolkit"
    fi
    if [ -f service/libonnxruntime_providers_cuda.so ]; then
        # The CUDA the Service was built for (package.sh: MPAI_CUDA, 13 by default).
        cuv=$(cat cuda.version 2> /dev/null || echo 13)
        if [ "$cuv" = 12 ]; then pk="cuda-libraries-12-8 libcudnn9-cuda-12"; need="driver 525 or later (CUDA 12.8 libraries by minor-version compatibility)"
        else pk="cuda-libraries-13-0 libcudnn9-cuda-13"; need="driver 580 or later"; fi
        ldconfig -p | grep -q 'libcudnn.so.9' && ldconfig -p | grep -q "libcublasLt.so.$cuv" \
            && echo "CUDA $cuv and cuDNN 9: present - the ONNX models will run on the GPU" \
            || { echo "WARNING: the Service has ONNX Runtime for CUDA $cuv but CUDA $cuv / cuDNN 9 libraries are missing - the ONNX models will run on the CPU."
                 echo "  As root (Ubuntu), from NVIDIA's apt repository: apt-get install -y $pk   (and $need)"; }
    else
        echo "The Service was packaged for the CPU (package.sh without MPAI_GPU=1): the ONNX models run on the CPU"
    fi
fi

mkdir -p bin src
echo "== Piper 1.2.0 (TTS)"
if [ ! -x bin/piper/piper ]; then
    [ -f src/piper_linux_x86_64.tar.gz ] || curl -sSL -o src/piper_linux_x86_64.tar.gz https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz
    tar xzf src/piper_linux_x86_64.tar.gz -C bin
fi
bin/piper/piper --version

echo "== whisper.cpp 1.9.3 (ASR), built here, one file"
# whisper-server keeps the model loaded between turns; whisper-cli is the fallback.
# Built with CUDA where there is a working GPU and the CUDA toolkit, for the CPU
# otherwise; bin/whisper.build says which, and a build of the other kind is redone.
want=cpu; [ -n "$gpu" ] && [ -n "$nvcc" ] && want=cuda
have=$(cat bin/whisper.build 2> /dev/null || echo cpu)
if [ -n "$prebuilt" ]; then
    # Never rebuilt here, whatever this server has: a missing CUDA library is said, not hidden.
    [ -x bin/whisper-cli ] && [ -x bin/whisper-server ] || { echo "bin/whisper.build says cuda-prebuilt but whisper-cli / whisper-server are missing."; exit 1; }
    missinglib=$(ldd bin/whisper-server 2> /dev/null | grep 'not found' || true)
    [ -z "$missinglib" ] || { echo "WARNING: whisper-server cannot find: $missinglib"; echo "  As root (Ubuntu), from NVIDIA's apt repository: apt-get install -y cuda-libraries-$(cat cuda.version 2> /dev/null | sed 's/^12$/12-8/;s/^13$/13-0/' | grep . || echo 13-0)"; }
elif [ ! -x bin/whisper-cli ] || [ ! -x bin/whisper-server ] || [ "$have" != "$want" ]; then
    [ -f src/whisper.cpp-v1.9.3.tar.gz ] || curl -sSL -o src/whisper.cpp-v1.9.3.tar.gz https://codeload.github.com/ggml-org/whisper.cpp/tar.gz/refs/tags/v1.9.3
    tar xzf src/whisper.cpp-v1.9.3.tar.gz -C src
    flags=""
    [ "$want" = cuda ] && flags="-DGGML_CUDA=1 -DCMAKE_CUDA_COMPILER=$nvcc -DCMAKE_CUDA_ARCHITECTURES=native"
    echo "building for the $want (this takes a few minutes with CUDA)"
    (cd src/whisper.cpp-1.9.3 && rm -rf "build-$want" \
        && cmake -B "build-$want" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DWHISPER_BUILD_TESTS=OFF $flags > /dev/null \
        && cmake --build "build-$want" -j"$(nproc)" --target whisper-cli whisper-server > /dev/null)
    cp "src/whisper.cpp-1.9.3/build-$want/bin/whisper-cli" bin/whisper-cli
    cp "src/whisper.cpp-1.9.3/build-$want/bin/whisper-server" bin/whisper-server
    echo "$want" > bin/whisper.build
fi
echo "whisper.cpp built for the $(cat bin/whisper.build 2> /dev/null || echo cpu)"
hash=$(sha256sum bin/whisper-cli | cut -d' ' -f1 | tr a-f A-F)
sed -i "s#\"SHA256:ExecutablePath\": \"[^\"]*\"#\"SHA256:ExecutablePath\": \"$hash\"#" aim-settings.json
echo "whisper-cli $hash, written into aim-settings.json"
hash=$(sha256sum bin/whisper-server | cut -d' ' -f1 | tr a-f A-F)
sed -i "s#\"SHA256:ServerPath\": \"[^\"]*\"#\"SHA256:ServerPath\": \"$hash\"#" aim-settings.json
echo "whisper-server $hash, written into aim-settings.json"

echo "== Ollama 0.34.4 and llama3.2:3b (EDP, in MAD and MPD)"
if [ ! -x ollama/bin/ollama ]; then
    [ -f src/ollama-linux-amd64.tar.zst ] || curl -sSL -o src/ollama-linux-amd64.tar.zst https://github.com/ollama/ollama/releases/download/v0.34.4/ollama-linux-amd64.tar.zst
    mkdir -p ollama && tar --zstd -xf src/ollama-linux-amd64.tar.zst -C ollama
fi
url=$(grep -o '"OllamaUrl": "[^"]*"' aim-settings.json | cut -d'"' -f4)
export OLLAMA_HOST="${url#http://}" OLLAMA_MODELS="$root/ollama-models"
started=""
if ! curl -s --max-time 2 "$url/api/tags" > /dev/null; then
    ollama/bin/ollama serve > /dev/null 2>&1 & started=$!
    for i in $(seq 1 30); do curl -s --max-time 2 "$url/api/tags" > /dev/null && break; sleep 1; done
fi
ollama/bin/ollama list | grep -q "llama3.2:3b" || ollama/bin/ollama pull llama3.2:3b
ollama/bin/ollama list | grep "llama3.2:3b"
[ -n "$started" ] && kill "$started"

echo "== the certificate of the browser client's host"
if [ ! -f tls/cert.pem ]; then
    mkdir -p tls
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout tls/key.pem -out tls/cert.pem -days 365 \
        -subj "/CN=$(hostname -f)" -addext "subjectAltName=DNS:$(hostname -f),DNS:localhost" 2> /dev/null
    echo "A self-signed certificate for $(hostname -f): replace tls/cert.pem and tls/key.pem with one browsers trust."
fi
echo "Set up in $root. Next: the systemd units (README.md) - or, in a container without systemd, ./run.sh start."

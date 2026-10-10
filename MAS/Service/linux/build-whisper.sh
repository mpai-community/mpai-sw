#!/bin/bash
# BUILD whisper.cpp 1.9.3 WITH CUDA, ONCE, ON A BUILD MACHINE - not on every server.
# The CUDA build of whisper.cpp is heavy (nvcc compiles the GGML kernels, one process per
# core, each large): a server of modest cores and memory cannot finish it. Build it here,
# for the GPU the servers have, and hand the result to package.sh:
#
#   MAS/Service/linux/build-whisper.sh <out-folder> [cuda-architectures]
#   MPAI_WHISPER_DIR=<out-folder> MAS/Service/linux/package.sh <folder> [install-root]
#
# cuda-architectures: the compute capability of the GPU the servers have - one is much
# lighter to build than all: 75 T4, 80 A100, 86 A10G / RTX A4000-A6000 / RTX 3090,
# 89 L4 / L40S / RTX 4000 Ada / RTX 4080-4090, 90 H100, 120 Blackwell (RTX PRO, RTX 50xx;
# CUDA 12.8 or later); several as "80;86". Default
# "native": the GPU of this machine (nvidia-smi must find one).
#
# The build machine needs the CUDA toolkit (nvcc), of the same CUDA as the servers' ONNX
# Runtime (package.sh: CUDA 13 by default, CUDA 12 with MPAI_CUDA=12), so that one set of
# CUDA libraries serves both - g++, cmake, curl. For servers with an NVIDIA driver older
# than 580 (550, say): apt cuda-toolkit-12-8, the same 12.8 as the libraries (cuda-libraries-12-8:
# two CUDA 12 versions side by side leave the loader's choice to chance), not 13.
# A server then needs only the CUDA *libraries* (apt: cuda-libraries-13-0) and the NVIDIA
# driver: no nvcc, no g++, no cmake. <out-folder>/bin receives whisper-cli, whisper-server
# and whisper.build ("cuda-prebuilt"), which tells setup.sh not to build again.
set -e
out="${1:?the folder to build into}"
arch="${2:-native}"

nvcc=$(command -v nvcc || ls /usr/local/cuda/bin/nvcc 2> /dev/null || true)
[ -n "$nvcc" ] || { echo "No CUDA toolkit (nvcc) here: this is the machine that needs it."; exit 1; }
missing=""; for c in g++ cmake curl; do command -v "$c" > /dev/null || missing="$missing $c"; done
[ -z "$missing" ] || { echo "Missing:$missing (apt-get install -y g++ cmake curl)"; exit 1; }
if [ "$arch" = native ] && ! { command -v nvidia-smi > /dev/null && nvidia-smi > /dev/null 2>&1; }; then
    echo "No GPU here to build for: give the architectures, e.g. $0 $out 86"; exit 1
fi

# One nvcc per ~4 GB of memory, at most one per core: what a build machine of 8 cores and
# 32 GB can finish.
# In a container the machine's memory and cores are not the container's: its cgroup limits
# (memory.max, cpu.max) are what it can use. MPAI_BUILD_JOBS sets the number outright.
mem_gb=$(awk '/MemTotal/ {printf "%d", $2/1048576}' /proc/meminfo)
lim=$(cat /sys/fs/cgroup/memory.max 2> /dev/null || echo max)
[ "$lim" != max ] && [ "$lim" -gt 0 ] 2> /dev/null && [ $(( lim / 1073741824 )) -lt "$mem_gb" ] && mem_gb=$(( lim / 1073741824 ))
cores=$(nproc)
read -r quota period < /sys/fs/cgroup/cpu.max 2> /dev/null || quota=max
if [ "${quota:-max}" != max ] && [ "${period:-0}" -gt 0 ] 2> /dev/null; then
    q=$(( (quota + period - 1) / period )); [ "$q" -ge 1 ] && [ "$q" -lt "$cores" ] && cores=$q
fi
jobs=$(( mem_gb / 4 )); [ "$jobs" -ge 1 ] || jobs=1
[ "$jobs" -le "$cores" ] || jobs=$cores
jobs="${MPAI_BUILD_JOBS:-$jobs}"

work="$out/src"; mkdir -p "$out/bin" "$work"
[ -f "$work/whisper.cpp-v1.9.3.tar.gz" ] || curl -sSL -o "$work/whisper.cpp-v1.9.3.tar.gz" https://codeload.github.com/ggml-org/whisper.cpp/tar.gz/refs/tags/v1.9.3
tar xzf "$work/whisper.cpp-v1.9.3.tar.gz" -C "$work"

echo "building for CUDA architectures '$arch' with $jobs parallel jobs ($cores cores, $mem_gb GB); $("$nvcc" --version | tail -1)"
(cd "$work/whisper.cpp-1.9.3" && rm -rf build-cuda \
    && cmake -B build-cuda -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DWHISPER_BUILD_TESTS=OFF \
             -DGGML_CUDA=1 -DCMAKE_CUDA_COMPILER="$nvcc" -DCMAKE_CUDA_ARCHITECTURES="$arch" > /dev/null \
    && cmake --build build-cuda -j"$jobs" --target whisper-cli whisper-server)
cp "$work/whisper.cpp-1.9.3/build-cuda/bin/whisper-cli" "$work/whisper.cpp-1.9.3/build-cuda/bin/whisper-server" "$out/bin/"
echo "cuda-prebuilt" > "$out/bin/whisper.build"
echo "$arch" > "$out/bin/whisper.arch"
echo "Built in $out/bin (CUDA architectures: $arch). Next: MPAI_WHISPER_DIR=$out package.sh ..."
ldd "$out/bin/whisper-cli" | grep -E 'cuda|cublas' || true

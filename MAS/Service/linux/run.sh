#!/bin/bash
# RUN MPAI-MAS WITHOUT SYSTEMD - in a Docker container, a RunPod pod: the three programs the
# systemd units start, started here directly, with their logs and process ids under this folder.
# After setup.sh, as the user that owns the folder (root, in a pod):
#
#   ./run.sh start      Ollama, then the MAS Service (it warms up, up to a minute or two), then the client's host
#   ./run.sh status     what runs, where the models run (GPU or CPU), the Apps the Service offers
#   ./run.sh logs [ollama|service|client]
#   ./run.sh stop
#
# The client's host listens on plain HTTP, port 8880 (MPAI_CLIENT_PORT): RunPod's proxy gives it
# https://<pod-id>-8880.proxy.runpod.net, which browsers accept - and microphones need https.
# Expose port 8880 as an HTTP port of the pod. To have the host do the https itself, as the
# systemd unit does (certificate in tls/), MPAI_CLIENT_TLS=1 ./run.sh start: port 443 (MPAI_CLIENT_PORT).
# MPAI_ONNX_DEVICE (auto, cuda, cpu) is passed on to the Service; auto is the default.
set -u
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"
mkdir -p logs run
port="${MPAI_CLIENT_PORT:-8880}"
[ "${MPAI_CLIENT_TLS:-0}" = 1 ] && port="${MPAI_CLIENT_PORT:-443}"

# The GPU's compiled kernels are kept here, with the install, not in ~/.nv of the container: on a new
# GPU generation the first run compiles them (about a minute, once) and a container's own disk may be
# lost with the pod. 4 GB is the most it keeps (the default, 256 MB, would drop some).
mkdir -p "$root/cuda-cache"
export CUDA_CACHE_PATH="$root/cuda-cache" CUDA_CACHE_MAXSIZE=4294967296

alive() { [ -f "run/$1.pid" ] && kill -0 "$(cat "run/$1.pid")" 2> /dev/null; }
waitfor() { # url seconds
    for _ in $(seq 1 "$2"); do curl -sk --max-time 2 "$1" > /dev/null && return 0; sleep 1; done; return 1
}

start() {
    if alive ollama; then echo "Ollama: already running ($(cat run/ollama.pid))"; else
        echo "Ollama: starting"
        OLLAMA_HOST=127.0.0.1:11434 OLLAMA_MODELS="$root/ollama-models" OLLAMA_KEEP_ALIVE=-1 \
            nohup ollama/bin/ollama serve > logs/ollama.log 2>&1 &
        echo $! > run/ollama.pid
        waitfor http://127.0.0.1:11434/api/tags 60 || { echo "Ollama did not answer: see logs/ollama.log"; exit 1; }
    fi
    if alive service; then echo "MAS Service: already running ($(cat run/service.pid))"; else
        echo "MAS Service: starting (it answers a few turns of each App first)"
        [ -f service-token.env ] && { set -a; . ./service-token.env; set +a; }
        MPAI_ONNX_DEVICE="${MPAI_ONNX_DEVICE:-auto}" nohup service/MasService mas-server.json > logs/service.log 2>&1 &
        echo $! > run/service.pid
        waitfor http://127.0.0.1:5005/MPAI/AIFU/Apps 300 || { echo "The Service did not answer in 5 minutes: see logs/service.log"; exit 1; }
    fi
    if alive client; then echo "Client's host: already running ($(cat run/client.pid))"; else
        echo "Client's host: starting on port $port"
        args=(--Service http://127.0.0.1:5005/)
        if [ "${MPAI_CLIENT_TLS:-0}" = 1 ]; then
            args+=(--Urls "https://0.0.0.0:$port" --Kestrel:Certificates:Default:Path="$root/tls/cert.pem" --Kestrel:Certificates:Default:KeyPath="$root/tls/key.pem")
        else args+=(--Urls "http://0.0.0.0:$port"); fi
        # exec: the process id kept is the client's own, not that of a wrapper shell (stop would miss it).
        (cd client && ASPNETCORE_ENVIRONMENT=Production exec nohup ./RcaWeb.Host "${args[@]}" > "$root/logs/client.log" 2>&1) &
        echo $! > run/client.pid
        scheme=http; [ "${MPAI_CLIENT_TLS:-0}" = 1 ] && scheme=https
        waitfor "$scheme://127.0.0.1:$port/MPAI/AIFU/Apps" 60 || { echo "The client's host did not answer: see logs/client.log"; exit 1; }
    fi
    echo "Up. Locally: http://127.0.0.1:$port/   In a pod: https://<pod-id>-$port.proxy.runpod.net/"
    status
}

stop() {
    for p in client service ollama; do
        if alive "$p"; then echo "stopping $p ($(cat "run/$p.pid"))"; kill "$(cat "run/$p.pid")" 2> /dev/null; fi
        rm -f "run/$p.pid"
    done
}

status() {
    for p in ollama service client; do alive "$p" && echo "$p: running ($(cat "run/$p.pid"))" || echo "$p: not running"; done
    echo "-- where the models run"
    grep -h '\[ONNX\]' logs/service.log 2> /dev/null | sort -u | head -20
    OLLAMA_HOST=127.0.0.1:11434 ollama/bin/ollama ps 2> /dev/null
    command -v nvidia-smi > /dev/null && nvidia-smi --query-gpu=name,driver_version,memory.used,memory.total --format=csv,noheader
    echo "-- warm-up"
    grep -h 'warm-up' logs/service.log 2> /dev/null
}

case "${1:-}" in
    start) start ;;
    stop) stop ;;
    status) status ;;
    logs) tail -n 50 -f "logs/${2:-service}.log" ;;
    *) echo "usage: $0 start | stop | status | logs [ollama|service|client]"; exit 1 ;;
esac

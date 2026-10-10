#!/bin/bash
# AFTER A POD (CONTAINER) RESTART: a container's own disk is reset when it is stopped and started
# again - the /opt/mpai link and the packages installed with apt are gone, the programs are not
# running; only the volume (/workspace) is kept. This puts back what is lost and starts MPAI-MAS:
#
#   /workspace/mpai/pod-boot.sh
#
# It is safe to run at any time (what is there is left alone), and can be the pod's start command.
# The CUDA libraries and cuDNN come with the pod's image, not from here.
set -u
vol="${MPAI_VOLUME_DIR:-/workspace/mpai}"
[ -d "$vol" ] || { echo "$vol is not there: is the volume mounted? (MPAI_VOLUME_DIR names the folder)"; exit 1; }
[ -e /opt/mpai ] || { mkdir -p /opt && ln -sfn "$vol" /opt/mpai && echo "linked /opt/mpai -> $vol"; }

export DEBIAN_FRONTEND=noninteractive
need=""
for c in espeak-ng zstd curl openssl; do command -v "$c" > /dev/null || need="$need $c"; done
dotnet --list-runtimes 2> /dev/null | grep -q 'Microsoft.AspNetCore.App 10\.' || need="$need aspnetcore-runtime-10.0"
if [ -n "$need" ]; then
    echo "installing:$need"
    apt-get update -qq && apt-get install -y -qq $need || { echo "apt could not install:$need"; exit 1; }
fi

cd /opt/mpai && exec ./run.sh start

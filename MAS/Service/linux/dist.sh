#!/bin/bash
# DISTRIBUTE MPAI-MAS FOR A LINUX SERVER (M3248 3.3): a folder package.sh made, or
# one installed and set up, cut into parts a release can carry, with their SHA-256.
#
#   dist.sh <folder> <out> [install-root]
#
# Into <out>: thalia-linux.tar.part00, part01, ... of at most 1,900 MB each (a
# GitHub release takes files up to 2 GB) and SHA256SUMS. The archive holds one
# folder, mpai, laid out for install-root (default /opt/mpai): the paths of the
# configuration, the settings and the systemd units set for it. Left out: logs,
# downloaded sources, certificates and their keys, the gallery - a server makes its
# own certificate (setup.sh) and starts with an empty gallery.
#
# On the server, in the folder with the parts:
#   sha256sum -c SHA256SUMS
#   cat thalia-linux.tar.part* | sudo tar xf - -C "$(dirname <install-root>)"
#   sudo chown -R "$USER" <install-root> && cd <install-root> && ./setup.sh
set -e
from="${1:?the folder to distribute}"
out="${2:?the folder the parts go into}"
root="${3:-/opt/mpai}"
from="$(cd "$from" && pwd)"
[ -f "$from/mas-server.json" ] || { echo "$from has no mas-server.json: not a package."; exit 1; }
[ "$(basename "$root")" = "mpai" ] || { echo "The install root must end in /mpai: the archive holds one folder, mpai."; exit 1; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"

# Where the folder was set up for: its Apps directory, less /Apps.
was=$(grep -o '"AppDirectory": *"[^"]*"' "$from/mas-server.json" | cut -d'"' -f4 | sed 's#/Apps$##')
echo "== paths: $was -> $root"
# A copy of hard links beside the folder - no space, no time - in which the
# configuration is rewritten (a new file, the original untouched) and what is left
# out removed.
stage=$(mktemp -d -p "$(dirname "$from")")
trap 'rm -rf "$stage"' EXIT
cp -al "$from" "$stage/mpai"
cd "$stage/mpai"
rm -rf src tls tls-dev gallery start-wsl.sh mas-server-*.json
find . -name '*.log' -delete
for f in mas-server.json aim-settings.json systemd/*.service; do
    sed "s#$was#$root#g" "$from/$f" > "$f.new" && rm "$f" && mv "$f.new" "$f"
done
! grep -rl "$was" mas-server.json aim-settings.json systemd || { echo "Paths of $was left."; exit 1; }

echo "== the archive, in parts of 1,900 MB"
rm -f "$out"/thalia-linux.tar.part* "$out/SHA256SUMS"
tar cf - -C "$stage" mpai | split -b 1900M -d - "$out/thalia-linux.tar.part"
(cd "$out" && sha256sum thalia-linux.tar.part* > SHA256SUMS)
ls -l --block-size=M "$out"
echo "Distributed in $out, for $root."

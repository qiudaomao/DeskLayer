#!/bin/bash
#
# Spike 5: the shared conformance suite through the real Jint runtime on
# Linux. Publishes the existing win runner for linux-x64 and executes it on
# a Linux host over ssh (any headless box — no display needed).
#
# Usage: run.sh [ssh-host]     (default: the minipve docker LXC via pct)
#
# First result (2026-08-23, Debian LXC on minipve): ALL GREEN — 50 fixtures.
#
set -euo pipefail
cd "$(dirname "$0")/../../.."

HOST="${1:-}"
OUT=/tmp/dl-conf-linux

dotnet publish win/src/DeskLayer.Conformance -c Release -r linux-x64 \
    --self-contained -p:PublishSingleFile=true -o "$OUT" | tail -1

# COPYFILE_DISABLE: without it, macOS tar embeds AppleDouble ._*.js files
# that the runner picks up as (unparseable) fixtures.
COPYFILE_DISABLE=1 tar czf /tmp/dl-fixtures.tgz shared/conformance
tar czf /tmp/dl-conf.tgz -C "$OUT" DeskLayer.Conformance

if [ -n "$HOST" ]; then
    scp -q /tmp/dl-conf.tgz /tmp/dl-fixtures.tgz "$HOST":/tmp/
    ssh "$HOST" 'cd /tmp && rm -rf dl-conf && mkdir dl-conf && cd dl-conf \
        && tar xzf ../dl-conf.tgz && tar xzf ../dl-fixtures.tgz 2>/dev/null \
        && chmod +x DeskLayer.Conformance \
        && ./DeskLayer.Conformance shared/conformance | tail -3'
else
    scp -q /tmp/dl-conf.tgz /tmp/dl-fixtures.tgz minipve:/tmp/
    ssh minipve 'pct push 100 /tmp/dl-conf.tgz /tmp/dl-conf.tgz >/dev/null
        pct push 100 /tmp/dl-fixtures.tgz /tmp/dl-fixtures.tgz >/dev/null
        pct exec 100 -- bash -c "cd /tmp && rm -rf dl-conf && mkdir dl-conf && cd dl-conf \
            && tar xzf ../dl-conf.tgz && tar xzf ../dl-fixtures.tgz 2>/dev/null \
            && chmod +x DeskLayer.Conformance \
            && ./DeskLayer.Conformance shared/conformance | tail -3"'
fi

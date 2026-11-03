#!/bin/bash
set -euo pipefail

# Usage: ./scripts/synthesize-speech.sh "Hello there" [output.pcm] [voice]
if [[ $# -lt 1 || $# -gt 3 ]]; then
    echo "Usage: $0 TEXT [OUTPUT.pcm] [VOICE]" >&2
    exit 1
fi
if [[ $(uname -s) != Darwin ]]; then
    echo "This script requires macOS and its say command." >&2
    exit 1
fi
command -v ffmpeg >/dev/null || { echo "Install FFmpeg first: brew install ffmpeg" >&2; exit 1; }
repo_dir=$(cd -- "$(dirname -- "$0")/.." && pwd)
output=${2:-"$repo_dir/media/speech.pcm"}
mkdir -p -- "$(dirname -- "$output")"
temp_dir=$(mktemp -d)
trap 'rm -rf -- "$temp_dir"' EXIT
printf '%s\n' "$1" > "$temp_dir/text.txt"
if [[ $# -eq 3 ]]; then
    say -v "$3" -f "$temp_dir/text.txt" -o "$temp_dir/speech.aiff"
else
    say -f "$temp_dir/text.txt" -o "$temp_dir/speech.aiff"
fi
# Explicit conversion removes the container header and normalizes rate/channels.
ffmpeg -hide_banner -loglevel error -y -i "$temp_dir/speech.aiff" \
    -ar 16000 -ac 1 -c:a pcm_s16le -f s16le "$temp_dir/speech.pcm"
# Replace the file only after successful synthesis and conversion.
cp -- "$temp_dir/speech.pcm" "$output.tmp"
mv -f -- "$output.tmp" "$output"
printf 'Created %s (16 kHz, mono, signed 16-bit little-endian PCM)\n' "$output"

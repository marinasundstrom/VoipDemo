#!/usr/bin/env python3
"""Create an original short melody as raw 16 kHz mono s16le PCM."""
import math
import struct
from pathlib import Path

output = Path(__file__).resolve().parents[1] / "media" / "music.pcm"
output.parent.mkdir(exist_ok=True)
notes = [60, 64, 67, 72, 71, 67, 65, 62, 60, 64, 67, 69, 67, 64, 62, 60]
samples = bytearray()
for note in notes:
    frequency = 440 * 2 ** ((note - 69) / 12)
    for i in range(8000):
        t = i / 16000
        envelope = min(1, i / 160) * math.exp(-5 * t) * min(1, (7999 - i) / 160)
        value = envelope * (5000 * math.sin(2 * math.pi * frequency * t)
                            + 1200 * math.sin(4 * math.pi * frequency * t))
        samples.extend(struct.pack('<h', int(value)))
output.write_bytes(samples)
print(f"Created {output} (8 seconds of original demo music)")

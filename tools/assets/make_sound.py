"""Builds assets/match-found.wav, the built-in alert sound, from the original Pixabay MP3.

    python make_sound.py <path to the original .mp3> [<output .wav>]

The original is "Successful accepted reward" by tithuh on Pixabay (item 523721). It is not in this
repository: the Pixabay licence does not allow distributing it on its own. Download it yourself.

What happens to it (the numbers below were chosen by ear):
- mixed from stereo to mono;
- up to DECAY_FROM seconds it is left exactly as it is;
- from there it is split into three bands that add back up to exactly the original (zero-phase
  filters), and each band loses an extra amount (DB_TOTAL) on top of its own natural decay: the high
  ringing goes furthest and the low tone carries the ending. Most of that happens early: the extra
  loss follows 1 - exp(-t / FRONT), so it drops fast after DECAY_FROM and then flattens: it fades
  more at the beginning than at the end;
- it ends at END seconds, with a cosine fade over the last FINAL seconds;
- it is stored as IMA ADPCM (4 bits a sample), which Windows decodes natively and the app plays
  with PlaySound (not .NET's SoundPlayer, which refuses anything but plain PCM).

With the same MP3 and these numbers the output is identical, byte for byte, to the file in assets/.
"""
import math
import struct
import sys

import miniaudio
import numpy as np
from scipy.signal import butter, sosfiltfilt

END = 2.2               # seconds
DECAY_FROM = 1.1        # the original is untouched before this
EASE = 0.06             # the extra decay eases in over this long, so it does not start with a click
FRONT = 0.55            # seconds: how quickly the extra loss happens (smaller = more of it right away)
FINAL = 0.3             # cosine fade at the very end
CROSS_LOW, CROSS_HIGH = 500.0, 2000.0                      # Hz, the band edges
DB_TOTAL = {'low': 18.5, 'mid': 36.0, 'high': 66.5}        # extra loss per band by the end, in dB


def shape(source):
    d = miniaudio.decode_file(source, output_format=miniaudio.SampleFormat.SIGNED16)
    rate, s = d.sample_rate, d.samples
    mono = [(s[i] + s[i + 1]) / 2.0 for i in range(0, len(s), 2)] if d.nchannels == 2 else [float(v) for v in s]
    n = int(END * rate)
    x = np.array(mono[:n])
    low1 = sosfiltfilt(butter(4, CROSS_LOW, 'low', fs=rate, output='sos'), x)
    low2 = sosfiltfilt(butter(4, CROSS_HIGH, 'low', fs=rate, output='sos'), x)
    parts = {'low': low1, 'mid': low2 - low1, 'high': x - low2}
    assert np.max(np.abs(parts['low'] + parts['mid'] + parts['high'] - x)) < 1e-6
    t = np.maximum(np.arange(n) / rate - DECAY_FROM, 0)
    eased = np.where(t < EASE, t * t / (2 * EASE), t - EASE / 2)
    span = END - DECAY_FROM - EASE / 2
    curve = (1 - np.exp(-eased / FRONT)) / (1 - np.exp(-span / FRONT))     # 0 at DECAY_FROM, 1 at END
    y = np.zeros(n)
    for name, band in parts.items():
        y += band * 10 ** (-DB_TOTAL[name] * curve / 20)
    f_final = int((END - FINAL) * rate)
    u = (np.arange(n) - f_final) / (n - f_final)
    y *= np.where(u > 0, 0.5 * (1 + np.cos(np.pi * np.clip(u, 0, 1))), 1.0)
    return rate, [max(-32768, min(32767, int(round(v)))) for v in y]


STEPS = [7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
         130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
         1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845,
         8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767]
INDEX = [-1, -1, -1, -1, 2, 4, 6, 8]


def encode_sample(sample, pred, idx):
    step = STEPS[idx]
    diff = sample - pred
    code = 0
    if diff < 0:
        code = 8
        diff = -diff
    delta = step >> 3
    if diff >= step:
        code |= 4; diff -= step; delta += step
    step >>= 1
    if diff >= step:
        code |= 2; diff -= step; delta += step
    step >>= 1
    if diff >= step:
        code |= 1; delta += step
    pred = pred - delta if code & 8 else pred + delta
    pred = max(-32768, min(32767, pred))
    idx = max(0, min(88, idx + INDEX[code & 7]))
    return code, pred, idx


def ima_adpcm_wav(rate, pcm, block=1024):
    spb = (block - 4) * 2 + 1   # samples per block: one in the header, two per data byte
    data = bytearray()
    idx = 0
    pos = 0
    while pos < len(pcm):
        chunk = pcm[pos:pos + spb]
        chunk += [0] * (spb - len(chunk))
        pred = chunk[0]
        data += struct.pack('<hBB', pred, idx, 0)
        codes = []
        for v in chunk[1:]:
            c, pred, idx = encode_sample(v, pred, idx)
            codes.append(c)
        for k in range(0, len(codes), 2):
            data.append(codes[k] | (codes[k + 1] << 4))
        pos += spb
    fmt = struct.pack('<HHIIHHHH', 0x11, 1, rate, rate * block // spb, block, 4, 2, spb)
    body = (b'WAVE' + b'fmt ' + struct.pack('<I', len(fmt)) + fmt + b'fact' + struct.pack('<I', 4) + struct.pack('<I', len(pcm))
            + b'data' + struct.pack('<I', len(data)) + bytes(data))
    return b'RIFF' + struct.pack('<I', len(body)) + body


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    out = sys.argv[2] if len(sys.argv) > 2 else 'match-found.wav'
    rate, pcm = shape(sys.argv[1])
    wav = ima_adpcm_wav(rate, pcm)
    with open(out, 'wb') as f:
        f.write(wav)
    print('%s: %.2f s, %d bytes' % (out, len(pcm) / rate, len(wav)))


if __name__ == '__main__':
    main()

"""Joins PNG images of the same size into one animated PNG that loops, a few seconds per image.

    python tools/screenshots/make_apng.py <out.png> <frame1.png> <frame2.png> ... [--seconds 3.5]

The README shows the settings tabs this way: GitHub cannot run a slideshow, but it shows an animated
PNG as it is. Viewers without APNG support show the first frame. Standard library only.
"""
import struct
import sys
import zlib

SIGNATURE = b'\x89PNG\r\n\x1a\n'


def chunks(data):
    if data[:8] != SIGNATURE:
        raise ValueError('not a PNG')
    pos = 8
    while pos < len(data):
        length, kind = struct.unpack('>I4s', data[pos:pos + 8])
        yield kind, data[pos + 8:pos + 8 + length]
        pos += 12 + length


def chunk(kind, body):
    return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xFFFFFFFF)


def main(argv):
    seconds = 3.5
    if '--seconds' in argv:
        i = argv.index('--seconds')
        seconds = float(argv[i + 1])
        del argv[i:i + 2]
    out, frames = argv[0], argv[1:]
    if len(frames) < 2:
        raise SystemExit(__doc__)

    header, images = None, []
    for path in frames:
        with open(path, 'rb') as f:
            parts = list(chunks(f.read()))
        ihdr = next(body for kind, body in parts if kind == b'IHDR')
        if header is None:
            header = ihdr
        elif ihdr != header:
            raise SystemExit(f'{path}: size or pixel format differs from {frames[0]}')
        images.append(b''.join(body for kind, body in parts if kind == b'IDAT'))

    width, height = struct.unpack('>II', header[:8])
    delay = (round(seconds * 100), 100)
    seq = 0
    data = SIGNATURE + chunk(b'IHDR', header) + chunk(b'acTL', struct.pack('>II', len(images), 0))
    for n, image in enumerate(images):
        # whole-image frames that replace the previous one (dispose none, blend source)
        data += chunk(b'fcTL', struct.pack('>IIIIIHHBB', seq, width, height, 0, 0, delay[0], delay[1], 0, 0))
        seq += 1
        if n == 0:
            data += chunk(b'IDAT', image)
        else:
            data += chunk(b'fdAT', struct.pack('>I', seq) + image)
            seq += 1
    data += chunk(b'IEND', b'')
    with open(out, 'wb') as f:
        f.write(data)
    print(f'{out}: {len(images)} frames, {seconds} s each, {len(data) // 1024} KB')


if __name__ == '__main__':
    main(sys.argv[1:])

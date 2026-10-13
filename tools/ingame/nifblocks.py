"""Print a NIF (20.2.0.7, SSE) file's blocks with offsets/sizes, and hex-dump blocks of a given type."""
import struct, sys

def read_nif(path):
    d = open(path, 'rb').read()
    p = d.index(b'\n') + 1                      # header string
    ver, = struct.unpack_from('<I', d, p); p += 4
    endian = d[p]; p += 1
    user_ver, nblocks = struct.unpack_from('<II', d, p); p += 8
    bs_ver, = struct.unpack_from('<I', d, p); p += 4
    for _ in range(3):                            # author, process script, export script (byte-length strings)
        n = d[p]; p += 1 + n
    if bs_ver >= 130: n = d[p]; p += 1 + n        # (FO4+) max filepath
    ntypes, = struct.unpack_from('<H', d, p); p += 2
    types = []
    for _ in range(ntypes):
        n, = struct.unpack_from('<I', d, p); p += 4
        types.append(d[p:p + n].decode()); p += n
    tidx = struct.unpack_from(f'<{nblocks}H', d, p); p += 2 * nblocks
    sizes = struct.unpack_from(f'<{nblocks}I', d, p); p += 4 * nblocks
    nstrings, maxlen = struct.unpack_from('<II', d, p); p += 8
    for _ in range(nstrings):
        n, = struct.unpack_from('<I', d, p); p += 4 + n
    ngroups, = struct.unpack_from('<I', d, p); p += 4 + 4 * ngroups
    blocks = []
    for i in range(nblocks):
        t = types[tidx[i] & 0x7FFF]
        blocks.append((i, t, p, sizes[i], d[p:p + sizes[i]]))
        p += sizes[i]
    return blocks

path, want = sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None
for i, t, off, size, data in read_nif(path):
    if want is None:
        print(f'[{i}] {t} @{off} size {size}')
    elif t == want:
        print(f'[{i}] {t} @{off} size {size}')
        for k in range(0, min(size, 400), 16):
            chunk = data[k:k + 16]
            print(f'  {k:04x}: {chunk.hex(" ")}   floats {[round(x, 4) for x in struct.unpack_from(f"<{len(chunk)//4}f", chunk)]}')
        break

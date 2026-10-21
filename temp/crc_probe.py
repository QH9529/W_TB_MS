import itertools

def crc_gen(data, poly, init, refin, refout, xorout):
    crc = init
    for b in data:
        if refin:
            b = int(f'{b:08b}'[::-1], 2)
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ poly) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    if refout:
        crc = int(f'{crc:016b}'[::-1], 2)
    return crc ^ xorout

ALGS = {
    'modbus':      (0x8005, 0xFFFF, True,  True,  0x0000),
    'ccitt-false': (0x1021, 0xFFFF, False, False, 0x0000),
    'xmodem':      (0x1021, 0x0000, False, False, 0x0000),
    'ibm/arc':     (0x8005, 0x0000, True,  True,  0x0000),
    'maxim':       (0x8005, 0x0000, True,  True,  0xFFFF),
    'usb':         (0x8005, 0xFFFF, True,  True,  0xFFFF),
    'ccitt-true':  (0x1021, 0x0000, True,  True,  0x0000),
    'x25':         (0x1021, 0xFFFF, True,  True,  0xFFFF),
}

samples = [
    'F1 FF 04 75 31 08 00 00 00 00 00 04 00 27 72 2D',
    'F1 51 04 75 31 00 04 4D 8E',
    'F1 FF 04 75 35 08 4D 53 2D 57 31 38 30 00 7C 0F',
    'F1 51 03 9C A9 00 01 8D FE',
    'F1 FF 03 9C A9 02 00 00 D1 FF',
    'F1 51 03 9E 47 00 09 ED B5',
]

for smp in samples:
    b = bytes(int(x, 16) for x in smp.split())
    found = []
    for name, p in ALGS.items():
        for start in range(0, 3):
            for end in range(start + 3, len(b) - 1):
                c = crc_gen(b[start:end], *p)
                tail_lo_first = (b[end] | b[end + 1] << 8)   # 低字节在前
                tail_hi_first = (b[end] << 8 | b[end + 1])   # 高字节在前
                if c == tail_lo_first:
                    found.append(f'{name} 起点={start} 覆盖{end}字节 低字节在前')
                if c == tail_hi_first:
                    found.append(f'{name} 起点={start} 覆盖{end}字节 高字节在前')
    print(smp)
    print('   ->', found if found else '无匹配')

import re, collections

path = r'D:/1-ORVIBO/4-Utility class/W_TB_MS/4G串口.txt'
lines = open(path, encoding='utf-8', errors='ignore').read().splitlines()

def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc

recs = []
for l in lines:
    m = re.match(r'\[(\d+):(\d+):(\d+)\.(\d+)\]', l)
    if not m:
        continue
    h, mi, s, ms = m.groups()
    t = int(h) * 3600000 + int(mi) * 60000 + int(s) * 1000 + int(ms)
    data = bytes(int(x, 16) for x in (l.split('◆')[-1] if '◆' in l else '').split())
    recs.append((t, data))

buf = bytearray()
for _, d in recs:
    buf += d

# 帧结构: F1 同步头 + (方向 0x51=4G请求 / 0xFF=主控应答) + 功能码 + 寄存器地址 + 长度 + CRC16(Modbus, 低字节在前)
#   请求: F1 51 FC ADDR_H ADDR_L CNT_H CNT_L CRC CRC      -> 9 字节
#   应答: F1 FF FC ADDR_H ADDR_L BC DATA[BC] CRC CRC      -> 8+BC 字节
i, ok, resync = 0, 0, 0
frames, kinds, funcs = [], collections.Counter(), collections.Counter()
while i < len(buf) - 3:
    if buf[i] != 0xF1 or buf[i + 1] not in (0x51, 0xFF) or buf[i + 2] not in (0x03, 0x04, 0x06, 0x10):
        i += 1
        continue
    n = 9 if buf[i + 1] == 0x51 else 8 + buf[i + 5]
    if i + n > len(buf):
        break
    f = buf[i:i + n]
    if crc16(f[:-2]) == (f[-1] << 8 | f[-2]):
        ok += 1
        kinds[hex(f[1])] += 1
        funcs[hex(f[2])] += 1
        frames.append(f)
        i += n
    else:
        resync += 1
        i += 1

print('总字节 %d, CRC 通过帧 %d, 重同步 %d' % (len(buf), ok, resync))
print('方向统计:', dict(kinds), ' 功能码:', dict(funcs))
print('示例帧:')
for f in frames[:2] + frames[-2:]:
    print('  ', f.hex(' ').upper())

# 总线占用率 / 波特率反推
ts = [t for t, _ in recs]
gaps = [ts[i + 1] - ts[i] for i in range(len(ts) - 1)]
idle = sum(g for g in gaps if g > 200)
span = ts[-1] - ts[0]
busy_ms = span - idle
print('\n样本跨度 %.1f s, 其中空闲(>200ms)累计 %.1f s, 忙 %.1f s (占 %.0f%%)' % (
    span / 1000, idle / 1000, busy_ms / 1000, 100 * busy_ms / span))
print('忙时段字节率: %.0f B/s -> 反推波特率约 %d (8N1, 10bit/字节)' % (
    len(buf) / (busy_ms / 1000), len(buf) / (busy_ms / 1000) * 10))
print('空闲窗口: 每周期约 %.2f s (共 %d 个 >1s 的窗口)' % (idle / sum(1 for g in gaps if g > 1000) / 1000,
                                                   sum(1 for g in gaps if g > 1000)))

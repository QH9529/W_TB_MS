# -*- coding: utf-8 -*-
"""COM2 真机总线实测：验证软件轮询块与协议各区间的实际响应情况（只读，不写任何寄存器）。"""
import sys, time
import serial

PORT = "COM2"
SLAVE = 0xF1   # 热泵主机（拨码默认）
HOST = 0x5F    # PC 模拟主机地址

def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc

def build_req(func: int, start: int, qty: int) -> bytes:
    body = bytes([SLAVE, HOST, func]) + start.to_bytes(2, "big") + qty.to_bytes(2, "big")
    c = crc16(body)
    return body + bytes([c & 0xFF, c >> 8])

def parse_resp(resp: bytes, func: int, start: int):
    """按协议应答帧解析，返回 (ok, 值列表或错误说明)。"""
    if len(resp) < 7:
        return False, f"帧太短({len(resp)}B)"
    if resp[1] != 0xFF:
        return False, f"响应标识={resp[1]:02X}(应为FF)"
    if resp[0] != SLAVE:
        return False, f"从机地址={resp[0]:02X}"
    c = resp[-2] | (resp[-1] << 8)
    if crc16(resp[:-2]) != c:
        return False, "CRC错误"
    if resp[2] != func:
        return False, f"功能码={resp[2]:02X}(可能异常应答)"
    n = resp[5]
    data = resp[6:6 + n]
    if len(data) != n:
        return False, "数据长度不足"
    vals = [int.from_bytes(data[i:i + 2], "big") for i in range(0, n - 1, 2)]
    return True, vals

def test(ser, name, func, start, qty):
    req = build_req(func, start, qty)
    ser.reset_input_buffer()
    ser.write(req)
    t0 = time.perf_counter()
    resp = b""
    while time.perf_counter() - t0 < 0.3:
        chunk = ser.read(256)
        if chunk:
            resp += chunk
            # 响应帧完整判断：读到字节数后凑齐整帧
            if len(resp) >= 7 and len(resp) >= 7 + resp[5]:
                break
        else:
            time.sleep(0.005)
    dt = (time.perf_counter() - t0) * 1000
    if not resp:
        print(f"  [无应答]  {name}  ({dt:.0f}ms)")
        return None
    ok, info = parse_resp(resp, func, start)
    if ok:
        print(f"  [OK]      {name}  {dt:.0f}ms  值: {info}")
    else:
        print(f"  [异常]    {name}  {dt:.0f}ms  -> {info}  原始: {resp.hex(' ')}")
    return info if ok else None

def main():
    ser = serial.Serial(PORT, 9600, bytesize=8, parity=serial.PARITY_NONE,
                        stopbits=1, timeout=0.05, write_timeout=1)
    print(f"== {PORT} 9600/8/N/1  从机0xF1 主机0x{HOST:02X} ==\n")

    print("-- 主轮询块（软件 PollBlocks）--")
    blocks = [
        ("输入30001~30012(传感器)",   0x04, 30001, 12),
        ("输入30101~30110(状态)",     0x04, 30101, 10),
        ("输入30201~30242(运行数据)", 0x04, 30201, 42),
        ("输入30301~30304(故障记录头)",0x04, 30301, 4),
        ("保持40001~40012(参数)",     0x03, 40001, 12),
        ("保持40201~40239(参数)",     0x03, 40201, 39),
        ("保持40301~40324(参数)",     0x03, 40301, 24),
    ]
    for name, f, s, q in blocks:
        test(ser, name, f, s - 1, q)
        time.sleep(0.05)

    print("\n-- 本轮新增块 --")
    for name, f, s, q in [
        ("保持40701~40703(热水器)",   0x03, 40701, 3),
        ("保持40801~40802(累计时长)", 0x03, 40801, 2),
        ("保持40851~40858(杀菌/滤网)",0x03, 40851, 8),
    ]:
        test(ser, name, f, s - 1, q)
        time.sleep(0.05)

    print("\n-- 外机-4G 区间（本轮 UI 新接入，实测设备是否响应）--")
    for name, f, s, q in [
        ("保持20060~20080(风盘1模板)", 0x03, 20060, 21),
        ("保持21001~21017(分集水/在线)",0x03, 21001, 17),
    ]:
        test(ser, name, f, s - 1, q)
        time.sleep(0.05)

    print("\n-- Legacy 小分块抽查（30226热水温度等）--")
    for name, f, s, q in [
        ("输入30226~30228(热水上中下)", 0x04, 30226, 3),
        ("输入30229(拨码)",            0x04, 30229, 1),
        ("输入30231~30242(区间末尾)",   0x04, 30231, 12),
    ]:
        test(ser, name, f, s - 1, q)
        time.sleep(0.05)

    ser.close()
    print("\n完成。")

if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""COM2 真机实测（修正版）：直接用协议寄存器号寻址，校验响应回显地址与CRC。

修正点：上一版脚本把起始地址减了1，导致读数整体错位一格。
"""
import time, sys
import serial

PORT = "COM2"
SLAVE = 0xF1
HOST = 0x5F

def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc

def read(ser, name, func, start, qty, show=6):
    body = bytes([SLAVE, HOST, func]) + start.to_bytes(2, "big") + qty.to_bytes(2, "big")
    c = crc16(body)
    ser.reset_input_buffer()
    ser.write(body + bytes([c & 0xFF, c >> 8]))
    t0 = time.perf_counter(); resp = b""
    while time.perf_counter() - t0 < 0.3:
        chunk = ser.read(256)
        if chunk:
            resp += chunk
            if len(resp) >= 7 and len(resp) >= 7 + resp[5]:
                break
        else:
            time.sleep(0.005)
    dt = (time.perf_counter() - t0) * 1000
    if not resp:
        print(f"  [无应答] {name} ({dt:.0f}ms)"); return None
    if resp[2] & 0x80:
        print(f"  [异常码] {name} func={resp[2]:02X} 异常码={resp[3]:02X}"); return None
    if crc16(resp[:-2]) != (resp[-2] | (resp[-1] << 8)):
        print(f"  [CRC错] {name} raw={resp.hex(' ')}"); return None
    echo = int.from_bytes(resp[3:5], "big")
    n = resp[5]
    vals = [int.from_bytes(resp[6 + i:8 + i], "big") for i in range(0, n - 1, 2)]
    flag = "" if echo == start else f"  <<回显地址={echo} 与请求{start}不符>>"
    head = " ".join(f"{start+i}={v}" for i, v in enumerate(vals[:show]))
    print(f"  [OK] {name} {dt:.0f}ms n={n//2}  {head}{flag}")
    return vals

def ascii_at(vals, start, addr, count):
    idx = addr - start
    if vals is None or idx < 0 or idx + count > len(vals):
        return None
    bs = b""
    for i in range(count):
        bs += vals[idx + i].to_bytes(2, "big")
    return bs.rstrip(b"\x00").decode("ascii", "replace")

def main():
    ser = serial.Serial(PORT, 9600, bytesize=8, parity=serial.PARITY_NONE,
                        stopbits=1, timeout=0.05, write_timeout=1)

    print("== 一、输入寄存器 0x04（直接用协议寄存器号）==")
    v3 = read(ser, "30001~30012 版本信息", 0x04, 30001, 12, show=12)
    print("     产品型号(30005,8字节) =", ascii_at(v3, 30001, 30005, 4))
    v30 = read(ser, "30101~30110 状态", 0x04, 30101, 10, show=10)
    v302 = read(ser, "30201~30242 运行数据", 0x04, 30201, 42, show=4)
    if v302:
        print("     30209空调进水=%d 30211环境=%d 30213热水出水=%d 30221压机频率=%d" % (
            v302[8], v302[10], v302[12], v302[20]))
        print("     30226/27/28热水上中下=%d/%d/%d  30229拨码=0x%04X" % (
            v302[25], v302[26], v302[27], v302[28]))
        print("     30217/18累计耗电(高前)=%d,%d -> %.1f kWh" % (
            v302[16], v302[17], (v302[16] * 65536 + v302[17]) / 10))
        print("     30235/36压机运行时间(低前)=%d,%d -> %d s" % (
            v302[34], v302[35], v302[34] + v302[35] * 65536))
        print("     30231/32/33 电压/电流/功率 = %d/%d/%d" % (v302[30], v302[31], v302[32]))
    read(ser, "30301~30304 故障记录头", 0x04, 30301, 4, show=4)
    read(ser, "30305~30314 故障明细区", 0x04, 30305, 10, show=10)

    print("\n== 二、保持寄存器 0x03 ==")
    read(ser, "40001~40012", 0x03, 40001, 12, show=12)
    read(ser, "40201~40239", 0x03, 40201, 39, show=4)
    read(ser, "40301~40324", 0x03, 40301, 24, show=4)
    read(ser, "40701~40703 热水器", 0x03, 40701, 3, show=3)
    read(ser, "40801~40802 累计时长", 0x03, 40801, 2, show=2)
    v40851 = read(ser, "40851~40858 杀菌/滤网", 0x03, 40851, 8, show=8)
    if v40851:
        import datetime
        for base, label in ((0, "杀菌完成"), (4, "滤网清洁")):
            ts = v40851[base] | (v40851[base+1] << 16) | (v40851[base+2] << 32) | (v40851[base+3] << 48)
            print(f"     {label}时间 = {ts} -> {datetime.datetime.fromtimestamp(ts) if ts else '未记录'}")

    print("\n== 三、外机-4G 区：0x04 vs 0x03 功能码对比 ==")
    for func in (0x04, 0x03):
        read(ser, f"func=0x{func:02X} 20060~20080 风盘1模板", func, 20060, 21, show=3)
    for func in (0x04, 0x03):
        read(ser, f"func=0x{func:02X} 21001~21017 分集水/在线", func, 21001, 17, show=3)

    ser.close()
    print("\n完成。")

if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""验证 0x10 写多寄存器：向 40107~40110 写入当前真实时间（与 4G 模块每分钟同步的行为一致）。"""
import time, datetime
import serial

PORT, SLAVE, HOST = "COM2", 0xF1, 0x5F

def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc

def build_multi(addr, values):
    bc = len(values) * 2
    f = bytearray([SLAVE, HOST, 0x10, addr >> 8, addr & 0xFF, 0, len(values), bc])
    for v in values:
        f += bytes([v >> 8, v & 0xFF])
    c = crc16(bytes(f))
    return bytes(f) + bytes([c & 0xFF, c >> 8])

def main():
    ser = serial.Serial(PORT, 9600, 8, 'N', 1, timeout=0.05, write_timeout=1)

    now = int(time.time())
    words = [ (now >> (16 * i)) & 0xFFFF for i in range(4) ]
    frame = build_multi(40107, words)
    expect_crc_ok = crc16(frame[:-2]) == (frame[-2] | (frame[-1] << 8))
    print(f"请求帧({len(frame)}B): {frame.hex(' ')}  CRC自校验={'OK' if expect_crc_ok else 'FAIL'}")
    print(f"写入时间戳 {now} = {datetime.datetime.fromtimestamp(now)}  寄存器={[hex(w) for w in words]}")

    ser.reset_input_buffer()
    ser.write(frame)
    t0 = time.perf_counter(); resp = b""
    while time.perf_counter() - t0 < 0.5:
        chunk = ser.read(64)
        if chunk:
            resp += chunk
            if len(resp) >= 9:
                break
        else:
            time.sleep(0.005)
    dt = (time.perf_counter() - t0) * 1000

    if not resp:
        print(f"\n[失败] 无应答（{dt:.0f}ms）")
    else:
        print(f"\n应答帧: {resp.hex(' ')}  ({dt:.0f}ms)")
        func = resp[2]
        if func & 0x80:
            print(f"[失败] 异常响应，异常码=0x{resp[3]:02X}")
        else:
            echo_addr = (resp[3] << 8) | resp[4]
            echo_qty = (resp[5] << 8) | resp[6]
            crc_ok = len(resp) >= 9 and crc16(resp[:7]) == (resp[7] | (resp[8] << 8))
            print(f"[{'成功' if echo_addr == 40107 and echo_qty == 4 and crc_ok else '异常'}] "
                  f"回显地址={echo_addr}(期望40107) 回显数量={echo_qty}(期望4) CRC={'OK' if crc_ok else 'FAIL'}")

    # 复读状态寄存器确认设备仍正常（顺带看 4G 写计数 30110 是否变化）
    for name, func_code, start, qty in (("30107~30110", 0x04, 30107, 4),):
        b = bytearray([SLAVE, HOST, func_code, start >> 8, start & 0xFF, 0, qty])
        c = crc16(bytes(b))
        ser.reset_input_buffer()
        ser.write(bytes(b) + bytes([c & 0xFF, c >> 8]))
        t0 = time.perf_counter(); r = b""
        while time.perf_counter() - t0 < 0.3:
            ch = ser.read(64)
            if ch:
                r += ch
                if len(r) >= 7 + r[5]:
                    break
            else:
                time.sleep(0.005)
        if r:
            vals = [int.from_bytes(r[6+i:8+i], "big") for i in range(0, r[5]-1, 2)]
            print(f"复读 {name}: {dict(zip(range(start, start+len(vals)), vals))}")
        else:
            print(f"复读 {name}: 无应答")
    ser.close()

if __name__ == "__main__":
    main()

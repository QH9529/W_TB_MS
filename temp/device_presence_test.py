# -*- coding: utf-8 -*-
"""真机只读验证：外机-4G 在线位图（21016/21017）与 15 台风盘模板区（20000+60n）。

协议帧格式（W系列485通信协议 V2.4）：
  请求: 从机地址(0xF1) | 主机地址(0x5F) | 功能码 | 寄存器起始(2B) | 数量(2B) | CRC16
  应答: 从机地址       | 0xFF          | 功能码 | 寄存器起始(2B) | 字节数(1B) | 数据 | CRC16
地址即协议寄存器号（30001 -> 0x7531），不减 1。
"""
import sys
import time

import serial

PORT = "COM2"
SLAVE = 0xF1
HOST = 0x5F


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def read_block(ser: serial.Serial, func: int, start: int, qty: int, timeout: float = 0.3):
    body = bytes([SLAVE, HOST, func]) + start.to_bytes(2, "big") + qty.to_bytes(2, "big")
    crc = crc16(body)
    ser.reset_input_buffer()
    ser.write(body + bytes([crc & 0xFF, crc >> 8]))

    start_time = time.perf_counter()
    resp = b""
    while time.perf_counter() - start_time < timeout:
        chunk = ser.read(256)
        if chunk:
            resp += chunk
            if len(resp) >= 7 and len(resp) >= 7 + resp[5]:
                break
        else:
            time.sleep(0.005)

    if not resp:
        return None, "无应答"
    if resp[2] & 0x80:
        return None, f"异常码 0x{resp[3]:02X}"
    if crc16(resp[:-2]) != (resp[-2] | (resp[-1] << 8)):
        return None, "CRC错误"
    echo = int.from_bytes(resp[3:5], "big")
    if echo != start:
        return None, f"回显地址 {echo} 与请求 {start} 不符"
    count = resp[5]
    values = [int.from_bytes(resp[6 + i: 8 + i], "big") for i in range(0, count - 1, 2)]
    return values, "OK"


def main() -> int:
    ser = serial.Serial(PORT, 9600, bytesize=8, parity=serial.PARITY_NONE,
                        stopbits=1, timeout=0.05, write_timeout=1)
    failures = 0
    try:
        print("== 一、外机-4G 在线状态位图 ==")
        for addr, label in ((21016, "分集水器在线(21016)"), (21017, "风盘在线(21017)")):
            values, status = read_block(ser, 0x04, addr, 1)
            if values is None:
                print(f"  [失败] {label}: {status}")
                failures += 1
                continue
            bits = values[0]
            online = [i for i in range(1, 16) if bits & (1 << i)]
            print(f"  [OK] {label} = 0x{bits:04X} -> 在线: {online if online else '无'}")

        print("== 二、15 台风盘模板区（20000+60n，偏移 0~20）==")
        for fan in range(1, 16):
            base = 20000 + 60 * fan
            values, status = read_block(ser, 0x04, base, 21)
            if values is None:
                print(f"  [失败] 风盘{fan:>2}(0x{0x90 + fan:02X}) 起始{base}: {status}")
                failures += 1
                continue
            print(f"  [OK] 风盘{fan:>2}(0x{0x90 + fan:02X}) 起始{base}: "
                  f"阀={values[0]} 温度={values[13] / 10:.1f}℃ 型号={values[19]:#06x} "
                  f"功能集={values[20]:#06x} CO2={values[11]} PM2.5={values[12]}")

        print("== 三、分集水器状态区 21001~21017 ==")
        values, status = read_block(ser, 0x04, 21001, 17)
        if values is None:
            print(f"  [失败] {status}")
            failures += 1
        else:
            print("  [OK] " + " ".join(f"{21001 + i}={v:#06x}" for i, v in enumerate(values)))
        print("== 四、产测/身份/OTA/Flash 段首寄存器（只读探测，不写入）==")
        for addr, label in ((40401, "单板产测 40401"), (40501, "读写信息 40501(IMEI)"),
                            (40510, "室外机激活 40510"), (40601, "4G_OTA 40601"),
                            (40701, "RAM 段 40701(热水器出水温度)"), (40801, "Flash 40801(累计运行)")):
            values, status = read_block(ser, 0x03, addr, 2)
            if values is None:
                print(f"  [失败] {label}: {status}")
                failures += 1
            else:
                print(f"  [OK] {label}: {[f'{v:#06x}' for v in values]}")

        print(f"\n完成，失败 {failures} 项。")
    finally:
        ser.close()

    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

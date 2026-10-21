"""验证 40101（0x9CA5）的 0x06 写入帧能否下发并收到合规应答。

用 COM13 对上 COM14 的模拟外机（从机 0xF1，主机用 0x5F 便于区分来源）。
"""
from __future__ import annotations

import sys
import time

import serial

CRC_TABLE = None


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def frame(payload: bytes) -> bytes:
    return payload + crc16(payload).to_bytes(2, "little")


def write_single(port: serial.Serial, address: int, value: int, slave: int = 0xF1) -> bytes:
    request = frame(bytes([slave, 0x5F, 0x06, address >> 8, address & 0xFF, value >> 8, value & 0xFF]))
    port.reset_input_buffer()
    port.write(request)
    port.flush()
    time.sleep(0.15)
    return port.read(port.in_waiting or 16)


def main() -> int:
    try:
        port = serial.Serial("COM13", 9600, parity=serial.PARITY_NONE,
                             bytesize=8, stopbits=1, timeout=1.0)
    except Exception as exc:
        print(f"打开 COM13 失败：{exc}（先关掉 W_TB_MS.exe）")
        return 2

    with port:
        for value in (0, 3, 200):
            response = write_single(port, 40101, value)
            hex_text = " ".join(f"{b:02X}" for b in response)
            if len(response) >= 2 and crc16(response[:-2]) == int.from_bytes(response[-2:], "little"):
                echoed = int.from_bytes(response[5:7], "big") if len(response) >= 9 else -1
                status = f"CRC 通过，回显值 {echoed}（{response[3]:02X}{response[4]:02X}）"
            else:
                status = "CRC 校验失败" if response else "无应答"
            print(f"写 40101 = {value:<3} → {hex_text:<26} {status}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

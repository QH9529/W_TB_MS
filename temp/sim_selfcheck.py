#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""模拟外机自检：在与 W_TB_MS 完全相同的轮询块上验证 temp/sim_odu.py。

在 COM14 起模拟外机，在 COM13 扮演上位机（主机地址 0x5F / 从机 0xF1），
逐块请求并校验：响应标识 0xFF、地址回显、字节数、CRC、以及关键值是否符合场景。

用法：
    python sim_selfcheck.py --sim-port COM14 --pc-port COM13
"""

from __future__ import annotations

import argparse
import sys
import threading
import time
from typing import Dict, List, Optional, Tuple

try:
    import serial
except ImportError:  # pragma: no cover
    sys.exit("需要 pyserial：pip install pyserial")

sys.path.insert(0, __file__.rsplit("\\", 1)[0])
sys.path.insert(0, __file__.rsplit("/", 1)[0])

from sim_odu import (  # noqa: E402  （同目录导入）
    FAN_4G_MAX,
    build_all_maps,
    crc16,
    describe,
    parse_ids,
    SlaveSession,
)

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:  # pragma: no cover
    pass

PC_MASTER = 0x5F          # 上位机地址（协议里 4G 模块是 0x51，PC 模拟用 0x5F 便于区分）

# W_TB_MS/MainForm.cs 的 PollBlocks
POLL_BLOCKS: List[Tuple[int, int, int]] = [
    (0x04, 30001, 12),
    (0x04, 30101, 10),
    (0x04, 30201, 42),
    (0x04, 30301, 4),
    (0x03, 40001, 12),
    (0x03, 40201, 39),
    (0x03, 40301, 24),
    (0x03, 40701, 3),
    (0x03, 40801, 2),
    (0x03, 40851, 8),
    (0x03, 40401, 2),
    (0x03, 40501, 27),
    (0x04, 21001, 17),
]

# W_TB_MS/MainForm.cs 的 LegacyPollBlocks（设备拒绝合并读取时的回退）
LEGACY_BLOCKS: List[Tuple[int, int, int]] = [
    (0x04, 30001, 4), (0x04, 30005, 4), (0x04, 30009, 4),
    (0x04, 30101, 6), (0x04, 30201, 26), (0x04, 30226, 3),
    (0x04, 30107, 4), (0x04, 30229, 1), (0x04, 30231, 12),
    (0x04, 30301, 4),
    (0x03, 40001, 12), (0x03, 40201, 39), (0x03, 40301, 24),
    (0x03, 40701, 3), (0x03, 40801, 2), (0x03, 40851, 8),
    (0x03, 40401, 2), (0x03, 40501, 27),
]


def build_read_request(master: int, slave: int, func: int, address: int, quantity: int) -> bytes:
    body = bytes([slave, master, func, address >> 8, address & 0xFF, quantity >> 8, quantity & 0xFF])
    value = crc16(body)
    return body + bytes((value & 0xFF, value >> 8))


def build_write_request(master: int, slave: int, address: int, value: int) -> bytes:
    body = bytes([slave, master, 0x06, address >> 8, address & 0xFF, value >> 8, value & 0xFF])
    crc = crc16(body)
    return body + bytes((crc & 0xFF, crc >> 8))


def build_write_multi_request(master: int, slave: int, address: int, values: List[int]) -> bytes:
    body = bytearray([slave, master, 0x10, address >> 8, address & 0xFF,
                      len(values) >> 8, len(values) & 0xFF, len(values) * 2])
    for value in values:
        body += bytes((value >> 8, value & 0xFF))
    crc = crc16(bytes(body))
    return bytes(body) + bytes((crc & 0xFF, crc >> 8))


class PcMaster:
    """扮演上位机，解析逻辑与 ModbusRtuClient.ReadRegisters 一致。"""

    def __init__(self, port: str, slave: int, timeout: float = 0.5):
        self.serial = serial.Serial(port, 9600, bytesize=8, parity="N", stopbits=1, timeout=timeout)
        self.slave = slave

    def close(self) -> None:
        self.serial.close()

    def _read_exact(self, count: int) -> bytes:
        data = self.serial.read(count)
        if len(data) != count:
            raise TimeoutError(f"只收到 {len(data)}/{count} 字节")
        return data

    def read(self, func: int, address: int, quantity: int, slave: Optional[int] = None) -> List[int]:
        target = self.slave if slave is None else slave
        request = build_read_request(PC_MASTER, target, func, address, quantity)
        self.serial.reset_input_buffer()
        self.serial.write(request)
        header = self._read_exact(6)
        if header[0] != target:
            raise ValueError(f"从机地址不匹配: 0x{header[0]:02X}")
        if header[1] != 0xFF:
            raise ValueError(f"响应标识错误: 0x{header[1]:02X}")
        if header[2] != func:
            raise ValueError(f"功能码不匹配: 0x{header[2]:02X}")
        if (header[3], header[4]) != ((address >> 8), address & 0xFF):
            raise ValueError(f"地址回显不匹配: 0x{header[3]:02X}{header[4]:02X}")
        byte_count = header[5]
        if byte_count != quantity * 2:
            raise ValueError(f"字节数错误: {byte_count}, 期望 {quantity * 2}")
        rest = self._read_exact(byte_count + 2)
        frame = header + rest
        expected = crc16(frame[:-2])
        actual = frame[-2] | (frame[-1] << 8)
        if expected != actual:
            raise ValueError(f"CRC 错误: 计算 0x{expected:04X} 收到 0x{actual:04X}")
        payload = rest[:-2]
        return [(payload[i * 2] << 8) | payload[i * 2 + 1] for i in range(quantity)]

    def write_single(self, address: int, value: int, slave: Optional[int] = None) -> None:
        target = self.slave if slave is None else slave
        request = build_write_request(PC_MASTER, target, address, value)
        self.serial.reset_input_buffer()
        self.serial.write(request)
        frame = self._read_exact(9)
        if frame[1] != 0xFF or frame[2] != 0x06:
            raise ValueError(f"写响应异常: {frame.hex(' ')}")
        if (frame[3] << 8 | frame[4]) != address or (frame[5] << 8 | frame[6]) != value:
            raise ValueError(f"写响应回显不匹配: {frame.hex(' ')}")
        expected = crc16(frame[:-2])
        if expected != (frame[-2] | (frame[-1] << 8)):
            raise ValueError("写响应 CRC 错误")

    def write_multiple(self, address: int, values: List[int], slave: Optional[int] = None) -> None:
        target = self.slave if slave is None else slave
        request = build_write_multi_request(PC_MASTER, target, address, values)
        self.serial.reset_input_buffer()
        self.serial.write(request)
        frame = self._read_exact(9)
        if frame[1] != 0xFF or frame[2] != 0x10:
            raise ValueError(f"0x10 写响应异常: {frame.hex(' ')}")
        if (frame[3] << 8 | frame[4]) != address or (frame[5] << 8 | frame[6]) != len(values):
            raise ValueError(f"0x10 写响应回显不匹配: {frame.hex(' ')}")
        expected = crc16(frame[:-2])
        if expected != (frame[-2] | (frame[-1] << 8)):
            raise ValueError("0x10 写响应 CRC 错误")


# ---------------------------------------------------------------- 校验
class Report:
    def __init__(self) -> None:
        self.passed = 0
        self.failed = 0
        self.failures: List[str] = []

    def check(self, name: str, condition: bool, detail: str = "") -> None:
        if condition:
            self.passed += 1
            print(f"  [OK]   {name}" + (f"  {detail}" if detail else ""))
        else:
            self.failed += 1
            self.failures.append(f"{name} {detail}")
            print(f"  [FAIL] {name}  {detail}")

    def summary(self) -> int:
        print()
        print(f"自检结果：通过 {self.passed}，失败 {self.failed}")
        for item in self.failures:
            print(f"  - {item}")
        return 0 if self.failed == 0 else 1


def decode_ascii(values: List[int]) -> str:
    raw = bytearray()
    for value in values:
        raw += bytes(((value >> 8) & 0xFF, value & 0xFF))
    return raw.decode("ascii", "replace").rstrip("\x00 ")


def run_checks(master: PcMaster, fans_online: List[int], manifolds_online: List[int], report: Report) -> None:
    # ---- 1. 合并轮询块（软件默认路径）----
    print("\n[1] MainForm.PollBlocks 逐块读取")
    cache: Dict[int, int] = {}
    for func, address, quantity in POLL_BLOCKS:
        values = master.read(func, address, quantity)
        for index, value in enumerate(values):
            cache[address + index] = value
        report.check(f"0x{func:02X} {address} ×{quantity}",
                     len(values) == quantity,
                     f"首值=0x{values[0]:04X} 末值=0x{values[-1]:04X}")

    # ---- 2. Legacy 回退块 ----
    print("\n[2] LegacyPollBlocks 逐块读取（旧设备回退路径）")
    for func, address, quantity in LEGACY_BLOCKS:
        values = master.read(func, address, quantity)
        report.check(f"0x{func:02X} {address} ×{quantity}", len(values) == quantity)

    # ---- 3. 风盘模板（在线风盘才请求，20000+60×n）----
    print("\n[3] 在线风盘模板块（BuildPollBlocks 动态追加）")
    for fan in fans_online:
        base = 20000 + 60 * fan
        values = master.read(0x04, base, 21)
        cache.update({base + i: v for i, v in enumerate(values)})
        report.check(f"风盘{fan} 模板 {base} ×21", len(values) == 21,
                     f"型号=0x{values[19]:04X} 温度={values[13] / 10:.1f}℃ CO2={values[11]}")

    # ---- 4. 关键值核对 ----
    print("\n[4] 关键值核对")
    report.check("产品型号 30005~30008 = MS-W300", decode_ascii([cache.get(30005 + i, 0) for i in range(4)]) == "MS-W300",
                 decode_ascii([cache.get(30005 + i, 0) for i in range(4)]))
    report.check("空调进水 30209 = 13.5℃", cache.get(30209) == 135, f"{cache.get(30209, 0) / 10:.1f}℃")
    report.check("压缩机频率 30221 = 79Hz", cache.get(30221) == 79)
    report.check("热水上/中/下 30226~30228", [cache.get(30226), cache.get(30227), cache.get(30228)] == [403, 403, 403])
    report.check("热水器出水 40701 = 65.00℃", cache.get(40701) == 6500, f"{cache.get(40701, 0) / 100:.2f}℃")
    runtime = cache.get(40801, 0) | (cache.get(40802, 0) << 16)
    report.check("累计运行时长 40801~40802 = 108h", runtime == 108, f"{runtime}h")
    report.check("IMEI 40501 段可解码", decode_ascii([cache.get(40501 + i, 0) for i in range(9)]).startswith("8611"),
                 decode_ascii([cache.get(40501 + i, 0) for i in range(9)]))
    report.check("室外机激活 40510 = 1", cache.get(40510) == 1)

    # ---- 5. 在线位图 ----
    print("\n[5] 外机-4G 在线位图（风盘/分集水器分组显示的依据）")
    fan_expect = sum(1 << f for f in fans_online)
    manifold_expect = sum(1 << m for m in manifolds_online)
    report.check(f"21017 风盘在线位图 = 0x{fan_expect:04X}", cache.get(21017) == fan_expect,
                 f"实际 0x{cache.get(21017, 0):04X}")
    report.check(f"21016 分集水器在线位图 = 0x{manifold_expect:04X}", cache.get(21016) == manifold_expect,
                 f"实际 0x{cache.get(21016, 0):04X}")
    report.check("21001 分水器1 阀门位", cache.get(21001) == 0x0003, f"0x{cache.get(21001, 0):04X}")

    # ---- 6. 写入路径 ----
    print("\n[6] 写入路径（0x06 / 0x10）")
    master.write_single(40204, 125)
    report.check("0x06 写 40204=125 并回读", master.read(0x03, 40204, 1)[0] == 125)

    before = master.read(0x04, 30110, 1)[0]
    stamp = int(time.time())
    words = [(stamp >> (16 * i)) & 0xFFFF for i in range(4)]
    master.write_multiple(40107, words)
    report.check("0x10 写 40107~40110（同步时间）", True, f"0x{stamp:08X} → {[f'{w:04X}' for w in words]}")
    after = master.read(0x04, 30110, 1)[0]
    report.check("PC(0x5F) 写入不计入 30110 4G 写计数", before == after, f"{before} → {after}")

    # ---- 7. 其它从机地址 ----
    print("\n[7] 风盘(0x91)/地暖(0xA1)/MixPad(0x51) 从机应答")
    fan1 = master.read(0x03, 41001, 17, slave=0x91)
    report.check("风盘 0x91 读 41001~41017", len(fan1) == 17,
                 f"风速={fan1[2]} CO2={fan1[12]} 温度={fan1[14] / 10:.1f}℃")
    # 地暖阀门段的检查全部先写后读，避免依赖上一次运行残留的状态。
    master.write_single(42100, 1, slave=0xA1)          # 关闭所有分水器阀门
    all_off = master.read(0x03, 42001, 16, slave=0xA1)
    report.check("地暖 0xA1 写 42100=1 → 42001~42016 全关", all_off == [0] * 16, f"{all_off}")
    master.write_single(42001, 1, slave=0xA1)
    master.write_single(42003, 1, slave=0xA1)
    valves = master.read(0x03, 42001, 16, slave=0xA1)
    report.check("地暖 0xA1 置 42001/42003 后回读", valves[:4] == [1, 0, 1, 0],
                 f"{valves[:4]} … 合计 {sum(valves)} 路开")
    floor_ver = master.read(0x04, 32001, 6, slave=0xA1)
    report.check("地暖 0xA1 读 32001~32006（版本+故障）",
                 floor_ver[:2] == [1, 0] and floor_ver[2:4] == [2, 0] and floor_ver[4] == 0,
                 f"硬件v{floor_ver[0]}.{floor_ver[1]} 软件v{floor_ver[2]}.{floor_ver[3]} 故障={floor_ver[4]}")
    master.write_multiple(42001, [1, 1, 1, 1], slave=0xA1)
    valves2 = master.read(0x03, 42001, 4, slave=0xA1)
    report.check("地暖 0xA1 用 0x10 批量置位 4 路阀门", valves2 == [1, 1, 1, 1], f"{valves2}")
    mixpad = master.read(0x04, 33001, 1, slave=0x51)
    report.check("MixPad 0x51 读 33001 = 0xAA55", mixpad == [0xAA55], f"0x{mixpad[0]:04X}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="模拟外机自检")
    parser.add_argument("--sim-port", default="COM14")
    parser.add_argument("--pc-port", default="COM13")
    parser.add_argument("--fans", default="1,2,5")
    parser.add_argument("--manifolds", default="1,3")
    args = parser.parse_args(argv)

    fans_online = parse_ids(args.fans)
    manifolds_online = parse_ids(args.manifolds)
    maps = build_all_maps(fans_online, manifolds_online)

    session = SlaveSession(args.sim_port, maps, delay_ms=8.0, verbose=False)
    session.open()
    thread = threading.Thread(target=session.run_forever, daemon=True)
    thread.start()
    print(describe(fans_online, manifolds_online))
    print(f"模拟外机监听 {args.sim_port}，上位机使用 {args.pc_port}")

    master = PcMaster(args.pc_port, slave=0xF1)
    report = Report()
    try:
        run_checks(master, fans_online, manifolds_online, report)
    finally:
        master.close()
        session.close()
    return report.summary()


if __name__ == "__main__":
    raise SystemExit(main())

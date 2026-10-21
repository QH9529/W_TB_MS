#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""W 系列热泵「模拟外机」—— Modbus RTU 从机（用于 W_TB_MS 上位机联调验证）

为什么需要它：风盘/分集水器/地暖 的真实硬件往往不在手边，而 W_TB_MS 的
「外机-4G」相关界面（21016/21017 在线位图 → 风盘/分集水器分组显示与轮询、
20000+60×n 风盘模板 17 项、42001~ 地暖阀门）必须真有从机上报才看得到。
本脚本在虚拟/物理串口上扮演外机（0xF1）以及风盘（0x91~0x9F）、地暖（0xA1），
让上位机像连真机一样跑起来。

帧格式与 W_TB_MS/Modbus/ModbusRtuClient.cs 完全一致：
    请求：从机地址 | 主机地址 | 功能码 | 起始地址(2B) | 数量(2B) | CRC16(2B)
    应答：从机地址 | 0xFF     | 功能码 | 起始地址(2B) | 字节数(1B) | 数据(nB) | CRC16(2B)
    写应答(0x06/0x10)：从机地址 | 0xFF | 功能码 | 地址(2B) | 值/数量(2B) | CRC16(2B)
CRC16：多项式 0xA001、初值 0xFFFF、低字节在前。串口 9600-8-N-1。

用法（配合 com0com 虚拟串口对，例如 COM13 <-> COM14）：
    python sim_odu.py --port COM14                     # 默认：风盘 1/2/5 在线、分集水器 1/3 在线
    python sim_odu.py --port COM14 --fans 1,2 --manifolds 1
    python sim_odu.py --port COM14 --fans none --manifolds none    # 模拟没接任何从机
    python sim_odu.py --port COM14 --quiet             # 只打印启动信息，不逐帧打印
上位机连另一端（COM13）。

被模拟的地址段（协议 V2.4）：
    * 3xxxx 输入寄存器：30001~30011 版本/型号、30101~30110 故障与状态、30201~30242 温度/压力/
      频率、30226~30228 热水上中下、30231~30233 交流电压电流功率、30301~30304 故障记录头
    * 4xxxx 保持寄存器：40001~40012 常用命令、40201~40239 服务密码、40301~40324 工厂密码、
      40401~40402 产测、40501~40527 读写信息(IMEI/激活)、40701~40703 热水器、40801~40858 Flash
    * 外机-4G 区：20000+60×n 风盘模板（n=1~15）、21001~21015 分集水器状态、21016/21017 在线位图
    * 其它从机：0x91~0x9F 风盘（31001~/41001~/41101~/41201~）、0xA1 地暖（32001~/42001~/42100）
"""

from __future__ import annotations

import argparse
import sys
import threading
import time
from typing import Dict, Iterable, List, Optional

try:
    import serial
except ImportError:  # pragma: no cover
    sys.exit("需要 pyserial：pip install pyserial")

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:  # pragma: no cover
    pass

HUGE = 0xFFFF
FAN_4G_BASE = 20000
FAN_4G_STEP = 60
FAN_4G_COUNT = 21
FAN_4G_MAX = 15


# ---------------------------------------------------------------- CRC
def crc16(data: bytes) -> int:
    """Modbus CRC16（0xA001 / 0xFFFF）。"""
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def with_crc(payload: Iterable[int]) -> bytes:
    body = bytes(payload)
    value = crc16(body)
    return body + bytes((value & 0xFF, value >> 8))


def parse_ids(text: str) -> List[int]:
    """解析 '1,2,5' / 'none' / '1-3' 形式的序号列表。"""
    text = (text or "").strip()
    if text.lower() in ("", "none", "no", "空"):
        return []
    result: List[int] = []
    for part in text.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            start, end = part.split("-", 1)
            result.extend(range(int(start), int(end) + 1))
        else:
            result.append(int(part))
    return result


# ---------------------------------------------------------------- 寄存器场景
def _put(regs: Dict[int, int], addr: int, *values: int) -> None:
    for index, value in enumerate(values):
        regs[addr + index] = value & 0xFFFF


def _ascii(regs: Dict[int, int], addr: int, text: str, words: int) -> None:
    """按协议写入 ASCII 字符串（高字节在前，不足补 0x00）。"""
    raw = text.encode("ascii", "replace")[: words * 2].ljust(words * 2, b"\x00")
    for index in range(words):
        _put(regs, addr + index, (raw[index * 2] << 8) | raw[index * 2 + 1])


# 每台风盘模板的差异值（仅在线风盘会被上位机轮询到）
FAN_PROFILES = {
    1: dict(valve=1, speed=60, fresh=45, anion=1, heater=1, vswing=3, hswing=1,
            runtime=128, fault=0x0000, co2=780, pm25=12, temp=245, humidity=52,
            hw=(0x0001, 0x0002), sw=(0x0002, 0x0005), model=0x00AC, feature=0x007F),
    2: dict(valve=0, speed=30, fresh=0, anion=0, heater=1, vswing=0, hswing=0,
            runtime=64, fault=0x0004, co2=560, pm25=8, temp=238, humidity=55,
            hw=(0x0001, 0x0001), sw=(0x0002, 0x0003), model=0x00AC, feature=0x003F),
    5: dict(valve=1, speed=80, fresh=60, anion=1, heater=0, vswing=6, hswing=6,
            runtime=320, fault=0x0000, co2=900, pm25=25, temp=252, humidity=48,
            hw=(0x0001, 0x0003), sw=(0x0002, 0x0006), model=0x00AC, feature=0x007F),
}


def build_outer_map(fans_online: List[int], manifolds_online: List[int]) -> Dict[int, int]:
    """外机（0xF1~0xF4）寄存器表，取值参考现场真机 MS-W300 的实测形态。"""
    regs: Dict[int, int] = {}

    # ---- 版本/型号（3xxxx，0x04）----
    _put(regs, 30001, 0x0001, 0x0000)                 # 主控板硬件版本
    _put(regs, 30003, 0x0002, 0x0000)                 # 主控板软件版本
    _ascii(regs, 30005, "MS-W300", 4)                 # 产品型号
    _put(regs, 30009, 0x0001, 0x0000)                 # 压缩机驱动板版本
    _put(regs, 30011, 0x0001, 0x0000)                 # 风机驱动板版本

    # ---- 故障报警（30101~30105 全 0 = 无故障）----
    _put(regs, 30101, 0, 0, 0, 0, 0)

    # ---- 工作状态 / 通信状态 ----
    _put(regs, 30106, 0x0019)     # BIT0 开机 + BIT3/BIT4 工作模式=3(制热)
    _put(regs, 30107, 0x0000)     # 升级状态
    _put(regs, 30108, 135)        # 空调进水温度快速值 13.5℃
    _put(regs, 30109, 79)         # 压缩机频率快速值 79Hz
    _put(regs, 30110, 79)         # 4G 模块写保持寄存器累加数

    # ---- 温度/压力/频率（30201~30242）----
    _put(regs, 30201, 0x0004)     # 设备运行状态：BIT2 空调循环水泵接通
    _put(regs, 30202, 801)        # 排气温度 80.1℃
    _put(regs, 30203, 300)        # 热回收出口 30.0℃
    _put(regs, 30204, 280)        # 中盘 28.0℃
    _put(regs, 30205, 300)
    _put(regs, 30206, 260)
    _put(regs, 30207, 270)
    _put(regs, 30208, 150)        # 吸气 15.0℃
    _put(regs, 30209, 135)        # 空调进水 13.5℃
    _put(regs, 30210, 128)        # 空调出水 12.8℃
    _put(regs, 30211, 216)        # 环境温度 21.6℃
    _put(regs, 30212, 400)        # 热水进水 40.0℃
    _put(regs, 30213, 403)        # 热水出水 40.3℃
    _put(regs, 30214, 403)        # 热水温度 40.3℃
    _put(regs, 30215, 800)        # 低压 0.80MPa
    _put(regs, 30216, 2600)       # 高压 2.60MPa
    _put(regs, 30217, 390, 0)     # 累计耗电量 39.0kWh（低字在前）
    _put(regs, 30219, 250)        # EXV1 步数
    _put(regs, 30220, 180)        # EXV2 步数
    _put(regs, 30221, 79)         # 压缩机频率 79Hz
    _put(regs, 30222, 600)        # 上风机转速
    _put(regs, 30223, 1)
    _put(regs, 30224, 0)
    _put(regs, 30225, 600)        # 下风机转速
    _put(regs, 30226, 403, 403, 403)                 # 热水上/中/下 40.3℃
    _put(regs, 30229, 0x0000)     # K1 拨码：地址 0xF1、扩展水箱/热水器均支持
    # 30231~30233 交流电压/电流/功率：现场真机恒为 0，这里同样保持 0
    _put(regs, 30231, 0, 0, 0)
    _put(regs, 30234, 80)         # 目标频率
    _put(regs, 30235, 1497, 0)    # 累计运行时间（低字在前）
    _put(regs, 30237, 0, 0)       # 累计停机时间
    _put(regs, 30239, 3100)       # 母线电压 310.0V
    _put(regs, 30240, 55)         # 压缩机相电流 5.5A
    _put(regs, 30241, 12)         # 直流风机电流 1.2A
    _put(regs, 30242, 450)        # IPM 温度 45.0℃

    # ---- 本地故障记录头（30301~30304）----
    _put(regs, 30301, 0, 0, 0, 0)                     # 未触发读取流程、无历史故障

    # ---- 保持寄存器：常用命令回显区（40001~40012）----
    _put(regs, 40001, 0x0001)     # 开关机：开机
    _put(regs, 40002, 0x0001)     # 静音：静音
    _put(regs, 40003, 0x0000)
    _put(regs, 40004, 0x0000, 0x0000)
    _put(regs, 40006, 0x0000)
    _put(regs, 40007, 0x0001)     # 热水杀菌开
    _put(regs, 40008, 0x0000)
    _put(regs, 40009, 0x0001)
    _put(regs, 40010, 0x0000)
    _put(regs, 40011, 0x0000)
    _put(regs, 40012, 0x0004)     # 室内设备运行数量 4

    # ---- 服务密码段（40201~40239）----
    _put(regs, 40201, 0x0002)     # 工作模式：制热
    _put(regs, 40202, 0x0001)     # 生活热水启用
    _put(regs, 40204, 120)        # 制冷进水 12.0℃
    _put(regs, 40205, 70)
    _put(regs, 40206, 450)        # 制热进水 45.0℃
    _put(regs, 40207, 450)
    _put(regs, 40208, 0x0000)
    _put(regs, 40210, 20)
    _put(regs, 40211, 20)
    _put(regs, 40212, 0x0001)
    _put(regs, 40214, 0, 60)
    _put(regs, 40215, 50)
    _put(regs, 40219, 200)
    _put(regs, 40220, 10)
    _put(regs, 40221, 45)
    _put(regs, 40226, 0x0001)
    _put(regs, 40227, 0x0001)
    _put(regs, 40228, 0x0002)
    _put(regs, 40229, 0x0002)
    _put(regs, 40230, 0x0000)
    _put(regs, 40231, 5)
    _put(regs, 40232, 30)
    _put(regs, 40233, 10)
    _put(regs, 40236, 200)
    _put(regs, 40239, 30)

    # ---- 工厂密码段（40301~40324）----
    _put(regs, 40301, 0, 0, 0, 0)
    _put(regs, 40305, 120)
    _put(regs, 40306, 30)
    _put(regs, 40307, 900)
    _put(regs, 40308, 50)
    _put(regs, 40321, 0x0001)
    _put(regs, 40322, 500)
    _put(regs, 40323, 0x0002)
    _put(regs, 40324, 450)

    # ---- 产测 / 读写信息段（40401~40527）----
    _put(regs, 40401, 0x0000, 0x0000)
    _ascii(regs, 40501, "861123456789012", 9)         # IMEI（9 寄存器 ASCII）
    _put(regs, 40510, 0x0001)                          # 室外机已激活
    _put(regs, 40511, *([0x5A5A] * 8))                 # 激活 MD5（占位）
    _ascii(regs, 40519, "0" * 18, 9)                   # 单机模式：全部 '0'

    # ---- 热水器段（40701~40703）----
    _put(regs, 40701, 6500)       # 热水器出水温度 65.00℃（×100）
    _put(regs, 40702, 65)         # 热水量 65%
    _put(regs, 40703, 30)         # 加热时间 30min

    # ---- Flash 持久化（40801~40858）----
    _put(regs, 40801, 108, 0)                          # 累计运行 108h（低字在前）
    now = int(time.time())
    stamp = now - 3 * 86400
    _put(regs, 40851, *[(stamp >> (16 * i)) & 0xFFFF for i in range(4)])   # 杀菌完成时间
    stamp2 = now - 12 * 3600
    _put(regs, 40855, *[(stamp2 >> (16 * i)) & 0xFFFF for i in range(4)])  # 滤网清洁时间

    # ---- 外机-4G：风盘模板（20000+60×n，n=1~15）----
    for fan in range(1, FAN_4G_MAX + 1):
        base = FAN_4G_BASE + FAN_4G_STEP * fan
        if fan in fans_online:
            profile = FAN_PROFILES.get(fan, FAN_PROFILES[1])
            _put(regs, base + 0, profile["valve"])
            _put(regs, base + 1, profile["speed"])
            _put(regs, base + 2, profile["fresh"])
            _put(regs, base + 3, profile["anion"])
            _put(regs, base + 4, profile["heater"])
            _put(regs, base + 5, profile["vswing"])
            _put(regs, base + 6, profile["hswing"])
            _put(regs, base + 7, 0)
            _put(regs, base + 8, 0)
            _put(regs, base + 9, profile["runtime"])
            _put(regs, base + 10, profile["fault"])
            _put(regs, base + 11, profile["co2"])
            _put(regs, base + 12, profile["pm25"])
            _put(regs, base + 13, profile["temp"])
            _put(regs, base + 14, profile["humidity"])
            _put(regs, base + 15, *profile["hw"])
            _put(regs, base + 17, *profile["sw"])
            _put(regs, base + 19, profile["model"])
            _put(regs, base + 20, profile["feature"])
        else:
            _put(regs, base, *([0] * FAN_4G_COUNT))

    # ---- 外机-4G：分集水器状态 + 在线位图 ----
    for manifold in range(1, 16):
        value = 0
        if manifold in manifolds_online:
            # 1 号分水器开 1、2 号阀门；3 号分水器开 8 号阀门；其余全开
            value = {1: 0x0003, 3: 0x0080}.get(manifold, 0x00FF)
        _put(regs, 21000 + manifold, value)
    _put(regs, 21016, sum(1 << m for m in manifolds_online))   # 分集水器在线位图（BIT1~BIT15）
    _put(regs, 21017, sum(1 << f for f in fans_online))        # 风盘在线位图

    return regs


def build_fan_map(fan: int) -> Dict[int, int]:
    """风盘从机（0x91~0x9F）自身的寄存器表。"""
    profile = FAN_PROFILES.get(fan, FAN_PROFILES[1])
    regs: Dict[int, int] = {}
    _put(regs, 31001, *profile["hw"])
    _put(regs, 31003, *profile["sw"])
    _put(regs, 31005, profile["model"])
    _put(regs, 31006, profile["feature"])
    _put(regs, 41001, 0x0000)
    _put(regs, 41002, profile["valve"])
    _put(regs, 41003, profile["speed"])
    _put(regs, 41004, profile["fresh"])
    _put(regs, 41005, profile["anion"])
    _put(regs, 41006, profile["heater"])
    _put(regs, 41007, profile["vswing"])
    _put(regs, 41008, profile["hswing"])
    _put(regs, 41011, profile["runtime"])
    _put(regs, 41012, profile["fault"])
    _put(regs, 41013, profile["co2"])
    _put(regs, 41014, profile["pm25"])
    _put(regs, 41015, profile["temp"])
    _put(regs, 41016, profile["humidity"])
    _put(regs, 41017, 0x0000)
    _put(regs, 41101, profile["model"])
    _put(regs, 41102, 300)
    _put(regs, 41103, 900)
    _put(regs, 41104, 0x0001)
    _put(regs, 41105, 300)
    _put(regs, 41106, 900)
    _put(regs, 41107, 0x0000)
    _put(regs, 41201, 0x0000)
    return regs


FLOOR_VALVE_FIRST = 42001
FLOOR_VALVE_LAST = 42016
FLOOR_VALVE_ALL_OFF = 42100


def build_floor_heating_map() -> Dict[int, int]:
    """地暖从机（0xA1~0xAF）寄存器表。

    分水器阀门 42001~42016（1=开，0=关），42100=关闭所有阀门（写命令）。
    上电初始状态：1、3、5 号阀门开，其余关。
    """
    regs: Dict[int, int] = {}
    _put(regs, 32001, 0x0001, 0x0000)     # 硬件版本
    _put(regs, 32003, 0x0002, 0x0000)     # 软件版本
    _put(regs, 32005, 0x0000)             # 故障码
    for valve in range(FLOOR_VALVE_FIRST, FLOOR_VALVE_LAST + 1):
        _put(regs, valve, 1 if valve in (42001, 42003, 42005) else 0)
    _put(regs, FLOOR_VALVE_ALL_OFF, 0x0000)
    return regs


def _apply_floor_heating_command(regs: Dict[int, int], address: int, value: int) -> None:
    """还原真机的联动行为：写 42100=1 会关闭所有分水器阀门。"""
    if address == FLOOR_VALVE_ALL_OFF and value == 1 and FLOOR_VALVE_FIRST in regs:
        for valve in range(FLOOR_VALVE_FIRST, FLOOR_VALVE_LAST + 1):
            if valve in regs:
                regs[valve] = 0


def build_mixpad_map() -> Dict[int, int]:
    """MixPad 底壳从机（0x51~0x5F）寄存器表。"""
    regs: Dict[int, int] = {}
    _put(regs, 33001, 0xAA55)
    _put(regs, 33100, 0x0000)
    _put(regs, 43001, 0x0000)
    _put(regs, 43002, sum(1 << i for i in (1, 2)))
    _put(regs, 43003, 0)
    _put(regs, 43004, 0)
    _put(regs, 43005, 0x0002)
    _put(regs, 43006, 0x0002)
    return regs


def build_all_maps(fans_online: List[int], manifolds_online: List[int]) -> Dict[int, Dict[int, int]]:
    """构造各从机地址 -> 寄存器表 的映射。"""
    maps: Dict[int, Dict[int, int]] = {}
    outer = build_outer_map(fans_online, manifolds_online)
    for address in (0xF1, 0xF2, 0xF3, 0xF4):
        maps[address] = outer       # 同一份外机表，拨码 F1~F4 都能应答
    for fan in fans_online:
        if 1 <= fan <= FAN_4G_MAX:
            maps[0x90 + fan] = build_fan_map(fan)
    maps[0xA1] = build_floor_heating_map()
    for address in range(0x51, 0x60):
        maps[address] = build_mixpad_map()
    return maps


# ---------------------------------------------------------------- 会话
class SlaveSession:
    """按 W 协议解析请求并应答。"""

    def __init__(self, port: str, maps: Dict[int, Dict[int, int]], delay_ms: float = 10.0, verbose: bool = True):
        self.port = port
        self.maps = maps
        self.delay = delay_ms / 1000.0
        self.verbose = verbose
        self.serial = None
        self._stop = threading.Event()
        self.stats = {"request": 0, "response": 0, "bad_crc": 0, "unknown_slave": 0, "write": 0}

    # ---- 生命周期 ----
    def open(self) -> None:
        self.serial = serial.Serial(self.port, 9600, bytesize=8, parity="N", stopbits=1, timeout=0.02)
        self.serial.reset_input_buffer()
        self.serial.reset_output_buffer()

    def close(self) -> None:
        self._stop.set()
        if self.serial and self.serial.is_open:
            self.serial.close()

    # ---- 主循环 ----
    def run_forever(self) -> None:
        buffer = bytearray()
        while not self._stop.is_set():
            chunk = self.serial.read(512)
            if chunk:
                buffer.extend(chunk)
            while True:
                frame_len = self._frame_length(buffer)
                if frame_len is None:
                    break
                frame = bytes(buffer[:frame_len])
                del buffer[:frame_len]
                self._handle(frame)
                time.sleep(self.delay)
            if len(buffer) > 4096:
                buffer.clear()

    @staticmethod
    def _frame_length(buffer: bytearray) -> Optional[int]:
        """按功能码推算一帧长度；返回 None 表示数据还不够。"""
        if len(buffer) < 8:
            return None
        func = buffer[2]
        if func == 0x10:
            byte_count = buffer[7]
            if byte_count <= 0 or byte_count % 2 or byte_count > 250:
                del buffer[:1]              # 明显异常：滑动一个字节重新同步
                return None
            # 帧头 8 字节（从机+主机+功能码+地址2B+数量2B+字节数）+ 数据 + CRC2B
            total = 10 + byte_count
            return total if len(buffer) >= total else None
        if func in (0x03, 0x04, 0x06):
            return 9 if len(buffer) >= 9 else None
        del buffer[:1]                      # 未知功能码，滑动重同步
        return None

    # ---- 帧处理 ----
    def _handle(self, frame: bytes) -> None:
        expected = crc16(frame[:-2])
        actual = frame[-2] | (frame[-1] << 8)
        if expected != actual:
            self.stats["bad_crc"] += 1
            self._log(f"帧 CRC 错误，丢弃: {frame.hex(' ').upper()}")
            return

        self.stats["request"] += 1
        slave, master, func = frame[0], frame[1], frame[2]
        regs = self.maps.get(slave)
        if regs is None:
            self.stats["unknown_slave"] += 1
            self._log(f"从机 0x{slave:02X} 未定义，不应答")
            return

        if func in (0x03, 0x04):
            response = self._read(slave, func, frame, regs)
        elif func == 0x06:
            response = self._write_single(slave, master, frame, regs)
        elif func == 0x10:
            response = self._write_multiple(slave, master, frame, regs)
        else:
            response = with_crc([slave, 0xFF, func | 0x80, 0x01])

        if response is None:
            return
        self.serial.write(response)
        self.stats["response"] += 1
        self._log(f"RX {frame.hex(' ').upper()}  →  TX {response.hex(' ').upper()}")

    def _read(self, slave: int, func: int, frame: bytes, regs: Dict[int, int]) -> Optional[bytes]:
        start = (frame[3] << 8) | frame[4]
        quantity = (frame[5] << 8) | frame[6]
        if quantity < 1 or quantity > 125:
            return with_crc([slave, 0xFF, func | 0x80, 0x03])
        data = bytearray()
        for offset in range(quantity):
            value = regs.get(start + offset, 0)
            data.append((value >> 8) & 0xFF)
            data.append(value & 0xFF)
        return with_crc([slave, 0xFF, func, frame[3], frame[4], len(data)] + list(data))

    def _write_single(self, slave: int, master: int, frame: bytes, regs: Dict[int, int]) -> bytes:
        address = (frame[3] << 8) | frame[4]
        value = (frame[5] << 8) | frame[6]
        regs[address] = value
        _apply_floor_heating_command(regs, address, value)
        self.stats["write"] += 1
        self._count_4g_write(master)
        self._log(f"写入 {slave:#04x}/{address} = {value} (0x{value:04X})")
        return with_crc([slave, 0xFF, 0x06, frame[3], frame[4], frame[5], frame[6]])

    def _write_multiple(self, slave: int, master: int, frame: bytes, regs: Dict[int, int]) -> bytes:
        start = (frame[3] << 8) | frame[4]
        quantity = (frame[5] << 8) | frame[6]
        values = [(frame[8 + i * 2] << 8) | frame[9 + i * 2] for i in range(quantity)]
        for index, value in enumerate(values):
            regs[start + index] = value
            _apply_floor_heating_command(regs, start + index, value)
        self.stats["write"] += 1
        self._count_4g_write(master)
        formatted = " ".join(f"{v:04X}" for v in values)
        self._log(f"批量写入 {slave:#04x}/{start} 共 {quantity} 个: {formatted}")
        return with_crc([slave, 0xFF, 0x10, frame[3], frame[4], frame[5], frame[6]])

    def _count_4g_write(self, master: int) -> None:
        """真机只统计 4G 模块（0x51）的写：模拟行为一致，方便核对 30110。"""
        if master == 0x51:
            outer = self.maps.get(0xF1)
            if outer is not None:
                outer[30110] = (outer.get(30110, 0) + 1) & 0xFFFF

    def _log(self, message: str) -> None:
        if self.verbose:
            stamp = time.strftime("%H:%M:%S")
            print(f"[{stamp}] {message}", flush=True)


def describe(fans_online: List[int], manifolds_online: List[int]) -> str:
    """启动摘要，便于和上位机界面对照。"""
    fan_text = "、".join(str(f) for f in fans_online) or "无"
    manifold_text = "、".join(str(m) for m in manifolds_online) or "无"
    fan_bits = sum(1 << f for f in fans_online)
    manifold_bits = sum(1 << m for m in manifolds_online)
    return (
        f"外机 0xF1（MS-W300）已就绪\n"
        f"  21017 风盘在线位图     = 0x{fan_bits:04X} → 在线风盘：{fan_text}\n"
        f"  21016 分集水器在线位图 = 0x{manifold_bits:04X} → 在线分集水器：{manifold_text}\n"
        f"  风盘模板区 20000+60×n 已按在线风盘填值\n"
        f"  另外应答：0x91~0x9F 风盘、0xA1 地暖、0x51~0x5F MixPad 底壳"
    )


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="W 系列热泵模拟外机（Modbus RTU 从机）")
    parser.add_argument("--port", required=True, help="监听串口，例如 COM14")
    parser.add_argument("--fans", default="1,2,5", help="在线风盘序号，如 1,2,5 或 none")
    parser.add_argument("--manifolds", default="1,3", help="在线分集水器序号，如 1,3 或 none")
    parser.add_argument("--delay", type=float, default=10.0, help="应答延时（毫秒），协议要求 6~40ms")
    parser.add_argument("--quiet", action="store_true", help="不逐帧打印报文")
    args = parser.parse_args(argv)

    fans_online = parse_ids(args.fans)
    manifolds_online = parse_ids(args.manifolds)
    maps = build_all_maps(fans_online, manifolds_online)

    session = SlaveSession(args.port, maps, delay_ms=args.delay, verbose=not args.quiet)
    print(describe(fans_online, manifolds_online), flush=True)
    print(f"监听 {args.port} @9600-8-N-1，应答延时 {args.delay:.0f}ms。Ctrl+C 退出。", flush=True)

    try:
        session.open()
        session.run_forever()
    except KeyboardInterrupt:
        print("\n收到 Ctrl+C，退出。", flush=True)
    except serial.SerialException as error:
        print(f"串口错误：{error}", flush=True)
        return 1
    finally:
        session.close()
        stats = session.stats
        print(f"统计：请求 {stats['request']}，应答 {stats['response']}，写入 {stats['write']}，"
              f"CRC 错误 {stats['bad_crc']}，未知从机 {stats['unknown_slave']}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

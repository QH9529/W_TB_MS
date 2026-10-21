"""可写寄存器审计：把代码里开放的写入地址与协议 V2.4 逐条对照。

检查三类问题：
  1. 代码标了可写、协议里是枚举型，但没有「值含义」表 → 弹窗缺属性提示
  2. 值含义表与协议文案不一致（例如把「禁止除霜/允许除霜」写成「无请求/请求除霜」）
  3. 协议里可写、但代码的 WriteRules 里没有 → 界面写不了

用法: python temp/audit_writable.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from protocol_holdings import load_sheet, PATH  # noqa: E402

import zipfile  # noqa: E402
import xml.etree.ElementTree as ET  # noqa: E402

ROOT = Path(r"D:/1-ORVIBO/4-Utility class/W_TB_MS")
CS = ROOT / "W_TB_MS/MainForm.RegisterTable.cs"
NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"


def holding_sheet_target() -> str:
    z = zipfile.ZipFile(PATH)
    wb = ET.fromstring(z.read("xl/workbook.xml"))
    rels = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
    relmap = {r.get("Id"): r.get("Target") for r in rels}
    for sh in wb.iter(NS + "sheet"):
        if sh.get("name") == "保持寄存器":
            rid = sh.get("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")
            return "xl/" + relmap[rid].lstrip("/").replace("xl/", "", 1)
    raise SystemExit("未找到保持寄存器表")


def block(text: str, marker: str) -> str:
    """截取 `marker = new() { ... };` 之间的内容（按大括号配平）。"""
    start = text.index(marker)
    brace = text.index("{", start)
    depth = 0
    for i in range(brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[brace : i + 1]
    raise SystemExit(f"{marker} 块解析失败")


# ---------- 协议侧 ----------
ENTRY = re.compile(r"(?:0x[0-9A-Fa-f]+|\d+)\s*[：:]")


def protocol_enums(range_text: str, note: str = "") -> dict[int, str]:
    """从「传输值范围」里解析 值→含义。

    行文格式多样：`0x0000：关机（默认） / 0x0001：开机`、
    `0：解除所有故障报警 / X：解除对应故障码报警`（第二项不是数字）、
    `0x0000：否 0x0001：是`（无分隔符）。
    只要文本里出现「数字/十六进制 + 冒号」就认为存在值说明。
    """
    text = range_text
    matches = list(ENTRY.finditer(text))
    if not matches:
        return {}
    found: dict[int, str] = {}
    for index, match in enumerate(matches):
        raw = match.group(0)
        value_text = re.match(r"(?:0x[0-9A-Fa-f]+|\d+)", raw).group(0)
        start = match.end()
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        meaning = re.split(r"[/／\n\r]", text[start:end])[0]
        meaning = re.sub(r"[（(]\s*默认\s*[)）]", "", meaning).strip().strip("，,。;；")
        try:
            value = int(value_text, 16) if value_text.lower().startswith("0x") else int(value_text)
        except ValueError:
            continue
        if meaning:
            found.setdefault(value, meaning)
    return found if ("：" in text or ":" in text) else {}


def main() -> int:
    source = CS.read_text(encoding="utf-8")

    write_block = block(source, "WriteRules = new()")
    write_addrs = {int(a) for a in re.findall(r"\[(\d{5})\]\s*=\s*new\(", write_block)}

    regmap_source = (ROOT / "W_TB_MS/Models/RegisterMap.cs").read_text(encoding="utf-8")
    constants = {
        name: int(value)
        for name, value in re.findall(
            r"public\s+const\s+ushort\s+(\w+)\s*=\s*(\d{5})\s*;", regmap_source
        )
    }

    # 整段 0x10 写入的多寄存器数据块（IMEI / 激活 MD5 / 单机并联模式）也算"已开放写入"，
    # 它们走 BlockWriteRules + 专用对话框，不依赖 WriteRules 的单寄存器范围。
    # 键用的是 RegisterMap 常量，需要经 constants 解析成数值。
    block_write_source = block(source, "BlockWriteRules = new()")
    block_write_addrs = {int(a) for a in re.findall(r"\[(\d{5})\]\s*=\s*new\(", block_write_source)}
    block_write_addrs |= {
        constants[name]
        for name in re.findall(r"\[RegisterMap\.(\w+)\]\s*=\s*new\(", block_write_source)
        if name in constants
    }

    # 40107 同步时间是 4 寄存器 UNIX64 整段写入，走 WriteSyncTimeAsync 专用对话框。
    special_write_addrs = {constants["SYNC_TIME_ADDR"]} if "SYNC_TIME_ADDR" in constants else set()
    opened_addrs = write_addrs | block_write_addrs | special_write_addrs
    # BlockWriteRules 的值里带着起始地址与寄存器个数，界面行是按起始地址注册的。
    block_ranges = [
        (constants.get(name, 0), int(count))
        for name, count in re.findall(
            r"\[RegisterMap\.(\w+)\]\s*=\s*new\(\s*RegisterMap\.\w+,\s*(\d+),", block_write_source
        )
    ]

    meaning_block = block(source, "ParameterValueMeanings = new()")
    meaning_addrs = {int(a) for a in re.findall(r"\[(\d{5})\]\s*=\s*new\(\)", meaning_block)}

    ui_addrs = {int(a) for a in re.findall(r"AddReg\(\s*(\d{5})\s*,", source)}
    ui_addrs |= {
        constants[const]
        for const in re.findall(r"AddReg\(\s*RegisterMap\.(\w+)\s*,", source)
        if const in constants
    }

    # 界面可写性完全由 RegisterMap.IsWritableInUi 决定（见 Models/RegisterMap.cs），这里镜像实现，
    # 反向校验"界面注册地址"里哪些还有写入入口。
    def is_writable_in_ui(addr: int) -> bool:
        if not (40001 <= addr <= 49999):
            return False                                  # GetAccess == ReadOnly
        if 40601 <= addr <= 40699 or 40801 <= addr <= 40899:
            return False                                  # IsDisplayOnlyReadSegment
        if 40401 <= addr <= 40499:
            return False                                  # 已从界面移除的产测段
        if (41001 <= addr <= 41299 or 42001 <= addr <= 42199
                or 43001 <= addr <= 43099):
            return False                                  # GetSlaveDeviceHint != null
        return True

    def is_hidden_device_segment(addr: int) -> bool:
        return 41101 <= addr <= 43006                     # IsHiddenDeviceSegment

    def ui_label(addr: int) -> str:
        if is_hidden_device_segment(addr) or 40401 <= addr <= 40499:
            return "界面已移除"
        if not is_writable_in_ui(addr):
            return "界面只读"
        return "界面可写"


    rows = load_sheet(holding_sheet_target())
    protocol: dict[int, dict] = {}
    for r in sorted(rows):
        cells = rows[r]
        raw = str(cells.get(2, "")).strip()
        if not re.fullmatch(r"\d{5}", raw):
            continue
        addr = int(raw)
        protocol[addr] = {
            "name": str(cells.get(4, "")).replace("\n", " ").strip(),
            "range": str(cells.get(5, "")).replace("\n", " ").strip(),
            "note": str(cells.get(6, "")).replace("\n", " ").strip(),
            "row": r,
        }

    print(f"代码 WriteRules 可写地址 : {len(write_addrs)} 个")
    print(f"代码 值含义表覆盖地址    : {len(meaning_addrs)} 个")
    print(f"界面 AddReg 注册地址     : {len(ui_addrs)} 个")
    print(f"协议 保持寄存器条目      : {len(protocol)} 个")
    print()

    # 1) 可写 + 协议有值说明 + 代码无值含义
    #    40106 的枚举单独放在 RegisterMap.ClearDeviceCmdMap，不算漏。
    #    40101 的值含义由 DescribeClearFaultOptions/DescribeClearFaultMeaning 专用分支提供
    #    （0 = 解除全部故障报警 / X = 解除对应故障码报警，X 复用 RegisterMap.FaultCodeMap），
    #    它的第二项不是数字枚举，不能靠 ParameterValueMeanings 表达，也不算漏。
    standalone_enums = {
        constants.get("CLEAR_DEVICE_INFO_ADDR"),
        constants.get("CLEAR_FAULT_ADDR"),
    }
    print("=" * 100)
    print("【问题1】可写、协议有值说明、但代码没有值含义表（弹窗缺属性提示）")
    print("=" * 100)
    missing = []
    for addr in sorted(write_addrs):
        if addr in meaning_addrs or addr in standalone_enums:
            continue
        info = protocol.get(addr)
        if info is None:
            continue
        enums = protocol_enums(info["range"], info["note"])
        if enums:
            missing.append((addr, info, enums))
    if not missing:
        print("  无")
    for addr, info, enums in missing:
        options = " / ".join(f"{k}={v}" for k, v in sorted(enums.items()))
        print(f"  {addr}  {info['name']}")
        print(f"       协议范围: {info['range'][:110]}")
        print(f"       协议枚举: {options[:130]}")
    print()

    # 2) 值含义与协议文案比对
    print("=" * 100)
    print("【问题2】已有值含义表，但与协议文案不一致（需人工判断是措辞差异还是语义错误）")
    print("=" * 100)
    code_meaning = {}
    for addr in sorted(meaning_addrs):
        seg = re.search(
            rf"\[{addr}\]\s*=\s*new\(\)\s*\{{(.*?)\}}\s*,?\s*(?=\[\d{{5}}\]|$)",
            meaning_block,
            re.S,
        )
        if not seg:
            continue
        pairs = re.findall(r"\[(\d+)\]\s*=\s*\"([^\"]*)\"", seg.group(1))
        code_meaning[addr] = {int(k): v for k, v in pairs}

    mismatch = 0
    for addr, code_map in sorted(code_meaning.items()):
        info = protocol.get(addr)
        if info is None:
            continue
        proto_map = protocol_enums(info["range"], info["note"])
        if not proto_map:
            continue
        rows_out = []
        for value, code_text in sorted(code_map.items()):
            proto_text = proto_map.get(value, "")
            if proto_text and proto_text not in code_text and code_text not in proto_text:
                rows_out.append(f"        值 {value}: 代码「{code_text}」 vs 协议「{proto_text}」")
        if rows_out:
            mismatch += 1
            print(f"  {addr}  {info['name']}")
            print("\n".join(rows_out))
    if mismatch == 0:
        print("  无")
    print()

    # 3) 协议可写、代码未开放
    print("=" * 100)
    print("【问题3】协议里有值定义/可写、但代码没有写入入口（WriteRules 与 BlockWriteRules 都没有）")
    print("=" * 100)
    not_open = []
    for addr, info in sorted(protocol.items()):
        if addr in opened_addrs:
            continue
        enums = protocol_enums(info["range"], info["note"])
        if enums and 40000 <= addr <= 40999:
            not_open.append((addr, info, enums))
    if not not_open:
        print("  无")
    for addr, info, enums in not_open:
        mark = f" [{ui_label(addr)}]"
        print(f"  {addr}  {info['name']}{mark}")
        print(f"       协议范围: {info['range'][:110]}")
    print()

    # 4) 界面显示"写入"、但既不在 WriteRules 也不在 BlockWriteRules（点了没有范围提示/值含义）
    print("=" * 100)
    print("【问题4】界面按协议显示可写、但代码未开放（写入框缺范围与值含义提示）")
    print("=" * 100)
    gap = sorted(
        a for a in ui_addrs
        if a not in opened_addrs and is_writable_in_ui(a)
    )
    if not gap:
        print("  无")
    for addr in gap:
        info = protocol.get(addr)
        name = info["name"] if info else "(协议无此地址)"
        rng = info["range"][:80] if info else ""
        print(f"  {addr}  {name}")
        if rng:
            print(f"       协议范围: {rng}")
    print()

    # 4b) 界面可写性盘点
    print("=" * 100)
    print("【问题4b】界面可写性盘点（判据：RegisterMap.IsWritableInUi 的镜像实现）")
    print("=" * 100)
    writable_rows = sorted(a for a in ui_addrs if is_writable_in_ui(a))
    leftover_hidden = sorted(a for a in ui_addrs if is_hidden_device_segment(a))
    read_only_rows = sorted(
        a for a in ui_addrs
        if not is_writable_in_ui(a) and not is_hidden_device_segment(a) and 40001 <= a <= 49999
    )
    print(f"  界面可写行             : {len(writable_rows)} 个"
          f"（代码已开放 {len([a for a in writable_rows if a in opened_addrs])} 个）")
    print(f"  界面只读行（协议读写） : {len(read_only_rows)} 个 → {read_only_rows}")
    print(f"  仍残留的 41101~43006 行: {len(leftover_hidden)} 个"
          f"（应为 0）{leftover_hidden if leftover_hidden else ''}")
    print(f"  整段 0x10 写入数据块   : "
          + "、".join(f"{start}~{start + count - 1}" for start, count in block_ranges))
    print(f"  专用对话框写入地址     : {sorted(special_write_addrs)}")
    print()

    # 5) WriteRules 里协议查不到的地址
    print("=" * 100)
    print("【问题5】WriteRules / BlockWriteRules 已开放、但协议保持寄存器表里查不到")
    print("=" * 100)
    unknown = sorted(a for a in opened_addrs if a not in protocol)
    if not unknown:
        print("  无")
    for addr in unknown:
        print(f"  {addr}  {'[界面可写]' if is_writable_in_ui(addr) else '[界面不可写]'}")
    print()

    # 6) 已开放但界面没有写入入口
    print("=" * 100)
    print("【问题6】代码已开放写入、但界面没注册（用户没有写入入口）")
    print("=" * 100)
    orphan = sorted(a for a in opened_addrs if a not in ui_addrs)
    if not orphan:
        print("  无")
    for addr in orphan:
        info = protocol.get(addr)
        print(f"  {addr}  {info['name'] if info else ''}")
    print()

    # 7) 界面可写、但模型层判为不可写（IsWritableInUi 与 WriteRules 不一致）
    print("=" * 100)
    print("【问题7】界面可写、但模型层 IsWritableInUi 判为不可写（两处判据打架）")
    print("=" * 100)
    conflict = sorted(a for a in opened_addrs if not is_writable_in_ui(a))
    if not conflict:
        print("  无")
    for addr in conflict:
        info = protocol.get(addr)
        print(f"  {addr}  {info['name'] if info else ''}  [{ui_label(addr)}]")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

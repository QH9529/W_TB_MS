"""从协议 V2.4 的「保持寄存器」表里抽出全部寄存器定义，输出为 TSV。

用法:
    python temp/protocol_holdings.py            # 全部行
    python temp/protocol_holdings.py 40101 40200 # 只看地址区间
"""
from __future__ import annotations

import re
import sys
import zipfile
import xml.etree.ElementTree as ET

PATH = r"D:/1-ORVIBO/4-Utility class/W_TB_MS/W系列485通信协议_V2.4.xlsx"
NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"


def col_idx(ref: str) -> int:
    m = re.match(r"([A-Z]+)", ref)
    n = 0
    for ch in m.group(1):
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def load_sheet(target: str) -> dict:
    z = zipfile.ZipFile(PATH)
    shared = [
        "".join(t.text or "" for t in si.iter(NS + "t"))
        for si in ET.fromstring(z.read("xl/sharedStrings.xml")).iter(NS + "si")
    ]
    root = ET.fromstring(z.read(target))
    rows: dict = {}
    for row in root.iter(NS + "row"):
        for c in row.iter(NS + "c"):
            v = c.find(NS + "v")
            t = c.get("t")
            if t == "s" and v is not None:
                val = shared[int(v.text)]
            elif t == "inlineStr":
                val = "".join(x.text or "" for x in c.iter(NS + "t"))
            elif v is not None:
                val = v.text
            else:
                continue
            rows.setdefault(int(row.get("r")), {})[col_idx(c.get("r"))] = val
    return rows


def main() -> int:
    z = zipfile.ZipFile(PATH)
    wb = ET.fromstring(z.read("xl/workbook.xml"))
    rels = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
    relmap = {r.get("Id"): r.get("Target") for r in rels}
    target = None
    for sh in wb.iter(NS + "sheet"):
        if sh.get("name") == "保持寄存器":
            rid = sh.get("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")
            target = "xl/" + relmap[rid].lstrip("/").replace("xl/", "", 1)
    if target is None:
        print("未找到工作表", file=sys.stderr)
        return 2

    low = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    high = int(sys.argv[2]) if len(sys.argv) > 2 else 99999

    rows = load_sheet(target)
    print("行号\t十进制地址\t十六进制\t设备类型\t功能\t传输值范围\t备注")
    for r in sorted(rows):
        cells = rows[r]
        # 列: 1=设备类型 2=十进制 3=十六进制 4=功能 5=传输值范围 6=备注
        raw_addr = str(cells.get(2, "")).strip()
        if not re.fullmatch(r"\d{4,5}", raw_addr):
            continue
        addr = int(raw_addr)
        if not (low <= addr <= high):
            continue
        def cell(i: int, width: int = 0) -> str:
            text = str(cells.get(i, "")).replace("\n", " / ").replace("\t", " ")
            return text[:width] if width else text
        print(f"{r}\t{addr}\t{cell(3)}\t{cell(1)}\t{cell(4)}\t{cell(5)}\t{cell(6, 120)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

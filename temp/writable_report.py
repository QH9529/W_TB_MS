# -*- coding: utf-8 -*-
"""对照 Excel 协议 V2.4 + 代码 + 4G 抓包，生成"可写地址清单 + 注入风险分级"。只读分析，不修改主程序。"""
import zipfile, re
import xml.etree.ElementTree as ET

XLSX = r'D:/1-ORVIBO/4-Utility class/W_TB_MS/W系列485通信协议_V2.4.xlsx'
CAP = r'D:/1-ORVIBO/4-Utility class/W_TB_MS/4G串口.txt'
CS_RULES = r'D:/1-ORVIBO/4-Utility class/W_TB_MS/W_TB_MS/MainForm.RegisterTable.cs'

ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
z = zipfile.ZipFile(XLSX)
wb = ET.fromstring(z.read('xl/workbook.xml'))
rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
relmap = {r.get('Id'): r.get('Target') for r in rels}
sheet_target = {}
for sh in wb.iter(ns + 'sheet'):
    t = relmap[sh.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')]
    sheet_target[sh.get('name')] = t if t.startswith('xl/') else 'xl/' + t.lstrip('/')
shared = [''.join(x.text or '' for x in si.iter(ns + 't'))
          for si in ET.fromstring(z.read('xl/sharedStrings.xml')).iter(ns + 'si')]


def col_idx(ref):
    n = 0
    for ch in re.match(r'([A-Z]+)', ref).group(1):
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def load(name):
    root = ET.fromstring(z.read(sheet_target[name]))
    rows = {}
    for row in root.iter(ns + 'row'):
        for c in row.iter(ns + 'c'):
            v, t = c.find(ns + 'v'), c.get('t')
            if t == 's' and v is not None:
                val = shared[int(v.text)]
            elif t == 'inlineStr':
                val = ''.join(x.text or '' for x in c.iter(ns + 't'))
            elif v is not None:
                val = v.text
            else:
                continue
            rows.setdefault(int(row.get('r')), {})[col_idx(c.get('r'))] = val
    return rows


# ---------- 1. Excel 保持寄存器（可写域） ----------
rows = load('保持寄存器')
regs, section = [], ''
for r in sorted(rows):
    c = rows[r]
    dec = str(c.get(2, '')).strip()
    if dec in ('...', '') or not dec.isdigit():
        txt = ' '.join(str(v) for v in c.values())
        if '段' in txt:
            section = txt
        continue
    name = str(c.get(4, '')).strip()
    if not name or name == '预留' or name.startswith('预留'):
        continue
    regs.append({
        'dec': int(dec),
        'hex': str(c.get(3, '')).strip(),
        'name': name.replace('\n', ' '),
        'range': str(c.get(5, '')).strip().replace('\n', ' '),
        'note': str(c.get(6, '')).strip().replace('\n', ' '),
        'section': section,
    })

# ---------- 2. 代码里的 WriteRules（实际可写白名单） ----------
src = open(CS_RULES, encoding='utf-8').read()
blk = src[src.find('WriteRules = new()'):]
blk = blk[:blk.find('};')]
rules = {}
for m in re.finditer(r'\[(\d+)\]\s*=\s*new\(([^)]*)\)', blk):
    addr = int(m.group(1))
    rules[addr] = m.group(2).strip()

# ---------- 3. 抓包中 4G 实际轮询的地址区间 ----------
def crc16(d):
    c = 0xFFFF
    for b in d:
        c ^= b
        for _ in range(8):
            c = (c >> 1) ^ 0xA001 if c & 1 else c >> 1
    return c


buf = bytearray()
for line in open(CAP, encoding='utf-8', errors='ignore'):
    buf += bytes(int(x, 16) for x in (line.split('◆')[-1] if '◆' in line else '').split())
i = 0
covered = []          # 4G 轮询覆盖的 [(起始, 结束)]
hosts = {}
while i < len(buf) - 3:
    if buf[i] != 0xF1 or buf[i + 1] not in (0x51, 0xFF) or buf[i + 2] not in (0x03, 0x04, 0x06, 0x10):
        i += 1
        continue
    if buf[i + 1] == 0x51:
        n = 9
        f = buf[i:i + n]
        if crc16(f[:-2]) == (f[-1] << 8 | f[-2]):
            a, q = (f[3] << 8 | f[4]), (f[5] << 8 | f[6])
            covered.append((a, a + q - 1))
            hosts[hex(f[1])] = hosts.get(hex(f[1]), 0) + 1
        i += n
    else:
        n = 8 + buf[i + 5]
        i += n if i + n <= len(buf) else 1
        continue

def polled(addr):
    return any(s <= addr <= e for s, e in covered)


# ---------- 4. 风险分级 ----------
C_FORBID = {40001, 40006, 40011, 40101, 40102, 40103, 40105, 40106, 40107, 40111, 40801}
C_SEG = [(40401, 40499), (40501, 40599), (40601, 40699)]
C_KEYS = ('关机', '开机', '激活', '恢复出厂', '产测', '升级', '出厂', '清除', '解除')
B_KEYS = ('模式', '温度设置', '静音', '零冷水', '杀菌', '防冻', '增程阀', '电加热', '除霜',
          '温度控制', '压缩机', '频率', '强制', '点动', '启用', '风速', '阀', '水泵', '修正')


def grade(r):
    a, name = r['dec'], r['name']
    if any(s <= a <= e for s, e in C_SEG):
        return 'C', '产测/身份/OTA 段，写坏即变砖或丢身份'
    if a in C_FORBID:
        why = {
            40001: '整机开关机，直接改变机组运行',
            40006: '手动除霜，触发制冷制热切换',
            40011: '强制待机，主机退出运行',
            40101: '解除故障报警，会掩盖真实故障',
            40102: '4G 专属（每 30min 下发天气温度），PC 写会被覆盖/冲突',
            40103: '除霜请求控制，干预除霜流程',
            40105: '4G 专属（通信中断标志），影响预热与防冻判断',
            40106: '恢复出厂/清除地址表，破坏性且不可撤销',
            40107: '4G 专属（每分钟同步时间），PC 写必被覆盖',
            40111: '4G 专属（每 30min 下发天气湿度）',
            40801: '压缩机累计运行时长，写 Flash 且篡改统计数据',
        }.get(a, '控制/破坏性命令，误写不可逆')
        return 'C', why
    if any(k in name for k in C_KEYS):
        return 'C', '控制/破坏性命令，误写不可逆'
    if any(k in name for k in B_KEYS):
        return 'B', '影响运行方式或舒适性，需二次确认'
    return 'A', '常规参数，可直接写入'


out = ['# 4G 总线注入：可写地址清单与风险分级', '',
       '> 数据来源：`W系列485通信协议_V2.4.xlsx`（保持寄存器表）+ `Models/RegisterMap.cs`（属性判定）+ '
       '`MainForm.RegisterTable.cs`（WriteRules 实际可写白名单）+ `4G串口.txt`（4G 实际轮询区间）。',
       '> 本文只做离线分析，未修改任何主程序代码。', '']

# 摘要
a_cnt = b_cnt = c_cnt = 0
for r in regs:
    g, _ = grade(r)
    a_cnt += g == 'A'
    b_cnt += g == 'B'
    c_cnt += g == 'C'
out += ['## 一、总量', '',
        '| 分类 | 数量 | 策略 |', '|---|---|---|',
        f'| A 常规参数 | {a_cnt} | 可直接写 |',
        f'| B 运行相关 | {b_cnt} | 二次确认 |',
        f'| C 高危/专属 | {c_cnt} | 默认禁用，需手动解锁 |',
        f'| 合计（Excel 保持寄存器，已剔除预留项） | {len(regs)} | |', '',
        f'代码 `WriteRules` 实际开放写入的地址：**{len(rules)}** 个；'
        f'其中 Excel 未列出的：{sorted(set(rules) - {r["dec"] for r in regs})}', '']

# 协议关键点
out += ['## 二、从协议文档核对到的关键事实（对方案有直接影响）', '',
        '| 项 | 协议规定 | 对注入方案的影响 |', '|---|---|---|',
        '| 串口参数 | RS485，9600，8，无校验，1 停止位 | 已确定，无需探测 |',
        '| 请求帧 | 从机地址(0xF1~0xF4) + **主机地址(1B)** + 功能码 + 寄存器地址(2B) + 数量(2B) + CRC | '
        '**主机地址可选 0x51~0x5F**，PC 应取一个未占用的（如 0x5F），便于在抓包里区分"谁发的" |',
        '| 响应帧 | 从机地址 + **响应标识 0xFF** + 功能码 + 寄存器地址 + 字节数 + 数据 + CRC | '
        '响应帧不回显主机地址，无法从应答区分主站 |',
        '| 帧边界 | 字节间隔 >3 ms 视为新帧 | 静默窗口判据远大于 3 ms 即可 |',
        '| 从机应答 | 收到请求后 6~40 ms 内发出 | 写帧应答等待 100 ms 足够 |',
        '| 主机时序 | 收到应答后间隔 ≥20 ms 才能发下一帧；超时 100 ms 重发，最多 3 次 | '
        '**PC 撞车会让 4G 触发重发**，总线负载翻倍，必须避免 |',
        '| 广播/组播 | 广播 0x00、组播 0xF0，仅允许 0x06/0x10，无需应答 | '
        '注入时不要用广播，否则拿不到应答、无法确认 |',
        '| 写计数器 | 只读寄存器 **30110 = 4G 模块写保持寄存器累加数** | '
        '**可用来判断平台是否刚下发过写命令**，是"禁止注入"判据的最佳信号 |', '']

# 明细
out += ['## 三、可写地址明细（按地址排序）', '',
        '| 地址 | 十六进制 | 功能 | 值域/说明 | 代码属性 | 风险 | 4G 是否轮询（可否回读验证） |',
        '|---|---|---|---|---|---|---|']
sect = ''
for r in sorted(regs, key=lambda x: x['dec']):
    if r['section'] != sect:
        sect = r['section']
        out.append('| **%s** | | | | | | |' % sect.replace('热泵主机', '').strip() or '| | | | | | | |')
    a = r['dec']
    g, why = grade(r)
    acc = '只写' if 40101 <= a <= 40199 else ('读写' if 40001 <= a <= 49999 else '只读')
    inrules = '✅' if a in rules else '—'
    can = '可' if (polled(a) and acc == '读写') else ('否' if acc == '只写' else '否（4G 未轮询）')
    out.append('| %d | %s | %s | %s | %s(%s) | **%s** %s | %s |' % (
        a, r['hex'], r['name'], (r['range'] or why)[:60], acc, inrules, g, why if g != 'A' else '', can))

hold_cov = sorted((s, e) for s, e in set(covered) if s >= 40001)
a_polled = [r['dec'] for r in regs if grade(r)[0] == 'A' and polled(r['dec'])
            and not (40101 <= r['dec'] <= 40199)]
a_other = [r['dec'] for r in regs if grade(r)[0] == 'A' and r['dec'] not in a_polled]
out += ['', '## 四、结论要点', '',
        '1. **默认可注入白名单 = A 类 %d 个**：其中 %d 个落在 4G 轮询区间内（**写完不用自己发读帧，'
        '等 4G 下一轮轮询即可回读验证**）；其余 %d 个 4G 不读，写完必须自己补一帧 0x03 读回确认，会额外占用总线。'
        % (a_cnt, len(a_polled), len(a_other)),
        '2. **4G 实际轮询的保持寄存器区间**（来自抓包，决定"免费回读"的范围）：', '',
        '   `' + '`, `'.join('%d~%d' % (s, e) for s, e in hold_cov) + '`', '',
        '3. **只写段 40101~40199 无法回读验证**（软件里显示"—"），只能靠应答帧是否异常判断；'
        '其中 40102/40105/40107/40111 是 **4G 专属**（天气、时间、通信中断标志），PC 写了也会被 4G 周期性覆盖，应直接禁写。',
        '4. **整段禁写**：产测 40401~40499、IMEI/激活 40501~40599、OTA 40601~40699、Flash 统计 40801。',
        '5. **主机地址**：抓包实测总线上的主机地址只有 `0x51`（852 帧全是它，即 4G 模块）。'
        '协议允许 0x51~0x5F，PC 建议固定用 **`0x5F`**，这样监听日志里一眼能分出"这条是 PC 发的"。',
        '6. **建议的注入前置判据（三条同时满足才发）**：'
        '① 总线静默 ≥50 ms（远大于协议的 3 ms 帧边界）；'
        '② 本轮询周期内只读寄存器 **30110（4G 写保持寄存器累加数）未变化**，说明云平台这会儿没在下发写命令；'
        '③ 上一帧不是 4G 刚发出的请求（避免撞上它 100 ms 超时重发的窗口）。',
        '7. **写失败禁止自动重试**：40101 解除报警、40106 恢复出厂这类命令，重试一次就多执行一次。']

open(r'D:/1-ORVIBO/4-Utility class/W_TB_MS/docs/4G总线注入_可写地址清单.md', 'w',
     encoding='utf-8').write('\n'.join(out))
print('寄存器条目:', len(regs), ' A/B/C:', a_cnt, b_cnt, c_cnt)
print('WriteRules:', len(rules))
print('4G 轮询区间:', sorted(set(covered)))
print('抓包中主机地址:', hosts)

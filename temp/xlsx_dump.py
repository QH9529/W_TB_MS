import zipfile, re
import xml.etree.ElementTree as ET

path = r'D:/1-ORVIBO/4-Utility class/W_TB_MS/W系列485通信协议_V2.4.xlsx'
z = zipfile.ZipFile(path)
ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
wb = ET.fromstring(z.read('xl/workbook.xml'))
rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
relmap = {r.get('Id'): r.get('Target') for r in rels}
sheets = []
for sh in wb.iter(ns + 'sheet'):
    t = relmap[sh.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')]
    if not t.startswith('xl/'):
        t = 'xl/' + t.lstrip('/')
    sheets.append((sh.get('name'), t))

sst = ET.fromstring(z.read('xl/sharedStrings.xml'))
shared = [''.join(t.text or '' for t in si.iter(ns + 't')) for si in sst.iter(ns + 'si')]


def col_idx(ref):
    m = re.match(r'([A-Z]+)', ref)
    n = 0
    for ch in m.group(1):
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def load(target):
    root = ET.fromstring(z.read(target))
    rows = {}
    for row in root.iter(ns + 'row'):
        for c in row.iter(ns + 'c'):
            v = c.find(ns + 'v')
            t = c.get('t')
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


import sys
want = sys.argv[1] if len(sys.argv) > 1 else '协议说明'
start = int(sys.argv[2]) if len(sys.argv) > 2 else 1
end = int(sys.argv[3]) if len(sys.argv) > 3 else 200
for name, target in sheets:
    if name != want:
        continue
    rows = load(target)
    for r in sorted(rows):
        if not (start <= r <= end):
            continue
        cells = rows[r]
        line = [str(cells.get(i, '')).replace('\n', ' / ') for i in range(max(cells) + 1)]
        print('%d|%s' % (r, '|'.join(line)))

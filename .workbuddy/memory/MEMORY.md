# W_TB_MS 项目长期笔记

## 4G 模块 ↔ 主控 RS485 总线协议（已对照 V2.4 协议文档 + 抓包验证，2026-09-29）

协议文档《W系列485通信协议_V2.4.xlsx》"协议说明"页为准（以下已用 `4G串口.txt` 1704 帧 CRC 全部通过验证）：

```
请求帧: 从机地址(0xF1~0xF4) | 主机地址(0x51~0x5F) | 功能码 | 寄存器起始地址(2B) | 寄存器数量(2B) | CRC16(2B)
应答帧: 从机地址           | 响应标识 0xFF       | 功能码 | 寄存器起始地址(2B) | 字节数(1B) | 数据(nB) | CRC16(2B)
```

- **第一字节 0xF1 是从机（热泵主机）地址**（拨码决定 F1~F4），**第二字节 0x51 是主机地址**（4G 模块），
  0xFF 是响应标识，不是地址。早期误判为"F1 同步头 + 方向字节"，已更正。
- CRC16 = Modbus CRC，低字节在前，校验范围覆盖整帧（含从机地址）。
- 串口参数：**9600 / 8 / 无校验 / 1**（文档明确，无需探测）。
- 时序：字节间隔 >3 ms = 新帧；从机 6~40 ms 内应答；主机收到应答后 ≥20 ms 才能发下一帧；
  主机等应答 100 ms，超时重发，最多 3 次。
- 广播 0x00 / 组播 0xF0 只允许 0x06/0x10 且无应答。
- 寄存器编号即协议里的十进制地址（40001 = 0x9C41；30001 = 0x7531）。
- **寻址铁律：寄存器号就是 PDU 地址，不要减 1**（读"寄存器 30001"就发 30001/0x7531）。
  写测试脚本时最容易犯的错是 `start-1`，会导致整段读数错位一格（2026-09-29 踩过）。
- 只读寄存器 **30110 = 4G 模块写保持寄存器累加数**，可用来判断平台是否刚下发过写命令；
  该计数**只统计 4G 模块发出的写**，PC（主机地址 0x5F）写入不会累加。
- 4G 实测只用主机地址 **0x51**；PC 模拟建议用 **0x5F**，便于在监听日志里区分来源。
- 轮询节奏：周期约 2.5 s，忙约 1.08 s，静默约 1.42 s；4G 轮询的保持寄存器区间见
  `docs/4G总线注入_可写地址清单.md`。

## 真机实测基线（COM2，2026-09-29）
- 台上设备：**MixStation W300**（30005~30008 = "MS-W300"），从机 0xF1、拨码 30229=0x0000。
- 全部协议区间（输入 30001/30101/30201/30301、保持 40001/40201/40301/40701/40801/40851、
  外机-4G 20060/21001）**应答正常、CRC 全过，响应 89~181 ms**。
- 外机-4G 区（20060~20080、21001~21017）**0x04 与 0x03 均可读**（协议标注 0x04，轮询用 0x04）。
- **0x10 写多寄存器可用**：写 40107~40110 应答 `F1 FF 10 9C AB 00 04 ... CRC`（回显地址+数量）。
- 参考量：压机频率 79Hz、排气 80.1℃、空调进水 13.5℃、环境 8.9℃、热水出水 40.3℃、
  累计耗电 39.0kWh、压机运行时间 1497s、累计运行 108h、热水器出水 65.00℃。
- 遗留待确认：**30231~30233（AC电压/电流/功率）恒为 0**，需确认是硬件未接交流采样还是机型映射差异。

## 代码侧现状（v1.0.20）
- `Models/RegisterMap.cs`：`GetAccess()` **按协议逐段对齐** → 40101~40199 = 只写；其余 40001~49999 = 读写
  （协议原文：40001 常用读写 / 40201 服务密码 / 40301 工厂密码 / 40401 单板产测密码 / 40501 读写信息 /
  40601 4G_OTA / 40701 用户应用层 RAM / 40801 Flash 持久化，**除 40101 段外都是"读写命令段"**）。
  高危段（40401~40499、40501~40599、40601~40699、40801~40899）用 `IsHighRiskWriteSegment()` 在写入前弹二次确认。
- ⚠️ 风盘 41001~41201 / 地暖 42001~42100 / MixPad 43001~43006 落在 40001~49999，`GetAccess` 判为**读写**，
  但这些寄存器实际在各自从机设备上（`GetSlaveDeviceHint()` 给出归属），UI 用 `forceReadOnly` 展示为只读，
  **写入口由 `WriteRegisterFromRowAsync` 按行"属性"列拦截**（只放行"读写/只写"行），新增只读行务必保持这一约束。
- `MainForm.RegisterTable.cs`：`WriteRules` = 82 个可写地址（含 40701~40703）；另有 **40107 同步时间走独立 0x10 整段写流程**
  （`TryBuildUnix64Words`，支持 "yyyy-MM-dd HH:mm:ss" 或 10 位 UNIX 秒，低字在前），合计可写 83 个。
- 精度规则：`DecimalsForScale`（Scale 10→1 位小数、100→2 位小数）。40701~40703 中 40701 是 ×100（65.00℃）。
- 轮询块（`MainForm.cs` PollBlocks）：30001/30101/30201(42)/30301、40001、40201、40301、40701、
  40801、40851×8（含滤网清洁时间）、40401×2、40501×27（IMEI/激活/MD5/单机并联）、21001×17；
  **风盘模板块由 `BuildPollBlocks()` 动态追加——只为 21017 位图中"在线"的风盘请求**（每台 21 寄存器）。
- **外机-4G 风盘 15 台**：`X = 20000 + 60×n`，n=1~15 对应内机 **0x91~0x9F**；`GetFan4GBaseAddress` /
  `GetFan4GDeviceAddress` / `TryGetFan4GOffset` / `IsFan4GOnline` / `IsManifoldOnline`。
  模板偏移 V2.4：CO2=11/PM25=12/温度=13/湿度=14/型号=19/功能集=20（`GetFan4GAddress(1,0)=20060`）。
- **设备在线过滤**：UI 建 15 个风盘组 + 15 个分集水器组（默认折叠），按 21017/21016 在线位图
  （BIT1~BIT15 = 第 1~15 台，BIT0 保留）决定隐藏/展开；顶栏"显示未检测到的风盘/分集水器"勾选框可强制全显
  （`ApplyDevicePresence` / `SetGroupHidden` / `SetGroupCollapsed`，仅位图变化时才应用，不覆盖用户手动展开）。
- 显示解码：风盘温度 ÷10、型号枚举（0x00AC=自研交流风盘）、故障码 BIT0~BIT9、功能集 BIT0~BIT6；
  风盘硬件/软件版本号 2 寄存器合成；IMEI 与单机并联模式按 9 寄存器 ASCII（`DecodeAscii`）。
- 版本号 1.0.20（发布要改 **5 处**：`VERSION`、csproj 的 `Version`/`AssemblyVersion`/`FileVersion`、`CHANGELOG.md`）。
- **写入二次确认弹窗带"值含义"**：`TryGetValueMeanings` / `FormatValueMeaningList` /
  `DescribeWriteValueMeaning` / `BuildValueMeaningBlock`（`MainForm.RegisterTable.cs`），
  文案含 `参数/寄存器/输入值/协议值/值含义(本次 X = Y)/可选值(1 = 制冷 / 2 = 制热)/风险提示`。
- **40101 手动解除故障报警**不是 0/1 开关：协议为 `0：解除所有故障报警 / X：解除对应故障码报警`（X = 故障码号）。
  走专门分支 `IsClearFaultRegister` / `DescribeFaultCodeRanges` / `DescribeClearFaultOptions` /
  `DescribeClearFaultMeaning`，并接到上面三个出口；故障码名复用 **`RegisterMap.FaultCodeMap`（1~66 连续 + 100，共 67 条）**。
- ⚠️ **界面显示"读写"但 `WriteRules` 未开放的热泵主机段地址**（点写入只有 fallback 提示、无范围/值含义校验，
  值按 `new WriteRule(0, 65535)` 编码，但仍在高危段内会弹二次确认）：
  40401/40402（产测入口）、40501/40510/40511/40519（IMEI/激活/单机并联）、
  40601~40674（OTA 升级段）、40801/40851~40858（累计运行时长与持久化时间戳）。**待拍板：补进 WriteRules 还是改只读。**
- 可写寄存器审计脚本：`temp/audit_writable.py`（+`temp/protocol_holdings.py` 抽协议表），
  6 类差异报告，输出可直接存 `temp/audit_writable_report.txt`；改协议或改 `WriteRules` 后应重跑。
- **从机地址路由**：`ModbusRtuClient` 有显式从机地址重载
  `ReadInputRegisters/ReadHoldingRegisters/WriteSingleRegister/WriteMultipleRegisters(…, byte slaveAddress)`，
  可临时访问 0x91/0xA1/0x51 而**不改动 `SlaveAddress` 属性**（全部在同一把 `_lock` 内，不影响轮询线程）。
  `SlaveAddress`（0xF1）/`MasterAddress`（0x51）默认值不变。

## 模拟外机与端到端验证（2026-09-29）
- **com0com v3.0.0** 已安装（`C:\Program Files (x86)\com0com\`），虚拟串口对 **COM13 ↔ COM14**。
- `temp/sim_odu.py`：Python 写的 W 协议 Modbus **从机**，按帧首字节同时扮演 4 类设备——
  0xF1 热泵 / 0x91~0x9F 风盘 / 0xA1~0xAF 地暖 / 0x51~0x5F MixPad 底壳。
  参数 `--port --fans --manifolds --delay --quiet`；兼容 0x03/0x04/0x06/0x10。
- `temp/sim_selfcheck.py`：COM14 起模拟器、COM13 做上位机，用**与软件相同的轮询块**逐块核对 → **54/54 通过**。
- ⚠️ 模拟器进程**状态是持久的**（写入会改内存 map），所以自检里的可写项必须**先写后读**，不能假设初始值。
- 地暖从机映射：阀门 **42001~42016**，写 **42100=1 会联动全关**（`_apply_floor_heating_command`）。
- 界面自动化验证：`temp/gui_verify.py`（连接+读表）、`temp/ui_toggle_check.py`（勾选框状态）、
  `temp/gui_screenshot.py`（滚轮定位+截图）。用 **pywinauto UIA backend**；
  取勾选框状态必须走原生 `TogglePattern`（`GetCurrentPattern` → `QueryInterface(IUIAutomationTogglePattern)`），
  `click_input()` 不生效；滚动用 `pywinauto.mouse.scroll(coords=anchor, wheel_dist=-3)`。
- ⚠️ **`RegisterToolForm`（寄存器读写工具）是孤儿窗体**：`ui_adjust_v2` 已移除主界面入口按钮，
  全解决方案无 `new RegisterToolForm(...)`。要提供"按从机设备读写"入口，需在主界面新增目标设备选择。
- ⚠️ 编译前必须关掉正在运行的 `W_TB_MS.exe`，否则 apphost 复制失败（MSB3021/MSB3027）。
- ⚠️ **版本号要改 5 处**：`VERSION`、csproj 的 `Version` / `AssemblyVersion` / `FileVersion`、`CHANGELOG.md`。
  标题栏取 `AssemblyVersion`（`Assembly.GetName().Version`），只改 `Version` 标题不会变。
- ⚠️ **UIA 驱动 WinForms 的四个坑**（`temp/ui_write_dialog_shot.py` 已封装解法）：
  ① 别的窗口盖住时会点错目标 → 先 `SetWindowPos(HWND_TOPMOST)`；
  ② `ShowDialog` 的模态窗体不在 `app.windows()` 里 → 用 ctypes `EnumWindows` 按 pid+标题找 HWND；
  ③ `DataGridView` 行虚拟化，滚动后旧元素抛 COMError → 每轮重新扫描（wheel_dist=±3 ≈ 9 行）；
  ④ 分组行 ▶ 折叠时成员行不渲染、永远滚不到 → 先点开分组；滚轮锚点要落在表格上。
  "操作"列在 UIA 里是 Button（不是 DataItem），`操作 行 N`、Value=行属性。

## 设备侧注意事项
- 软件"连接"为主动轮询模式，独占一个 COM 口；监听/注入需额外串口，且注入期间自己是"聋的"，建议双串口。
- 现场接入建议使用隔离型 USB-RS485 转换器。
- 分析脚本（只读分析，不改主程序）：`temp/analyze_bus.py`、`temp/crc_probe.py`、
  `temp/xlsx_dump.py`、`temp/writable_report.py`。
- 真机测试脚本（pyserial，隔离 venv `C:\Users\admin\.workbuddy\binaries\python\envs\default`）：
  `temp/bus_test2.py`（全区间只读体检，**已修正寻址**）、`temp/sync_time_test.py`（0x10 写 40107）、
  `temp/device_presence_test.py`（15 台风盘模板 + 在线位图 + 产测/身份/OTA/Flash 段首寄存器只读探测）。
  （`temp/bus_test.py` 有 `start-1` 的历史 bug，勿再直接用。）
- ⚠️ 解析应答帧时**字节数是 `resp[5]`、数据从 `resp[6]` 开始**；曾误用 `resp[2]`（功能码）导致全部误报"无应答"。

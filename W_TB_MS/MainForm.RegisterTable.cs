using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using W_TB_MS.Modbus;
using W_TB_MS.Models;

namespace W_TB_MS
{
    public partial class MainForm : Form
    {
        // ==================== 寄存器表格初始化 ====================

        private sealed record WriteRule(
            decimal Minimum,
            decimal Maximum,
            decimal Scale = 1,
            bool Signed = false,
            bool AllowFFFF = false);

        private static readonly Dictionary<ushort, WriteRule> WriteRules = new()
        {
            [40001] = new(0, 1), [40002] = new(0, 2), [40003] = new(0, 1),
            [40004] = new(1, 50000, AllowFFFF: true), [40005] = new(0, 360, AllowFFFF: true),
            [40006] = new(0, 1), [40007] = new(0, 1), [40008] = new(0, 1),
            [40009] = new(0, 2), [40010] = new(0, 1), [40011] = new(0, 1),
            [40012] = new(0, 32),
            [40101] = new(0, 65535), [40102] = new(-100, 100, 10, true),
            [40103] = new(0, 1), [40105] = new(0, 1), [40106] = new(0, 3),
            [40111] = new(0, 100, 10),
            [40201] = new(1, 2), [40202] = new(0, 1), [40203] = new(0, 1),
            [40204] = new(10, 25, 10), [40205] = new(5, 20, 10),
            [40206] = new(20, 55, 10), [40207] = new(25, 60, 10),
            [40208] = new(0, 1), [40209] = new(10, 90), [40210] = new(1, 8, 10),
            [40211] = new(1, 8, 10), [40212] = new(0, 1),
            [40214] = new(40, 120), [40215] = new(3, 8, 10), [40216] = new(2, 5, 10),
            [40217] = new(-15, -2, 10, true), [40218] = new(-40, -15, 10, true),
            [40219] = new(5, 45, 10), [40220] = new(5, 15), [40221] = new(25, 90),
            [40222] = new(5, 20, 10), [40223] = new(0, 1),
            [40224] = new(3, 30), [40225] = new(2, 10),
            [40226] = new(0, 1), [40227] = new(0, 1),
            [40228] = new(0, 2), [40229] = new(0, 2), [40230] = new(0, 2),
            [40231] = new(1, 1000), [40232] = new(1, 1000), [40233] = new(1, 1000),
            [40235] = new(1, 1000), [40236] = new(1, 1000), [40237] = new(1, 1000),
            [40238] = new(1, 1000), [40239] = new(0, 600),
            [40301] = new(-5, 5, 10, true), [40302] = new(-5, 5, 10, true),
            [40303] = new(-5, 5, 10, true), [40304] = new(-5, 5, 10, true),
            [40305] = new(30, 140), [40306] = new(0, 80),
            [40307] = new(500, 1000), [40308] = new(3, 10, 10),
            [40309] = new(5, 40), [40310] = new(5, 50),
            [40311] = new(0, 140), [40312] = new(0, 140), [40313] = new(0, 140),
            [40314] = new(0, 140), [40315] = new(0, 140), [40316] = new(0, 140),
            [40317] = new(0, 140), [40318] = new(0, 140), [40319] = new(0, 140),
            [40320] = new(0, 140), [40321] = new(0, 3, 10),
            [40322] = new(30, 70, 10), [40323] = new(0, 3, 10),
            [40324] = new(30, 70, 10),
            [40510] = new(0, 2),
            [40701] = new(0, 100, 100), [40702] = new(0, 100), [40703] = new(0, 65535)
        };

        /// <summary>整段（0x10）写入的多寄存器数据块的编码方式。</summary>
        internal enum BlockWriteKind
        {
            /// <summary>ASCII 数字/字母串，逐字节写入；寄存器内高字节在前（与界面解码对称）。</summary>
            AsciiString,

            /// <summary>16 字节二进制，按 32 位十六进制录入；寄存器内低字节在前（协议原文）。</summary>
            Md5Bytes
        }

        /// <summary>一个需要整段 0x10 写入的多寄存器数据块（单寄存器写入对它没有意义）。</summary>
        internal sealed record BlockWriteDefinition(
            ushort StartAddress,
            int RegisterCount,
            BlockWriteKind Kind,
            string Title,
            string Hint,
            string Placeholder);

        /// <summary>
        /// 热泵主机 405xx 读写信息段里需要整段写入的数据块：
        /// 40501~40509 IMEI、40511~40518 激活 MD5、40519~40527 单机/并联模式。
        /// 40510「室外机激活」是普通单寄存器枚举，走 WriteRules。
        /// </summary>
        private static readonly Dictionary<ushort, BlockWriteDefinition> BlockWriteRules = new()
        {
            [RegisterMap.IMEI_ADDR] = new(
                RegisterMap.IMEI_ADDR, 9, BlockWriteKind.AsciiString,
                "唯一识别码（4G 模块 IMEI 号）",
                "协议：40501~40509 共 9 寄存器（18 字节），15~17 位数字按 ASCII 逐字节写入，\n"
                + "不足 18 字节的部分填 0x00；寄存器内高字节在前。",
                "如 861100000000000"),

            [RegisterMap.ACTIVATION_MD5_START_ADDR] = new(
                RegisterMap.ACTIVATION_MD5_START_ADDR, 8, BlockWriteKind.Md5Bytes,
                "室外机激活 MD5 加密段0（40511~40518）",
                "协议：IMEI 号的 128 位 MD5 值共 16 字节（8 寄存器），寄存器内低字节在前。\n"
                + "可直接输入 32 位十六进制，或输入 15~17 位 IMEI 由本工具计算 MD5。",
                "32 位十六进制，或 15~17 位 IMEI"),

            [RegisterMap.UNIT_MODE_ADDR] = new(
                RegisterMap.UNIT_MODE_ADDR, 9, BlockWriteKind.AsciiString,
                "热泵主机单机/并联模式（40519~40527）",
                "协议：9 寄存器（18 字节）ASCII 数字串。\n"
                + "单机模式写 18 个 '0'（全 0x30）；并联模式写另一台热泵主机的 IMEI 号。",
                "单机模式：000000000000000000")
        };

        /// <summary>取该地址对应的整段写入定义；不是数据块地址则返回 false。</summary>
        internal static bool TryGetBlockWriteDefinition(ushort address, out BlockWriteDefinition? definition)
        {
            if (BlockWriteRules.TryGetValue(address, out BlockWriteDefinition? found))
            {
                definition = found;
                return true;
            }
            definition = null;
            return false;
        }

        // Hold-register values with protocol-defined state meanings. Other
        // registers remain displayed using their numeric/scaled value.
        private static readonly Dictionary<ushort, Dictionary<ushort, string>> ParameterValueMeanings = new()
        {
            [40001] = new() { [0] = "关机", [1] = "开机" },
            [40002] = new() { [0] = "普通", [1] = "静音", [2] = "超级静音" },
            [40003] = new() { [0] = "关闭", [1] = "打开" },
            [40006] = new() { [0] = "关闭", [1] = "打开" },
            [40007] = new() { [0] = "关闭", [1] = "运行" },
            [40008] = new() { [0] = "关闭", [1] = "运行" },
            [40009] = new() { [0] = "关闭", [1] = "打开" },
            [40010] = new() { [0] = "关闭", [1] = "打开" },
            [40011] = new() { [0] = "关闭", [1] = "强制待机" },
            [40103] = new() { [0] = "禁止除霜", [1] = "允许除霜" },
            [40105] = new() { [0] = "未超过2小时", [1] = "已超过2小时" },
            [40201] = new() { [1] = "制冷", [2] = "制热" },
            [40202] = new() { [0] = "禁用", [1] = "启用" },
            [40203] = new() { [0] = "不支持", [1] = "支持" },
            [40208] = new() { [0] = "进水温度", [1] = "出水温度" },
            [40212] = new() { [0] = "节能", [1] = "舒适" },
            [40223] = new() { [0] = "持续运行", [1] = "交替运行" },
            [40226] = new() { [0] = "禁用", [1] = "启用" },
            [40227] = new() { [0] = "禁用", [1] = "启用" },
            [40228] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40229] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40230] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40510] = new() { [0] = "未激活", [1] = "允许使用", [2] = "禁止使用" },
            [40601] = new() { [0] = "否", [1] = "是" },
            [40602] = new() { [1] = "热泵空调外机" },
            [30506] = new()
            {
                [0] = "空闲", [1] = "准备升级", [2] = "正在接收", [3] = "正在部署",
                [4] = "升级成功", [5] = "升级包校验错误", [6] = "升级文件校验错误",
                [7] = "Flash写入错误", [8] = "软件版本不符"
            }
        };

        private void InitRegisterTable()
        {
            // 分组标题用灰色背景行
            void AddGroup(string title)
            {
                int idx = dgvData.Rows.Add("▶ " + title, "", "", "", "", "");
                dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                dgvData.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(220, 225, 235);
                dgvData.Rows[idx].DefaultCellStyle.Font = new Font("Microsoft YaHei", 9, FontStyle.Bold);
                dgvData.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(40, 40, 120);
                dgvData.Rows[idx].DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 225, 235);
                dgvData.Rows[idx].DefaultCellStyle.SelectionForeColor = Color.FromArgb(40, 40, 120);
                _groupRows.Add(idx);
                _groupCollapsed[idx] = false;
            }

            void AddReg(ushort addr, string name, string unit, bool forceReadOnly = false)
            {
                // 41101~43006（风盘设置/产测、地暖控制、MixPad 网络管理）在各自从机设备上，
                // 主界面连热泵主机地址读不到也写不了，整段不展示。
                if (RegisterMap.IsHiddenDeviceSegment(addr))
                    return;

                // 行的可写性完全由 RegisterMap.IsWritableInUi 决定：协议可写、又不是
                // "只读展示段"(4G_OTA / Flash) 与已移除的产测段、也不属于其它从机设备。
                // forceReadOnly 仅用于显式再收紧（风盘/地暖/底壳段）。
                RegisterAccess access = forceReadOnly || !RegisterMap.IsWritableInUi(addr)
                    ? RegisterAccess.ReadOnly
                    : RegisterMap.GetAccess(addr);
                string accessText = access switch
                {
                    RegisterAccess.ReadWrite => "读写",
                    RegisterAccess.WriteOnly => "只写",
                    _ => "只读"
                };
                string currentValue = access == RegisterAccess.WriteOnly ? "—" : "--";
                string actionText = access == RegisterAccess.ReadWrite ? "写入" : "执行";
                int idx = dgvData.Rows.Add("   " + name, currentValue, unit, addr.ToString(), accessText, actionText);
                dgvData.Rows[idx].Tag = addr;
                if (access == RegisterAccess.ReadOnly)
                    dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                _rowMap[addr] = idx;
            }

            void AddBitReg(ushort addr, int bit, string name)
            {
                int idx = dgvData.Rows.Add(
                    "   " + name,
                    "--",
                    "位",
                    $"{addr} BIT{bit}",
                    "只读",
                    "");
                dgvData.Rows[idx].Tag = (addr, bit);
                dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                _bitRowMap[(addr, bit)] = idx;
            }

            void AddFaultBits(ushort address, Dictionary<int, string> definitions)
            {
                foreach (var definition in definitions.OrderBy(item => item.Key))
                    AddBitReg(address, definition.Key, definition.Value);
            }

            void AddStatusBits(ushort address, Dictionary<int, BitDefinition> definitions)
            {
                foreach (var definition in definitions.OrderBy(item => item.Key))
                    AddBitReg(address, definition.Key, definition.Value.Name);
            }

            // --- 版本信息 ---
            AddGroup("【版本信息】");
            AddReg(30001, "主控板硬件版本号", "");
            AddReg(30003, "主控板软件版本号", "");
            AddReg(30005, "产品型号", "");
            AddReg(30009, "压缩机驱动板软件版本号", "");
            AddReg(30011, "风机驱动板软件版本号", "");

            // --- 故障报警 ---
            AddGroup("【故障报警状态】");
            AddFaultBits(30101, RegisterMap.FaultReg1Bits);
            AddFaultBits(30102, RegisterMap.FaultReg2Bits);
            AddFaultBits(30103, RegisterMap.FaultReg3Bits);
            AddFaultBits(30104, RegisterMap.FaultReg4Bits);
            AddFaultBits(30105, RegisterMap.FaultReg5Bits);

            // --- 工作状态 ---
            AddGroup("【工作状态】");
            AddStatusBits(30106, RegisterMap.StatusWordBits);
            AddStatusBits(30201, RegisterMap.DeviceStatusBits);
            AddReg(30221, "压缩机频率", "Hz");
            AddReg(30234, "压缩机目标频率", "Hz");
            AddReg(30109, "压缩机频率（快速）", "Hz");
            AddReg(30222, "上风机转速", "RPM");
            AddReg(30225, "下风机转速", "RPM");
            // The value cell includes the minute/second suffix for these durations.
            AddReg(30235, "压缩机运行时间", "");
            AddReg(30237, "压缩机停止时间", "");
            AddReg(30219, "主路EXV开度", "步");
            AddReg(30220, "辅路EXV开度", "步");
            AddReg(30223, "电动三通球阀1", "步");
            AddReg(30224, "电动三通球阀2", "步");

            AddGroup("【通信与升级状态】");
            AddReg(30107, "热泵主机升级状态", "");
            AddReg(30108, "空调进水温度（快速）", "℃");
            AddReg(30110, "4G写保持寄存器计数", "次");
            AddStatusBits(30229, RegisterMap.DipSwitchBits);

            // --- 本地故障记录 ---
            AddGroup("【本地故障记录】");
            AddReg(RegisterMap.FAULT_HISTORY_START_ADDR, "启动本地故障数据读取流程", "");
            AddReg(RegisterMap.FAULT_HISTORY_TOTAL_ADDR, "本地故障数据总条数", "条");
            AddReg(RegisterMap.FAULT_HISTORY_READ_COUNT_ADDR, "本地故障数据已读条数", "条");
            AddReg(RegisterMap.FAULT_HISTORY_BATCH_COUNT_ADDR, "本地故障数据本次传送条数", "条");
            AddReg(RegisterMap.FAULT_HISTORY_RECORD1_ADDR, "故障数据1(记录区30305~30354,每条5寄存器)", "");

            // --- 温度数据 ---
            AddGroup("【温度数据】");
            AddReg(30202, "排气温度", "℃");
            AddReg(30203, "热回收出口温度", "℃");
            AddReg(30204, "中盘温度", "℃");
            AddReg(30205, "出盘温度", "℃");
            AddReg(30206, "经济器进口温度", "℃");
            AddReg(30207, "经济器出口温度", "℃");
            AddReg(30208, "吸气温度", "℃");
            AddReg(30209, "空调进水温度", "℃");
            AddReg(30210, "空调出水温度", "℃");
            AddReg(30211, "环境温度", "℃");
            AddReg(30212, "热水进水温度", "℃");
            AddReg(30213, "热水出水温度", "℃");
            AddReg(30214, "热水温度", "℃");
            AddReg(RegisterMap.HW_UPPER_TEMP_ADDR, "热水上温度", "℃");
            AddReg(RegisterMap.HW_MID_TEMP_ADDR, "热水中温度", "℃");
            AddReg(RegisterMap.HW_LOWER_TEMP_ADDR, "热水下温度", "℃");
            AddReg(30242, "压缩机IPM模块温度", "℃");
            AddReg(30215, "低压压力", "bar");
            AddReg(30216, "高压压力", "bar");
            AddReg(30217, "累计耗电量", "kWh");

            AddGroup("【电气数据】");
            AddReg(30231, "AC电压", "V");
            AddReg(30232, "AC电流", "A");
            AddReg(30233, "当前功率", "W");
            AddReg(30239, "DC电压", "V");
            AddReg(30240, "压缩机电流", "A");
            AddReg(30241, "DC风机电流", "A");

            // --- 保持寄存器（常用命令） ---
            AddGroup("【常用命令 - 保持寄存器】");
            AddReg(40001, "开关机", "");
            AddReg(40002, "静音模式设置", "");
            AddReg(40003, "零冷水开关", "");
            AddReg(40004, "零冷水开启时长", "min");
            AddReg(40005, "零冷水剩余时长", "min");
            AddReg(40006, "手动除霜", "");
            AddReg(40007, "热水杀菌开关", "");
            AddReg(40008, "防冻电加热开关", "");
            AddReg(40009, "热水增程阀开关（W180）", "");
            AddReg(40010, "零冷水点动开关", "");
            AddReg(40011, "主机强制待机（W180）", "");
            AddReg(40012, "室内设备运行数量（W180）", "台");

            // --- 服务密码段 ---
            AddGroup("【服务密码段 - 温度设置】");
            AddReg(40201, "工作模式设置", "");
            AddReg(40202, "生活热水启用", "");
            AddReg(40203, "零冷水泵支持状态", "");
            AddReg(40204, "制冷进水温度设置", "℃");
            AddReg(40205, "制冷出水温度设置", "℃");
            AddReg(40206, "制热进水温度设置", "℃");
            AddReg(40207, "制热出水温度设置", "℃");
            AddReg(40208, "温度控制依据", "");
            AddReg(40209, "能力计算周期", "s");
            AddReg(40210, "空调停机温差", "℃");
            AddReg(40211, "空调启动温差", "℃");
            AddReg(40212, "生活热水模式", "");
            AddReg(40214, "空调水泵预运行时间", "s");
            AddReg(40215, "冬季防冻温度", "℃");
            AddReg(40216, "制冷防冻温度", "℃");
            AddReg(40217, "除霜检测A点（DFA）", "℃");
            AddReg(40218, "除霜检测B点（DFB）", "℃");
            AddReg(40219, "除霜退出温度", "℃");
            AddReg(40220, "除霜最长运行时间", "min");
            AddReg(40221, "除霜间隔时间", "min");
            AddReg(40222, "允许除霜最高环温", "℃");
            AddReg(40223, "待机水泵运行方式", "");
            AddReg(40224, "待机水泵交替间隔", "min");
            AddReg(40225, "待机水泵交替运行时间", "min");
            AddReg(40226, "压缩机预热启用", "");
            AddReg(40227, "断电记忆启用", "");
            AddReg(40228, "空调循环水泵开关", "");
            AddReg(40229, "热水水泵开关", "");
            AddReg(40230, "零冷水水泵开关", "");
            AddReg(40231, "零冷水点动运行时长", "min");
            AddReg(40232, "零冷水水泵开启时长", "min");
            AddReg(40233, "零冷水水泵关闭时长", "min");
            AddReg(40235, "零冷水整点启动时长", "s");
            AddReg(40236, "零冷水泵启动水温", "℃");
            AddReg(40237, "零冷水启动持续时间", "s");
            AddReg(40238, "热水进水不可用温度", "℃");
            AddReg(40239, "空调水泵延时打开时长（W180）", "s");

            // --- 工厂密码段 ---
            AddGroup("【工厂密码段】");
            AddReg(40301, "制热进水温度补偿", "℃");
            AddReg(40302, "制热出水温度补偿", "℃");
            AddReg(40303, "制冷进水温度补偿", "℃");
            AddReg(40304, "制冷出水温度补偿", "℃");
            AddReg(40305, "压缩机最高频率", "Hz");
            AddReg(40306, "压缩机最低频率", "Hz");
            AddReg(40307, "风机最高转速", "RPM");
            AddReg(40308, "目标过热度", "℃");
            AddReg(40309, "主路EXV控制周期", "s");
            AddReg(40310, "辅路EXV控制周期", "s");
            AddReg(40311, "制冷共振频率1", "Hz");
            AddReg(40312, "制冷共振频率2", "Hz");
            AddReg(40313, "制冷共振频率3", "Hz");
            AddReg(40314, "制冷共振频率4", "Hz");
            AddReg(40315, "制冷共振频率5", "Hz");
            AddReg(40316, "制热共振频率1", "Hz");
            AddReg(40317, "制热共振频率2", "Hz");
            AddReg(40318, "制热共振频率3", "Hz");
            AddReg(40319, "制热共振频率4", "Hz");
            AddReg(40320, "制热共振频率5", "Hz");
            AddReg(40321, "制热最高水温修正K1", "");
            AddReg(40322, "制热最高水温修正K2", "℃");
            AddReg(40323, "制热最高水温修正K3", "");
            AddReg(40324, "制热最高水温修正K4", "℃");

            // --- 只写命令 ---
            AddGroup("【只写命令】");
            AddReg(40101, "手动解除故障报警", "");
            AddReg(40102, "同步天气温度(4G)", "℃");
            AddReg(40103, "除霜请求控制", "");
            AddReg(40105, "通信中断超2小时标志", "");
            AddReg(40106, "清除设备信息", "");
            AddReg(RegisterMap.SYNC_TIME_ADDR, "同步时间给热泵主机(4G,40107~40110 共4寄存器UNIX64)", "");
            AddReg(40111, "同步天气湿度(4G)", "%");

            // --- 用户应用层数据读写命令段（RAM）40701~40799 ---
            AddGroup("【用户应用层数据读写段 RAM（40701~40799）】");
            AddReg(RegisterMap.HEATER_OUT_TEMP_ADDR, "热水器出水温度", "℃");
            AddReg(RegisterMap.HEATER_CAPACITY_ADDR, "热水器热水量", "%");
            AddReg(RegisterMap.HEATER_HEAT_TIME_ADDR, "热水器加热时间", "min");

            // --- Flash 持久化数据段 40801~40899（设备自写，界面只读） ---
            AddGroup("【Flash 持久化数据段（40801~40899）· 只读】");
            AddReg(RegisterMap.COMP_RUNTIME_ADDR, "压缩机累计运行时长", "h");
            AddReg(RegisterMap.STERILIZE_DONE_TIME_START_ADDR, "热水杀菌完成时间(UNIX64)", "");
            AddReg(RegisterMap.FAN_FILTER_CLEAN_TIME_START_ADDR, "风机滤网清洁时间(UNIX64)", "");

            // --- 产测与本地OTA状态 ---
            AddGroup("【产测与本地OTA状态】");
            AddReg(RegisterMap.BOARD_TEST_STEP_ADDR, "单板产测步骤", "");
            AddReg(RegisterMap.LOCAL_OTA_ALLOWED_ADDR, "设备是否可升级(OTA Flag)", "");
            AddReg(RegisterMap.LOCAL_OTA_PACKAGE_SIZE_ADDR, "升级包大小", "");
            AddReg(RegisterMap.LOCAL_OTA_PACKET_INDEX_ADDR, "请求包序号", "");
            AddReg(RegisterMap.LOCAL_OTA_STATUS_ADDR, "热泵主机升级状态(30506)", "");

            // --- 机组设备地址表 ---
            AddGroup("【机组设备485地址表】");
            AddReg(RegisterMap.DEVICE_ADDRESS_TABLE_START_ADDR, "机组设备485地址表(设备0/1地址,共32寄存器)", "");

            // --- 热泵主机读写信息段 40501~40599（IMEI / 室外机激活 / 单机并联模式） ---
            AddGroup("【读写信息段：IMEI/激活（40501~40599）· 可写入，写前二次确认】");
            AddReg(RegisterMap.IMEI_ADDR, "唯一识别码(4G IMEI,9寄存器整段写入)", "");
            AddReg(RegisterMap.OUTDOOR_ACTIVATE_ADDR, "室外机激活", "");
            AddReg(RegisterMap.ACTIVATION_MD5_START_ADDR, "室外机激活MD5加密段0~7(8寄存器整段写入)", "");
            AddReg(RegisterMap.UNIT_MODE_ADDR, "热泵主机单机/并联模式(9寄存器整段写入)", "");

            // --- 热泵主机 4G_OTA 段 40601~40699（升级由 4G 通道驱动，界面只读） ---
            AddGroup("【4G_OTA 段（40601~40699）· 只读】");
            AddReg(RegisterMap.OTA_START_ADDR, "是否启动新的升级", "");
            AddReg(RegisterMap.OTA_TYPE_ADDR, "升级类型", "");
            AddReg(RegisterMap.OTA_VERSION_ADDR, "升级软件版本", "");
            AddReg(RegisterMap.OTA_FILE_SIZE_ADDR, "升级文件大小", "B");
            AddReg(RegisterMap.OTA_PACKET_NUM_ADDR, "当前升级包序号", "");
            AddReg(RegisterMap.OTA_PACKET_LEN_ADDR, "当前升级包长度", "B");
            AddReg(RegisterMap.OTA_PACKET_DATA_ADDR, "当前升级包数据(64寄存器)", "");
            AddReg(RegisterMap.OTA_PACKET_CRC_ADDR, "当前升级包校验码", "");

            // --- 风盘/地暖/MixPad 设备段（寄存器在各从机设备上，PC 连热泵主机地址时读不到，仅作协议参照） ---
            AddGroup("【风盘控制段（风盘设备 0x91~0x9F）】");
            AddReg(RegisterMap.FAN_SHUTDOWN_ADDR, "风盘关机", "", true);
            AddReg(RegisterMap.FAN_VALVE_SWITCH_ADDR, "风盘阀开关", "", true);
            AddReg(RegisterMap.FAN_SPEED_SET_ADDR, "风盘风速设置", "", true);
            AddReg(RegisterMap.FRESH_AIR_SPEED_ADDR, "新风风速", "", true);
            AddReg(RegisterMap.ANION_SWITCH_ADDR, "负离子除菌", "", true);
            AddReg(RegisterMap.E_HEATER_SWITCH_ADDR, "电加热", "", true);
            AddReg(RegisterMap.FAN_3D_VERT_ADDR, "3D风口上下扫风", "", true);
            AddReg(RegisterMap.FAN_3D_HORIZ_ADDR, "3D风口左右扫风", "", true);
            AddReg(RegisterMap.FRESH_AIR_RUNTIME_ADDR, "新风运行时长", "h", true);
            AddReg(RegisterMap.FAN_FAULT_CODE_ADDR, "风盘故障码", "", true);
            AddReg(RegisterMap.CO2_CONCENTRATION_ADDR, "CO2浓度", "ppm", true);
            AddReg(RegisterMap.PM25_CONCENTRATION_ADDR, "PM2.5浓度", "μg/m³", true);
            AddReg(RegisterMap.FAN_TEMP_ADDR, "风盘温度", "℃", true);
            AddReg(RegisterMap.FAN_HUMIDITY_ADDR, "风盘湿度", "%", true);
            AddReg(RegisterMap.FAN_SPEED_RESET_ADDR, "重置风盘电机转速限值", "", true);

            // --- 41101~43006（风盘设置/产测、地暖控制、MixPad 网络管理）已整段从界面移除 ---
            AddGroup("【风盘/地暖版本信息（设备本地）】");
            AddReg(RegisterMap.FAN_HW_VERSION_ADDR, "风盘硬件版本号", "", true);
            AddReg(RegisterMap.FAN_SW_VERSION_ADDR, "风盘软件版本号", "", true);
            AddReg(RegisterMap.FAN_MODEL_ADDR, "风盘型号", "", true);
            AddReg(RegisterMap.FAN_FEATURE_SET_ADDR, "风盘功能集", "", true);
            AddReg(RegisterMap.FLOOR_HEATING_HW_VERSION_ADDR, "地暖硬件版本号", "", true);
            AddReg(RegisterMap.FLOOR_HEATING_SW_VERSION_ADDR, "地暖软件版本号", "", true);
            AddReg(RegisterMap.FLOOR_HEATING_FAULT_ADDR, "地暖故障码", "", true);

            AddGroup("【MixPad 只读信息（底壳本地）】");
            AddReg(RegisterMap.MIXPAD_SCAN_ADDR, "设备扫描地址", "", true);
            AddReg(RegisterMap.MIXPAD_CMD_FLAG_ADDR, "有无命令数据标识", "", true);
            AddReg(RegisterMap.MIXPAD_CMD_DATA_START_ADDR, "缓存的命令数据1(区33101~33199)", "", true);

            // --- 外机-4G 数据交换区（协议"外机-4G"表，功能码0x04只读） ---
            // 风盘模板：X = 20000 + 60×n，n=0 保留，n=1~15 依次对应内机 0x91~0x9F。
            // 15 台的分组与行全部建好，再按 21017（风盘在线状态 BIT1~BIT15）决定显示与轮询：
            // 未检测到的整组隐藏（可用"显示未检测到的设备"开关强制显示），已检测到的自动展开。
            for (int fan = 1; fan <= RegisterMap.FAN_4G_MAX_COUNT; fan++)
            {
                int fanIndex = fan;
                ushort Fan(ushort offset) => RegisterMap.GetFan4GAddress(fanIndex, offset);

                AddGroup($"【外机-4G 风盘{fan}（内机0x{RegisterMap.GetFan4GDeviceAddress(fan):X2}）· 状态未知】");
                int groupRow = dgvData.Rows.Count - 1;
                _fan4GGroupRows[fan] = groupRow;
                SetGroupCollapsed(groupRow, true);

                AddReg(Fan(RegisterMap.FAN_4G_VALVE_OFFSET), "风盘阀开关", "");
                AddReg(Fan(RegisterMap.FAN_4G_SPEED_OFFSET), "风盘风速设置(0~100无极)", "");
                AddReg(Fan(RegisterMap.FAN_4G_FRESH_AIR_OFFSET), "新风风速(0~100)", "");
                AddReg(Fan(RegisterMap.FAN_4G_ANION_OFFSET), "负离子除菌", "");
                AddReg(Fan(RegisterMap.FAN_4G_HEATER_OFFSET), "电加热", "");
                AddReg(Fan(RegisterMap.FAN_4G_VERTICAL_SWING_OFFSET), "3D风口上下扫风(0~6)", "");
                AddReg(Fan(RegisterMap.FAN_4G_HORIZONTAL_SWING_OFFSET), "3D风口左右扫风(0~6)", "");
                AddReg(Fan(RegisterMap.FAN_4G_FRESH_AIR_RUNTIME_OFFSET), "新风运行时长", "h");
                AddReg(Fan(RegisterMap.FAN_4G_FAULT_CODE_OFFSET), "风盘故障码(BIT0~BIT9)", "");
                AddReg(Fan(RegisterMap.FAN_4G_CO2_OFFSET), "CO2浓度", "ppm");
                AddReg(Fan(RegisterMap.FAN_4G_PM25_OFFSET), "PM2.5浓度", "μg/m³");
                AddReg(Fan(RegisterMap.FAN_4G_TEMPERATURE_OFFSET), "温度", "℃");
                AddReg(Fan(RegisterMap.FAN_4G_HUMIDITY_OFFSET), "湿度", "%");
                AddReg(Fan(RegisterMap.FAN_4G_HW_VERSION_OFFSET), "硬件版本号(H/ML)", "");
                AddReg(Fan(RegisterMap.FAN_4G_SW_VERSION_OFFSET), "软件版本号(H/ML)", "");
                AddReg(Fan(RegisterMap.FAN_4G_MODEL_OFFSET), "风盘型号", "");
                AddReg(Fan(RegisterMap.FAN_4G_FEATURE_SET_OFFSET), "功能集(BIT0~BIT6)", "");
            }

            // 分集水器状态：21001~21015 依次对应 1~15 号，按 21016 在线位图决定显示。
            for (int manifold = 1; manifold <= RegisterMap.MANIFOLD_MAX_COUNT; manifold++)
            {
                AddGroup($"【外机-4G 分集水器{manifold}· 状态未知】");
                int groupRow = dgvData.Rows.Count - 1;
                _manifoldGroupRows[manifold] = groupRow;
                SetGroupCollapsed(groupRow, true);
                AddReg(RegisterMap.GetManifold4GStatusAddress(manifold), "状态(BIT0~BIT7=1~8号阀门)", "");
            }

            AddGroup("【外机-4G 从机在线状态】");
            AddReg(RegisterMap.MANIFOLD_ONLINE_STATUS_ADDR, "分集水器在线状态(BIT1~BIT15)", "");
            AddReg(RegisterMap.FAN_4G_ONLINE_STATUS_ADDR, "风盘在线状态(BIT1~BIT15)", "");
        }

        // ==================== 分组折叠 / 从机在线呈现 ====================

        /// <summary>折叠/展开指定分组标题行及其成员行（成员行以"下一个分组行"为界）。</summary>
        private void SetGroupCollapsed(int groupIdx, bool collapse)
        {
            if (groupIdx < 0 || groupIdx >= dgvData.Rows.Count || !_groupRows.Contains(groupIdx))
                return;

            _groupCollapsed[groupIdx] = collapse;
            bool hidden = _groupHidden.Contains(groupIdx);
            SetGroupTitleText(groupIdx, StripGroupMarker(dgvData.Rows[groupIdx].Cells[0].Value?.ToString()));
            dgvData.Rows[groupIdx].Visible = !hidden;
            for (int i = groupIdx + 1; i < dgvData.Rows.Count; i++)
            {
                if (_groupRows.Contains(i))
                    break;
                dgvData.Rows[i].Visible = !collapse && !hidden;
            }
        }

        /// <summary>整组显示/隐藏（用于"未检测到设备"的过滤）。</summary>
        private void SetGroupHidden(int groupIdx, bool hidden)
        {
            if (groupIdx < 0 || groupIdx >= dgvData.Rows.Count || !_groupRows.Contains(groupIdx))
                return;

            if (hidden)
                _groupHidden.Add(groupIdx);
            else
                _groupHidden.Remove(groupIdx);

            bool collapsed = _groupCollapsed.TryGetValue(groupIdx, out bool value) && value;
            dgvData.Rows[groupIdx].Visible = !hidden;
            for (int i = groupIdx + 1; i < dgvData.Rows.Count; i++)
            {
                if (_groupRows.Contains(i))
                    break;
                dgvData.Rows[i].Visible = !hidden && !collapsed;
            }
        }

        /// <summary>设置分组标题（保留 ▶/▼ 折叠标记）。</summary>
        private void SetGroupTitleText(int groupIdx, string title)
        {
            bool collapsed = _groupCollapsed.TryGetValue(groupIdx, out bool value) && value;
            string text = (collapsed ? "▶ " : "▼ ") + title;
            if (!string.Equals(dgvData.Rows[groupIdx].Cells[0].Value?.ToString(), text, StringComparison.Ordinal))
                dgvData.Rows[groupIdx].Cells[0].Value = text;
        }

        private static string StripGroupMarker(string? cellText)
        {
            string text = cellText ?? string.Empty;
            int index = 0;
            while (index < text.Length && (text[index] == '▶' || text[index] == '▼' || text[index] == ' '))
                index++;
            return text[index..];
        }

        /// <summary>
        /// 按从机在线位图刷新风盘/分集水器分组的可见性、标题与折叠状态。
        /// 未检测到的分组默认整组隐藏（勾选"显示未检测到的设备"后可见并保持折叠），
        /// 新检测到上线的设备自动展开，并从此加入轮询。
        /// </summary>
        private void ApplyDevicePresence(
            int fanMask,
            int manifoldMask,
            int newlyOnlineFan,
            int newlyOnlineManifold)
        {
            foreach (var pair in _fan4GGroupRows)
            {
                int fan = pair.Key;
                bool online = (fanMask & (1 << fan)) != 0;
                bool visible = _showUndetectedDevices || !_fan4GOnlineKnown || online;
                SetGroupHidden(pair.Value, !visible);
                if (visible)
                {
                    string state = !_fan4GOnlineKnown ? "状态未知" : online ? "在线" : "未检测到";
                    SetGroupTitleText(pair.Value,
                        $"【外机-4G 风盘{fan}（内机0x{RegisterMap.GetFan4GDeviceAddress(fan):X2}）· {state}】");
                }
                if ((newlyOnlineFan & (1 << fan)) != 0)
                    SetGroupCollapsed(pair.Value, false);
            }

            foreach (var pair in _manifoldGroupRows)
            {
                int manifold = pair.Key;
                bool online = (manifoldMask & (1 << manifold)) != 0;
                bool visible = _showUndetectedDevices || !_manifoldOnlineKnown || online;
                SetGroupHidden(pair.Value, !visible);
                if (visible)
                {
                    string state = !_manifoldOnlineKnown ? "状态未知" : online ? "在线" : "未检测到";
                    SetGroupTitleText(pair.Value, $"【外机-4G 分集水器{manifold}· {state}】");
                }
                if ((newlyOnlineManifold & (1 << manifold)) != 0)
                    SetGroupCollapsed(pair.Value, false);
            }
        }

        /// <summary>"显示未检测到的设备"开关切换。</summary>
        private void SetShowUndetectedDevices(bool show)
        {
            _showUndetectedDevices = show;
            ApplyDevicePresence(
                Volatile.Read(ref _onlineFanMask),
                Volatile.Read(ref _onlineManifoldMask),
                0,
                0);
        }

        private async Task WriteRegisterFromRowAsync(int rowIndex)
        {
            if (_writeInProgress || rowIndex < 0 || rowIndex >= dgvData.Rows.Count)
                return;
            if (dgvData.Rows[rowIndex].Tag is not ushort address)
                return;

            DataGridViewRow targetRow = dgvData.Rows[rowIndex];
            // 行级写权限：风盘(41001~41201)/地暖(42001~42100)/MixPad(43001~43006) 等段
            // 的地址落在 40001~49999 内，GetAccess 会判为读写，但这些寄存器在各自从机设备上，
            // 由 PC 通过热泵地址写入是无效甚至危险的，UI 上统一按只读呈现，这里必须拦截。
            string accessText = targetRow.Cells["Access"].Value?.ToString() ?? "";
            if (accessText is not ("读写" or "只写"))
                return;

            RegisterAccess access = RegisterMap.GetAccess(address);
            if (access == RegisterAccess.ReadOnly)
                return;
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接串口，再写入参数。", "未连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 40107~40110 为 4 寄存器 64 位 UNIX 时间戳，需要整段写入（0x10）。
            if (address == RegisterMap.SYNC_TIME_ADDR)
            {
                await WriteSyncTimeAsync(targetRow);
                return;
            }

            // 40501 IMEI / 40511 激活 MD5 / 40519 单机并联模式都是多寄存器数据块，
            // 单寄存器写入对它们没有意义，必须整段写入（0x10）。
            if (TryGetBlockWriteDefinition(address, out BlockWriteDefinition? block))
            {
                await WriteBlockAsync(targetRow, address, block!, access);
                return;
            }

            DataGridViewRow row = targetRow;
            string name = row.Cells["Name"].Value?.ToString()?.Trim() ?? address.ToString();
            string unit = row.Cells["Unit"].Value?.ToString() ?? "";
            string currentValue = row.Cells["Value"].Value?.ToString() ?? "";
            if (!TryShowWriteDialog(address, name, unit, currentValue, out string input))
                return;
            if (!TryEncodeParameterValue(address, input, out ushort rawValue, out string error))
            {
                MessageBox.Show(error, "输入无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string displayValue = FormatParameterValue(address, rawValue);
            string operation = access == RegisterAccess.WriteOnly ? "执行" : "写入";
            // 枚举型参数补上"本次值对应什么含义 + 有哪些可选值"，避免只看数字误操作。
            string meaningNote = BuildValueMeaningBlock(address, rawValue);
            // 协议虽标注读写，但产测/身份/OTA/Flash 段误写后果严重，写入前给出明确警示。
            string riskNote = RegisterMap.IsHighRiskWriteSegment(address)
                ? "\n\n⚠ 高危段：误写可能导致设备身份丢失、升级异常或统计数据被改写！"
                : "";
            DialogResult confirmed = MessageBox.Show(
                $"参数：{name}\n寄存器：{address}\n输入值：{displayValue}{unit}\n协议值：{rawValue} (0x{rawValue:X4}){meaningNote}{riskNote}\n\n确认{operation}？",
                $"确认{operation}",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.OK)
                return;

            DataGridViewCell actionCell = row.Cells["Action"];
            _writeInProgress = true;
            actionCell.Value = "处理中";
            _pollTimer?.Stop();
            try
            {
                ushort? readBack = await Task.Run<ushort?>(() =>
                {
                    _modbus.WriteSingleRegister(address, rawValue);
                    if (access == RegisterAccess.ReadWrite)
                        return _modbus.ReadHoldingRegisters(address, 1)[0];
                    return null;
                });

                if (readBack.HasValue)
                {
                    SetRegisterDisplayValue(address, FormatParameterValue(address, readBack.Value));
                    MessageBox.Show(
                        $"写入成功，设备回读值：{FormatParameterValue(address, readBack.Value)}{unit}",
                        "写入成功",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("命令已发送并收到设备应答。只写寄存器没有可回读的当前值。", "执行成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"写入 {address} 失败: {ex.Message}");
                MessageBox.Show($"写入失败：{ex.Message}", "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                actionCell.Value = operation;
                _writeInProgress = false;
                if (_isPolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        /// <summary>
        /// 40107~40110 同步时间：解析输入为 4 寄存器 64 位 UNIX 时间戳（低地址寄存器存低 16 位）。
        /// 支持 10 位 UNIX 秒，或 "yyyy-MM-dd HH:mm:ss" 形式的本地时间。
        /// </summary>
        internal static bool TryBuildUnix64Words(string input, out ushort[] words, out string error)
        {
            words = Array.Empty<ushort>();
            error = "";
            string text = input.Trim();
            if (text.Length == 0)
            {
                error = "请输入日期时间（如 2026-09-29 15:20:00）或 10 位 UNIX 秒。";
                return false;
            }

            long seconds;
            if (text.Length is >= 9 and <= 10 && text.All(char.IsDigit))
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                {
                    error = "UNIX 秒格式无效。";
                    return false;
                }
            }
            else if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime local)
                     || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                seconds = new DateTimeOffset(local).ToUnixTimeSeconds();
            }
            else
            {
                error = "无法识别的时间格式，请输入如 2026-09-29 15:20:00 或 10 位 UNIX 秒。";
                return false;
            }

            if (seconds < 0)
            {
                error = "时间戳不能为负值。";
                return false;
            }

            ulong raw = (ulong)seconds;
            words = new ushort[4];
            for (int i = 0; i < words.Length; i++)
                words[i] = (ushort)((raw >> (16 * i)) & 0xFFFF);
            return true;
        }

        /// <summary>
        /// 整段写入块的编码入口：按定义选择 ASCII 或 MD5 编码，输出 n 个寄存器（低地址在前）。
        /// </summary>
        internal static bool TryBuildBlockWords(ushort address, string input, out ushort[] words, out string error)
        {
            if (!BlockWriteRules.TryGetValue(address, out BlockWriteDefinition? definition))
            {
                words = Array.Empty<ushort>();
                error = "该地址不是整段写入的数据块。";
                return false;
            }

            return definition.Kind switch
            {
                BlockWriteKind.Md5Bytes =>
                    TryBuildMd5BlockWords(input, definition.RegisterCount, out words, out error),
                _ => TryBuildAsciiBlockWords(input, definition.RegisterCount, out words, out error)
            };
        }

        /// <summary>
        /// ASCII 字符串 → 寄存器块。寄存器内高字节在前，与界面 <c>DecodeAscii</c> 的读法严格对称，
        /// 保证「写进去再读回来」得到同一个字符串。不足部分按协议填 0x00。
        /// </summary>
        internal static bool TryBuildAsciiBlockWords(
            string input, int registerCount, out ushort[] words, out string error)
        {
            words = Array.Empty<ushort>();
            error = "";
            string text = input.Trim();
            if (text.Length == 0)
            {
                error = "请输入要写入的字符串。";
                return false;
            }
            if (text.Any(ch => ch > 0x7F))
            {
                error = "只支持 ASCII 字符（数字、字母），不能包含中文或全角符号。";
                return false;
            }

            int capacity = registerCount * 2;
            if (text.Length > capacity)
            {
                error = $"内容过长：该字段共 {registerCount} 个寄存器，最多 {capacity} 个字符，当前 {text.Length} 个。";
                return false;
            }

            var bytes = new byte[capacity];
            for (int i = 0; i < text.Length; i++)
                bytes[i] = (byte)text[i];

            words = new ushort[registerCount];
            for (int i = 0; i < registerCount; i++)
                words[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            return true;
        }

        /// <summary>
        /// 16 字节二进制（激活 MD5）→ 寄存器块，寄存器内低字节在前（协议原文"低位在前"）。
        /// 输入可以是 32 位十六进制（允许空格/短横线/冒号分隔），也可以是 15~17 位 IMEI，
        /// 后者按协议"IMEI 号的 128 位 MD5 值"直接对 IMEI 的 ASCII 取 MD5。
        /// </summary>
        internal static bool TryBuildMd5BlockWords(
            string input, int registerCount, out ushort[] words, out string error)
        {
            words = Array.Empty<ushort>();
            error = "";
            string text = input.Trim();
            if (text.Length == 0)
            {
                error = "请输入 32 位十六进制 MD5 值，或 15~17 位 IMEI 号。";
                return false;
            }

            byte[] bytes;
            if (text.Length is >= 15 and <= 17 && text.All(char.IsDigit))
            {
                bytes = MD5.HashData(Encoding.ASCII.GetBytes(text));
            }
            else
            {
                string compact = new string(text.Where(ch => ch is not (' ' or '-' or ':' or '\t')).ToArray());
                if (compact.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    compact = compact[2..];
                // 每字节 2 个十六进制字符：registerCount 个寄存器 = registerCount * 2 字节 = registerCount * 4 字符
                int expectedChars = registerCount * 4;
                if (compact.Length != expectedChars)
                {
                    error = $"十六进制长度不对：应为 {expectedChars} 个字符（{registerCount * 2} 字节），当前 {compact.Length} 个。";
                    return false;
                }
                if (!compact.All(Uri.IsHexDigit))
                {
                    error = "包含非十六进制字符，请输入 0~9 / A~F。";
                    return false;
                }
                bytes = Convert.FromHexString(compact);
            }

            words = new ushort[registerCount];
            for (int i = 0; i < registerCount; i++)
                words[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            return true;
        }

        /// <summary>寄存器块 → 可读文本（ASCII 段给出字符串，MD5 段给出十六进制），用于确认与回读显示。</summary>
        internal static string FormatBlockWords(ushort address, ushort[] words)
        {
            if (!BlockWriteRules.TryGetValue(address, out BlockWriteDefinition? definition))
                return string.Join(" ", words.Select(word => $"0x{word:X4}"));

            if (definition.Kind == BlockWriteKind.Md5Bytes)
                // MD5 段按惯例用小写十六进制显示，逐字节还原（与低字节在前的写入规则对称）。
                return string.Join(" ", words.Select(word => $"{(byte)word:x2}{(byte)(word >> 8):x2}"));

            var builder = new StringBuilder(words.Length * 2);
            foreach (ushort word in words)
            {
                builder.Append((char)(word >> 8));
                builder.Append((char)(word & 0xFF));
            }
            return builder.ToString().TrimEnd('\0', ' ');
        }

        /// <summary>把寄存器块拆成 "0x3836 0x3131 …" 形式，供确认弹窗核对逐寄存器内容。</summary>
        private static string FormatBlockRegisterValues(ushort[] words) =>
            string.Join(" ", words.Select(word => $"0x{word:X4}"));

        /// <summary>整段写入的按钮/标题用词（只写段是"执行"，读写段是"写入"）。</summary>
        internal static string DescribeBlockWriteOperation(ushort address) =>
            RegisterMap.GetAccess(address) == RegisterAccess.WriteOnly ? "执行" : "写入";

        /// <summary>
        /// 整段写入的二次确认正文。把它做成纯函数，弹窗内容就能被单元测试锁定，
        /// 不必依赖界面自动化去抠 WinForms 对话框文本。
        /// </summary>
        internal static string BuildBlockWriteConfirmText(ushort address, ushort[] words)
        {
            if (words.Length == 0
                || !BlockWriteRules.TryGetValue(address, out BlockWriteDefinition? definition))
                return "";

            int last = definition.StartAddress + definition.RegisterCount - 1;
            string operation = DescribeBlockWriteOperation(address);
            return $"参数：{definition.Title}\n"
                + $"寄存器：{definition.StartAddress}~{last}（{definition.RegisterCount} 个寄存器，0x10 整段写入）\n"
                + $"写入值：{FormatBlockWords(address, words)}\n"
                + $"寄存器值：{FormatBlockRegisterValues(words)}\n\n"
                + "⚠ 高危段：误写可能导致设备身份丢失、激活失效或组网模式异常！\n\n"
                + $"确认{operation}？";
        }


        private async Task WriteSyncTimeAsync(DataGridViewRow row)
        {
            using var dialog = new Form
            {
                Text = "同步时间给热泵主机",
                ClientSize = new Size(420, 170),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lblName = new Label
            {
                Text = $"同步时间给热泵主机  [{RegisterMap.SYNC_TIME_ADDR}~{RegisterMap.SYNC_TIME_ADDR + 3}]",
                Location = new Point(16, 15),
                AutoSize = true,
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };
            var lblValue = new Label { Text = "设置值：", Location = new Point(16, 52), AutoSize = true };
            var txtValue = new TextBox
            {
                Location = new Point(78, 48),
                Width = 320,
                Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            var lblHint = new Label
            {
                Text = "协议：40107~40110 共 4 寄存器，64 位 UNIX 时间戳，低地址存低 16 位（4G 模块每分钟写一次）",
                Location = new Point(16, 84),
                Size = new Size(388, 40),
                ForeColor = Color.DimGray
            };
            var btnOk = new Button { Text = "确定", Location = new Point(238, 130), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(326, 130), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { lblName, lblValue, txtValue, lblHint, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;
            dialog.Shown += (s, e) => { txtValue.SelectAll(); txtValue.Focus(); };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            if (!TryBuildUnix64Words(txtValue.Text, out ushort[] words, out string error))
            {
                MessageBox.Show(error, "输入无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long unixSeconds = words[0] | ((long)words[1] << 16) | ((long)words[2] << 32) | ((long)words[3] << 48);
            DialogResult confirmed = MessageBox.Show(
                $"寄存器：{RegisterMap.SYNC_TIME_ADDR}~{RegisterMap.SYNC_TIME_ADDR + 3}（0x10 整段写入）\n"
                + $"时间戳：{unixSeconds}（{DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime:yyyy-MM-dd HH:mm:ss}）\n"
                + $"寄存器值：{string.Join(" ", words.Select(w => $"0x{w:X4}"))}\n\n确认执行？",
                "确认执行",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.OK)
                return;

            DataGridViewCell actionCell = row.Cells["Action"];
            _writeInProgress = true;
            actionCell.Value = "处理中";
            _pollTimer?.Stop();
            try
            {
                await Task.Run(() => _modbus.WriteMultipleRegisters(RegisterMap.SYNC_TIME_ADDR, words));
                AppendLog("OK", $"同步时间成功：{unixSeconds}（{RegisterMap.SYNC_TIME_ADDR}~{RegisterMap.SYNC_TIME_ADDR + 3}）");
                MessageBox.Show(
                    $"同步时间成功，已写入 {DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime:yyyy-MM-dd HH:mm:ss}。",
                    "执行成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"同步时间失败: {ex.Message}");
                MessageBox.Show($"同步时间失败：{ex.Message}", "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                actionCell.Value = "执行";
                _writeInProgress = false;
                if (_isPolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        /// <summary>
        /// 整段写入 405xx 读写信息段的数据块（IMEI / 激活 MD5 / 单机并联模式）：
        /// 录入 → 解析 → 二次确认 → 0x10 写入 → 回读核对。
        /// </summary>
        private async Task WriteBlockAsync(
            DataGridViewRow row, ushort address, BlockWriteDefinition definition, RegisterAccess access)
        {
            using var dialog = new Form
            {
                Text = $"写入 {definition.Title}",
                ClientSize = new Size(470, 216),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lblName = new Label
            {
                Text = $"{definition.Title}  [{definition.StartAddress}~{definition.StartAddress + definition.RegisterCount - 1}]",
                Location = new Point(16, 14),
                Size = new Size(438, 20),
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };
            var lblValue = new Label { Text = "写入值：", Location = new Point(16, 50), AutoSize = true };
            string existingValue = row.Cells["Value"].Value?.ToString() ?? "";
            if (existingValue is "" or "--")
                existingValue = "";
            var txtValue = new TextBox
            {
                Location = new Point(78, 46),
                Width = 376,
                Text = existingValue
            };
            var lblHint = new Label
            {
                Text = definition.Hint,
                Location = new Point(16, 80),
                Size = new Size(438, 46),
                ForeColor = Color.DimGray
            };
            var lblRequired = new Label
            {
                Text = $"（{definition.RegisterCount} 个寄存器，0x10 整段写入）",
                Location = new Point(16, 130),
                Size = new Size(438, 18),
                ForeColor = Color.DimGray
            };
            var btnOk = new Button { Text = "确定", Location = new Point(288, 168), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(376, 168), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { lblName, lblValue, txtValue, lblHint, lblRequired, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;
            dialog.Shown += (s, e) => { txtValue.SelectAll(); txtValue.Focus(); };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            if (!TryBuildBlockWords(address, txtValue.Text, out ushort[] words, out string error))
            {
                MessageBox.Show(error, "输入无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string readable = FormatBlockWords(address, words);
            string operation = DescribeBlockWriteOperation(address);
            DialogResult confirmed = MessageBox.Show(
                BuildBlockWriteConfirmText(address, words),
                $"确认{operation}",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.OK)
                return;

            DataGridViewCell actionCell = row.Cells["Action"];
            _writeInProgress = true;
            actionCell.Value = "处理中";
            _pollTimer?.Stop();
            try
            {
                ushort[]? readBack = await Task.Run<ushort[]?>(() =>
                {
                    _modbus.WriteMultipleRegisters(definition.StartAddress, words);
                    try
                    {
                        return _modbus.ReadHoldingRegisters(definition.StartAddress, (ushort)definition.RegisterCount);
                    }
                    catch
                    {
                        // 回读失败不影响写入结论（部分设备对该段不支持 0x03 读）。
                        return null;
                    }
                });

                AppendLog("OK",
                    $"{definition.Title} 写入成功：{readable}"
                    + $"（{definition.StartAddress}~{definition.StartAddress + definition.RegisterCount - 1}）");

                string body = readBack is { Length: > 0 }
                    ? $"写入成功，设备回读值：{FormatBlockWords(address, readBack)}\n"
                      + $"回读寄存器：{FormatBlockRegisterValues(readBack)}"
                    : $"命令已发送并收到设备应答（该段未回读到值）。\n本次写入：{readable}";
                MessageBox.Show(body, "写入成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"写入 {address} 失败: {ex.Message}");
                MessageBox.Show($"写入失败：{ex.Message}", "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                actionCell.Value = operation;
                _writeInProgress = false;
                if (_isPolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private bool TryShowWriteDialog(
            ushort address,
            string name,
            string unit,
            string currentValue,
            out string input)
        {
            using var dialog = new Form
            {
                Text = RegisterMap.GetAccess(address) == RegisterAccess.WriteOnly ? "执行寄存器命令" : "写入参数",
                ClientSize = new Size(420, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lblName = new Label
            {
                Text = $"{name}  [{address}]",
                Location = new Point(16, 15),
                AutoSize = true,
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };
            var lblCurrent = new Label
            {
                Text = RegisterMap.GetAccess(address) == RegisterAccess.WriteOnly
                    ? "当前值：只写命令不可读取"
                    : $"当前值：{currentValue}{unit}",
                Location = new Point(16, 45),
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            var lblValue = new Label { Text = "设置值：", Location = new Point(16, 79), AutoSize = true };
            var txtValue = new TextBox
            {
                Location = new Point(78, 75),
                Width = 185,
                Text = currentValue is "--" or "—" ? "" : currentValue
            };
            var lblUnit = new Label { Text = unit, Location = new Point(270, 79), AutoSize = true };
            var lblHint = new Label
            {
                Text = BuildWriteHint(address, unit),
                Location = new Point(16, 109),
                Size = new Size(388, 35),
                ForeColor = Color.DimGray
            };

            // 枚举型参数在输入框下方直接列出"1=XX、2=XX"，不必去翻协议。
            string valueList = FormatValueMeaningList(address);
            Label? lblValues = null;
            int extraHeight = 0;
            if (valueList.Length > 0)
            {
                const int charsPerLine = 42;
                int lines = (valueList.Length + charsPerLine - 1) / charsPerLine;
                int height = 16 * lines;
                lblValues = new Label
                {
                    Text = $"可选值：{valueList}",
                    Location = new Point(16, 147),
                    Size = new Size(388, height),
                    ForeColor = Color.FromArgb(0, 90, 160)
                };
                extraHeight = height + 6;
            }

            int buttonTop = 150 + extraHeight;
            var btnOk = new Button { Text = "确定", Location = new Point(238, buttonTop), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(326, buttonTop), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            var dialogControls = new List<Control> { lblName, lblCurrent, lblValue, txtValue, lblUnit, lblHint };
            if (lblValues != null)
                dialogControls.Add(lblValues);
            dialogControls.Add(btnOk);
            dialogControls.Add(btnCancel);
            dialog.Controls.AddRange(dialogControls.ToArray());
            dialog.ClientSize = new Size(420, buttonTop + 28 + 16);
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;
            dialog.Shown += (s, e) => { txtValue.SelectAll(); txtValue.Focus(); };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                input = "";
                return false;
            }

            input = txtValue.Text;
            return true;
        }

        /// <summary>
        /// 按协议精度返回显示/校验小数位数：Scale=1 → 整数，10 → 1 位小数（如温度 ×10），
        /// 100 → 2 位小数（如 40701 热水器出水温度，传输值=实际值×100）。
        /// </summary>
        internal static int DecimalsForScale(decimal scale) => scale switch
        {
            10 => 1,
            100 => 2,
            _ => 0
        };

        private static string BuildWriteHint(ushort address, string unit)
        {
            if (IsClearFaultRegister(address))
                return $"允许值：0（解除全部故障报警）或 {DescribeFaultCodeRanges()}（解除对应故障码报警）；也可输入0x开头的十六进制原始值。";

            if (!WriteRules.TryGetValue(address, out WriteRule? rule))
                return "请输入十进制数或0x开头的十六进制原始值。";

            string suffix = string.IsNullOrEmpty(unit) ? "" : unit;
            string precision = DecimalsForScale(rule.Scale) switch
            {
                1 => "，支持1位小数",
                2 => "，支持2位小数",
                _ => ""
            };
            string special = rule.AllowFFFF ? "；也可输入0xFFFF" : "";
            return $"允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}{suffix}{precision}{special}";
        }

        /// <summary>
        /// 40101「手动解除故障报警」不是普通 0/1 枚举：写 0 解除全部故障报警，
        /// 写 X 解除对应故障码的报警（X 即协议附录「热泵主机故障码」编号）。
        /// </summary>
        private static bool IsClearFaultRegister(ushort address) =>
            address == RegisterMap.CLEAR_FAULT_ADDR;

        /// <summary>故障码表里可用编号的紧凑区间描述，如 "1～66、100"。</summary>
        private static string DescribeFaultCodeRanges()
        {
            List<int> codes = new(RegisterMap.FaultCodeMap.Keys);
            if (codes.Count == 0)
                return "X";
            codes.Sort();

            var segments = new List<string>();
            int start = codes[0];
            int previous = codes[0];
            for (int index = 1; index <= codes.Count; index++)
            {
                bool contiguous = index < codes.Count && codes[index] == previous + 1;
                if (contiguous)
                {
                    previous = codes[index];
                    continue;
                }
                segments.Add(start == previous ? $"{start}" : $"{start}～{previous}");
                if (index < codes.Count)
                    start = previous = codes[index];
            }
            return string.Join("、", segments);
        }

        /// <summary>40101 可选值总览："0 = 解除所有故障报警 / 1～66、100 = 解除对应故障码报警"。</summary>
        private static string DescribeClearFaultOptions() =>
            $"0 = 解除所有故障报警 / {DescribeFaultCodeRanges()} = 解除对应故障码报警";

        /// <summary>40101 本次写入值的说明，附上该故障码的名称。</summary>
        private static string DescribeClearFaultMeaning(ushort rawValue)
        {
            if (rawValue == 0)
                return "本次 0 = 解除所有故障报警\n说明：写入 0x0000 会一次性清除当前全部故障报警";

            string fault = RegisterMap.FaultCodeMap.TryGetValue(rawValue, out string? name)
                ? $"故障码 {rawValue}（{name}）"
                : $"故障码 {rawValue}（协议附录未定义该故障码，请确认）";
            return $"本次 {rawValue} = 解除 {fault} 的报警\n说明：0 = 解除所有故障报警；X = 解除对应故障码报警";
        }

        /// <summary>
        /// 取该寄存器"值 → 含义"的枚举表：优先用参数表枚举（ParameterValueMeanings），
        /// 再补协议里单独定义的枚举（40106 清除设备信息的 ClearDeviceCmdMap）。
        /// 返回 null 表示该寄存器不是枚举型参数。
        /// </summary>
        internal static Dictionary<ushort, string>? TryGetValueMeanings(ushort address)
        {
            if (ParameterValueMeanings.TryGetValue(address, out Dictionary<ushort, string>? meanings))
                return meanings;
            if (address == RegisterMap.CLEAR_DEVICE_INFO_ADDR)
                return RegisterMap.ClearDeviceCmdMap;
            return null;
        }

        /// <summary>按值升序输出枚举列表，如 "1 = 制冷 / 2 = 制热"。</summary>
        internal static string FormatValueMeaningList(ushort address)
        {
            if (IsClearFaultRegister(address))
                return DescribeClearFaultOptions();

            Dictionary<ushort, string>? meanings = TryGetValueMeanings(address);
            if (meanings == null || meanings.Count == 0)
                return "";

            var ordered = new List<KeyValuePair<ushort, string>>(meanings);
            ordered.Sort((left, right) => left.Key.CompareTo(right.Key));
            var parts = new List<string>(ordered.Count);
            foreach (KeyValuePair<ushort, string> pair in ordered)
                parts.Add($"{pair.Key} = {pair.Value}");
            return string.Join(" / ", parts);
        }

        /// <summary>
        /// 写入前的"值含义"说明：本次要写的值对应什么含义 + 该寄存器的全部可选值。
        /// 例："1 对应制冷、2 对应制热"。非枚举参数返回空串。
        /// </summary>
        internal static string DescribeWriteValueMeaning(ushort address, ushort rawValue)
        {
            var lines = new List<string>();
            if (IsClearFaultRegister(address))
            {
                lines.Add(DescribeClearFaultMeaning(rawValue));
                lines.Add($"可选值：{DescribeClearFaultOptions()}");
                return string.Join("\n", lines);
            }

            Dictionary<ushort, string>? meanings = TryGetValueMeanings(address);
            if (meanings != null && meanings.Count > 0)
            {
                string current = meanings.TryGetValue(rawValue, out string? mapped)
                    ? $"本次 {rawValue} = {mapped}"
                    : $"本次 {rawValue}（协议未定义该取值，请确认）";
                lines.Add(current);
                lines.Add($"可选值：{FormatValueMeaningList(address)}");
            }
            else if (WriteRules.TryGetValue(address, out WriteRule? rule)
                     && rule.AllowFFFF && rawValue == ushort.MaxValue)
            {
                lines.Add("本次 0xFFFF = 永久有效（不受时长限制）");
            }
            return string.Join("\n", lines);
        }

        /// <summary>确认弹窗里的"值含义"整段文本（含小标题），无枚举时返回空串。</summary>
        private static string BuildValueMeaningBlock(ushort address, ushort rawValue)
        {
            string detail = DescribeWriteValueMeaning(address, rawValue);
            return detail.Length == 0 ? "" : $"\n\n值含义：{detail}";
        }

        internal static bool TryEncodeParameterValue(ushort address, string input, out ushort rawValue, out string error)
        {
            rawValue = 0;
            error = "";
            WriteRule rule = WriteRules.TryGetValue(address, out WriteRule? configured)
                ? configured
                : new WriteRule(0, 65535);
            string text = input.Trim();
            if (text.Length == 0)
            {
                error = "请输入要写入的值。";
                return false;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ushort.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rawValue))
                {
                    error = "十六进制值格式无效。";
                    return false;
                }
                if (rule.AllowFFFF && rawValue == ushort.MaxValue)
                    return true;

                decimal decoded = rule.Signed ? (short)rawValue / rule.Scale : rawValue / rule.Scale;
                if (decoded < rule.Minimum || decoded > rule.Maximum)
                {
                    error = $"数值超出协议允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}。";
                    return false;
                }
                return true;
            }

            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value) &&
                !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                error = "请输入有效的数字，或输入0x开头的十六进制原始值。";
                return false;
            }
            if (value < rule.Minimum || value > rule.Maximum)
            {
                error = $"数值超出协议允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}。";
                return false;
            }

            decimal scaled = value * rule.Scale;
            if (scaled != decimal.Truncate(scaled))
            {
                int decimals = DecimalsForScale(rule.Scale);
                error = decimals == 0 ? "该参数只支持整数。" : $"该参数最多支持{decimals}位小数。";
                return false;
            }
            if (rule.Signed)
            {
                int signedValue = decimal.ToInt32(scaled);
                if (signedValue < short.MinValue || signedValue > short.MaxValue)
                {
                    error = "数值超出16位有符号寄存器范围。";
                    return false;
                }
                rawValue = unchecked((ushort)(short)signedValue);
            }
            else
            {
                if (scaled < ushort.MinValue || scaled > ushort.MaxValue)
                {
                    error = "数值超出16位寄存器范围。";
                    return false;
                }
                rawValue = decimal.ToUInt16(scaled);
            }
            return true;
        }

        internal static string FormatParameterValue(ushort address, ushort rawValue)
        {
            // 外机-4G 风盘模板（20000+60n，偏移 0~20）按协议含义显示。
            if (RegisterMap.TryGetFan4GOffset(address, out _, out ushort fanOffset))
                return FormatFan4GValue(fanOffset, rawValue);

            if (ParameterValueMeanings.TryGetValue(address, out Dictionary<ushort, string>? meanings))
            {
                string meaning = meanings.TryGetValue(rawValue, out string? mapped)
                    ? mapped
                    : $"未知({rawValue})";
                return $"0x{rawValue:X4} / {meaning}";
            }

            if (!WriteRules.TryGetValue(address, out WriteRule? rule))
                return rawValue.ToString(CultureInfo.CurrentCulture);
            if (rule.AllowFFFF && rawValue == ushort.MaxValue)
                return "0xFFFF";
            int decimals = DecimalsForScale(rule.Scale);
            if (decimals == 0)
                return rawValue.ToString(CultureInfo.CurrentCulture);

            decimal value = rule.Signed ? (short)rawValue / rule.Scale : rawValue / rule.Scale;
            return value.ToString("0." + new string('0', decimals), CultureInfo.CurrentCulture);
        }

        /// <summary>外机-4G 风盘模板单个寄存器的显示（偏移 0~20，见协议"外机-4G"表）。</summary>
        internal static string FormatFan4GValue(ushort offset, ushort rawValue)
        {
            switch (offset)
            {
                case RegisterMap.FAN_4G_VALVE_OFFSET:
                case RegisterMap.FAN_4G_ANION_OFFSET:
                case RegisterMap.FAN_4G_HEATER_OFFSET:
                    return $"{rawValue} / {(rawValue == 0 ? "关闭" : "打开")}";
                case RegisterMap.FAN_4G_TEMPERATURE_OFFSET:
                    // 协议：精度 0.1，传输值 = 实际值 × 10
                    return (rawValue / 10.0).ToString("0.0", CultureInfo.CurrentCulture);
                case RegisterMap.FAN_4G_MODEL_OFFSET:
                    return $"{rawValue} / {Fan4GModelText(rawValue)}";
                case RegisterMap.FAN_4G_FAULT_CODE_OFFSET:
                    return DecodeBitList(rawValue, Fan4GFaultBitNames);
                case RegisterMap.FAN_4G_FEATURE_SET_OFFSET:
                    return DecodeBitList(rawValue, Fan4GFeatureBitNames);
                default:
                    return rawValue.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static readonly string[] Fan4GFaultBitNames =
        {
            "风盘485地址拨码错误", "风盘型号拨码错误", "风盘与底壳485通信故障", "风盘电机1故障",
            "风盘电机2故障", "新风电机故障", "CO2传感器故障", "温湿度传感器故障",
            "PM2.5传感器故障", "排水故障"
        };

        private static readonly string[] Fan4GFeatureBitNames =
        {
            "新风", "负离子", "3D风口", "电辅热", "温湿度", "PM2.5", "新风电机(0:DC310V/1:DC12V)"
        };

        internal static string Fan4GModelText(ushort rawValue) => rawValue switch
        {
            0x0000 => "拓普韦尔风盘转接板型号",
            0x0001 => "34机型",
            0x0002 => "51机型",
            0x0003 => "68机型",
            0x0004 => "85机型",
            0x0005 => "102机型",
            0x0006 => "136机型",
            0x0007 => "170机型",
            0x00AC => "自研交流风盘型号",
            _ => "未定义型号"
        };

        private static string DecodeBitList(ushort rawValue, IReadOnlyList<string> bitNames)
        {
            if (rawValue == 0)
                return "0 / 无";

            var parts = new List<string>();
            for (int bit = 0; bit < bitNames.Count && bit < 16; bit++)
            {
                if ((rawValue & (1 << bit)) != 0)
                    parts.Add($"BIT{bit}:{bitNames[bit]}");
            }
            return parts.Count == 0
                ? $"0x{rawValue:X4} / 未定义位"
                : $"0x{rawValue:X4} / {string.Join("、", parts)}";
        }
    }
}

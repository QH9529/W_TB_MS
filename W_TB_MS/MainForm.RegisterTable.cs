using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
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
            [40324] = new(30, 70, 10)
        };

        // Hold-register values with protocol-defined state meanings. Other
        // registers remain displayed using their numeric/scaled value.
        private static readonly Dictionary<ushort, Dictionary<ushort, string>> ParameterValueMeanings = new()
        {
            [40001] = new() { [0] = "关机", [1] = "开机" },
            [40002] = new() { [0] = "普通", [1] = "静音", [2] = "超级静音" },
            [40003] = new() { [0] = "关闭", [1] = "打开" },
            [40006] = new() { [0] = "无请求", [1] = "请求除霜" },
            [40007] = new() { [0] = "关闭", [1] = "运行" },
            [40008] = new() { [0] = "关闭", [1] = "运行" },
            [40009] = new() { [0] = "关闭", [1] = "打开" },
            [40010] = new() { [0] = "关闭", [1] = "打开" },
            [40011] = new() { [0] = "关闭", [1] = "强制待机" },
            [40103] = new() { [0] = "无请求", [1] = "请求除霜" },
            [40105] = new() { [0] = "关闭", [1] = "启用" },
            [40201] = new() { [1] = "制冷", [2] = "制热" },
            [40202] = new() { [0] = "禁用", [1] = "启用" },
            [40203] = new() { [0] = "不支持", [1] = "支持" },
            [40208] = new() { [0] = "进水温度", [1] = "出水温度" },
            [40212] = new() { [0] = "节能", [1] = "舒适" },
            [40223] = new() { [0] = "不交替", [1] = "交替" },
            [40226] = new() { [0] = "禁用", [1] = "启用" },
            [40227] = new() { [0] = "禁用", [1] = "启用" },
            [40228] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40229] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40230] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" }
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

            void AddReg(ushort addr, string name, string unit)
            {
                RegisterAccess access = RegisterMap.GetAccess(addr);
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
            AddReg(30226, "热水上温度", "℃");
            AddReg(30227, "热水中温度", "℃");
            AddReg(30228, "热水下温度", "℃");
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
            AddReg(40111, "同步天气湿度(4G)", "%");
        }

        private async Task WriteRegisterFromRowAsync(int rowIndex)
        {
            if (_writeInProgress || rowIndex < 0 || rowIndex >= dgvData.Rows.Count)
                return;
            if (dgvData.Rows[rowIndex].Tag is not ushort address)
                return;

            RegisterAccess access = RegisterMap.GetAccess(address);
            if (access == RegisterAccess.ReadOnly)
                return;
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接串口，再写入参数。", "未连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DataGridViewRow row = dgvData.Rows[rowIndex];
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
            DialogResult confirmed = MessageBox.Show(
                $"参数：{name}\n寄存器：{address}\n输入值：{displayValue}{unit}\n协议值：{rawValue} (0x{rawValue:X4})\n\n确认{operation}？",
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
            var btnOk = new Button { Text = "确定", Location = new Point(238, 150), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(326, 150), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { lblName, lblCurrent, lblValue, txtValue, lblUnit, lblHint, btnOk, btnCancel });
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

        private static string BuildWriteHint(ushort address, string unit)
        {
            if (!WriteRules.TryGetValue(address, out WriteRule? rule))
                return "请输入十进制数或0x开头的十六进制原始值。";

            string suffix = string.IsNullOrEmpty(unit) ? "" : unit;
            string precision = rule.Scale == 10 ? "，支持1位小数" : "";
            string special = rule.AllowFFFF ? "；也可输入0xFFFF" : "";
            return $"允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}{suffix}{precision}{special}";
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
                error = rule.Scale == 10 ? "该参数最多支持1位小数。" : "该参数只支持整数。";
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
            if (rule.Scale == 1)
                return rawValue.ToString(CultureInfo.CurrentCulture);

            decimal value = rule.Signed ? (short)rawValue / rule.Scale : rawValue / rule.Scale;
            return value.ToString("0.0", CultureInfo.CurrentCulture);
        }
    }
}

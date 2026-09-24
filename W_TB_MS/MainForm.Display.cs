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
        // ==================== 数据更新 ====================

        // ==================== 辅助 ====================

        private void UpdateDisplay(Dictionary<ushort, ushort> values)
        {
            if (InvokeRequired) { Invoke(() => UpdateDisplay(values)); return; }

            ushort Get(ushort address) => values.TryGetValue(address, out ushort value) ? value : (ushort)0;
            bool Has(ushort address) => values.ContainsKey(address);
            bool hasCompleteFaultData = Enumerable.Range(30101, 5).All(address => Has((ushort)address));

            ushort[] faultRegs = { Get(30101), Get(30102), Get(30103), Get(30104), Get(30105) };
            ushort status = Get(30106);
            ushort deviceStatus = Get(30201);
            ushort workMode = (ushort)((status >> 3) & 0x07);
            bool isOn = (status & 0x0001) != 0;
            bool defrostRequested = (status & 0x1000) != 0;
            bool defrostRunning = (deviceStatus & 0x1000) != 0;

            // 原地更新表格值（不再重建）
            void SetVal(ushort addr, string value)
            {
                SetRegisterDisplayValue(addr, value);
            }

            // 先写入所有已读取寄存器的原始值，再覆盖需要换算或解释的字段。
            foreach (var pair in values)
                SetVal(pair.Key, FormatParameterValue(pair.Key, pair.Value));

            if (Has(30101)) UpdateFaultBitRows(30101, Get(30101), RegisterMap.FaultReg1Bits);
            if (Has(30102)) UpdateFaultBitRows(30102, Get(30102), RegisterMap.FaultReg2Bits);
            if (Has(30103)) UpdateFaultBitRows(30103, Get(30103), RegisterMap.FaultReg3Bits);
            if (Has(30104)) UpdateFaultBitRows(30104, Get(30104), RegisterMap.FaultReg4Bits);
            if (Has(30105)) UpdateFaultBitRows(30105, Get(30105), RegisterMap.FaultReg5Bits);
            if (Has(30106)) UpdateStatusBitRows(30106, Get(30106), RegisterMap.StatusWordBits);
            if (Has(30201)) UpdateStatusBitRows(30201, Get(30201), RegisterMap.DeviceStatusBits);
            if (Has(30229)) UpdateStatusBitRows(30229, Get(30229), RegisterMap.DipSwitchBits);

            if (Has(30001)) SetVal(30001, FormatRegisterWords(values, 30001, 2));
            if (Has(30003)) SetVal(30003, FormatRegisterWords(values, 30003, 2));
            if (Has(30005)) SetVal(30005, DecodeAscii(values, 30005, 4));
            if (Has(30009)) SetVal(30009, FormatRegisterWords(values, 30009, 2));
            if (Has(30011)) SetVal(30011, FormatRegisterWords(values, 30011, 2));

            if (Has(30106))
                SetVal(30106, $"0x{status:X4} / {(isOn ? "开机" : "关机")} / {WorkModeMapGet(workMode)}");
            if (Has(30201))
                SetVal(30201, $"0x{deviceStatus:X4}");
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (HasNumericCurveValue(definition, values))
                {
                    double value = DecodeNumericCurveValue(definition, values);
                    SetVal(definition.Address, FormatNumericCurveValue(definition, value));
                }
            }

            // 故障报警寄存器 - 显示原始十六进制值
            for (int i = 0; i < faultRegs.Length; i++)
            {
                ushort address = (ushort)(30101 + i);
                if (Has(address))
                    SetVal(address, $"0x{faultRegs[i]:X4}");
            }

            if (Has(30106))
            {
                string silentText = Has(40002) ? SilentMapGet(Get(40002)) : "数据缺失";
                string defrostText = Has(30201)
                    ? (defrostRequested || defrostRunning ? "是" : "否")
                    : "数据缺失";
                SetLabelText(lblWorkMode, $"工作状态: 电源 {(isOn ? "开机" : "关机")} | 静音 {silentText} | 除霜 {defrostText}");
                SetLabelText(lblRuntimeMode, $"运行模式1: {RuntimeModeText(values)}", Color.Red);
                SetLabelText(lblRuntimeMode2, $"运行模式2: {RuntimeMode2Text(values)}", Color.Red);
                SetLabelText(lblSetMode, $"设置模式: {SetModeText(values)}", Color.Black);
            }
            else
            {
                SetLabelText(lblWorkMode, "工作状态: 数据不完整");
                SetLabelText(lblRuntimeMode, "运行模式1: --", Color.Red);
                SetLabelText(lblRuntimeMode2, "运行模式2: --", Color.Red);
                SetLabelText(lblSetMode, "设置模式: --", Color.Black);
            }

            int faultCount = -1;
            if (hasCompleteFaultData)
            {
                string faultSignature = string.Join(",", faultRegs);
                if (!string.Equals(faultSignature, _lastFaultSignature, StringComparison.Ordinal))
                {
                    lstFaults.BeginUpdate();
                    try
                    {
                        lstFaults.Items.Clear();
                        CheckFaultBits(faultRegs[0], RegisterMap.FaultReg1Bits, "30101", 1);
                        CheckFaultBits(faultRegs[1], RegisterMap.FaultReg2Bits, "30102", 17);
                        CheckFaultBits(faultRegs[2], RegisterMap.FaultReg3Bits, "30103", 33);
                        CheckFaultBits(faultRegs[3], RegisterMap.FaultReg4Bits, "30104", 49);
                        CheckFaultBits(faultRegs[4], RegisterMap.FaultReg5Bits, "30105", 65);
                    }
                    finally
                    {
                        lstFaults.EndUpdate();
                    }
                    _lastFaultSignature = faultSignature;
                    _lastFaultCount = lstFaults.Items.Count;
                }
                faultCount = _lastFaultCount;
                SetLabelText(lblFaultCode, faultCount == 0 ? "故障汇总: 无故障" : $"故障汇总: {faultCount} 项",
                    faultCount == 0 ? Color.Green : Color.Red);
            }
            else
            {
                _lastFaultSignature = string.Empty;
                _lastFaultCount = -1;
                SetLabelText(lblFaultCode, "故障汇总: 数据不完整", Color.Orange);
            }

            if (faultCount > 0)
            { SetLabelText(lblStatus, "⚠ 检测到故障报警！", Color.Red); }
            else if (_isPolling)
            { SetLabelText(lblStatus, $"已连接: {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps", Color.Green); }

            // 曲线
            DateTime sampleTime = DateTime.Now;
            if (!CanRecordCurveSample(values))
                return;

            _timeData.Add(sampleTime.ToOADate());
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (!HasNumericCurveValue(definition, values))
                    return; // 寄存器块读取失败时整条样本不落库，保持各序列索引对齐
                _numericCurveData[definition.Address].Add(DecodeNumericCurveValue(definition, values));
            }

            foreach (ushort address in BitCurveRegisterAddresses)
            {
                if (!values.TryGetValue(address, out ushort bitValue))
                    return;
                _allBitRegisterData[address].Add(bitValue);
            }

            PruneExpiredCurveData(sampleTime);
            if (_activeAutomaticArchiveTask == null || _activeAutomaticArchiveTask.IsCompleted)
                _activeAutomaticArchiveTask = TryAutomaticArchiveAsync(sampleTime);
            _chartDataDirty = true;
        }

        private static void SetLabelText(Label label, string text, Color? foreColor = null)
        {
            if (!string.Equals(label.Text, text, StringComparison.Ordinal))
                label.Text = text;
            if (foreColor.HasValue && label.ForeColor != foreColor.Value)
                label.ForeColor = foreColor.Value;
        }

        private void SetRegisterDisplayValue(ushort address, string value)
        {
            if (!_rowMap.TryGetValue(address, out int rowIndex) || rowIndex >= dgvData.Rows.Count)
                return;

            DataGridViewRow row = dgvData.Rows[rowIndex];
            DataGridViewCell valueCell = row.Cells["Value"];
            if (!string.Equals(valueCell.Value?.ToString(), value, StringComparison.Ordinal))
                valueCell.Value = value;
            // 仅温度字段执行超限高亮，避免电压和功率等正常值被误判。
            bool isTemperature = row.Cells["Unit"].Value?.ToString() == "℃";
            Color color = isTemperature && double.TryParse(value, out double numericValue) && numericValue > 80
                ? Color.Red
                : Color.Black;
            if (row.DefaultCellStyle.ForeColor != color)
                row.DefaultCellStyle.ForeColor = color;
        }

        private static void SetBitRowColor(DataGridViewRow row, bool nonZero)
        {
            Color color = nonZero ? Color.Red : Color.Black;
            if (row.DefaultCellStyle.ForeColor != color)
                row.DefaultCellStyle.ForeColor = color;
        }

        private void UpdateFaultBitRows(ushort address, ushort rawValue, Dictionary<int, string> definitions)
        {
            foreach (var definition in definitions)
            {
                if (!_bitRowMap.TryGetValue((address, definition.Key), out int rowIndex))
                    continue;

                bool active = (rawValue & (1 << definition.Key)) != 0;
                DataGridViewRow row = dgvData.Rows[rowIndex];
                string value = active ? "1 / 触发" : "0 / 正常";
                if (!string.Equals(row.Cells["Value"].Value?.ToString(), value, StringComparison.Ordinal))
                    row.Cells["Value"].Value = value;
                SetBitRowColor(row, active);
            }
        }

        private void UpdateStatusBitRows(ushort address, ushort rawValue, Dictionary<int, BitDefinition> definitions)
        {
            foreach (var definition in definitions)
            {
                if (!_bitRowMap.TryGetValue((address, definition.Key), out int rowIndex))
                    continue;

                bool enabled = (rawValue & (1 << definition.Key)) != 0;
                string state = enabled ? definition.Value.OneText : definition.Value.ZeroText;
                DataGridViewRow row = dgvData.Rows[rowIndex];
                string value = $"{(enabled ? 1 : 0)} / {state}";
                if (!string.Equals(row.Cells["Value"].Value?.ToString(), value, StringComparison.Ordinal))
                    row.Cells["Value"].Value = value;
                SetBitRowColor(row, enabled);
            }
        }

        private void AddRow(string name, string value, string unit, string addr)
        {
            int idx = dgvData.Rows.Add(name, value, unit, addr);
            if (double.TryParse(value, out double v) && name.Contains("温度") && v > 80)
                dgvData.Rows[idx].DefaultCellStyle.ForeColor = Color.Red;
        }

        private void CheckFaultBits(ushort reg, Dictionary<int, string> defs, string regAddr, int firstFaultCode)
        {
            for (int b = 0; b < 16; b++)
            {
                if ((reg & (1 << b)) != 0)
                {
                    string name = defs.TryGetValue(b, out string? definedName) ? definedName : "未定义故障位";
                    int faultCode = firstFaultCode + b;
                    if (RegisterMap.FaultCodeMap.ContainsKey(faultCode))
                    {
                        string reset = RegisterMap.FaultResetMethodMap.TryGetValue(faultCode, out string? method)
                            ? method
                            : "清除方式未定义";
                        lstFaults.Items.Add($"[{regAddr}] BIT{b} / 故障码{faultCode}: {name}（{reset}）");
                    }
                    else
                    {
                        lstFaults.Items.Add($"[{regAddr}] BIT{b}: {name}");
                    }
                }
            }
        }

        private static string FormatRegisterWords(Dictionary<ushort, ushort> values, ushort startAddress, int count)
        {
            return string.Join(" ", Enumerable.Range(0, count)
                .Select(i => values.TryGetValue((ushort)(startAddress + i), out ushort value) ? $"{value:X4}" : "----"));
        }

        private static string DecodeAscii(Dictionary<ushort, ushort> values, ushort startAddress, int count)
        {
            var bytes = new List<byte>(count * 2);
            for (int i = 0; i < count; i++)
            {
                ushort value = values.TryGetValue((ushort)(startAddress + i), out ushort word) ? word : (ushort)0;
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\0', ' ');
        }

        private static string WorkModeMapGet(ushort m) => RegisterMap.WorkModeMap.TryGetValue(m, out var v) ? v : $"未知({m})";

        // ---------------- 虚拟状态曲线：运行模式1 / 运行模式2 ----------------
        // 这两个地址是软件内部编号，设备上无对应寄存器，不参与 Modbus 轮询，
        // 仅在轮询结果分发时由真实寄存器派生（见 DeriveModeRegisters），用于状态页曲线与导出。
        // 运行模式1（30990）由 30106 bit3~5 派生，枚举：0=数据缺失，1=制冷待机，2=制冷报警停机，
        // 3=制热待机，4=制热报警停机，5=除霜中，6=压缩机预热，7=--
        // 运行模式2（30991）由 30106 bit3~5 + bit6 派生，枚举：0=数据缺失，1=制冷，2=制冷+热水，
        // 3=制热，4=制热+热水，5=热水，6=压机未运行
        internal const ushort RUNTIME_MODE_CURVE_ADDR = 30990;   // 运行模式1（30106 bit3~5 派生）
        internal const ushort RUNTIME_MODE2_CURVE_ADDR = 30991;  // 运行模式2（30106 bit3~5 + bit6 派生）

        // ---- 运行模式1 枚举 ----
        internal const ushort RUNTIME_MODE_DATA_MISSING = 0;
        internal const ushort RUNTIME_MODE1_COOLING_STANDBY = 1;        // 制冷待机
        internal const ushort RUNTIME_MODE1_COOLING_ALARM_STOP = 2;     // 制冷报警停机
        internal const ushort RUNTIME_MODE1_HEATING_STANDBY = 3;        // 制热待机
        internal const ushort RUNTIME_MODE1_HEATING_ALARM_STOP = 4;     // 制热报警停机
        internal const ushort RUNTIME_MODE1_DEFROSTING = 5;             // 除霜中
        internal const ushort RUNTIME_MODE1_PREHEATING = 6;             // 压缩机预热
        internal const ushort RUNTIME_MODE1_NONE = 7;                   // --（000B / 011B）

        // ---- 运行模式2 枚举 ----
        internal const ushort RUNTIME_MODE2_COOLING = 1;                // 制冷
        internal const ushort RUNTIME_MODE2_COOLING_HOT_WATER = 2;      // 制冷+热水
        internal const ushort RUNTIME_MODE2_HEATING = 3;                // 制热
        internal const ushort RUNTIME_MODE2_HEATING_HOT_WATER = 4;      // 制热+热水
        internal const ushort RUNTIME_MODE2_HOT_WATER = 5;              // 热水
        internal const ushort RUNTIME_MODE2_COMPRESSOR_OFF = 6;         // 压机未运行

        internal static readonly Dictionary<ushort, string> RuntimeModeMeanings = new()
        {
            [RUNTIME_MODE_DATA_MISSING] = "数据缺失",
            [RUNTIME_MODE1_COOLING_STANDBY] = "制冷待机",
            [RUNTIME_MODE1_COOLING_ALARM_STOP] = "制冷报警停机",
            [RUNTIME_MODE1_HEATING_STANDBY] = "制热待机",
            [RUNTIME_MODE1_HEATING_ALARM_STOP] = "制热报警停机",
            [RUNTIME_MODE1_DEFROSTING] = "除霜中",
            [RUNTIME_MODE1_PREHEATING] = "压缩机预热",
            [RUNTIME_MODE1_NONE] = " -- ",
        };

        internal static readonly Dictionary<ushort, string> RuntimeMode2Meanings = new()
        {
            [RUNTIME_MODE_DATA_MISSING] = "数据缺失",
            [RUNTIME_MODE2_COOLING] = "制冷",
            [RUNTIME_MODE2_COOLING_HOT_WATER] = "制冷+热水",
            [RUNTIME_MODE2_HEATING] = "制热",
            [RUNTIME_MODE2_HEATING_HOT_WATER] = "制热+热水",
            [RUNTIME_MODE2_HOT_WATER] = "热水",
            [RUNTIME_MODE2_COMPRESSOR_OFF] = "压机未运行",
        };

        /// <summary>
        /// 由 30106 实际状态计算运行模式1枚举：bit3~5（001=制冷待机，010=制冷报警停机，
        /// 100=制热待机，101=制热报警停机，110=除霜中，111=压缩机预热，000/011=--）。
        /// 30106 缺失时返回 0（数据缺失）。
        /// </summary>
        internal static ushort ComputeRuntimeModeEnum(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.STATUS_WORD_ADDR, out ushort status))
                return RUNTIME_MODE_DATA_MISSING;

            return ((status >> 3) & 0x07) switch
            {
                0b001 => RUNTIME_MODE1_COOLING_STANDBY,
                0b010 => RUNTIME_MODE1_COOLING_ALARM_STOP,
                0b100 => RUNTIME_MODE1_HEATING_STANDBY,
                0b101 => RUNTIME_MODE1_HEATING_ALARM_STOP,
                0b110 => RUNTIME_MODE1_DEFROSTING,
                0b111 => RUNTIME_MODE1_PREHEATING,
                _ => RUNTIME_MODE1_NONE // 000B / 011B
            };
        }

        /// <summary>
        /// 由 30106 实际状态计算运行模式2枚举：bit3~5 与 bit6 组合。
        /// 000+bit6=0→制冷；000+bit6=1→制冷+热水；011+bit6=0→制热；011+bit6=1→制热+热水；
        /// 其他 bit3~5+bit6=1→热水；其他 bit3~5+bit6=0→压机未运行。30106 缺失时返回 0（数据缺失）。
        /// </summary>
        internal static ushort ComputeRuntimeMode2Enum(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.STATUS_WORD_ADDR, out ushort status))
                return RUNTIME_MODE_DATA_MISSING;

            ushort baseBits = (ushort)((status >> 3) & 0x07);
            // bit6 生活热水运行状态；30223 电动三通球阀1 位置（≠0=通热水，热水相关模式才显示）
            bool hotWater = (status & 0x0040) != 0
                && values.TryGetValue(RegisterMap.THREE_WAY_VALVE1_ADDR, out ushort valve1)
                && valve1 != 0;

            return (baseBits, hotWater) switch
            {
                (0b000, false) => RUNTIME_MODE2_COOLING,
                (0b000, true) => RUNTIME_MODE2_COOLING_HOT_WATER,
                (0b011, false) => RUNTIME_MODE2_HEATING,
                (0b011, true) => RUNTIME_MODE2_HEATING_HOT_WATER,
                (_, true) => RUNTIME_MODE2_HOT_WATER,
                _ => RUNTIME_MODE2_COMPRESSOR_OFF
            };
        }

        /// <summary>轮询后向 values 注入虚拟模式寄存器（30990/30991），供状态页曲线与导出使用。</summary>
        internal static void DeriveModeRegisters(Dictionary<ushort, ushort> values)
        {
            values[RUNTIME_MODE_CURVE_ADDR] = ComputeRuntimeModeEnum(values);
            values[RUNTIME_MODE2_CURVE_ADDR] = ComputeRuntimeMode2Enum(values);
        }

        private static string RuntimeModeText(IReadOnlyDictionary<ushort, ushort> values) =>
            RuntimeModeMeanings.TryGetValue(ComputeRuntimeModeEnum(values), out var runtimeText) ? runtimeText : "数据缺失";

        private static string RuntimeMode2Text(IReadOnlyDictionary<ushort, ushort> values) =>
            RuntimeMode2Meanings.TryGetValue(ComputeRuntimeMode2Enum(values), out var runtimeText) ? runtimeText : "数据缺失";

        /// <summary>
        /// 顶部状态栏"设置模式"文本：由 40001（开关机）、40201（1=制冷，2=制热）与 40202（生活热水启用）组合。
        /// 40001=1 时判断 40201 显示制冷/制热；40001=0 时不判断 40201，不显示制冷/制热。
        /// 40202=1 时显示热水；40001 和 40202 都是 0 时才显示"关机"，40001=0 且 40202=1 显示"热水"。
        /// </summary>
        internal static string SetModeText(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.ON_OFF_ADDR, out ushort onOff) ||
                !values.TryGetValue(RegisterMap.SET_WORK_MODE_ADDR, out ushort mode) ||
                !values.TryGetValue(RegisterMap.HOT_WATER_ENABLE_ADDR, out ushort hotWaterRaw))
                return "数据缺失";

            // 40001=0：不判断 40201，不显示制冷/制热；40202=1 显示"热水"，40202=0 显示"关机"
            if (onOff == 0)
                return hotWaterRaw != 0 ? "热水" : "关机";

            string baseMode = mode switch
            {
                1 => "制冷",
                2 => "制热",
                _ => "数据缺失"
            };
            if (baseMode == "数据缺失")
                return baseMode;

            return hotWaterRaw != 0 ? $"{baseMode}+热水" : baseMode;
        }

        private static string SilentMapGet(ushort m) => RegisterMap.SilentModeMap.TryGetValue(m, out var v) ? v : $"未知({m})";
    }
}

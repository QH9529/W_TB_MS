using System;
using System.Drawing;
using System.Windows.Forms;
using W_TB_MS.Modbus;

namespace W_TB_MS
{
    public class RegisterToolForm : Form
    {
        private readonly ModbusRtuClient _modbus;

        private ComboBox cmbFunc = null!;
        private NumericUpDown nudAddr = null!;
        private NumericUpDown nudQuantity = null!;
        private TextBox txtWriteValue = null!;
        private DataGridView dgvResult = null!;
        private RichTextBox rtbRaw = null!;
        private Label lblInfo = null!;

        public RegisterToolForm(ModbusRtuClient modbus)
        {
            _modbus = modbus;
            InitUI();
        }

        private void InitUI()
        {
            Text = "寄存器读写工具";
            Size = new Size(750, 550);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 400);

            // ===== 顶部：操作区 =====
            var panelTop = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };

            var lblFunc = new Label { Text = "功能码:", Location = new Point(10, 12), AutoSize = true };
            cmbFunc = new ComboBox { Location = new Point(70, 9), Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbFunc.Items.AddRange(new object[]
            {
                "0x04 - 读输入寄存器 (只读)",
                "0x03 - 读保持寄存器 (读写)",
                "0x06 - 写单个保持寄存器",
            });
            cmbFunc.SelectedIndex = 0;
            cmbFunc.SelectedIndexChanged += (s, e) => UpdateLayout();

            var lblAddr = new Label { Text = "起始地址:", Location = new Point(10, 45), AutoSize = true };
            nudAddr = new NumericUpDown { Location = new Point(80, 42), Width = 100, Minimum = 1, Maximum = 65535, Value = 30101 };

            var lblQty = new Label { Text = "数量:", Location = new Point(195, 45), AutoSize = true };
            nudQuantity = new NumericUpDown { Location = new Point(235, 42), Width = 60, Minimum = 1, Maximum = 125, Value = 10 };

            var lblWriteVal = new Label { Text = "写入值:", Location = new Point(310, 45), AutoSize = true };
            txtWriteValue = new TextBox { Location = new Point(365, 42), Width = 80, Text = "0" };
            var lblHex = new Label { Text = "(十进制)", Location = new Point(450, 45), AutoSize = true, ForeColor = Color.Gray };

            var btnRead = new Button { Text = "读取", Location = new Point(10, 78), Width = 80, Height = 30 };
            btnRead.Click += (s, e) => DoRead();

            var btnWrite = new Button { Text = "写入", Location = new Point(100, 78), Width = 80, Height = 30 };
            btnWrite.Click += (s, e) => DoWrite();

            lblInfo = new Label { Text = "", Location = new Point(200, 85), AutoSize = true, ForeColor = Color.Gray };

            panelTop.Controls.AddRange(new Control[] {
                lblFunc, cmbFunc, lblAddr, nudAddr, lblQty, nudQuantity,
                lblWriteVal, txtWriteValue, lblHex, btnRead, btnWrite, lblInfo
            });

            // ===== 中间：结果表格 =====
            dgvResult = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(245, 245, 245) },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(60, 120, 200), ForeColor = Color.White, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) },
            };
            dgvResult.Columns.Add("Index", "序号");
            dgvResult.Columns.Add("Address", "寄存器地址");
            dgvResult.Columns.Add("Dec", "十进制值");
            dgvResult.Columns.Add("Hex", "十六进制值");
            dgvResult.Columns.Add("Bin", "二进制值");

            // ===== 底部：原始报文 =====
            var panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(5) };
            var lblRaw = new Label { Text = "原始报文:", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) };
            rtbRaw = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.FixedSingle,
            };
            panelBottom.Controls.Add(rtbRaw);
            panelBottom.Controls.Add(lblRaw);

            Controls.Add(dgvResult);
            Controls.Add(panelBottom);
            Controls.Add(panelTop);

            UpdateLayout();
        }

        private void UpdateLayout()
        {
            bool isWrite = cmbFunc.SelectedIndex == 2;
            nudQuantity.Visible = !isWrite;
            txtWriteValue.Visible = isWrite;
            // 找到对应label
            foreach (Control c in ((Panel)Controls[2]).Controls)
            {
                if (c is Label lbl)
                {
                    if (lbl.Text == "数量:") lbl.Visible = !isWrite;
                    if (lbl.Text == "写入值:" || lbl.Text == "(十进制)") lbl.Visible = isWrite;
                }
            }

            // 自动填充常用地址
            if (cmbFunc.SelectedIndex == 0) // 读输入
            {
                if (nudAddr.Value < 30001 || nudAddr.Value > 39999)
                    nudAddr.Value = 30101;
            }
            else // 读/写保持
            {
                if (nudAddr.Value < 40001 || nudAddr.Value > 49999)
                    nudAddr.Value = 40001;
            }
        }

        private void DoRead()
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ushort addr = (ushort)nudAddr.Value;
                ushort qty = (ushort)nudQuantity.Value;
                ushort[] values;

                byte functionCode = cmbFunc.SelectedIndex == 0 ? (byte)0x04 : (byte)0x03;
                byte[] reqFrame = ModbusRtuClient.BuildReadRequestFrame(
                    _modbus.MasterAddress, _modbus.SlaveAddress, functionCode, addr, qty);

                if (cmbFunc.SelectedIndex == 0)
                    values = _modbus.ReadInputRegisters(addr, qty);
                else
                    values = _modbus.ReadHoldingRegisters(addr, qty);

                // 显示结果
                dgvResult.Rows.Clear();
                for (int i = 0; i < values.Length; i++)
                {
                    string hex = $"0x{values[i]:X4}";
                    string bin = Convert.ToString(values[i], 2).PadLeft(16, '0');
                    dgvResult.Rows.Add(i, addr + i, values[i], hex, bin);
                }

                // 构造协议响应帧用于显示
                int byteCount = values.Length * 2;
                byte[] respFrame = new byte[byteCount + 8];
                respFrame[0] = _modbus.SlaveAddress;
                respFrame[1] = 0xFF;
                respFrame[2] = reqFrame[2];
                respFrame[3] = (byte)(addr >> 8);
                respFrame[4] = (byte)addr;
                respFrame[5] = (byte)byteCount;
                for (int i = 0; i < values.Length; i++)
                {
                    respFrame[6 + i * 2] = (byte)(values[i] >> 8);
                    respFrame[7 + i * 2] = (byte)(values[i] & 0xFF);
                }
                ushort respCrc = ModbusRtuClient.CalculateCrc(respFrame, respFrame.Length - 2);
                respFrame[^2] = (byte)(respCrc & 0xFF);
                respFrame[^1] = (byte)(respCrc >> 8);

                ShowRaw("TX", reqFrame);
                ShowRaw("RX", respFrame);

                lblInfo.Text = $"读取成功: {values.Length} 个寄存器";
                lblInfo.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                lblInfo.Text = $"读取失败: {ex.Message}";
                lblInfo.ForeColor = Color.Red;
            }
        }

        private void DoWrite()
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ushort addr = (ushort)nudAddr.Value;
                ushort value = ParseRegisterValue(txtWriteValue.Text);
                byte[] request = ModbusRtuClient.BuildWriteSingleRegisterFrame(
                    _modbus.MasterAddress, _modbus.SlaveAddress, addr, value);

                ShowRaw("TX", request);
                _modbus.WriteSingleRegister(addr, value);

                lblInfo.Text = $"写入成功: 地址={addr}, 值={value} (0x{value:X4})";
                lblInfo.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                lblInfo.Text = $"写入失败: {ex.Message}";
                lblInfo.ForeColor = Color.Red;
            }
        }

        private void ShowRaw(string dir, byte[] data)
        {
            string hex = BitConverter.ToString(data).Replace("-", " ");
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string arrow = dir == "TX" ? "→" : "←";
            rtbRaw.AppendText($"[{time}] {arrow} {dir}: {hex}\n");
            rtbRaw.ScrollToCaret();
        }

        private static ushort ParseRegisterValue(string text)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt16(text[2..], 16);
            return ushort.Parse(text);
        }
    }
}

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
        // ==================== 通信日志 ====================

        // ==================== 串口连接 ====================

        // ==================== 轮询 ====================

        private void ChooseArchiveFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择曲线自动存档目录（每6小时同时保存LOG和Excel）",
                SelectedPath = _archiveFolder,
                ShowNewFolderButton = true,
                UseDescriptionForTitle = true
            };
            // 启动阶段不指定隐藏的主窗体作为 owner，避免文件夹选择框出现在后台而阻塞主界面。
            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            try
            {
                ArchiveSettingsStore.SaveArchiveFolder(dialog.SelectedPath);
                _archiveFolder = Path.GetFullPath(dialog.SelectedPath);
                _toolTip.SetToolTip(btnArchiveFolder, _archiveFolder);
                AppendLog("SYS", $"曲线存档路径: {_archiveFolder}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法使用该存档路径：{ex.Message}",
                    "存档路径",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_closeConfirmed)
                return;

            e.Cancel = true;
            if (_closePromptInProgress)
                return;

            _closePromptInProgress = true;
            bool resumePolling = _pollTimer?.Enabled == true;
            _pollTimer?.Stop();
            try
            {
                if (_activeAutomaticArchiveTask is { IsCompleted: false })
                {
                    UseWaitCursor = true;
                    lblStatus.Text = "正在完成曲线自动存档...";
                    // 存档可能因目标盘离线等 IO 问题挂起，限时等待，超时后允许用户强制关闭。
                    Task completed = await Task.WhenAny(
                        _activeAutomaticArchiveTask,
                        Task.Delay(TimeSpan.FromSeconds(30)));
                    if (completed != _activeAutomaticArchiveTask)
                        AppendLog("ERR", "等待自动存档超时（30 秒），未存档数据可在下次启动前手动导出");
                }

                bool hasUnsavedSamples = _timeData.Count > 0
                    && _timeData[^1] > _lastSavedSampleTime;
                if (!hasUnsavedSamples)
                {
                    _closeConfirmed = true;
                    Close();
                    return;
                }

                DialogResult result = MessageBox.Show(
                    $"当前仍有未存档的曲线数据。\n\n是否同时导出 LOG 和 Excel 后再关闭？\n存档路径：{_archiveFolder}",
                    "保存曲线数据",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                if (result == DialogResult.Cancel)
                    return;
                if (result == DialogResult.No)
                {
                    _closeConfirmed = true;
                    Close();
                    return;
                }

                UseWaitCursor = true;
                int startIndex = double.IsNegativeInfinity(_lastSavedSampleTime)
                    ? 0
                    : FindFirstTimeIndexAtOrAfter(
                        _timeData,
                        Math.BitIncrement(_lastSavedSampleTime));
                if (startIndex < _timeData.Count)
                {
                    CompactCurveHistoryData snapshot = CreateCurveHistorySnapshot(startIndex);
                    (string logPath, string excelPath) = await SaveArchivePairAsync(snapshot);
                    MessageBox.Show(
                        $"曲线数据已保存：\n{logPath}\n{excelPath}",
                        "存档完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"关闭前存档失败：{ex.Message}\n\n软件将保持打开，请检查存档路径后重试。",
                    "存档失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                _closePromptInProgress = false;
                if (!_closeConfirmed && resumePolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private void ToggleLog()
        {
            _logVisible = !_logVisible;
            panelLog.Height = _logVisible ? 200 : 36;
            btnToggleLog.Text = _logVisible ? "▼ 隐藏" : "▲ 展开";
        }

        private void FlushPendingLogLines()
        {
            if (IsDisposed || Disposing || rtbLog.IsDisposed)
                return;

            var lines = new List<string>(100);
            while (lines.Count < 100 && _pendingLogLines.TryDequeue(out string? line))
                lines.Add(line);
            if (lines.Count == 0)
                return;

            rtbLog.AppendText(string.Concat(lines));
            rtbLog.ScrollToCaret();
            if (rtbLog.Lines.Length > 500)
            {
                int firstLineToKeep = Math.Max(0, rtbLog.Lines.Length - 300);
                int removeLength = rtbLog.GetFirstCharIndexFromLine(firstLineToKeep);
                if (removeLength > 0)
                {
                    rtbLog.Select(0, removeLength);
                    rtbLog.SelectedText = "";
                }
            }
        }

        /// <summary>记录系统/错误文本消息（SYS/ERR）。</summary>
        private void AppendLog(string direction, string message)
            => AppendLog(direction, System.Text.Encoding.UTF8.GetBytes(message));

        private void AppendLog(string direction, byte[] data)
        {
            if (IsDisposed || Disposing)
                return;

            // TX/RX 帧数量很大，默认只保留系统消息和错误；需要抓包时勾选“详细帧”。
            if (direction is not ("SYS" or "ERR")
                && (!_verboseCommunicationLog || !_logVisible))
                return;

            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string line;
            if (direction is "SYS" or "ERR")
            {
                string message = System.Text.Encoding.UTF8.GetString(data);
                line = $"[{time}] {direction}: {message}\n";
            }
            else
            {
                string hex = BitConverter.ToString(data).Replace("-", " ");
                string arrow = direction == "TX" ? "→" : "←";
                line = $"[{time}] {arrow} {direction}: {hex}\n";
            }

            _pendingLogLines.Enqueue(line);
            if (!InvokeRequired)
                FlushPendingLogLines();
        }

        private void RefreshPorts()
        {
            cmbPort.Items.Clear();
            foreach (var port in ModbusRtuClient.GetAvailablePorts())
                cmbPort.Items.Add(port);
            if (cmbPort.Items.Count > 0)
                cmbPort.SelectedIndex = 0;
        }

        private void BtnConnect_Click(object? sender, EventArgs e)
        {
            if (_isPolling)
            {
                StopPolling();
                StopBackgroundReconnect();
                _failCount = 0;
                _modbus.Close();
                btnConnect.Text = "连接";
                lblStatus.Text = "已断开";
                lblStatus.ForeColor = Color.Gray;
                lblConnIndicator.Text = "";
                lblConnIndicator.ForeColor = Color.Gray;
                cmbSlaveAddr.Enabled = true;
                return;
            }

            if (cmbPort.SelectedItem == null)
            {
                MessageBox.Show("请选择串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cmbBaudRate.SelectedItem == null)
            {
                MessageBox.Show("请选择波特率", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cmbSlaveAddr.SelectedItem == null)
            {
                MessageBox.Show("请选择从机地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                lblConnIndicator.Text = "连接中...";
                lblConnIndicator.ForeColor = Color.Orange;

                _modbus.BaudRate = int.Parse(cmbBaudRate.SelectedItem.ToString()!);
                _modbus.SlaveAddress = Convert.ToByte(cmbSlaveAddr.SelectedItem.ToString()!.Replace("0x", ""), 16);
                _modbus.Open(cmbPort.SelectedItem.ToString()!);
                _lastPortName = cmbPort.SelectedItem.ToString();

                btnConnect.Text = "断开";
                lblStatus.Text = $"已连接: {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps, 从机=0x{_modbus.SlaveAddress:X2}";
                lblStatus.ForeColor = Color.Green;
                lblConnIndicator.Text = "连接成功";
                lblConnIndicator.ForeColor = Color.Green;
                cmbSlaveAddr.Enabled = false;
                AppendLog("SYS", $"已连接 {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps");

                StartPolling();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblConnIndicator.Text = "连接失败";
                lblConnIndicator.ForeColor = Color.Red;
                AppendLog("SYS", $"连接失败: {ex.Message}");
                _modbus.Close();
                cmbSlaveAddr.Enabled = true;
            }
        }

        private void StartPolling()
        {
            _isPolling = true;
            int generation = Interlocked.Increment(ref _pollGeneration);

            _timeData.Clear();
            foreach (List<double> numericData in _numericCurveData.Values)
                numericData.Clear();
            foreach (List<ushort> registerData in _allBitRegisterData.Values)
                registerData.Clear();
            _nextCurvePruneAt = DateTime.MinValue;
            _automaticArchiveSegmentStart = DateTime.Now;
            _nextAutomaticArchiveAt = _automaticArchiveSegmentStart + AutomaticArchiveInterval;
            _nextAutomaticArchiveRetryAt = DateTime.MinValue;
            _lastSavedSampleTime = double.NegativeInfinity;
            _lastFaultSignature = string.Empty;
            _lastFaultCount = -1;
            _chartDataDirty = true;
            _pollTimer = new System.Windows.Forms.Timer { Interval = (int)nudInterval.Value };
            _pollTimer.Tick += async (s, e) => await PollDataAsync(generation);
            _pollTimer.Start();
            _ = PollDataAsync(generation);
        }

        private void StopPolling()
        {
            _isPolling = false;
            Interlocked.Increment(ref _pollGeneration);
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        /// <summary>计算第 attempt 次重连前的等待时间：1s、2s、4s… 封顶 30s。</summary>
        internal static int ComputeReconnectDelayMs(int attempt)
        {
            if (attempt <= 0)
                return ReconnectBaseDelayMs;
            double delay = ReconnectBaseDelayMs * Math.Pow(2, attempt - 1);
            return (int)Math.Min(delay, ReconnectMaxDelayMs);
        }

        private void StartBackgroundReconnect(int generation)
        {
            if (Interlocked.Exchange(ref _reconnectActive, 1) != 0)
                return; // 已有重连在跑

            CancellationTokenSource cts = new();
            _reconnectCts = cts;
            CancellationToken token = cts.Token;
            int attempt = 0;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested
                        && _isPolling
                        && generation == Volatile.Read(ref _pollGeneration))
                    {
                        int delayMs = ComputeReconnectDelayMs(attempt++);
                        string waitText = delayMs >= 1000
                            ? $"{delayMs / 1000.0:0.#} 秒后重试"
                            : $"{delayMs} 毫秒后重试";
                        UpdateReconnectStatus($"重连中... (第 {attempt} 次, {waitText})");
                        try { await Task.Delay(delayMs, token); }
                        catch (OperationCanceledException) { return; }

                        bool reconnected = false;
                        try
                        {
                            reconnected = await Task.Run(() => TryReconnect(token), token);
                        }
                        catch (OperationCanceledException) { return; }

                        if (!reconnected) continue;

                        // 成功：恢复轮询与 UI
                        if (InvokeRequired)
                        {
                            if (IsDisposed || Disposing) return;
                            await Task.Run(() => Invoke(() => OnReconnectSucceeded(generation)));
                        }
                        else
                        {
                            OnReconnectSucceeded(generation);
                        }
                        return;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectActive, 0);
                    cts.Dispose();
                }
            }, CancellationToken.None);
        }

        private void OnReconnectSucceeded(int generation)
        {
            if (generation != Volatile.Read(ref _pollGeneration))
            {
                // 重连期间用户已手动断开，保持断开状态
                _modbus.Close();
                return;
            }
            UpdateReconnectStatus($"已连接: {_lastPortName} @ {_modbus.BaudRate}bps (重连恢复)");
            lblStatus.ForeColor = Color.Green;
            lblConnIndicator.Text = "连接成功";
            lblConnIndicator.ForeColor = Color.Green;
            if (_isPolling && _pollTimer != null) _pollTimer.Start();
        }

        private void StopBackgroundReconnect()
        {
            Interlocked.Exchange(ref _reconnectActive, 0);
            CancellationTokenSource? cts = Interlocked.Exchange(ref _reconnectCts, null);
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { } // 后台任务已自行释放
        }

        private bool TryReconnect(CancellationToken token)
        {
            if (string.IsNullOrEmpty(_lastPortName))
                return false;
            try
            {
                // 串口已消失（USB 拔出）时等待重新枚举，避免对不存在的口反复 Open
                if (!ModbusRtuClient.GetAvailablePorts().Contains(_lastPortName, StringComparer.OrdinalIgnoreCase))
                {
                    AppendLog("SYS", $"串口 {_lastPortName} 不存在，等待重新接入...");
                    return false;
                }
                token.ThrowIfCancellationRequested();
                _modbus.Close();
                _modbus.Open(_lastPortName);
                _modbus.ReadInputRegisters(30101, 6);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("SYS", $"重连失败: {ex.Message}");
                return false;
            }
        }

        private void UpdateReconnectStatus(string text)
        {
            if (InvokeRequired) { Invoke(() => UpdateReconnectStatus(text)); return; }
            lblStatus.Text = text;
            AppendLog("SYS", text);
        }

        private async Task PollDataAsync(int generation)
        {
            if (!_isPolling
                || generation != Volatile.Read(ref _pollGeneration)
                || !_modbus.IsConnected)
                return;
            if (Interlocked.Exchange(ref _pollInProgress, 1) != 0) return;

            try
            {
                _pollTimer?.Stop();

                Dictionary<ushort, ushort> values = await Task.Run(() => ReadPollRegisters(generation));
                if (generation != Volatile.Read(ref _pollGeneration))
                    return;
                DeriveModeRegisters(values);
                UpdateDisplay(values);

                SetLabelText(lblUpdateTime, $"更新: {DateTime.Now:HH:mm:ss.fff}");
                _failCount = 0;
            }
            catch (OperationCanceledException) when (generation != Volatile.Read(ref _pollGeneration))
            {
            }
            catch (Exception ex)
            {
                AppendLog("ERR", ex is TimeoutException ? "通信超时" : ex.Message);
                _failCount++;
                if (_failCount >= ReconnectThreshold)
                {
                    _failCount = 0;
                    _pollTimer?.Stop();
                    UpdateReconnectStatus($"连接异常，进入自动重连 ({(ex is TimeoutException ? "通信超时" : ex.Message)})");
                    StartBackgroundReconnect(generation);
                }
                else
                {
                    lblStatus.Text = ex is TimeoutException ? "通信超时..." : $"错误: {ex.Message}";
                    lblStatus.ForeColor = Color.Orange;
                }
            }
            finally
            {
                Volatile.Write(ref _pollInProgress, 0);
                // 后台重连进行中时不重启计时器，恢复连接后由重连循环统一拉起
                if (_isPolling
                    && generation == Volatile.Read(ref _pollGeneration)
                    && Volatile.Read(ref _reconnectActive) == 0
                    && !_writeInProgress
                    && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private Dictionary<ushort, ushort> ReadPollRegisters(int generation)
        {
            Dictionary<ushort, ushort> values = ReadPollBlockSet(
                PollBlocks, generation, out int essentialBlocksRead, out bool mergedReadFailed);
            if (!mergedReadFailed && essentialBlocksRead > 0)
                return values;

            // 设备不支持合并读取时回退，保证老型号仍可使用。
            AppendLog("SYS", "连续寄存器读取失败，回退到兼容分块模式");
            values = ReadPollBlockSet(LegacyPollBlocks, generation, out essentialBlocksRead, out _);
            if (essentialBlocksRead == 0)
                throw new TimeoutException("核心状态寄存器均未响应");
            return values;
        }

        private Dictionary<ushort, ushort> ReadPollBlockSet(
            IReadOnlyList<(byte FunctionCode, ushort StartAddress, ushort Quantity)> blocks,
            int generation,
            out int essentialBlocksRead,
            out bool hadFailure)
        {
            var values = new Dictionary<ushort, ushort>();
            essentialBlocksRead = 0;
            hadFailure = false;
            foreach (var block in blocks)
            {
                if (generation != Volatile.Read(ref _pollGeneration))
                    throw new OperationCanceledException();
                try
                {
                    ushort[] blockValues = block.FunctionCode == 0x04
                        ? _modbus.ReadInputRegisters(block.StartAddress, block.Quantity)
                        : _modbus.ReadHoldingRegisters(block.StartAddress, block.Quantity);
                    for (int i = 0; i < blockValues.Length; i++)
                        values[(ushort)(block.StartAddress + i)] = blockValues[i];
                    if (block.StartAddress is 30101 or 30201 or 30231)
                        essentialBlocksRead++;
                }
                catch (Exception ex)
                {
                    hadFailure = true;
                    AppendLog("ERR",
                        $"读取 {block.StartAddress}/{block.Quantity} 失败: {ex.Message}");
                }
            }
            return values;
        }
    }
}

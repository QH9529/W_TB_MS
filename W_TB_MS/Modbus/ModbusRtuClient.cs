using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Diagnostics;

namespace W_TB_MS.Modbus
{
    /// <summary>
    /// W系列热泵 Modbus RTU 串口通信客户端
    /// 协议：请求帧为从机地址+主机地址+功能码+地址/数量+CRC，响应帧增加0xFF标识和起始地址字段。
    /// </summary>
    public class ModbusRtuClient : IDisposable
    {
        private SerialPort? _serialPort;
        private readonly object _lock = new();
        private CancellationTokenSource _operationCancellation = new();

        // 通信参数（协议默认）
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public int ReadTimeout { get; set; } = 500;
        public int WriteTimeout { get; set; } = 500;
        // 协议要求帧间至少约 6ms；较大的固定延时会在多块轮询时累积成明显卡顿。
        public int InterFrameDelayMilliseconds { get; set; } = 6;

        // 设备地址
        public byte MasterAddress { get; set; } = 0x51;  // MixPad主机地址
        public byte SlaveAddress { get; set; } = 0xF1;   // 热泵从机地址

        public bool IsConnected => _serialPort?.IsOpen ?? false;
        public event Action<string, byte[]>? FrameObserved;

        /// <summary>取消当前串口读写，避免关闭或重连时等待完整超时。</summary>
        public void CancelPendingOperations() => _operationCancellation.Cancel();

        /// <summary>获取可用串口列表，按 COM 序号从小到大排列</summary>
        public static string[] GetAvailablePorts() => SortPortNames(SerialPort.GetPortNames());

        internal static string[] SortPortNames(IEnumerable<string> portNames) =>
            portNames
                .OrderBy(GetPortSequence)
                .ThenBy(portName => portName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static int GetPortSequence(string portName)
        {
            return portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(portName.AsSpan(3), out int sequence)
                    ? sequence
                    : int.MaxValue;
        }

        /// <summary>打开串口</summary>
        public void Open(string portName)
        {
            lock (_lock)
            {
                _operationCancellation.Cancel();
                CloseCore();
                _operationCancellation.Dispose();
                _operationCancellation = new CancellationTokenSource();
                _serialPort = new SerialPort(portName, BaudRate, Parity, DataBits, StopBits)
                {
                    ReadTimeout = ReadTimeout,
                    WriteTimeout = WriteTimeout
                };
                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
            }
        }

        /// <summary>关闭串口</summary>
        public void Close()
        {
            _operationCancellation.Cancel();
            lock (_lock)
                CloseCore();
        }

        private void CloseCore()
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
            _serialPort?.Dispose();
            _serialPort = null;
        }

        /// <summary>
        /// 读输入寄存器 (功能码 0x04)
        /// W协议请求帧：从机地址 + 主机地址 + 功能码 + 起始地址(2B) + 数量(2B) + CRC(2B)
        /// </summary>
        /// <param name="startAddress">寄存器起始地址（十进制，如30101）</param>
        /// <param name="quantity">读取数量</param>
        /// <returns>寄存器值数组</returns>
        public ushort[] ReadInputRegisters(ushort startAddress, ushort quantity)
        {
            return ReadRegisters(0x04, startAddress, quantity);
        }

        /// <summary>
        /// 读保持寄存器 (功能码 0x03)
        /// </summary>
        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort quantity)
        {
            return ReadRegisters(0x03, startAddress, quantity);
        }

        /// <summary>写单个保持寄存器 (功能码 0x06)</summary>
        public void WriteSingleRegister(ushort address, ushort value)
        {
            CancellationToken cancellationToken = _operationCancellation.Token;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_serialPort?.IsOpen != true)
                    throw new InvalidOperationException("串口未打开");

                // 请求帧: 从机地址 + 主机地址 + 0x06 + 寄存器地址(2B) + 值(2B) + CRC(2B)
                byte[] request = BuildWriteSingleRegisterFrame(MasterAddress, SlaveAddress, address, value);

                _serialPort.DiscardInBuffer();
                _serialPort.Write(request, 0, request.Length);
                FrameObserved?.Invoke("TX", request);

                // 读取响应
                byte[] response = ReadResponse(0x06, cancellationToken);
                FrameObserved?.Invoke("RX", response);
                ValidateResponse(response, 0x06);
                if (response[3] != (byte)(address >> 8) || response[4] != (byte)address ||
                    response[5] != (byte)(value >> 8) || response[6] != (byte)value)
                {
                    throw new InvalidDataException("写入响应回显的寄存器地址或数值不匹配");
                }
                cancellationToken.WaitHandle.WaitOne(InterFrameDelayMilliseconds);
            }
        }

        private ushort[] ReadRegisters(byte functionCode, ushort startAddress, ushort quantity)
        {
            CancellationToken cancellationToken = _operationCancellation.Token;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_serialPort?.IsOpen != true)
                    throw new InvalidOperationException("串口未打开");

                byte[] request = BuildReadRequestFrame(MasterAddress, SlaveAddress, functionCode, startAddress, quantity);

                _serialPort.DiscardInBuffer();
                _serialPort.Write(request, 0, request.Length);
                FrameObserved?.Invoke("TX", request);

                // 等待响应 (协议要求6ms~40ms)
                byte[] response = ReadResponse(functionCode, cancellationToken);
                FrameObserved?.Invoke("RX", response);
                ValidateResponse(response, functionCode);

                // 解析数据
                // W协议响应: 从机地址 + 响应标识 + 功能码 + 起始地址(2B) + 字节数 + 数据 + CRC(2B)
                if (response[3] != (byte)(startAddress >> 8) || response[4] != (byte)startAddress)
                    throw new InvalidDataException($"响应地址不匹配: 0x{response[3]:X2}{response[4]:X2}");
                int byteCount = response[5];
                if ((byteCount & 1) != 0 || byteCount > 250 || byteCount != quantity * 2 || response.Length != byteCount + 8)
                    throw new InvalidDataException($"响应数据长度错误: 字节数={byteCount}, 实际帧长={response.Length}");
                int registerCount = byteCount / 2;
                ushort[] result = new ushort[registerCount];
                for (int i = 0; i < registerCount; i++)
                {
                    result[i] = (ushort)((response[6 + i * 2] << 8) | response[7 + i * 2]);
                }
                cancellationToken.WaitHandle.WaitOne(InterFrameDelayMilliseconds);
                return result;
            }
        }

        private byte[] ReadResponse(byte expectedFunctionCode, CancellationToken cancellationToken)
        {
            if (_serialPort == null) throw new InvalidOperationException("串口未打开");

            long deadline = Stopwatch.GetTimestamp() + (long)(ReadTimeout * (double)Stopwatch.Frequency / 1000.0);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                // 抓包显示串口可能一次返回多帧，先同步到完整的 F1 FF 功能帧头。
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadByteUntil(deadline, cancellationToken, out byte first) || first != SlaveAddress)
                    continue;
                if (!ReadByteUntil(deadline, cancellationToken, out byte marker) || marker != 0xFF)
                    continue;
                if (!ReadByteUntil(deadline, cancellationToken, out byte function) || function != expectedFunctionCode && function != (expectedFunctionCode | 0x80))
                    continue;

                if ((function & 0x80) != 0)
                {
                    byte[] exception = new byte[6] { first, marker, function, 0, 0, 0 };
                    ReadExact(exception, 3, 3, deadline, cancellationToken);
                    return exception;
                }

                if (function is 0x03 or 0x04)
                {
                    if (!ReadByteUntil(deadline, cancellationToken, out byte addressHigh) || !ReadByteUntil(deadline, cancellationToken, out byte addressLow))
                        throw new TimeoutException("读取响应超时");
                    if (!ReadByteUntil(deadline, cancellationToken, out byte count) || count > 250 || (count & 1) != 0)
                        throw new InvalidDataException("响应字节数无效");
                    byte[] response = new byte[count + 8];
                    response[0] = first; response[1] = marker; response[2] = function;
                    response[3] = addressHigh; response[4] = addressLow; response[5] = count;
                    ReadExact(response, 6, count + 2, deadline, cancellationToken);
                    return response;
                }

                // 0x06/0x10 写响应为地址/数量回显，固定 9 字节（含 CRC）。
                byte[] writeResponse = new byte[9] { first, marker, function, 0, 0, 0, 0, 0, 0 };
                ReadExact(writeResponse, 3, 6, deadline, cancellationToken);
                return writeResponse;
            }

            throw new TimeoutException("读取响应超时");
        }

        private bool ReadByteUntil(long deadline, CancellationToken cancellationToken, out byte value)
        {
            value = 0;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (_serialPort?.BytesToRead > 0)
                    {
                        value = (byte)_serialPort.ReadByte();
                        return true;
                    }
                }
                catch (TimeoutException) { }
                if (cancellationToken.WaitHandle.WaitOne(1))
                    cancellationToken.ThrowIfCancellationRequested();
            }
            return false;
        }

        private void ReadExact(byte[] buffer, int offset, int count, long deadline, CancellationToken cancellationToken)
        {
            for (int i = 0; i < count; i++)
            {
                if (!ReadByteUntil(deadline, cancellationToken, out buffer[offset + i]))
                    throw new TimeoutException("读取响应超时");
            }
        }

        private void ValidateResponse(byte[] response, byte expectedFunctionCode)
        {
            if (response.Length < 6)
                throw new InvalidDataException("响应数据不完整");

            // W协议：第2字节为响应标识，应为0xFF
            if (response[1] != 0xFF)
                throw new InvalidDataException($"响应标识错误: 0x{response[1]:X2}，期望 0xFF");

            if (response[0] != SlaveAddress)
                throw new InvalidDataException($"从机地址不匹配: 0x{response[0]:X2}，期望 0x{SlaveAddress:X2}");

            // 检查功能码（异常响应时最高位为1）
            byte funcCode = response[2];
            if ((funcCode & 0x80) != 0)
            {
                string errorCode = response.Length > 3 ? $"0x{response[3]:X2}" : "未知";
                throw new InvalidDataException($"Modbus异常响应: 功能码=0x{funcCode:X2}, 异常码={errorCode}");
            }

            if (funcCode != expectedFunctionCode)
                throw new InvalidDataException($"功能码不匹配: 0x{funcCode:X2}，期望 0x{expectedFunctionCode:X2}");

            if (funcCode is 0x03 or 0x04 && response.Length < 8)
                throw new InvalidDataException("读响应缺少地址或字节数");
            int expectedLength = funcCode is 0x03 or 0x04 ? response[5] + 8 : 9;
            if (response.Length != expectedLength)
                throw new InvalidDataException($"响应帧长度错误: 实际={response.Length}, 期望={expectedLength}");

            ushort expectedCrc = CalculateCrc(response, response.Length - 2);
            ushort actualCrc = (ushort)(response[^2] | (response[^1] << 8));
            if (expectedCrc != actualCrc)
                throw new InvalidDataException($"CRC校验失败: 计算=0x{expectedCrc:X4}, 收到=0x{actualCrc:X4}");
        }

        /// <summary>构造读寄存器请求帧（W协议共9字节，CRC低字节在前）</summary>
        internal static byte[] BuildReadRequestFrame(byte masterAddress, byte slaveAddress, byte functionCode, ushort startAddress, ushort quantity)
        {
            if (functionCode is not (0x03 or 0x04))
                throw new ArgumentOutOfRangeException(nameof(functionCode), "读寄存器功能码只能是0x03或0x04");
            if (quantity is 0 or > 125)
                throw new ArgumentOutOfRangeException(nameof(quantity), "读取数量必须在1到125之间");
            byte[] request = new byte[9];
            request[0] = slaveAddress;
            request[1] = masterAddress;
            request[2] = functionCode;
            request[3] = (byte)(startAddress >> 8);
            request[4] = (byte)(startAddress & 0xFF);
            request[5] = (byte)(quantity >> 8);
            request[6] = (byte)(quantity & 0xFF);
            ushort crc = CalculateCrc(request, 7);
            request[7] = (byte)(crc & 0xFF);
            request[8] = (byte)(crc >> 8);
            return request;
        }

        internal static byte[] BuildWriteSingleRegisterFrame(byte masterAddress, byte slaveAddress, ushort address, ushort value)
        {
            byte[] request = new byte[9]
            {
                slaveAddress, masterAddress, 0x06,
                (byte)(address >> 8), (byte)address,
                (byte)(value >> 8), (byte)value, 0, 0
            };
            ushort crc = CalculateCrc(request, 7);
            request[7] = (byte)crc;
            request[8] = (byte)(crc >> 8);
            return request;
        }

        /// <summary>Modbus CRC-16 校验计算</summary>
        public static ushort CalculateCrc(byte[] data, int length)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (length < 0 || length > data.Length) throw new ArgumentOutOfRangeException(nameof(length));
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
            }
            return crc;
        }

        public void Dispose()
        {
            Close();
            _operationCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

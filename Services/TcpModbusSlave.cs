using Core;
using Core.Event;
using Core.Interfaces;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Timers; // 使用 System.Timers.Timer
using System.Windows;
using static ImTools.ImMap;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ModbusSlave.Services
{
    /// <summary>
    /// TCP 模拟 Modbus RTU 从站服务
    /// </summary>
    public class TcpModbusSlave : BindableBase, IModbusSlave, IDisposable
    {
        private readonly TcpListener _listener;
        private TcpClient _client;
        private TcpSerial _tcpSerial;

        // 锁对象，用于保护 dataBuffer
        private readonly object _lockObject = new object();

        private readonly IEventAggregator _aggregator;

        // 核心缓冲区
        private List<byte> _dataBuffer = new List<byte>();
        public List<byte> DataBuffer { get => _dataBuffer; set => _dataBuffer = value; }

        // 帧检测定时器 (3.5 字符时间)
        private Timer _frameTimer;
        // 3.5 字符时间的毫秒数
        private double _frameInterval;

        #region Serial Parameters

        private int baudRate = 9600;
        public int BaudRate
        {
            get => baudRate;
            set => SetProperty(ref baudRate, value, OnSerialParameterChanged);
        }

        private int dataBits = 8;
        public int DataBits
        {
            get => dataBits;
            set => SetProperty(ref dataBits, value, OnSerialParameterChanged);
        }

        private bool hasParity = false;
        public bool HasParity
        {
            get => hasParity;
            set => SetProperty(ref hasParity, value, OnSerialParameterChanged);
        }

        private double stopBits = 1.0;
        public double StopBits
        {
            get => stopBits;
            set => SetProperty(ref stopBits, value, OnSerialParameterChanged);
        }

        // 参数变更时触发
        private void OnSerialParameterChanged()
        {
            CalWaitTime(); // 重新计算静默时间
            RecreateSerial(); // 重建串口对象（如果需要模拟串口参数变更导致连接重置）
        }

        #endregion

        public TcpModbusSlave(IEventAggregator eventAggregator)
        {
            _aggregator = eventAggregator;
            _listener = new TcpListener(IPAddress.Any, 8889);

            // 初始化默认参数
            BaudRate = 9600;
            DataBits = 8;
            HasParity = false;
            StopBits = 1.0;

            CalWaitTime(); // 初始化时间计算
            InitializeTimer();

            _listener.Start();
            WaitforConnection();
        }

        private void InitializeTimer()
        {
            _frameTimer = new Timer();
            _frameTimer.AutoReset = false; // 只触发一次
            _frameTimer.Elapsed += OnFrameTimeOut;
        }

        /// <summary>
        /// 计算 3.5 字符时间 (Modbus RTU 核心)
        /// </summary>
        public void CalWaitTime()
        {
            // 1. 计算一个字符有多少个位
            // 起始位(1) + 数据位 + 校验位(0或1) + 停止位
            double bitsPerChar = 1.0 + DataBits + (HasParity ? 1.0 : 0.0) + StopBits;

            // 2. 计算传输一个位需要的时间 (秒)
            double timePerBit = 1.0 / BaudRate;

            // 3. 计算一个字符的时间 (秒)
            double timePerChar = timePerBit * bitsPerChar;

            // 4. 计算 3.5 个字符的时间 (毫秒)
            double waitTimeMs = timePerChar * 3.5 * 1000;

            // Modbus 协议规定：波特率 > 19200 时，固定使用 1.75ms 作为超时时间
            // 但为了稳定性，通常至少给 10ms 或 20ms，因为 Windows 定时器精度有限
            if (BaudRate > 19200)
            {
                _frameInterval = 2.0; // 这里的最小值取决于系统Timer精度，太小可能导致断帧
            }
            else
            {
                _frameInterval = waitTimeMs;
            }

            // 稍微增加一点冗余，防止网络抖动造成的误判
            // 对于 Tcp 模拟环境，通常建议至少设置 10ms-20ms 以上
            if (_frameInterval < 10) _frameInterval = 10;

            // 更新定时器间隔
            if (_frameTimer != null)
            {
                _frameTimer.Interval = _frameInterval;
            }
        }

        private void RecreateSerial()
        {
            if (_client == null || !_client.Connected)
                return;

            // 注意：这里需要考虑是否真的需要 new 一个新的 TcpSerial
            // 如果 TcpSerial 只是透传流，改变波特率其实只影响 WaitTime 计算，不影响 TCP 流本身
            // 除非 TcpSerial 内部有根据波特率限速的逻辑，否则通常不需要重建对象，只需更新 WaitTime

            // 如果确实需要重建： //需要动的只有一个canwaittime
            //_tcpSerial?.Dispose();
            //_tcpSerial = new TcpSerial(
            //    _client.GetStream(),
            //    BaudRate,
            //    DataBits,
            //    HasParity,
            //    StopBits
            //);
            //_tcpSerial.OnReceivedData += ReceiveByte;
            //_tcpSerial.OnNetWorkStopped += NetWorkStopped;
        }

        public void WaitforConnection()
        {
            Task.Run(async () =>
            {
                try
                {
                    // 接受连接
                    var client = await _listener.AcceptTcpClientAsync();

                    // 如果已有连接，释放旧的（只支持单连接示例）
                    _client?.Dispose();
                    _client = client;

                    // 创建新的处理实例
                    _tcpSerial?.Dispose();
                    _tcpSerial = new TcpSerial(_client.GetStream(), BaudRate, DataBits, HasParity, StopBits);
                    _tcpSerial.OnReceivedData += ReceiveByte;
                    _tcpSerial.OnNetWorkStopped += NetWorkStopped;
                }
                catch (Exception ex)
                {
                    // 处理监听停止或异常
                    Debug.WriteLine($"Accept Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 接收到一个字节
        /// </summary>
        public void ReceiveByte(byte b)
        {
            //快速存入缓冲区
            lock (_lockObject)
            {
                _dataBuffer.Add(b);
            }

            // 重置定时器
            // 只要有数据源源不断进来，定时器就会一直被 Stop 再 Start，永远不会触发 Elapsed
            // 一旦数据停了，过了 _frameInterval 时间后，OnFrameTimeOut 就会触发
            _frameTimer.Stop();
            _frameTimer.Start();
        }

        /// <summary>
        /// 帧超时处理：采用协议感知 + 滑动窗口法识别帧
        /// 完美解决：粘包、分包、头部对齐、多帧处理
        /// </summary>
        private void OnFrameTimeOut(object sender, ElapsedEventArgs e)
        {
            // 快速返回的小保护
            List<byte[]> validFramesToPublish = new List<byte[]>();
            List<byte> snapshot; //快照的英文,勿忘我

            // 1) 短锁：拷贝并清空原 buffer（若 buffer 超大则直接清空并返回）
            lock (_lockObject)
            {
                if (_dataBuffer.Count == 0)
                    return;

                if (_dataBuffer.Count > 4096)
                {
                    //Debug.WriteLine("缓冲溢出");
                    _dataBuffer.Clear();
                    return;
                }

                // 拷贝快照并清空原来缓冲：之后新的 ReceiveByte 会追加到 _dataBuffer（不会丢）
                snapshot = new List<byte>(_dataBuffer);
                _dataBuffer.Clear();
            }

            //  在本地快照上处理 //滑动窗口法
            List<byte> tail = new List<byte>(); // 未完成的尾部，回写到原 _dataBuffer 前端
            try
            {
                while (snapshot.Count > 0)
                {
                    // 第一步 如果不足最小长度，直接保存为尾部并退出
                    if (snapshot.Count < 4)
                    {
                        tail.AddRange(snapshot);
                        break;
                    }

                    //第二步
                    int expectedLength = GetExpectedRequestLength(snapshot); //  >0 确定长度, 0 表示需要更多字节, -1 表示无效头部

                    if (expectedLength == -1)
                    {
                        // 垃圾字节，滑动丢弃首字节，继续尝试
                        snapshot.RemoveAt(0);
                        continue;
                    }

                    if (expectedLength == 0 || snapshot.Count < expectedLength)
                    {
                        // 帧尚未收齐（分包），整个 snapshot 作为尾部保留
                        tail.AddRange(snapshot);
                        break;
                    }

                    // 有足够数据，进行 CRC 校验（使用你的 CheckCrc(buffer, offset, length)）
                    if (CheckCrc(snapshot, 0, expectedLength))
                    {
                        // 提取完整帧
                        byte[] frameBytes = new byte[expectedLength];
                        snapshot.CopyTo(0, frameBytes, 0, expectedLength);

                        // 直接保存 byte[]，在锁外发布（避免字符串转换开销）
                        validFramesToPublish.Add(frameBytes);

                        // 移除已处理帧，继续循环处理可能的粘包
                        snapshot.RemoveRange(0, expectedLength);
                    }
                    else
                    {   //CRC失败不能整个清空,剩余部分仍可能提取出有价值的完整帧
                        snapshot.RemoveAt(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("rtu帧处理异常: " + ex);
                // 出错时尽量保留 snapshot 的残余，写回原 buffer
                tail.AddRange(snapshot);
            }

            // 3) 将未完成的尾部按时间顺序插回原始缓冲前端（短锁）
            if (tail.Count > 0)
            {
                lock (_lockObject)
                {
                    // 插到前面，确保尾部（旧的未完成数据）在新的到达字节之前
                    _dataBuffer.InsertRange(0, tail);
                }
            }

            // 4) 锁外发布完整帧（发布与执行都在这里做，避免阻塞接收）
            foreach (var frameBytes in validFramesToPublish)
            {
                try
                {
                    // 直接发布 byte[] 更高效（订阅者需改为接收 byte[]）
                    _aggregator.GetEvent<MessageEvent>().Publish(frameBytes);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Publish exception: " + ex);
                }
            }
        }

        /// <summary>
        /// 核心辅助：根据 Modbus 从站协议判断预期的 Request 帧长度
        /// </summary>
        private int GetExpectedRequestLength(List<byte> buffer)
        {
            // 至少需要 2 字节 (SlaveID + FunctionCode) 才能判断
            if (buffer.Count < 2) return 0;

            byte funcCode = buffer[1];

            switch (funcCode)
            {
                // 标准定长指令 (主站请求)
                case 0x01: // Read Coils
                case 0x02: // Read Discrete Inputs
                case 0x03: // Read Holding Registers
                case 0x04: // Read Input Registers
                case 0x05: // Write Single Coil
                case 0x06: // Write Single Register
                    // 结构: ID(1) + Func(1) + Addr(2) high low + Data/Qty(2) high low  + CRC(2) low high = 8字节
                    return 8;
                //0x05 Write Single Coil
                //Byte0: Slave ID
                //Byte1: Function = 0x05
                //Byte2: Output Address Hi
                //Byte3: Output Address Lo
                //Byte4: Value Hi(FF 或 00) //决定写为0还是1
                //Byte5: Value Lo(00)
                //Byte6: CRC Lo
                //Byte7: CRC Hi

                //0x06 Write Single Register
                //Byte0: Slave ID
                //Byte1: Function = 0x06
                //Byte2: Register Address Hi
                //Byte:  Register Address Lo
                //Byte4  Register Value Hi
                //Byte5: Register Value Lo
                //Byte6: CRC Lo
                //Byte7: CRC Hi

                // 变长指令 (主站请求)  //这两个比较麻烦
                case 0x0F: // Write Multiple Coils
                case 0x10: // Write Multiple Registers
                    // 结构: ID(1) + Func(1) + Addr(2) + Qty(2) + ByteCount(1) + Data(N)(每个两字节 ushort) + CRC(2)
                    // 头部固定长度直到 ByteCount 是 7 字节
                    if (buffer.Count < 7) return 0; // 数据太少，还读不到 ByteCount

                    byte byteCount = buffer[6];
                    // 总长度 = 前缀(7) + 数据(N) + CRC(2)
                    return 9 + byteCount;

                default:
                    // 未知功能码，或者不是标准的 Modbus 请求
                    // 如果你需要支持更多自定义功能码，请在这里添加
                    return -1;
            }
        }

        /// <summary>
        /// 验证缓冲区指定片段的 CRC (避免复制数组)
        /// </summary>
        /// <param name="buffer">源缓冲区</param>
        /// <param name="start">起始索引</param>
        /// <param name="length">校验长度</param>
        /// <returns></returns>
        private bool CheckCrc(List<byte> buffer, int start, int length)
        {
            if (length < 4) return false;
            int end = start + length;

            // 接收到的 CRC (最后两个字节)
            byte receivedCrcLo = buffer[end - 2];
            byte receivedCrcHi = buffer[end - 1];

            // 计算 CRC (不包含最后两个字节)
            UInt16 calculatedCrc = 0xFFFF;
            for (int i = start; i < end - 2; i++)
            {
                calculatedCrc ^= buffer[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((calculatedCrc & 0x0001) != 0)
                    {
                        calculatedCrc >>= 1;
                        calculatedCrc ^= 0xA001;
                    }
                    else
                    {
                        calculatedCrc >>= 1;
                    }
                }
            }

            byte calcLo = (byte)(calculatedCrc & 0xFF);
            byte calcHi = (byte)((calculatedCrc >> 8) & 0xFF);

            return (receivedCrcLo == calcLo) && (receivedCrcHi == calcHi);
        }

        public void NetWorkStopped()
        {
            // 停止定时器
            _frameTimer.Stop();

            // 清理旧连接
            _client?.Dispose();
            _tcpSerial?.Dispose();

            lock (_lockObject)
            {
                _dataBuffer.Clear(); // 清空残留数据
            }

            //小巧思之断网后立即等待
            WaitforConnection();
        }

        public async void WriteByteAsync(byte b)
        {
            if (_tcpSerial != null)
                await _tcpSerial.WriteBytesAsync(new byte[] { b });
        }

        public async Task WriteBytesAsync(byte[] bytes)
        {
            if (_tcpSerial != null)
                await _tcpSerial.WriteBytesAsync(bytes);
        }

        public void Dispose()
        {
            _frameTimer?.Stop();
            _frameTimer?.Dispose();
            _listener?.Stop();
            _client?.Dispose();
            _tcpSerial?.Dispose();
        }
    }
}
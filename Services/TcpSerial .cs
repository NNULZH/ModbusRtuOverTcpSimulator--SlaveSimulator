using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ModbusSlave.Services
{
    public class TcpSerial :IDisposable //读到字节时直接提交
    {   
        private CancellationTokenSource CTS = new CancellationTokenSource();

        private readonly NetworkStream _stream;

        public Action<byte> OnReceivedData;

        public Action OnNetWorkStopped;

        // 串口参数（用于计算传输时间）
        public int BaudRate { get; }
        public int DataBits { get; } = 8;
        public bool HasParity { get; } = false;
        public double StopBits { get; } = 1.0; // 1 或 2 也可以是 1.5（若需要）
        public TcpSerial(NetworkStream stream,
                         int baudRate = 9600,
                         int dataBits = 8,
                         bool hasParity = false,
                         double stopBits = 1.0)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            BaudRate = baudRate;
            DataBits = dataBits;
            HasParity = hasParity;
            StopBits = stopBits;

            Task.Run(()=>StartListening(CTS.Token));
        }

        /// <summary>
        /// 写单字节（发送“数据区”）。发送完数据后会 busy-wait（使用 Stopwatch）等待模拟的传输时长。
        /// </summary>
        public async void WriteByteAsync(byte b)
        {
            await WriteBytesAsync(new byte[] { b });
        }

        /// <summary>
        /// 写字节数组（发送“数据区”）。立刻写入网络流，然后使用 Stopwatch 精确模拟整段数据的传输延时。
        /// </summary>
        /// <summary>
        /// 异步写字节数组。先模拟传输延时，再发送数据。
        /// </summary>
        public async Task WriteBytesAsync(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (!_stream.CanWrite) throw new InvalidOperationException("NetworkStream 不可写");

            // 1. 先延时 (Simulate Delay FIRST)
            // 这里我们计算传输需要多少毫秒
            double delayMs = CalculateTransmissionTime(bytes.Length);

            // 如果时间较长，使用 Task.Delay 释放线程；如果时间极短，Task.Delay 不准，可以直接发
            if (delayMs > 0)
            {
                // 转换为整数毫秒，Task.Delay 最小单位通常是 15ms 左右
                // 如果需要极高精度（比如 <10ms），模拟器通常会忽略或累积，
                // 或者使用 Task.Delay(TimeSpan)
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
            }

            try
            {
                // 2. 再发送 
                // 使用异步写入，不阻塞线程
                await _stream.WriteAsync(bytes, 0, bytes.Length);
                await _stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TcpSerial Write Exception: {ex.Message}");
            }
        }

        private double CalculateTransmissionTime(int byteCount)
        {
            double parity = HasParity ? 1.0 : 0.0;
            // 起始位(1) + 数据位 + 校验位 + 停止位
            double bitsPerByte = 1.0 + DataBits + parity + StopBits;
            return (bitsPerByte * byteCount * 1000.0) / BaudRate;
        }

        async void StartListening(CancellationToken token)//在构造之后使用,向上层提交接收的字节  //接收到字节立刻提交吗?暂时立刻提交 // 就应该立刻提交
        {
            byte[] acceptedByte  = new byte[1];
            while (!token.IsCancellationRequested)
            {
                try 
                { 
                    int received =  await _stream.ReadAsync(acceptedByte, 0, 1,token);
                    
                    if (received > 0)
                    {
                        OnReceivedData.Invoke(acceptedByte[0]);
                    }
                }
                catch(IOException) { _stream.Close(); OnNetWorkStopped.Invoke(); break; } // 对方关闭程序时关闭连接
                catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose() //释放资源有几点原则
        {
            //清除后台线程 listening;
            try { CTS.Cancel(); }catch{ }
            CTS.Dispose();
            //关闭流
            _stream?.Close(); // 清空实现了Idispose的子对象 这里close时会自动dispose
            //清除事件引用
            OnNetWorkStopped = null;
            OnReceivedData = null;
        }
    }
}

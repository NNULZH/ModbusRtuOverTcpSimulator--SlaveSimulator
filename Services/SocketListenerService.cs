using Core.Event;
using ModbusSlave.Models;
using Prism.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Core;

namespace ModbusSlave.Services  //就先暂时使用普通的信道传输吧,之后再使用串口信道
{
    class SocketListenerService : ISocketListenerService
    {
        private TcpListener listener;
        private TcpClient masterClient;
        private NetworkStream masterStream;

        public TcpListener Listener => listener;
        public TcpClient MasterClient => masterClient;
        public NetworkStream MasterStream => masterStream;

        public IEventAggregator Aggregator { get; }

        public SocketListenerService(IEventAggregator aggregator)
        {
            Aggregator = aggregator;

            listener = new TcpListener(IPAddress.Any, 8889);
            Task.Run(Start);   // 不要阻塞构造函数
        }

        private async Task Start()
        {
            listener.Start();

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                MessageBox.Show("未知主站接入");
                masterClient = client;
                masterStream = masterClient.GetStream();

                _ = Task.Run(() => HandleClient(masterClient));
            }
        }

        private void HandleClient(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new MessageBuffer(Aggregator);

            try
            {
                while (true)
                {
                    int data = stream.ReadByte();
                    if (data == -1) break;
                    buffer.AddByte((byte)data);
                }
            }
            catch (IOException)
            {
                //连接中断
            }
        }

        public async Task WriteToMasterAsync(List<byte> bytes)
        {
            if (masterStream == null) return;

            var buffer = bytes.ToArray();
            await masterStream.WriteAsync(buffer, 0, buffer.Length);

            await Task.Delay(4); // 模拟响应时间
        }
    }

}

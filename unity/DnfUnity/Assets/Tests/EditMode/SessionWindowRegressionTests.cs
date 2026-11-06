using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DnfUnity.Tests
{
    // 会话收窗的运行时回归:Unity 的 Mono 里 NetworkStream.ReadAsync 不响应取消令牌,
    // 收窗若靠取消令牌掐读,服务端静默时会无限期等(控制台运行时则正常,-dnfnet 曾卡在这里)。
    // 回环对端只连接不发一字节:ReceiveAsync 的窗口必须靠自己按时结束,返回空列表。
    public sealed class SessionWindowRegressionTests
    {
        [UnityTest]
        public IEnumerator SilentPeer_ReceiveWindowEndsOnTime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            var acceptWatch = Stopwatch.StartNew();
            while (!acceptTask.IsCompleted && acceptWatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                yield return null;
            }
            Assert.IsTrue(acceptTask.IsCompleted, "回环连接没有被接受");
            using (TcpClient server = acceptTask.Result)
            {
                var session = new GameClientSession(client.GetStream());
                Task<List<DecodedFrame>> windowTask = session.ReceiveAsync(TimeSpan.FromMilliseconds(1500));
                var watch = Stopwatch.StartNew();
                while (!windowTask.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(6))
                {
                    yield return null;
                }
                Assert.IsTrue(windowTask.IsCompleted, "静默对端下收窗没有按时结束");
                Assert.IsFalse(windowTask.IsFaulted, "收窗出错: " +
                    (windowTask.Exception != null ? windowTask.Exception.GetBaseException().GetType().Name : ""));
                Assert.AreEqual(0, windowTask.Result.Count, "静默对端不该解出帧");
            }
            client.Dispose();
            listener.Stop();
        }
    }
}

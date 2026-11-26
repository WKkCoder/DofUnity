using System.Net;
using System.Net.Sockets;
using System.Text;
using Dnf.Net.ChannelDirectory;

namespace Dnf.NetFake;

/// <summary>
/// 本地回环频道目录假服(真实 4 步协议: 0x0b→0x0c 密钥交换, 0x05→0x06 版本检查, 0x01→0x03 zlib+AES-128-ECB 频道列表)。
/// 返回指向本机回环游戏频道端口的合成频道目录。
/// </summary>
public sealed class FakeChannelDirectoryServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();
    private readonly int _gameChannelPort;
    private readonly string _groupTag;
    private readonly string _channelName;
    private int _requestsServed;

    public FakeChannelDirectoryServer(int gameChannelPort, string groupTag = "siroco", string channelName = "syn01")
    {
        _gameChannelPort = gameChannelPort;
        _groupTag = string.IsNullOrEmpty(groupTag) ? "siroco" : groupTag;
        _channelName = string.IsNullOrEmpty(channelName) ? "syn01" : channelName;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public int RequestsServed => Volatile.Read(ref _requestsServed);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            lock (_lock)
            {
                _clients.Add(client);
            }
            _ = Task.Run(() => ServeClientAsync(client));
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                // 1) 0x0b -> 0x0c (status=0 + 32B session key, first 16B used as AES-128 key)
                ChannelServiceFrame req1 = await ChannelServiceFrameCodec.ReadFrameAsync(stream, ChannelServiceConstants.MaxPayloadBytes, _stop.Token).ConfigureAwait(false);
                if (req1 == null || req1.Header.Command != ChannelServiceConstants.CmdKeyExchangeRequest)
                {
                    return;
                }
                byte[] sessionKey32 = new byte[32];
                for (int i = 0; i < 32; i++)
                {
                    sessionKey32[i] = unchecked((byte)(0x90 + i));
                }
                byte[] sessionKey16 = new byte[16];
                Buffer.BlockCopy(sessionKey32, 0, sessionKey16, 0, 16);

                byte[] keyReplyBody = new byte[ChannelServiceConstants.KeyExchangeReplyBodyLength]; // 4B status=0 + 32B key
                Buffer.BlockCopy(sessionKey32, 0, keyReplyBody, 4, 32);
                await SendFrameAsync(stream, ChannelServiceConstants.CmdKeyExchangeReply, keyReplyBody, _stop.Token).ConfigureAwait(false);

                // 2) 0x05 -> 0x06 (upToDate = 1, 16B AES-128-ECB)
                ChannelServiceFrame req2 = await ChannelServiceFrameCodec.ReadFrameAsync(stream, ChannelServiceConstants.MaxPayloadBytes, _stop.Token).ConfigureAwait(false);
                if (req2 == null || req2.Header.Command != ChannelServiceConstants.CmdVersionCheckRequest)
                {
                    return;
                }
                byte[] verPlain = new byte[16];
                verPlain[0] = 1; // upToDate = 1
                byte[] verCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, verPlain, 16);
                await SendFrameAsync(stream, ChannelServiceConstants.CmdVersionCheckReply, verCipher, _stop.Token).ConfigureAwait(false);

                // 3) 0x01 -> 0x03 (AES-128-ECB encrypted binary channel list, then zlib compressed)
                ChannelServiceFrame req3 = await ChannelServiceFrameCodec.ReadFrameAsync(stream, ChannelServiceConstants.MaxPayloadBytes, _stop.Token).ConfigureAwait(false);
                if (req3 == null || req3.Header.Command != ChannelServiceConstants.CmdChannelListRequest)
                {
                    return;
                }
                byte[] binaryList = BuildBinaryChannelList(_groupTag, _channelName, 1000, 100, "127.0.0.1", _gameChannelPort);
                byte[] encryptedList = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, binaryList, 16);
                byte[] compressedList = FakeWireCodec.ZlibCompress(encryptedList);
                await SendFrameAsync(stream, ChannelServiceConstants.CmdChannelListReply, compressedList, _stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _requestsServed);
            }
            catch
            {
            }
        }
    }

    private static async Task SendFrameAsync(Stream stream, byte cmd, byte[] body, CancellationToken ct)
    {
        byte[] wire = ChannelServiceFrameCodec.Build(cmd, body, flag: 1);
        await stream.WriteAsync(wire, 0, wire.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static byte[] BuildBinaryChannelList(string groupTag, string channelName, int maxCap, int load, string host, int port)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 1); // 1 group
        WriteFixed(ms, groupTag, 20);
        WriteI32(ms, 1); // 1 channel
        WriteFixed(ms, channelName, 20);
        WriteI32(ms, maxCap);
        WriteI32(ms, load);
        WriteFixed(ms, host, 16);
        WriteI32(ms, port);
        return ms.ToArray();
    }

    private static void WriteI32(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BitConverter.TryWriteBytes(buf, value);
        ms.Write(buf);
    }

    private static void WriteFixed(MemoryStream ms, string text, int len)
    {
        byte[] buf = new byte[len];
        if (!string.IsNullOrEmpty(text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, buf, 0, Math.Min(bytes.Length, len));
        }
        ms.Write(buf, 0, len);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_lock)
        {
            foreach (TcpClient c in _clients)
            {
                try { c.Dispose(); } catch { }
            }
            _clients.Clear();
        }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop.Dispose();
    }
}

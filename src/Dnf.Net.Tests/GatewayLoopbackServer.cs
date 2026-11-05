using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dnf.Net.Tests;

/// <summary>一个收到的请求(只含测试关心的部分)。</summary>
internal sealed class LoopbackRequest
{
    public string Method { get; set; }
    public string Target { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 测试进程内、只监听回环地址的极简 HTTP/1.1 服务器:一条连接处理一个请求,响应由测试提供的委托直接写到流上。
/// 只用于验证传输层在真实套接字上的行为(精确的请求头、不跟随重定向、超时、响应体上限);不连接任何外部地址。
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<LoopbackRequest, Stream, CancellationToken, Task> _handler;
    private readonly Task _acceptLoop;
    private int _connections;

    public LoopbackServer(Func<LoopbackRequest, Stream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl
    {
        get { return "http://127.0.0.1:" + Port; }
    }

    public ConcurrentQueue<LoopbackRequest> Requests { get; } = new();

    public int Connections
    {
        get { return Volatile.Read(ref _connections); }
    }

    public CancellationToken StopToken
    {
        get { return _stop.Token; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                LoopbackRequest request = await ReadRequestAsync(stream, _stop.Token);
                if (request == null)
                {
                    return;
                }
                Requests.Enqueue(request);
                await _handler(request, stream, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private static async Task<LoopbackRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        MemoryStream head = new();
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return null;
            }
            head.WriteByte(one[0]);
            byte[] data = head.GetBuffer();
            int n = (int)head.Length;
            if (n >= 4 && data[n - 4] == '\r' && data[n - 3] == '\n' && data[n - 2] == '\r' && data[n - 1] == '\n')
            {
                break;
            }
            if (n > 16384)
            {
                return null;
            }
        }
        string[] lines = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] first = lines[0].Split(' ');
        LoopbackRequest request = new() { Method = first[0], Target = first[1] };
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
        }
        if (request.Headers.TryGetValue("Content-Length", out string lengthText) && int.TryParse(lengthText, out int length) && length > 0)
        {
            byte[] body = new byte[length];
            int got = 0;
            while (got < length)
            {
                int read = await stream.ReadAsync(body.AsMemory(got, length - got), cancellationToken);
                if (read == 0)
                {
                    return null;
                }
                got += read;
            }
            request.Body = body;
        }
        return request;
    }

    /// <summary>写一个完整响应(带 Content-Length 与 Connection: close)。</summary>
    public static async Task WriteResponseAsync(Stream stream, int status, byte[] body, string[] extraHeaders, CancellationToken cancellationToken)
    {
        StringBuilder head = new();
        head.Append("HTTP/1.1 ").Append(status).Append(" Test\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: close\r\n");
        foreach (string header in extraHeaders ?? Array.Empty<string>())
        {
            head.Append(header).Append("\r\n");
        }
        head.Append("\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}

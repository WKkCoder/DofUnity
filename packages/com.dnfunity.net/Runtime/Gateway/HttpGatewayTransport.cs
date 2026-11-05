using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 基于 <see cref="HttpClient"/> 的传输实现。要点:
    /// 默认不自动跟随重定向(把带密文的 POST 悄悄改成别处的 GET 没有意义,3xx 原样交给调用方当传输错误);
    /// POST 的 Content-Type 精确为 <c>application/octet-stream</c>(用字节体,避免字符串体自动追加 <c>; charset=</c>);
    /// 响应体读到 <see cref="GatewayProtocol.MaxResponseBytes"/> 为止,超出抛 <see cref="InvalidDataException"/>;
    /// 超时覆盖"发出请求到读完响应体"的全过程,超时抛 <see cref="TimeoutException"/>,调用方取消仍抛 <see cref="OperationCanceledException"/>。
    /// 构造时可注入 <see cref="HttpMessageHandler"/>(测试用,不连真实网络)。
    /// </summary>
    public sealed class HttpGatewayTransport : IGatewayTransport, IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly HttpClient _http;
        private readonly TimeSpan _timeout;

        /// <summary>默认处理器(不跟随重定向)与默认超时。</summary>
        public HttpGatewayTransport()
            : this(GatewayOptions.DefaultTimeout)
        {
        }

        /// <summary>默认处理器(不跟随重定向)与指定超时。</summary>
        public HttpGatewayTransport(TimeSpan timeout)
            : this(new HttpClientHandler { AllowAutoRedirect = false }, timeout)
        {
        }

        /// <summary>注入处理器(本对象负责在 <see cref="Dispose"/> 时释放它)。</summary>
        public HttpGatewayTransport(HttpMessageHandler handler, TimeSpan timeout)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            _timeout = timeout;
            _http = new HttpClient(handler);
            // 超时由本类用链接的取消令牌统一实施(含读响应体);HttpClient 自带的超时只覆盖到响应头
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        }

        /// <inheritdoc />
        public async Task<GatewayHttpResponse> GetTextAsync(string url, CancellationToken cancellationToken = default)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                return await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<GatewayHttpResponse> PostTextAsync(string url, string body, CancellationToken cancellationToken = default)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                ByteArrayContent content = new ByteArrayContent(Utf8.GetBytes(body ?? string.Empty));
                content.Headers.ContentType = new MediaTypeHeaderValue(GatewayProtocol.RequestContentType);
                request.Content = content;
                return await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>释放底层 <see cref="HttpClient"/> 与处理器。</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        private async Task<GatewayHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(_timeout);
                try
                {
                    using (HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    {
                        string text = await ReadBodyAsync(response.Content, limit.Token).ConfigureAwait(false);
                        return new GatewayHttpResponse((int)response.StatusCode, text, ReadRetryAfter(response));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
                {
                    throw new TimeoutException("gateway request timed out");
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return string.Empty;
            }
            using (Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > GatewayProtocol.MaxResponseBytes)
                    {
                        throw new InvalidDataException("gateway response exceeds the size limit");
                    }
                    buffer.Write(chunk, 0, read);
                }
                return Utf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }

        private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue header = response.Headers.RetryAfter;
            if (header == null)
            {
                return null;
            }
            if (header.Delta.HasValue)
            {
                return header.Delta.Value;
            }
            if (header.Date.HasValue)
            {
                TimeSpan wait = header.Date.Value - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Gateway
{
    /// <summary>传输层返回的原始 HTTP 响应:状态码与文本体。传输层不解释它们(4xx/5xx 也照常返回,不抛)。</summary>
    public readonly struct GatewayHttpResponse
    {
        public GatewayHttpResponse(int statusCode, string text, TimeSpan? retryAfter = null)
        {
            StatusCode = statusCode;
            Text = text;
            RetryAfter = retryAfter;
        }

        /// <summary>HTTP 状态码。</summary>
        public int StatusCode { get; }

        /// <summary>响应体文本(UTF-8 解码);没有响应体时为空串或 null。</summary>
        public string Text { get; }

        /// <summary>响应头 <c>Retry-After</c> 给出的等待时间;没有该头为 null。</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>状态码是否在 200–299。</summary>
        public bool IsSuccessStatus
        {
            get { return StatusCode >= 200 && StatusCode <= 299; }
        }
    }

    /// <summary>
    /// 网关客户端用的传输接口,便于在测试里换成内存实现。传输失败(连不上、超时、响应体过大)用异常表达:
    /// 超时抛 <see cref="TimeoutException"/>;调用方取消抛 <see cref="OperationCanceledException"/>;其余 I/O 问题抛各自的异常。
    /// 只要拿到了 HTTP 响应,不论状态码,都作为 <see cref="GatewayHttpResponse"/> 返回。
    /// </summary>
    public interface IGatewayTransport
    {
        /// <summary>GET 一个地址。</summary>
        Task<GatewayHttpResponse> GetTextAsync(string url, CancellationToken cancellationToken = default);

        /// <summary>POST 文本体(<c>Content-Type: application/octet-stream</c>)。</summary>
        Task<GatewayHttpResponse> PostTextAsync(string url, string body, CancellationToken cancellationToken = default);
    }
}

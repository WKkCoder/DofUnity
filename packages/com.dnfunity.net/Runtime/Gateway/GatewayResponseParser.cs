using System;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 把认证响应的明文解析成 <see cref="GatewayResult"/>:<c>0|令牌</c> = 登录成功;<c>success</c> = 注册/改密/找回成功;
    /// 其余是错误键(已知的映射到枚举,未知的原文保留)。解析是上下文无关的:登录是否应该带令牌由调用方判断。
    /// 宽容点(全部集中在这里):前导空白、错误键/success 的尾随空白与大小写;令牌本身一律原样保留,不 trim。
    /// </summary>
    public static class GatewayResponseParser
    {
        /// <summary>解析响应明文。返回的结果没有 HTTP 状态码(0),由调用方补。</summary>
        public static GatewayResult Parse(string plaintext)
        {
            if (plaintext == null)
            {
                return GatewayResult.Protocol("response is empty");
            }
            string text = plaintext.TrimStart();
            if (text.StartsWith(GatewayProtocol.TokenPrefix, StringComparison.Ordinal))
            {
                string token = text.Substring(GatewayProtocol.TokenPrefix.Length);
                // 空令牌(或只有空白)说明成功码后面什么都没给,继续往下走游戏服登录只会失败,所以在这里就当协议错误
                if (token.Trim().Length == 0)
                {
                    return GatewayResult.Protocol("login response carries an empty token");
                }
                return GatewayResult.WithToken(token);
            }
            string key = text.TrimEnd();
            if (key.Length == 0)
            {
                return GatewayResult.Protocol("response is empty");
            }
            if (key == "0")
            {
                return GatewayResult.Protocol("success code without a token");
            }
            // 错误键是单个标识符;带分隔符的不是键(也可能是别的形状的成功响应),不能把它当错误原文保留下来——里面可能有令牌
            if (key.IndexOf(GatewayProtocol.FieldSeparator) >= 0)
            {
                return GatewayResult.Protocol("response shape is not recognized");
            }
            if (string.Equals(key, GatewayProtocol.SuccessText, StringComparison.OrdinalIgnoreCase))
            {
                return GatewayResult.PlainSuccess();
            }
            return GatewayResult.ServerError(key, GatewayProtocol.ErrorCodeOf(key));
        }
    }
}

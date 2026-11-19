using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>Channel Service(7001)只读拉取结局。</summary>
    public enum ChannelDirectoryLoadOutcome
    {
        /// <summary>成功拿到并解析出频道快照。</summary>
        Ok = 0,

        /// <summary>调用参数不合法。</summary>
        InvalidArgument = 1,

        /// <summary>网络连接、读/写或超时失败。</summary>
        IoError = 2,

        /// <summary>帧头、命令号、flag 或帧长检查失败。</summary>
        FrameError = 3,

        /// <summary>0x0c 密钥交换返回非 0 状态码。</summary>
        KeyExchangeRejected = 4,

        /// <summary>0x0c 响应长度不等于 36 字节。</summary>
        BadKeyExchangeLength = 5,

        /// <summary>0x06 版本检查值既不是 0 也不是 1,或解密后长度不足。</summary>
        VersionRejected = 6,

        /// <summary>zlib 解压或 AES-128-ECB 解密失败。</summary>
        DecompressOrDecryptFailed = 7,

        /// <summary>0x03 频道列表二进制布局校验失败。</summary>
        ParseFailed = 8,
    }

    /// <summary>单次只读目录查询的脱敏结果(不携带账号、令牌与原始载荷字节)。</summary>
    public sealed class ChannelDirectoryLoadResult
    {
        private ChannelDirectoryLoadResult(
            ChannelDirectoryLoadOutcome outcome,
            ChannelDirectorySnapshot snapshot,
            uint? handshakeStatus,
            bool? versionUpToDate,
            ChannelInfoParseStatus? parseStatus,
            string errorDetail)
        {
            Outcome = outcome;
            Snapshot = snapshot;
            HandshakeStatus = handshakeStatus;
            VersionUpToDate = versionUpToDate;
            ParseStatus = parseStatus;
            ErrorDetail = errorDetail;
        }

        /// <summary>是否成功。</summary>
        public bool Succeeded => Outcome == ChannelDirectoryLoadOutcome.Ok;

        /// <summary>结局分类。</summary>
        public ChannelDirectoryLoadOutcome Outcome { get; }

        /// <summary>成功时返回频道快照;失败时为 null。</summary>
        public ChannelDirectorySnapshot Snapshot { get; }

        /// <summary>0x0c 状态码(若有)。</summary>
        public uint? HandshakeStatus { get; }

        /// <summary>0x06 是否最新(若有)。</summary>
        public bool? VersionUpToDate { get; }

        /// <summary>0x03 二进制解析状态(若有)。</summary>
        public ChannelInfoParseStatus? ParseStatus { get; }

        /// <summary>脱敏的失败说明(不含主机/端口/账号/密钥/原始字节)。</summary>
        public string ErrorDetail { get; }

        internal static ChannelDirectoryLoadResult Success(ChannelDirectorySnapshot snapshot, uint handshakeStatus, bool upToDate)
        {
            return new ChannelDirectoryLoadResult(ChannelDirectoryLoadOutcome.Ok, snapshot, handshakeStatus, upToDate, ChannelInfoParseStatus.Ok, null);
        }

        internal static ChannelDirectoryLoadResult Fail(
            ChannelDirectoryLoadOutcome outcome,
            string errorDetail,
            uint? handshakeStatus = null,
            bool? versionUpToDate = null,
            ChannelInfoParseStatus? parseStatus = null)
        {
            return new ChannelDirectoryLoadResult(outcome, null, handshakeStatus, versionUpToDate, parseStatus, errorDetail);
        }
    }

    /// <summary>
    /// Channel Service(7001)只读客户端状态机:<c>0x0b→0x0c</c>、<c>0x05→0x06</c>、条件性 <c>0x09</c>、<c>0x01→0x03</c>。
    /// 每次调用最多执行单次完整只读请求,绝不自动重连或重试。
    /// </summary>
    public static class ChannelDirectoryClient
    {
        /// <summary>连接给定地址与端口执行单次只读目录拉取。</summary>
        public static async Task<ChannelDirectoryLoadResult> LoadAsync(
            string host,
            int port = ChannelServiceConstants.DefaultPort,
            string cachedVersion = ChannelServiceConstants.DefaultScriptVersion,
            ChannelScriptData fallbackScript = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "目标地址或端口参数无效");
            }

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
            if (effectiveTimeout <= TimeSpan.Zero)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "超时时间必须大于 0");
            }

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(effectiveTimeout);
                using (var tcp = new TcpClient())
                {
                    try
                    {
                        Task connectTask = tcp.ConnectAsync(host, port);
                        Task delayTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                        Task finished = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
                        if (finished != connectTask)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "连接频道目录服务超时");
                        }
                        await connectTask.ConfigureAwait(false);
                        using (NetworkStream stream = tcp.GetStream())
                        {
                            return await LoadFromStreamAsync(stream, cachedVersion, fallbackScript, timeoutCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "频道目录读取超时");
                    }
                    catch (SocketException ex)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "TCP 连接或读写失败(" + ex.SocketErrorCode + ")");
                    }
                    catch (IOException)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "网络流读写失败");
                    }
                }
            }
        }

        /// <summary>在已连接的双向字节流上执行完整 4 步只读交互。</summary>
        public static async Task<ChannelDirectoryLoadResult> LoadFromStreamAsync(
            Stream stream,
            string cachedVersion = ChannelServiceConstants.DefaultScriptVersion,
            ChannelScriptData fallbackScript = null,
            CancellationToken cancellationToken = default)
        {
            if (stream == null)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "流不能为空");
            }

            string normalizedVersion = string.IsNullOrEmpty(cachedVersion)
                ? ChannelServiceConstants.DefaultScriptVersion
                : cachedVersion;
            if (normalizedVersion.IndexOf('\0') >= 0 || Encoding.ASCII.GetByteCount(normalizedVersion) >= ChannelServiceConstants.AesBlockSize)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.InvalidArgument, "脚本版本号必须是少于 16 字节且不含 NUL 的 ASCII 串");
            }

            try
            {
                // 1) 0x0b -> 0x0c: 使用预共享初始密钥发送 "dnf_game" (32B AES-ECB),收明文状态 + 32B 新密钥
                byte[] initialKey = ChannelServiceConstants.CreateInitialAesKey();
                byte[] helloCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(
                    initialKey,
                    ChannelServiceConstants.CreateGreetingPlaintext(),
                    32);
                await SendFrameAsync(stream, ChannelServiceConstants.CmdKeyExchangeRequest, helloCipher, cancellationToken).ConfigureAwait(false);

                ChannelServiceFrame keyReply = await ChannelServiceFrameCodec.ReadFrameAsync(
                    stream,
                    ChannelServiceConstants.MaxPayloadBytes,
                    cancellationToken).ConfigureAwait(false);
                if (keyReply == null)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "等待密钥交换响应时连接关闭");
                }
                if (keyReply.Header.Flag < 1 || keyReply.Header.Command != ChannelServiceConstants.CmdKeyExchangeReply)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "密钥交换响应帧头无效: cmd=0x" + keyReply.Header.Command.ToString("x2"));
                }
                if (keyReply.Body.Length != ChannelServiceConstants.KeyExchangeReplyBodyLength)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.BadKeyExchangeLength, "密钥交换响应长度不是 36 字节");
                }

                uint handshakeStatus = ChannelServiceFrameCodec.ReadUInt32Le(keyReply.Body, 0);
                if (handshakeStatus != 0)
                {
                    return ChannelDirectoryLoadResult.Fail(
                        ChannelDirectoryLoadOutcome.KeyExchangeRejected,
                        "密钥交换被拒绝(status=" + handshakeStatus + ")",
                        handshakeStatus: handshakeStatus);
                }
                byte[] sessionKey = new byte[ChannelServiceConstants.AesKeySize];
                Buffer.BlockCopy(keyReply.Body, 4, sessionKey, 0, sessionKey.Length);

                // 2) 0x05 -> 0x06: 发送零填充 16B 版本号密文
                byte[] versionBytes = Encoding.ASCII.GetBytes(normalizedVersion);
                byte[] versionCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(
                    sessionKey,
                    versionBytes,
                    ChannelServiceConstants.AesBlockSize);
                await SendFrameAsync(stream, ChannelServiceConstants.CmdVersionCheckRequest, versionCipher, cancellationToken).ConfigureAwait(false);

                ChannelServiceFrame verReply = await ChannelServiceFrameCodec.ReadFrameAsync(
                    stream,
                    ChannelServiceConstants.MaxVersionReplyBodyLength,
                    cancellationToken).ConfigureAwait(false);
                if (verReply == null)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "等待版本检查响应时连接关闭", handshakeStatus: handshakeStatus);
                }
                if (verReply.Header.Flag < 1 || verReply.Header.Command != ChannelServiceConstants.CmdVersionCheckReply)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "版本检查响应帧头无效: cmd=0x" + verReply.Header.Command.ToString("x2"), handshakeStatus: handshakeStatus);
                }
                if (verReply.Body.Length == 0 || verReply.Body.Length > ChannelServiceConstants.MaxVersionReplyBodyLength
                    || !ChannelServiceCrypto.TryDecryptAes128Ecb(sessionKey, verReply.Body, out byte[] verPlain))
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, "版本检查响应长度或 AES 解密失败", handshakeStatus: handshakeStatus);
                }
                if (verPlain.Length < 4)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.VersionRejected, "版本检查明文长度不足 4 字节", handshakeStatus: handshakeStatus);
                }

                uint upToDateFlag = ChannelServiceFrameCodec.ReadUInt32Le(verPlain, 0);
                if (upToDateFlag != 0 && upToDateFlag != 1)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.VersionRejected, "版本检查状态值非法: " + upToDateFlag, handshakeStatus: handshakeStatus);
                }

                bool upToDate = upToDateFlag == 1;
                string effectiveVersion = normalizedVersion;
                ChannelScriptData scriptData = fallbackScript ?? ChannelScriptData.Default;

                // 3) 若 upToDate == 0: 记下新版本号 [4..20),再发 0x09 拉取最新 ChannelScript.pvf
                if (!upToDate)
                {
                    if (verPlain.Length < 20)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.VersionRejected, "需要更新脚本但缺少 16 字节新版本号", handshakeStatus: handshakeStatus, versionUpToDate: false);
                    }
                    string serverVer = ChannelInfoBinaryParser.ReadFixedString(verPlain, 4, 16);
                    if (!string.IsNullOrEmpty(serverVer))
                    {
                        effectiveVersion = serverVer;
                    }

                    await SendFrameAsync(stream, ChannelServiceConstants.CmdScriptRequest, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                    ChannelServiceFrame scriptReply = await ChannelServiceFrameCodec.ReadFrameAsync(
                        stream,
                        ChannelServiceConstants.MaxPayloadBytes,
                        cancellationToken).ConfigureAwait(false);
                    if (scriptReply == null)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "等待脚本响应时连接关闭", handshakeStatus: handshakeStatus, versionUpToDate: false);
                    }
                    if (scriptReply.Header.Flag < 1 || scriptReply.Body.Length == 0)
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "脚本响应帧头无效", handshakeStatus: handshakeStatus, versionUpToDate: false);
                    }
                    if (!ChannelServiceCrypto.TryInflateZlibAndDecrypt(sessionKey, scriptReply.Body, out byte[] scriptPlain, out string scriptErr))
                    {
                        return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, "脚本包解包失败: " + scriptErr, handshakeStatus: handshakeStatus, versionUpToDate: false);
                    }
                    string scriptText = Encoding.UTF8.GetString(scriptPlain).TrimEnd('\0');
                    scriptData = ChannelScriptParser.Parse(scriptText).MergeFallback(fallbackScript);
                }

                // 4) 0x01 -> 0x03: 拉取压缩+加密的二进制频道列表
                await SendFrameAsync(stream, ChannelServiceConstants.CmdChannelListRequest, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                ChannelServiceFrame listReply = await ChannelServiceFrameCodec.ReadFrameAsync(
                    stream,
                    ChannelServiceConstants.MaxPayloadBytes,
                    cancellationToken).ConfigureAwait(false);
                if (listReply == null)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "等待频道列表响应时连接关闭", handshakeStatus: handshakeStatus, versionUpToDate: upToDate);
                }
                if (listReply.Header.Flag < 1 || listReply.Header.Command != ChannelServiceConstants.CmdChannelListReply || listReply.Body.Length == 0)
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "频道列表响应帧头无效: cmd=0x" + listReply.Header.Command.ToString("x2"), handshakeStatus: handshakeStatus, versionUpToDate: upToDate);
                }
                if (!ChannelServiceCrypto.TryInflateZlibAndDecrypt(sessionKey, listReply.Body, out byte[] listPlain, out string listErr))
                {
                    return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, "频道列表解包失败: " + listErr, handshakeStatus: handshakeStatus, versionUpToDate: upToDate);
                }

                ChannelInfoParseStatus parseStatus = ChannelInfoBinaryParser.TryParse(listPlain, true, out List<ChannelWireGroup> wireGroups);
                if (parseStatus != ChannelInfoParseStatus.Ok)
                {
                    return ChannelDirectoryLoadResult.Fail(
                        ChannelDirectoryLoadOutcome.ParseFailed,
                        "频道列表结构解析失败: " + parseStatus,
                        handshakeStatus: handshakeStatus,
                        versionUpToDate: upToDate,
                        parseStatus: parseStatus);
                }

                ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build(effectiveVersion, upToDate, scriptData, wireGroups);
                return ChannelDirectoryLoadResult.Success(snapshot, handshakeStatus, upToDate);
            }
            catch (EndOfStreamException ex)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, ex.Message);
            }
            catch (IOException)
            {
                return ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.IoError, "频道目录通信 I/O 失败");
            }
        }

        private static async Task SendFrameAsync(Stream stream, byte command, byte[] body, CancellationToken cancellationToken)
        {
            byte[] packet = ChannelServiceFrameCodec.Build(command, body);
            await stream.WriteAsync(packet, 0, packet.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

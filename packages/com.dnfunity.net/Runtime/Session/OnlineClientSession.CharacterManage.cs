using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>角色管理请求类型（<c>1/5</c> 创建、<c>1/6</c> 删除）。</summary>
    public enum OnlineCharacterManageKind
    {
        Create = 0,
        Delete = 1,
    }

    /// <summary>角色创建/删除会话调用的结局分类。</summary>
    public enum OnlineCharacterManageOutcomeKind
    {
        /// <summary>命令回复成功且已收到并解出最新 <c>0/2</c> 模式 2 全量名册。</summary>
        Succeeded = 0,

        /// <summary>服务端业务拒绝（回包首字节为 0，具体原因与原始字节见对应结果对象）。</summary>
        Rejected = 1,

        /// <summary>超时窗口内未收到 <c>1/5</c> 或 <c>1/6</c> 回复。</summary>
        NoReply = 2,

        /// <summary>收到对应包号的回复，但载荷布局无法解析。</summary>
        MalformedReply = 3,

        /// <summary>命令回复成功，但在名册窗口内未收到 <c>0/2</c> 模式 2 刷新推送。</summary>
        RosterMissing = 4,

        /// <summary>命令回复成功，但收到的 <c>0/2</c> 模式 2 名册载荷布局畸形。</summary>
        RosterMalformed = 5,

        /// <summary>发送或等待回复期间连接断开或发生读写错误（链路已关闭，状态已退回 <see cref="OnlineClientSessionState.LoggedIn"/>）。</summary>
        Disconnected = 6,

        /// <summary>帧层编码失败（未写出流）。</summary>
        FrameError = 7,
    }

    /// <summary><see cref="OnlineClientSession.CreateCharacterAsync"/> 与 <see cref="OnlineClientSession.DeleteCharacterAsync"/> 的只读结果。</summary>
    public sealed class OnlineCharacterManageResult
    {
        internal OnlineCharacterManageResult(
            OnlineCharacterManageKind operation,
            OnlineCharacterManageOutcomeKind outcome,
            CharacterCreateResult createResult,
            CharacterDeleteResult deleteResult,
            UserInfoNotice characterList,
            OnlineCharacterListStatus characterListStatus)
        {
            Operation = operation;
            Outcome = outcome;
            CreateResult = createResult;
            DeleteResult = deleteResult;
            CharacterList = characterList;
            CharacterListStatus = characterListStatus;
        }

        public OnlineCharacterManageKind Operation { get; }

        public OnlineCharacterManageOutcomeKind Outcome { get; }

        /// <summary>命令与名册刷新均成功。</summary>
        public bool IsSuccess => Outcome == OnlineCharacterManageOutcomeKind.Succeeded;

        /// <summary>服务端在回复中表示命令已被接受（含名册推送缺失或畸形的情况）。</summary>
        public bool CommandSucceeded =>
            CreateResult != null ? CreateResult.IsSuccess : (DeleteResult != null && DeleteResult.IsSuccess);

        public bool IsDisconnected => Outcome == OnlineCharacterManageOutcomeKind.Disconnected;

        /// <summary><c>1/5</c> 创建回复（收到并解析出回复时非 null）。</summary>
        public CharacterCreateResult CreateResult { get; }

        /// <summary><c>1/6</c> 删除回复（收到并解析出回复时非 null）。</summary>
        public CharacterDeleteResult DeleteResult { get; }

        /// <summary>失败回复携带的原始错误码字节；非业务拒绝时为 null。</summary>
        public byte? RawFailureCode
        {
            get
            {
                if (CreateResult != null && !CreateResult.IsSuccess)
                {
                    return CreateResult.FailureCode;
                }
                if (DeleteResult != null && !DeleteResult.IsSuccess)
                {
                    return DeleteResult.FailureCode;
                }
                return null;
            }
        }

        /// <summary>成功后服务端推送的 <c>0/2</c> 模式 2 全量角色列表；未收到或失败时为 null。</summary>
        public UserInfoNotice CharacterList { get; }

        public OnlineCharacterListStatus CharacterListStatus { get; }
    }

    public sealed partial class OnlineClientSession
    {
        /// <summary>
        /// 是否允许发出会修改服务端角色库的写命令（<c>1/5</c> 创建角色、<c>1/6</c> 删除角色）。
        /// 默认为 <c>false</c>（安全边界）；为 <c>false</c> 时调用创建或删除方法立即抛出 <see cref="InvalidOperationException"/>，
        /// 不获取门闩、不向流写出任何字节。
        /// </summary>
        public bool AllowCharacterManageWrites { get; set; } = false;

        /// <summary>
        /// 在选角阶段（<see cref="OnlineClientSessionState.Connected"/>）发出一次 <c>1/5 CREATE_CHARACTER</c> 请求，不重试。
        /// 成功后等待服务端推送 <c>0/2</c> 模式 2 全量名册并刷新会话缓存（同时替换 <see cref="_earlyFrames"/> 中旧的模式 2 <c>0/2</c> 帧）。
        /// 取消或中途断线时关闭频道连接并退回 <see cref="OnlineClientSessionState.LoggedIn"/>。
        /// </summary>
        public Task<OnlineCharacterManageResult> CreateCharacterAsync(
            byte job,
            byte[] nameUtf8,
            byte eventFlag = 0,
            CancellationToken cancellationToken = default)
        {
            if (!AllowCharacterManageWrites)
            {
                throw new InvalidOperationException("默认禁止创建角色写操作，须显式开启 AllowCharacterManageWrites");
            }
            if (nameUtf8 == null)
            {
                throw new ArgumentNullException(nameof(nameUtf8));
            }
            if (job > CharacterManagePayloads.MaxCreateJobId)
            {
                throw new ArgumentOutOfRangeException(nameof(job));
            }
            if (eventFlag > CharacterManagePayloads.MaxEventFlag)
            {
                throw new ArgumentOutOfRangeException(nameof(eventFlag));
            }
            if (CharacterNameRules.ValidateCreateName(nameUtf8) != CharacterNameValidationError.Valid)
            {
                throw new ArgumentException("角色名不符合创建结构规则", nameof(nameUtf8));
            }

            byte[] payload = CharacterManagePayloads.BuildCreateCharacter(job, nameUtf8, eventFlag);
            return ExecuteCharacterManageAsync(
                OnlineCharacterManageKind.Create,
                CharacterManagePayloads.CreateCharacterPacketId,
                payload,
                cancellationToken);
        }

        /// <summary>
        /// 在选角阶段（<see cref="OnlineClientSessionState.Connected"/>）发出一次 <c>1/6 DELETE_CHARACTER</c> 请求，不重试。
        /// 成功后等待服务端推送 <c>0/2</c> 模式 2 全量名册并刷新会话缓存（同时替换 <see cref="_earlyFrames"/> 中旧的模式 2 <c>0/2</c> 帧）。
        /// 取消或中途断线时关闭频道连接并退回 <see cref="OnlineClientSessionState.LoggedIn"/>。
        /// </summary>
        public Task<OnlineCharacterManageResult> DeleteCharacterAsync(
            byte slotIndex,
            byte[] confirmNameUtf8,
            CancellationToken cancellationToken = default)
        {
            if (!AllowCharacterManageWrites)
            {
                throw new InvalidOperationException("默认禁止删除角色写操作，须显式开启 AllowCharacterManageWrites");
            }
            if (confirmNameUtf8 == null)
            {
                throw new ArgumentNullException(nameof(confirmNameUtf8));
            }
            if (CharacterNameRules.ValidateDeleteConfirmationName(confirmNameUtf8) != CharacterNameValidationError.Valid)
            {
                throw new ArgumentException("删除确认名不符合结构规则", nameof(confirmNameUtf8));
            }

            byte[] payload = CharacterManagePayloads.BuildDeleteCharacter(slotIndex, confirmNameUtf8);
            return ExecuteCharacterManageAsync(
                OnlineCharacterManageKind.Delete,
                CharacterManagePayloads.DeleteCharacterPacketId,
                payload,
                cancellationToken);
        }

        private async Task<OnlineCharacterManageResult> ExecuteCharacterManageAsync(
            OnlineCharacterManageKind operationKind,
            ushort packetId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.Connected)
                {
                    throw new InvalidOperationException("当前状态不能执行角色创建或删除");
                }
                OnlineChannelLink link = _link;
                if (link == null || link.Session == null || !link.Session.IsReady)
                {
                    throw new InvalidOperationException("当前没有就绪的频道会话");
                }

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await ExecuteCharacterManageNoGateAsync(link, operationKind, packetId, payload, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await ResetChannelAfterManageFaultNoGateAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<OnlineCharacterManageResult> ExecuteCharacterManageNoGateAsync(
            OnlineChannelLink link,
            OnlineCharacterManageKind operationKind,
            ushort packetId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            FrameStatus sent;
            try
            {
                sent = await link.Session.SendAsync(PacketClass.Cmd, packetId, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                link.WriteFault = true;
                NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                await ResetChannelAfterManageFaultNoGateAsync().ConfigureAwait(false);
                return new OnlineCharacterManageResult(
                    operationKind, OnlineCharacterManageOutcomeKind.Disconnected, null, null, null, OnlineCharacterListStatus.NotReceived);
            }
            catch (ObjectDisposedException)
            {
                if (_disposed || State == OnlineClientSessionState.Disposed)
                {
                    throw new ObjectDisposedException(nameof(OnlineClientSession));
                }
                link.WriteFault = true;
                NotifyDisconnected(link, OnlineDisconnectReason.WriteError);
                await ResetChannelAfterManageFaultNoGateAsync().ConfigureAwait(false);
                return new OnlineCharacterManageResult(
                    operationKind, OnlineCharacterManageOutcomeKind.Disconnected, null, null, null, OnlineCharacterListStatus.NotReceived);
            }

            if (sent != FrameStatus.Ok)
            {
                return new OnlineCharacterManageResult(
                    operationKind, OnlineCharacterManageOutcomeKind.FrameError, null, null, null, OnlineCharacterListStatus.NotReceived);
            }

            OnlineTownWindows windows = _hooks.Windows ?? OnlineTownWindows.Default;
            TimeSpan replyTimeout = windows.Slot > TimeSpan.Zero ? windows.Slot : TimeSpan.FromSeconds(10);
            TimeSpan rosterTimeout = windows.CharacterList > TimeSpan.Zero ? windows.CharacterList : TimeSpan.FromSeconds(2);
            Stopwatch watch = Stopwatch.StartNew();
            TimeSpan deadline = replyTimeout;

            bool hasReply = false;
            bool malformedReply = false;
            CharacterCreateResult createResult = null;
            CharacterDeleteResult deleteResult = null;
            DecodedFrame rosterFrame = null;
            UserInfoNotice rosterNotice = null;
            OnlineCharacterListStatus rosterStatus = OnlineCharacterListStatus.NotReceived;

            while (true)
            {
                if (hasReply)
                {
                    if (malformedReply)
                    {
                        return new OnlineCharacterManageResult(
                            operationKind, OnlineCharacterManageOutcomeKind.MalformedReply, createResult, deleteResult, null, rosterStatus);
                    }
                    bool cmdOk = operationKind == OnlineCharacterManageKind.Create
                        ? createResult.IsSuccess
                        : deleteResult.IsSuccess;
                    if (!cmdOk)
                    {
                        return new OnlineCharacterManageResult(
                            operationKind, OnlineCharacterManageOutcomeKind.Rejected, createResult, deleteResult, null, OnlineCharacterListStatus.NotReceived);
                    }
                    if (rosterStatus == OnlineCharacterListStatus.Received && rosterNotice != null && rosterFrame != null)
                    {
                        ReplaceCachedCharacterList(rosterNotice, rosterFrame);
                        return new OnlineCharacterManageResult(
                            operationKind, OnlineCharacterManageOutcomeKind.Succeeded, createResult, deleteResult, rosterNotice, rosterStatus);
                    }
                }

                if (link.ReadFailed || link.Session.LastFrameError != FrameError.None || link.Session.PeerClosed)
                {
                    NotifyIfDisconnected(link);
                    await ResetChannelAfterManageFaultNoGateAsync().ConfigureAwait(false);
                    return new OnlineCharacterManageResult(
                        operationKind, OnlineCharacterManageOutcomeKind.Disconnected, createResult, deleteResult, null, rosterStatus);
                }

                TimeSpan left = deadline - watch.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    break;
                }

                List<DecodedFrame> frames = await link.Session.ReceiveAsync(left, stopAfterFrames: 1, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < frames.Count; i++)
                {
                    DecodedFrame frame = frames[i];
                    if (frame == null)
                    {
                        continue;
                    }
                    if (frame.Header.Class == PacketClass.Noti && frame.Header.PacketId == AntibotEnvelope.PacketId)
                    {
                        continue;
                    }
                    if (!hasReply && frame.Header.Class == PacketClass.Cmd && frame.Header.PacketId == packetId)
                    {
                        hasReply = true;
                        if (operationKind == OnlineCharacterManageKind.Create)
                        {
                            if (!CharacterManagePayloads.TryParseCreateResult(frame.Payload, out createResult))
                            {
                                malformedReply = true;
                            }
                            else if (createResult.IsSuccess && rosterStatus != OnlineCharacterListStatus.Received)
                            {
                                deadline = watch.Elapsed + rosterTimeout;
                            }
                        }
                        else
                        {
                            if (!CharacterManagePayloads.TryParseDeleteResult(frame.Payload, out deleteResult))
                            {
                                malformedReply = true;
                            }
                            else if (deleteResult.IsSuccess && rosterStatus != OnlineCharacterListStatus.Received)
                            {
                                deadline = watch.Elapsed + rosterTimeout;
                            }
                        }
                    }
                    else if (frame.Header.Class == PacketClass.Noti && frame.Header.PacketId == UserInfoParser.PacketId)
                    {
                        if (UserInfoParser.TryParse(frame.Payload, out UserInfoNotice parsed, out UserInfoParseStatus parseStatus))
                        {
                            rosterFrame = frame;
                            rosterNotice = parsed;
                            rosterStatus = OnlineCharacterListStatus.Received;
                        }
                        else if (parseStatus == UserInfoParseStatus.Malformed && rosterStatus != OnlineCharacterListStatus.Received)
                        {
                            rosterStatus = OnlineCharacterListStatus.Malformed;
                        }
                    }
                }
            }

            if (link.ReadFailed || link.Session.LastFrameError != FrameError.None || link.Session.PeerClosed)
            {
                NotifyIfDisconnected(link);
                await ResetChannelAfterManageFaultNoGateAsync().ConfigureAwait(false);
                return new OnlineCharacterManageResult(
                    operationKind, OnlineCharacterManageOutcomeKind.Disconnected, createResult, deleteResult, null, rosterStatus);
            }

            if (!hasReply)
            {
                return new OnlineCharacterManageResult(
                    operationKind, OnlineCharacterManageOutcomeKind.NoReply, null, null, null, OnlineCharacterListStatus.NotReceived);
            }

            OnlineCharacterManageOutcomeKind missingOutcome = rosterStatus == OnlineCharacterListStatus.Malformed
                ? OnlineCharacterManageOutcomeKind.RosterMalformed
                : OnlineCharacterManageOutcomeKind.RosterMissing;
            return new OnlineCharacterManageResult(
                operationKind, missingOutcome, createResult, deleteResult, null, rosterStatus);
        }

        private void ReplaceCachedCharacterList(UserInfoNotice rosterNotice, DecodedFrame rosterFrame)
        {
            _characterList = rosterNotice;
            _earlyFrames.RemoveAll(IsMode2UserInfoFrame);
            _earlyFrames.Add(rosterFrame);
        }

        private static bool IsMode2UserInfoFrame(DecodedFrame frame)
        {
            return frame != null
                && frame.Header.Class == PacketClass.Noti
                && frame.Header.PacketId == UserInfoParser.PacketId
                && frame.Payload != null
                && frame.Payload.Length > 0
                && frame.Payload[0] == UserInfoParser.CharacterListMode;
        }

        private async Task ResetChannelAfterManageFaultNoGateAsync()
        {
            await CloseLinkNoGateAsync().ConfigureAwait(false);
            if (State != OnlineClientSessionState.Disposed)
            {
                State = _token == null ? OnlineClientSessionState.Created : OnlineClientSessionState.LoggedIn;
            }
        }
    }
}

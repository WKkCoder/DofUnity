using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.NetFake;

/// <summary>可配置的故障剧本。</summary>
public enum FakeFaultMode
{
    None = 0,
    AuthFail,
    SelectRefused,
    MidDisconnect,
    SettleIncomplete,
    MalformedReply,
}

/// <summary>结算回城信号形态。</summary>
public enum FakeReturnSignalMode
{
    Noti2 = 0,
    AreaPair = 1,
}

/// <summary>单个合成房间定义。</summary>
public sealed class FakeRoomSpec
{
    public byte CellX { get; set; } = 0;
    public byte CellY { get; set; } = 0;
    public ushort MapId { get; set; } = 905;
    public int[] MonsterServerIds { get; set; } = new[] { 9100, 9101 };
}

/// <summary>假游戏频道服剧本配置。</summary>
public sealed class FakeChannelScenario
{
    public FakeFaultMode Fault { get; set; } = FakeFaultMode.None;
    public FakeReturnSignalMode ReturnSignal { get; set; } = FakeReturnSignalMode.AreaPair;
    public string ChannelName { get; set; } = "syn01";
    public byte TownVillage { get; set; } = 1;
    public byte TownArea { get; set; } = 0;
    public ushort CharacterUniqueId { get; set; } = 901;
    public uint InitialGold { get; set; } = 12345;
    public List<FakeCharacterDef> Characters { get; } = new()
    {
        new FakeCharacterDef { Uid = 901, Name = "Hero901", Job = 0, GrowTypeByte = 0, Level = 15, Status = 0 },
    };
    public List<(ushort DungeonId, byte Tier)> Permissions { get; } = new()
    {
        (1, 3),
        (2, 3),
        (3, 3),
        (901, 3),
    };
    public List<FakeRoomSpec> Rooms { get; } = new()
    {
        new FakeRoomSpec { CellX = 0, CellY = 0, MapId = 905, MonsterServerIds = new[] { 9100, 9101 } },
    };
    public short[] CardSelectRights { get; set; } = new short[] { 1, 0, -1, -1 };
    public int SettlementBaseExp { get; set; } = 1200;
    public int SettlementRankBonusExp { get; set; } = 300;
    public int SettlementGoldReward { get; set; } = 5500;
}

/// <summary>
/// 真实 TCP 协议栈的合成游戏频道假服:
/// 走真 BootstrapTransform + 真 14 槽 SessionCipher + 真 zlib + 真 561 信封(AntibotCipher),
/// 覆盖 登录 -> 角色列表 -> 选角 -> 进城 -> 切区 -> 进副本五步 -> 击杀 -> 多房间换房 -> 清图 -> 结算 -> 1/72..1/75 -> 回城与重试。
/// </summary>
public sealed class FakeGameChannelServer : IDisposable
{
    private readonly FakeChannelScenario _scenario;
    private readonly byte[] _keyBlock;
    private readonly byte[] _bootstrapFrame;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly List<TcpClient> _clients = new();
    private readonly object _clientLock = new();
    private readonly ConcurrentQueue<FakeReceivedCommand> _commands = new();
    private int _acceptedConnections;
    private int _activeConnections;
    private int _envelopeSeq = 3932378;

    public FakeGameChannelServer(FakeChannelScenario scenario = null)
    {
        _scenario = scenario ?? new FakeChannelScenario();
        _keyBlock = FakeWireCodec.MakeSyntheticKeyBlock();
        _bootstrapFrame = FakeWireCodec.BuildBootstrapFrame(_keyBlock, _scenario.ChannelName);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public IReadOnlyList<FakeReceivedCommand> ReceivedCommands => _commands.ToArray();

    public void CloseAllClients()
    {
        lock (_clientLock)
        {
            foreach (TcpClient client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
        }
    }

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
            lock (_clientLock)
            {
                _clients.Add(client);
            }
            Interlocked.Increment(ref _acceptedConnections);
            Interlocked.Increment(ref _activeConnections);
            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var cipher = new SessionCipher(_keyBlock);
                var writeLock = new SemaphoreSlim(1, 1);
                var state = new ConnState();

                async Task WriteRawAsync(byte[] frame)
                {
                    await writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        await stream.WriteAsync(frame, 0, frame.Length, _stop.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }

                async Task SendEncAsync(byte cls, ushort id, byte[] payload)
                {
                    await WriteRawAsync(FakeWireCodec.BuildEncryptedServerFrame(cipher, cls, id, payload)).ConfigureAwait(false);
                }

                async Task SendEnvelopeAsync(byte innerCls, ushort innerId, byte[] innerPlain)
                {
                    uint seq = unchecked((uint)Interlocked.Increment(ref _envelopeSeq));
                    await WriteRawAsync(FakeWireCodec.BuildEnvelope561Frame(innerCls, innerId, innerPlain, seq)).ConfigureAwait(false);
                }

                async Task SendPaddingAsync(int count)
                {
                    byte[] pad = FakeWireCodec.BuildEncryptedServerFrame(cipher, 0, 900, Array.Empty<byte>());
                    await writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            await stream.WriteAsync(pad, 0, pad.Length, _stop.Token).ConfigureAwait(false);
                        }
                        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }

                // 0) 引导帧 0/1
                await WriteRawAsync(_bootstrapFrame).ConfigureAwait(false);

                while (!_stop.IsCancellationRequested)
                {
                    FakeReceivedCommand cmd = await FakeWireCodec.ReadClientFrameAsync(stream, cipher, _stop.Token).ConfigureAwait(false);
                    if (cmd == null)
                    {
                        return;
                    }
                    _commands.Enqueue(cmd);

                    if (cmd.PacketClass != PacketClass.Cmd)
                    {
                        continue;
                    }

                    switch (cmd.PacketId)
                    {
                        case 0: // Heartbeat reply from client
                            break;

                        case 1: // Game Login
                            await SendEncAsync(1, 1, FakeWireCodec.BuildLoginOk("n900", 905)).ConfigureAwait(false);
                            // Login window cap = 8 frames: pad 7 so OnlineTownEntry finishes immediately
                            await SendPaddingAsync(7).ConfigureAwait(false);
                            break;

                        case 8: // Character list request (CMD 1/8)
                            await SendEncAsync(0, 2, FakeWireCodec.BuildUserInfoMode2(_scenario.Characters)).ConfigureAwait(false);
                            // CharacterList window cap = 12 frames -> pad 11 so OnlineTownEntry finishes immediately
                            await SendPaddingAsync(11).ConfigureAwait(false);
                            break;

                        case 4: // Select character (1/4)
                            if (_scenario.Fault == FakeFaultMode.SelectRefused)
                            {
                                await SendEncAsync(1, 4, FakeWireCodec.BuildSelectCharacterFailure(2)).ConfigureAwait(false);
                                await SendPaddingAsync(63).ConfigureAwait(false);
                                break;
                            }
                            await SendEncAsync(1, 4, FakeWireCodec.BuildSelectCharacterOk(_scenario.CharacterUniqueId, _scenario.TownVillage)).ConfigureAwait(false);
                            await SendEncAsync(0, 2, FakeWireCodec.BuildUserInfoMode2(_scenario.Characters)).ConfigureAwait(false);
                            await SendEncAsync(0, (ushort)DungeonPermissionParser.PacketId, FakeWireCodec.BuildDungeonPermissions(_scenario.Permissions)).ConfigureAwait(false);
                            await SendEncAsync(0, (ushort)ItemListParser.PacketId, FakeWireCodec.BuildBagSpaceWithCurrency(_scenario.InitialGold)).ConfigureAwait(false);
                            await SendEncAsync(0, (ushort)SlotJudge.EnterCompletePacketId, Array.Empty<byte>()).ConfigureAwait(false);
                            // Pad slot window to hit stopAfterFrames:64 immediately (5 frames already sent -> 59 pads)
                            await SendPaddingAsync(59).ConfigureAwait(false);
                            break;

                        case 38: // SetUserArea (1/38)
                            {
                                byte village = cmd.Payload.Length >= 2 ? cmd.Payload[0] : _scenario.TownVillage;
                                byte area = cmd.Payload.Length >= 2 ? cmd.Payload[1] : _scenario.TownArea;
                                ushort x = cmd.Payload.Length >= 4 ? (ushort)(cmd.Payload[2] | (cmd.Payload[3] << 8)) : (ushort)400;
                                ushort y = cmd.Payload.Length >= 6 ? (ushort)(cmd.Payload[4] | (cmd.Payload[5] << 8)) : (ushort)300;
                                await SendEncAsync(0, TownAreaPayloads.UserAreaPacketId, FakeWireCodec.BuildUserArea23(_scenario.CharacterUniqueId, village, area, x, y)).ConfigureAwait(false);
                                await SendEncAsync(0, TownAreaPayloads.AreaUsersPacketId, FakeWireCodec.BuildAreaUsers24(village, area, _scenario.CharacterUniqueId, x, y)).ConfigureAwait(false);
                            }
                            break;

                        case 15: // StartGame (1/15) -> 0/27 inside 561 envelope
                            await SendEnvelopeAsync(0, DungeonStartParser.PacketId, FakeWireCodec.BuildDungeonStart27()).ConfigureAwait(false);
                            break;

                        case 16: // SelectDungeon (1/16) -> 0/28 + 0/29 inside 561 envelopes
                            {
                                ushort dungeonId = cmd.Payload.Length >= 2
                                    ? (ushort)(cmd.Payload[0] | (cmd.Payload[1] << 8))
                                    : (ushort)901;
                                state.ResetDungeon(_scenario.Rooms);
                                FakeRoomSpec first = state.CurrentRoom;
                                await SendEnvelopeAsync(0, DungeonInfoParser.PacketId, FakeWireCodec.BuildDungeonInfo28(dungeonId)).ConfigureAwait(false);
                                await SendEnvelopeAsync(0, StartMapParser.PacketId, FakeWireCodec.BuildStartMap29(first.CellX, first.CellY, first.MapId, first.MonsterServerIds)).ConfigureAwait(false);
                            }
                            break;

                        case 42: // DieMonster (1/42)
                            {
                                ushort mobId = cmd.Payload.Length >= 2 ? (ushort)(cmd.Payload[0] | (cmd.Payload[1] << 8)) : (ushort)0;
                                if (_scenario.Fault == FakeFaultMode.MidDisconnect)
                                {
                                    // 收到第一条击杀后立刻断连
                                    return;
                                }
                                await SendEncAsync(0, (ushort)OnlineDungeonCombat.KillConfirmNoticeId, FakeWireCodec.BuildKillConfirm(mobId)).ConfigureAwait(false);
                                state.MarkKilled(mobId);
                                if (state.IsCurrentRoomCleared && state.IsLastRoom && !state.ClearNoticeSent)
                                {
                                    state.ClearNoticeSent = true;
                                    await SendEncAsync(0, (ushort)OnlineDungeonCombat.ClearNoticeId, Array.Empty<byte>()).ConfigureAwait(false);
                                }
                            }
                            break;

                        case 48: // MoveMap (1/48)
                            {
                                if (state.TryAdvanceRoom(out FakeRoomSpec next))
                                {
                                    await SendEnvelopeAsync(0, StartMapParser.PacketId, FakeWireCodec.BuildStartMap29(next.CellX, next.CellY, next.MapId, next.MonsterServerIds)).ConfigureAwait(false);
                                }
                                else
                                {
                                    await SendEncAsync(1, OnlineDungeonCombat.MoveRoomPacketId, new byte[] { 0, 19 }).ConfigureAwait(false);
                                }
                            }
                            break;

                        case 40: // RoomLoaded (1/40) -> 0/30
                            await SendEncAsync(0, (ushort)OnlineDungeonCombat.RoomLoadedNoticeId, new byte[4]).ConfigureAwait(false);
                            if (state.IsCurrentRoomCleared && state.IsLastRoom && !state.ClearNoticeSent)
                            {
                                state.ClearNoticeSent = true;
                                await SendEncAsync(0, (ushort)OnlineDungeonCombat.ClearNoticeId, Array.Empty<byte>()).ConfigureAwait(false);
                            }
                            break;

                        case 49: // SetPlayResult (1/49) -> 0x22 + 0x23
                            {
                                byte rank = cmd.Payload.Length >= 1 ? cmd.Payload[0] : (byte)1;
                                await SendEncAsync(0, (ushort)OnlineDungeonCombat.ResultNoticeId, FakeWireCodec.BuildSettlementA22(15200, rank, _scenario.CharacterUniqueId)).ConfigureAwait(false);
                                if (_scenario.Fault != FakeFaultMode.SettleIncomplete)
                                {
                                    await SendEncAsync(0, (ushort)OnlineDungeonCombat.RewardNoticeId, FakeWireCodec.BuildSettlementB23(
                                        _scenario.SettlementBaseExp, _scenario.SettlementRankBonusExp, _scenario.SettlementGoldReward)).ConfigureAwait(false);
                                }
                            }
                            break;

                        case 45: // GiveUp (1/45) -> 0/23 + 0/24
                            await SendEncAsync(0, TownAreaPayloads.UserAreaPacketId, FakeWireCodec.BuildUserArea23(_scenario.CharacterUniqueId, _scenario.TownVillage, _scenario.TownArea)).ConfigureAwait(false);
                            await SendEncAsync(0, TownAreaPayloads.AreaUsersPacketId, FakeWireCodec.BuildAreaUsers24(_scenario.TownVillage, _scenario.TownArea, _scenario.CharacterUniqueId)).ConfigureAwait(false);
                            break;

                        case 72: // ScoreScrollState (1/72)
                            if (_scenario.Fault == FakeFaultMode.MalformedReply)
                            {
                                // 写坏载荷(空包),触发畸形分支
                                await SendEncAsync(1, SettlementMenuPayloads.ScoreScrollStatePacketId, Array.Empty<byte>()).ConfigureAwait(false);
                            }
                            else
                            {
                                await SendEncAsync(1, SettlementMenuPayloads.ScoreScrollStatePacketId, FakeWireCodec.BuildScoreScrollReply72()).ConfigureAwait(false);
                            }
                            break;

                        case 73: // CardSelectRightState (1/73)
                            await SendEncAsync(1, SettlementMenuPayloads.CardSelectRightStatePacketId, FakeWireCodec.BuildCardSelectRightReply73(_scenario.CardSelectRights)).ConfigureAwait(false);
                            break;

                        case 74: // SelectCard (1/74) — no reply required by OnlineSettlementController
                            break;

                        case 75: // EplpCommand (1/75)
                            {
                                byte flag = cmd.Payload.Length >= 1 ? cmd.Payload[0] : SettlementMenuPayloads.EplpFlagExecute;
                                byte rawCmd = cmd.Payload.Length >= 2 ? cmd.Payload[1] : (byte)EplpCommand.ReturnToTown;
                                EplpCommand eplp = rawCmd <= SettlementMenuPayloads.MaxEplpCommand ? (EplpCommand)rawCmd : EplpCommand.ReturnToTown;
                                await SendEncAsync(1, SettlementMenuPayloads.EplpCommandPacketId, FakeWireCodec.BuildEplpReply75(true, flag, eplp)).ConfigureAwait(false);
                                if (flag == SettlementMenuPayloads.EplpFlagExecute)
                                {
                                    if (_scenario.ReturnSignal == FakeReturnSignalMode.Noti2)
                                    {
                                        await SendEncAsync(0, UserInfoParser.PacketId, FakeWireCodec.BuildUserInfoMode2(_scenario.Characters)).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await SendEncAsync(0, TownAreaPayloads.UserAreaPacketId, FakeWireCodec.BuildUserArea23(_scenario.CharacterUniqueId, _scenario.TownVillage, _scenario.TownArea)).ConfigureAwait(false);
                                        await SendEncAsync(0, TownAreaPayloads.AreaUsersPacketId, FakeWireCodec.BuildAreaUsers24(_scenario.TownVillage, _scenario.TownArea, _scenario.CharacterUniqueId)).ConfigureAwait(false);
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private sealed class ConnState
    {
        private List<FakeRoomSpec> _rooms = new();
        private int _roomIndex;
        private readonly HashSet<int> _alive = new();
        public bool ClearNoticeSent;

        public FakeRoomSpec CurrentRoom => _rooms[_roomIndex];
        public bool IsLastRoom => _roomIndex >= _rooms.Count - 1;
        public bool IsCurrentRoomCleared => _alive.Count == 0;

        public void ResetDungeon(List<FakeRoomSpec> rooms)
        {
            _rooms = rooms != null && rooms.Count > 0
                ? rooms
                : new List<FakeRoomSpec> { new FakeRoomSpec() };
            _roomIndex = 0;
            ClearNoticeSent = false;
            LoadAlive();
        }

        public void MarkKilled(ushort mobId)
        {
            _alive.Remove(mobId);
        }

        public bool TryAdvanceRoom(out FakeRoomSpec next)
        {
            if (_roomIndex + 1 < _rooms.Count)
            {
                _roomIndex++;
                LoadAlive();
                next = _rooms[_roomIndex];
                return true;
            }
            next = null;
            return false;
        }

        private void LoadAlive()
        {
            _alive.Clear();
            foreach (int id in _rooms[_roomIndex].MonsterServerIds)
            {
                _alive.Add(id);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        CloseAllClients();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop.Dispose();
    }
}

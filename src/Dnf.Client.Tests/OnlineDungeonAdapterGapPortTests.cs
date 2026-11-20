using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 联网副本会话接缝的真实现(<c>RealOnlineDungeonSessionPort</c>)"是否已断线"判断的缺口测试。
/// 判断的四个来源:会话已释放、没有频道连接、对端已关闭、帧层出过错;只要其中一个成立就算断线,四个都不成立才算在线。
/// 现有测试用的都是假接缝,从没走过真实现的这条判断。这里造真会话(<see cref="OnlineClientSession"/>,构造不做网络动作),
/// 再用反射塞一条以内存流为底的频道连接(游戏会话 <see cref="GameClientSession"/> 读内存流,没有任何套接字、不依赖真实时钟),
/// 逐个来源验证。真实现类型是 internal,经反射以会话为参数构造(与本程序集其它测试对联网层 internal 类型的做法一致)。
/// </summary>
public sealed class OnlineDungeonAdapterGapPortTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>构造真接缝(类型是 internal,经反射建,再按 internal 接口读 <c>IsDisconnected</c>)。</summary>
    private static bool IsDisconnected(OnlineClientSession session)
    {
        Type portType = typeof(OnlineDungeonSettlementReady).Assembly.GetType("Dnf.Client.RealOnlineDungeonSessionPort");
        Assert.NotNull(portType);
        object port = Activator.CreateInstance(portType, Any, null, new object[] { session }, null);
        Assert.NotNull(port);
        PropertyInfo property = portType.GetProperty("IsDisconnected", Any);
        Assert.NotNull(property);
        return (bool)property.GetValue(port);
    }

    /// <summary>把一条以给定游戏会话为底的频道连接塞进会话(私有字段 <c>_link</c>,值是 internal 类 <c>OnlineChannelLink</c>)。</summary>
    private static void AttachLink(OnlineClientSession session, GameClientSession game)
    {
        Type linkType = typeof(OnlineClientSession).Assembly.GetType("Dnf.Net.Session.OnlineChannelLink");
        Assert.NotNull(linkType);
        object link = Activator.CreateInstance(linkType, Any, null, new object[] { game, null, 0u, null }, null);
        Assert.NotNull(link);
        FieldInfo field = typeof(OnlineClientSession).GetField("_link", Any);
        Assert.NotNull(field);
        field.SetValue(session, link);
    }

    /// <summary>读空的内存流:第一次读到 0 字节,游戏会话记下"对端已关闭"。</summary>
    private static async Task<GameClientSession> ClosedGameAsync()
    {
        var game = new GameClientSession(new MemoryStream());
        await game.ReceiveAsync(TimeSpan.FromMilliseconds(50));
        Assert.True(game.PeerClosed);
        Assert.Equal(FrameError.None, game.LastFrameError);
        return game;
    }

    /// <summary>读到 15 个零字节:帧头里的总长度 0 小于最小帧长,帧层记下切帧错误,对端并没有关闭。</summary>
    private static async Task<GameClientSession> FaultedGameAsync()
    {
        var game = new GameClientSession(new MemoryStream(new byte[15]));
        await game.ReceiveAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(game.PeerClosed);
        Assert.Equal(FrameError.LengthBelowMinimum, game.LastFrameError);
        return game;
    }

    /// <summary>
    /// 保护:频道连接健康(对端没关、帧层没出错)的在线会话不算断线;没有频道连接、对端已关闭、
    /// 帧层出过错、会话已释放这四种各自都算断线(帧层出错但对端未关闭的那一种由真实现单独识别)。
    /// </summary>
    [Fact]
    public async Task RealPortReportsDisconnectedForEachBrokenSourceAndConnectedOtherwise()
    {
        // 没有频道连接:游戏会话为空 → 断线
        using (OnlineClientSession noLink = SessionProbe.NewUnconnected())
        {
            Assert.Null(noLink.GameSession);
            Assert.Equal(OnlineClientSessionState.Created, noLink.State);
            Assert.True(IsDisconnected(noLink));
        }

        // 健康连接:会话未释放、对端未关闭、帧层无错 → 在线
        using (OnlineClientSession healthy = SessionProbe.NewUnconnected())
        {
            var game = new GameClientSession(new MemoryStream());
            AttachLink(healthy, game);
            Assert.Same(game, healthy.GameSession);
            Assert.False(game.PeerClosed);
            Assert.Equal(FrameError.None, game.LastFrameError);
            Assert.NotEqual(OnlineClientSessionState.Disposed, healthy.State);
            Assert.False(IsDisconnected(healthy));
        }

        // 对端已关闭:帧层没错,只有关闭这一个来源 → 断线
        using (OnlineClientSession closed = SessionProbe.NewUnconnected())
        {
            AttachLink(closed, await ClosedGameAsync());
            Assert.True(IsDisconnected(closed));
        }

        // 帧层出过错:对端没关闭,只有帧层错误这一个来源 → 断线
        using (OnlineClientSession faulted = SessionProbe.NewUnconnected())
        {
            AttachLink(faulted, await FaultedGameAsync());
            Assert.True(IsDisconnected(faulted));
        }
    }

    /// <summary>
    /// 保护:会话一旦释放,即使频道连接的游戏会话本身健康也立刻算断线(释放优先于连接状态);
    /// 释放之前同一条健康连接是在线的,用来证明翻转来自会话状态。
    /// </summary>
    [Fact]
    public void RealPortTreatsADisposedSessionAsDisconnectedEvenWithAHealthyLink()
    {
        OnlineClientSession session = SessionProbe.NewUnconnected();
        var game = new GameClientSession(new MemoryStream());
        AttachLink(session, game);
        Assert.False(IsDisconnected(session)); // 释放前在线

        session.Dispose();

        Assert.Equal(OnlineClientSessionState.Disposed, session.State);
        Assert.True(IsDisconnected(session));
        Assert.False(game.PeerClosed); // 游戏会话本身没变:断线判断来自会话已释放
        Assert.Equal(FrameError.None, game.LastFrameError);
    }
}

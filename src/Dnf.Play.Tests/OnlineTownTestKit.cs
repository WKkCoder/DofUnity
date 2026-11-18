using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>假传输层收到的一次 1/38 发送(字段与 <see cref="ITownCommandTransport.SendSetUserAreaAsync"/> 的参数一一对应,外加令牌)。</summary>
internal readonly record struct KitSend(byte DstV, byte DstA, ushort X, ushort Y, ushort SrcV, ushort SrcA, byte Flags, CancellationToken Ct);

/// <summary>
/// 联网城镇测试共用的假传输层:记录每次发送(含令牌)、订阅与退订次数,发送行为由测试替换
/// (返回任意任务、返回 null、同步抛异常),下行事件与断线由测试手动推。
/// </summary>
internal sealed class TownKitTransport : ITownCommandTransport
{
    private Action<TownInboundEvent> _inbound;
    private Action _disconnected;

    public int SubscribeCount { get; private set; }

    public int UnsubscribeCount { get; private set; }

    public bool Disposed { get; private set; }

    public List<KitSend> Sent { get; } = new();

    /// <summary>每次发送调用时执行,返回交给协作者的任务(可以是 null);不设就返回已完成的任务。</summary>
    public Func<KitSend, Task> OnSend { get; set; }

    /// <summary>设了就在记录发送之后同步抛出它(模拟传输层同步失败)。</summary>
    public Exception ThrowOnSend { get; set; }

    public event Action<TownInboundEvent> InboundReceived
    {
        add { SubscribeCount++; _inbound += value; }
        remove { UnsubscribeCount++; _inbound -= value; }
    }

    public event Action Disconnected
    {
        add { SubscribeCount++; _disconnected += value; }
        remove { UnsubscribeCount++; _disconnected -= value; }
    }

    public Task SendSetUserAreaAsync(byte targetVillage, byte targetArea, ushort x, ushort y,
        ushort srcVillage, ushort srcArea, byte flags, CancellationToken cancellationToken)
    {
        var send = new KitSend(targetVillage, targetArea, x, y, srcVillage, srcArea, flags, cancellationToken);
        Sent.Add(send);
        if (ThrowOnSend != null)
        {
            throw ThrowOnSend;
        }
        return OnSend != null ? OnSend(send) : Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Push(TownInboundEvent ev) => _inbound?.Invoke(ev);

    public void RaiseDisconnected() => _disconnected?.Invoke();

    public void Dispose() => Disposed = true;
}

/// <summary>合成的下行事件构造(都是中性事件,数值全是测试用的)。</summary>
internal static class KitEvents
{
    public static TownInboundEvent UserArea(byte village, byte area, ushort uid) =>
        new TownInboundEvent(TownInboundKind.UserArea, village, area, uniqueId: uid);

    public static TownInboundEvent AreaUsers(byte village, byte area, params ushort[] uids)
    {
        var users = new TownInboundAreaUser[uids.Length];
        for (int i = 0; i < uids.Length; i++)
        {
            users[i] = new TownInboundAreaUser(uids[i], 130, 140);
        }
        return new TownInboundEvent(TownInboundKind.AreaUsers, village, area, areaUsers: users);
    }

    public static TownInboundEvent Error(byte code, int minutes = 0) =>
        new TownInboundEvent(TownInboundKind.SetUserAreaError, errorCode: code, remainingMinutes: minutes);

    public static TownInboundEvent Kicked(int reasonCode) =>
        new TownInboundEvent(TownInboundKind.Kicked, reasonCode: reasonCode);
}

/// <summary>
/// 假的城镇会话:实现出口来源与出口复位接口,记录每次 <c>Update</c> 的参数与 <c>ClearPendingExit</c> 的次数;
/// <see cref="Hero"/> 可以是真的 <see cref="TownHero"/>(此时位置、朝向取自它,<c>Update</c> 会推进它,和真会话一致),也可以是 null(位置由测试直接给)。
/// <see cref="Hero"/> 可写:用来构造"建会话时没有主角、回退时才有"这种只能这样观察到的分支。
/// </summary>
internal sealed class TownKitSession : IHeroSession, ITownExitSource, ITownExitReset
{
    private double _x;
    private double _y;
    private bool _left;

    public TownKitSession(string mapPath, TownHero hero = null, double x = 0, double y = 0, bool facingLeft = false)
    {
        MapPath = mapPath;
        Hero = hero;
        _x = x;
        _y = y;
        _left = facingLeft;
    }

    public string MapPath { get; }

    public TownHero Hero { get; set; }

    public TownCamera Camera => null;

    public int LayerCount => 0;

    public int NpcCount => 0;

    public List<(double DeltaMs, int DirX, int DirY)> Updates { get; } = new();

    public int ClearCalls { get; private set; }

    public TownExit? PendingExit { get; set; }

    public double HeroX
    {
        get => Hero != null ? Hero.X : _x;
        set => _x = value;
    }

    public double HeroY
    {
        get => Hero != null ? Hero.Y : _y;
        set => _y = value;
    }

    public bool HeroFacingLeft
    {
        get => Hero != null ? Hero.FacingLeft : _left;
        set => _left = value;
    }

    public void OutwardOf(TownExit exit, out int dx, out int dy)
    {
        dx = 0;
        dy = 0;
    }

    public void ClearPendingExit()
    {
        ClearCalls++;
        PendingExit = null;
    }

    public void Update(double deltaMs, int dirX, int dirY)
    {
        Updates.Add((deltaMs, dirX, dirY));
        Hero?.Update(deltaMs, dirX, dirY);
    }

    public List<SceneSprite> Build(long timeMs) => new();
}

/// <summary>只实现会话本身的最小假会话(没有出口来源与出口复位接口),用来走世界包装里"既不是主角会话也不是出口来源"的分支。</summary>
internal sealed class PlainKitSession : IHeroSession
{
    public PlainKitSession(string mapPath, TownHero hero = null)
    {
        MapPath = mapPath;
        Hero = hero;
    }

    public string MapPath { get; }

    public TownHero Hero { get; set; }

    public TownCamera Camera => null;

    public int LayerCount => 0;

    public int NpcCount => 0;

    public List<(double DeltaMs, int DirX, int DirY)> Updates { get; } = new();

    public void Update(double deltaMs, int dirX, int dirY)
    {
        Updates.Add((deltaMs, dirX, dirY));
        Hero?.Update(deltaMs, dirX, dirY);
    }

    public List<SceneSprite> Build(long timeMs) => new();
}

/// <summary>被包装的内层世界:出口去哪按"走出口"规则给,会话由测试给的工厂造(默认是不带主角的 <see cref="TownKitSession"/>,位置取图册落点)。</summary>
internal sealed class TownKitWorld : ITownWorld
{
    public TownKitWorld(TownAtlas atlas, TownPlace start)
    {
        Atlas = atlas;
        Start = start;
        Return = start;
    }

    public TownAtlas Atlas { get; }

    public TownPlace Start { get; }

    public TownPlace Return { get; }

    public Func<TownPlace, IHeroSession> Factory { get; set; }

    public Func<TownPlace, TownExit, bool, TownPlace?> ThroughBehavior { get; set; }

    public List<TownPlace> Created { get; } = new();

    public int ThroughCalls { get; private set; }

    public IHeroSession Create(TownPlace place)
    {
        Created.Add(place);
        if (Factory != null)
        {
            return Factory(place);
        }
        TownArrival arrival = Atlas.Arrive(place);
        return new TownKitSession("kit/" + place.Town + "/" + place.Area, null, arrival.X, arrival.Y, arrival.FacingLeft);
    }

    public SplashScene Splash(TownPlace? from, TownPlace to) => null;

    public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
    {
        ThroughCalls++;
        if (ThroughBehavior != null)
        {
            return ThroughBehavior(from, exit, facingLeft);
        }
        return exit.HasTarget ? new TownPlace(exit.Town, exit.Area, TownEntry.Exit, from.Town, from.Area, facingLeft) : (TownPlace?)null;
    }

    public bool TryRoute(TownPlace from, out TownExit exit)
    {
        exit = default;
        return false;
    }
}

/// <summary>合成图册里的一个出口矩形与去处(地图脚本 [town movable area] 里一个出口 = 6 个整数)。</summary>
internal readonly record struct KitExit(int X, int Y, int W, int H, int Town, int Area);

/// <summary>合成图册里的一块区域:类型标签、类型参数、可走区域矩形(每 4 个整数一个)与出口。</summary>
internal sealed class KitArea
{
    public KitArea(int id, string kind = "[normal]", int[] kindParams = null, int[] walk = null, KitExit[] exits = null)
    {
        Id = id;
        Kind = kind;
        KindParams = kindParams ?? Array.Empty<int>();
        Walk = walk ?? Array.Empty<int>();
        Exits = exits ?? Array.Empty<KitExit>();
    }

    public int Id { get; }

    public string Kind { get; }

    public int[] KindParams { get; }

    public int[] Walk { get; }

    public KitExit[] Exits { get; }
}

/// <summary>合成城镇图册的构造:全部是脚本行拼出来的内存数据,城镇的枚举顺序就是传入顺序。</summary>
internal static class TownKit
{
    public static ScriptLine Line(string tag, params object[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = values[i] is int iv ? new ScriptValue(ScriptValueKind.Int, iv, null) : new ScriptValue(ScriptValueKind.String, 0, (string)values[i]);
        }
        return new ScriptLine(tag, list);
    }

    /// <summary>按传入的城镇顺序造图册:(城镇编号, 区域们)。区域脚本的地图路径是 "K城镇/A区域.map" 的小写形式。</summary>
    public static TownAtlas Atlas(params (int Town, KitArea[] Areas)[] towns)
    {
        var paths = new Dictionary<int, string>();
        var scripts = new Dictionary<string, ScriptLine[]>();
        foreach ((int town, KitArea[] areas) in towns)
        {
            string twn = "town/k" + town + ".twn";
            paths[town] = twn;
            var twnLines = new List<ScriptLine>();
            foreach (KitArea a in areas)
            {
                string raw = "K" + town + "/A" + a.Id + ".map";
                var values = new List<object> { a.Id, raw, a.Kind };
                foreach (int p in a.KindParams)
                {
                    values.Add(p);
                }
                twnLines.Add(Line("[area]", values.ToArray()));
                var mapLines = new List<ScriptLine>();
                if (a.Walk.Length > 0)
                {
                    mapLines.Add(Line("[virtual movable area]", a.Walk.Select(v => (object)v).ToArray()));
                }
                if (a.Exits.Length > 0)
                {
                    var ints = new List<object>();
                    foreach (KitExit e in a.Exits)
                    {
                        ints.AddRange(new object[] { e.X, e.Y, e.W, e.H, e.Town, e.Area });
                    }
                    mapLines.Add(Line("[town movable area]", ints.ToArray()));
                }
                scripts["map/k" + town + "/a" + a.Id + ".map"] = mapLines.ToArray();
            }
            scripts[twn] = twnLines.ToArray();
        }
        return new TownAtlas(paths, p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null, _ => false);
    }

    /// <summary>一个出口:矩形 (x, y, w, h) 通往 (town, area)。</summary>
    public static TownExit Exit(int x, int y, int w, int h, int town, int area) => new TownExit(new WorldRect(x, y, w, h), town, area);

    /// <summary>用迷你职业夹具造一个真的 <see cref="TownHero"/>(不限制可走区域)。</summary>
    public static TownHero NewHero(DnfAssets assets, double x, double y, bool left = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), x, y, left);
    }
}

/// <summary>
/// 世界包装的测试台架:图册 + 假传输层 + 协作者 + 内层假世界 + <see cref="OnlineTownWorld"/>;
/// <c>heroes</c> 为 true 时内层世界造的会话带真的 <see cref="TownHero"/>(要迷你职业夹具,释放时一并清理)。
/// </summary>
internal sealed class TownWorldRig : IDisposable
{
    public const ushort SelfUid = 9302;

    private readonly AvatarFixture _fx;
    private readonly DnfAssets _assets;

    public TownWorldRig(TownAtlas atlas, TownPlace start, bool heroes = false, bool areaSync = true, long timeoutMs = 0)
    {
        Atlas = atlas;
        if (heroes)
        {
            _fx = new AvatarFixture();
            _assets = DnfAssets.Open(_fx.Root);
        }
        Coordinator = new OnlineTownCommandCoordinator(Transport, SelfUid, areaSync, timeoutMs);
        Inner = new TownKitWorld(atlas, start);
        if (heroes)
        {
            Inner.Factory = place =>
            {
                TownArrival arrival = atlas.Arrive(place);
                return new TownKitSession("kit/" + place.Town + "/" + place.Area, NewHero(arrival.X, arrival.Y, arrival.FacingLeft));
            };
        }
        World = new OnlineTownWorld(Inner, atlas, Coordinator, Logs.Add);
    }

    public TownAtlas Atlas { get; }

    public TownKitTransport Transport { get; } = new TownKitTransport();

    public OnlineTownCommandCoordinator Coordinator { get; }

    public TownKitWorld Inner { get; }

    public OnlineTownWorld World { get; }

    public List<string> Logs { get; } = new();

    public TownHero NewHero(double x, double y, bool left = false) => TownKit.NewHero(_assets, x, y, left);

    /// <summary>在 (from → exit) 上走一次 <c>Through</c>(朝向向右)。</summary>
    public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft = false) => World.Through(from, exit, facingLeft);

    /// <summary>对当前在途请求推送自己的 0/23 与 0/24 并轮询一次,返回结果(目标区是普通区域)。</summary>
    public AreaChangeResult ConfirmNormal(byte village, byte area, long nowMs)
    {
        Transport.Push(KitEvents.UserArea(village, area, SelfUid));
        Transport.Push(KitEvents.AreaUsers(village, area, SelfUid));
        return World.Poll(nowMs);
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        _assets?.Dispose();
        _fx?.Dispose();
    }
}

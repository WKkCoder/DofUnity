using System.Reflection;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 结算缺口补测(区域 B)共用的合成辅助:载荷构造、手造本地结算结果、回城用的替身世界与会话。
/// 只造合成数据(编号取 9700 段),不含任何原版内容;既有测试里的私有辅助类不改动,需要的部分在这里自己写一份。
/// </summary>
internal static class OnlineSettlementGapBKit
{
    public const int DungeonId = 9701;
    public const string DungeonPath = "dungeon/test/gapb.dgn";

    /// <summary>一个奖励槽:状态、金额、物品号、物品数量(预留字段恒为 0)。</summary>
    public static OnlineSettlementRewardSlotData Slot(byte status, int value, int itemId = 0, int itemData = 0)
    {
        return new OnlineSettlementRewardSlotData(status, 0, value, itemId, itemData);
    }

    /// <summary>一件任务清图物品(槽号恒为 1,其余字段为 0)。</summary>
    public static OnlineSettlementItemData Quest(int itemId, int data)
    {
        return new OnlineSettlementItemData(1, itemId, data, 0, 0, 0);
    }

    /// <summary>只填被测字段的奖励载荷;没给的字段是空表/0/假。</summary>
    public static OnlineSettlementRewardData Reward(
        int[] expValues = null,
        OnlineSettlementItemData[] questItems = null,
        OnlineSettlementRewardSlotData[] rewards = null,
        OnlineSettlementRewardSlotData[] premium = null)
    {
        return new OnlineSettlementRewardData(
            expValues: expValues,
            expKind: 0,
            questItems: questItems,
            rewards: rewards,
            receiverValue: 0,
            reservedBytes: null,
            premiumRewards: premium,
            percent: 0,
            isReturnUserParty: false,
            isEventCharacParty: false,
            hasDeadUserPenalty: false);
    }

    /// <summary>
    /// 一局联网结算快照:<paramref name="reward"/> 为 null 时奖励段是"不适用"(没有服务端经验块),否则是"解析成功";
    /// 通关结果段同理(<paramref name="result"/>)。
    /// </summary>
    public static OnlineDungeonSettlementData Data(OnlineSettlementRewardData reward, OnlineSettlementResultData result = null, int reportedKills = 0)
    {
        return new OnlineDungeonSettlementData(
            DungeonId,
            DungeonPath,
            difficulty: 0,
            reportedKills: reportedKills,
            droppedKills: 0,
            roomsEntered: 1,
            resultParseState: result != null ? OnlineSettlementParseState.Succeeded : OnlineSettlementParseState.NotApplicable,
            result: result,
            rewardParseState: reward != null ? OnlineSettlementParseState.Succeeded : OnlineSettlementParseState.NotApplicable,
            reward: reward);
    }

    /// <summary>只带本地结算结果的对照上下文。</summary>
    public static OnlineSettlementLocalContext LocalWith(DungeonResult result, int? partySlot = null)
    {
        return new OnlineSettlementLocalContext { LocalResult = result, PartySlot = partySlot };
    }

    /// <summary>
    /// 手造一个只有基础经验与评分加成经验的本地结算结果:<see cref="DungeonResult"/> 的构造器与这两项的写入口都是私有的
    /// (真实路径要经评分表与经验表一路算出来,数值不好精确控制),所以用反射直接写这两个属性;其余字段保持默认(没有评分)。
    /// </summary>
    public static DungeonResult LocalResult(long expBase, long expRankBonus)
    {
        var result = (DungeonResult)Activator.CreateInstance(typeof(DungeonResult), nonPublic: true);
        SetProperty(result, nameof(DungeonResult.ExpBase), expBase);
        SetProperty(result, nameof(DungeonResult.ExpRankBonus), expRankBonus);
        return result;
    }

    private static void SetProperty(object target, string name, object value)
    {
        PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property.SetValue(target, value);
    }
}

/// <summary>回城测试用的替身世界:每次 <see cref="Create"/> 记下落点并交给工厂造会话(工厂可以返回 null)。</summary>
internal sealed class OnlineSettlementGapBTownWorld : ITownWorld
{
    private readonly Func<TownPlace, IHeroSession> _factory;

    public OnlineSettlementGapBTownWorld(TownPlace start, TownPlace ret, Func<TownPlace, IHeroSession> factory)
    {
        Start = start;
        Return = ret;
        _factory = factory;
    }

    public TownPlace Start { get; }

    public TownPlace Return { get; }

    public List<TownPlace> Created { get; } = new List<TownPlace>();

    public IHeroSession Create(TownPlace place)
    {
        Created.Add(place);
        return _factory(place);
    }

    public SplashScene Splash(TownPlace? from, TownPlace to) => null;

    public TownPlace? Through(TownPlace from, TownExit exit, bool heroFacingLeft) => null;

    public bool TryRoute(TownPlace from, out TownExit exit)
    {
        exit = default;
        return false;
    }
}

/// <summary>回城测试用的替身会话:主角与镜头可以是真的也可以是 null;记录"清出口触发标志"被调了几次。</summary>
internal sealed class OnlineSettlementGapBHeroSession : IHeroSession, ITownExitReset
{
    public OnlineSettlementGapBHeroSession(TownHero hero, TownCamera camera)
    {
        Hero = hero;
        Camera = camera;
    }

    /// <summary><see cref="ClearPendingExit"/> 被调用的次数。</summary>
    public int ExitResets { get; private set; }

    public void ClearPendingExit()
    {
        ExitResets++;
    }

    public string MapPath => "fake/gapb";

    public TownHero Hero { get; }

    public TownCamera Camera { get; }

    public int LayerCount => 0;

    public int NpcCount => 0;

    public void Update(double deltaMs, int dirX, int dirY)
    {
    }

    public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
}

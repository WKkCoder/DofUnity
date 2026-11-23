using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>结算控制器上两个公开静态小函数的取值规则(期望值全是手算的字面量)。</summary>
public sealed class OnlineSettlementGapAStaticHelperTests
{
    [Fact]
    public void Menu_countdown_keeps_any_positive_value_defaults_the_rest_and_caps_below_the_server_timer()
    {
        // 保护:EffectiveMenuCountdownMs——正数(含 1)原样取;0 与负数取默认 50 秒;超过 59 秒的一律夹到 59 秒(严格短于服务端 60 秒定时器)。
        Assert.Equal(1, OnlineSettlementController.EffectiveMenuCountdownMs(1));
        Assert.Equal(2, OnlineSettlementController.EffectiveMenuCountdownMs(2));
        Assert.Equal(200, OnlineSettlementController.EffectiveMenuCountdownMs(200));
        Assert.Equal(58_999, OnlineSettlementController.EffectiveMenuCountdownMs(58_999));
        Assert.Equal(59_000, OnlineSettlementController.EffectiveMenuCountdownMs(59_000));   // 刚好 59 秒:原样

        Assert.Equal(50_000, OnlineSettlementController.EffectiveMenuCountdownMs(0));
        Assert.Equal(50_000, OnlineSettlementController.EffectiveMenuCountdownMs(-1));
        Assert.Equal(50_000, OnlineSettlementController.EffectiveMenuCountdownMs(long.MinValue));

        Assert.Equal(59_000, OnlineSettlementController.EffectiveMenuCountdownMs(59_001));
        Assert.Equal(59_000, OnlineSettlementController.EffectiveMenuCountdownMs(60_000));
        Assert.Equal(59_000, OnlineSettlementController.EffectiveMenuCountdownMs(90_000));
        Assert.Equal(59_000, OnlineSettlementController.EffectiveMenuCountdownMs(long.MaxValue));
    }

    [Fact]
    public void Card_slot_pick_returns_the_first_slot_with_the_right_and_exactly_minus_one_when_there_is_none()
    {
        // 保护:PickCardSlotFromRights——取第一个值恰为 1 的槽位下标;没有任何槽位为 1、空表、null 都精确返回 -1(不是别的负数)。
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(null));
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(Array.Empty<short>()));
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(new short[] { 0, 0, 0, 0 }));
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(new short[] { -1, -1, -1, -1 }));
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(new short[] { 0, -1, 2, 3 }));
        Assert.Equal(-1, OnlineSettlementController.PickCardSlotFromRights(new List<short> { 2, 0 }));

        Assert.Equal(0, OnlineSettlementController.PickCardSlotFromRights(new short[] { 1, 0, 0, 0 }));
        Assert.Equal(1, OnlineSettlementController.PickCardSlotFromRights(new short[] { 0, 1, 0, 0 }));
        Assert.Equal(2, OnlineSettlementController.PickCardSlotFromRights(new short[] { 0, -1, 1, 1 }));   // 多个 1:取第一个
        Assert.Equal(3, OnlineSettlementController.PickCardSlotFromRights(new short[] { -1, 0, 2, 1 }));
        Assert.Equal(0, OnlineSettlementController.PickCardSlotFromRights(new short[] { 1, 1, 1, 1 }));
    }
}

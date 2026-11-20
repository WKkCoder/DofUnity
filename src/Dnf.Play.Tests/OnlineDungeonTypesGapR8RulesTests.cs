using Dnf.Play;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本规则层第 8 轮缺口测试:钉住"可选最高难度"两个公开入口里 <c>hasUltimate</c> 参数的缺省值。
/// 现有覆盖(<c>OnlineDungeonSelectTests</c> 与前几轮缺口测试)里,每一处调用都显式给了第二个参数,
/// 生产代码里唯一的调用方(选择策略)也显式传 <c>_hasUltimate(id)</c>,所以缺省值从来没有被任何断言读到过;
/// 这里补上"省略第二个参数"的调用,并在刚好卡在分叉点两侧的档位上手算期望值。
/// 全部用合成编号与档位,不碰真实脚本。
/// 推导口径(两处共用):cap = 有终极难度 ? 3 : 2(<c>MaxSelectable</c> = 3);
/// 上限 = 档位 &lt;= 0 ? 0 : min(档位, cap)。所以档位 0/1/2 在两种口径下同值,档位 ≥ 3 才分叉(3 对 2)。
/// </summary>
public class OnlineDungeonTypesGapR8RulesTests
{
    private static KeyValuePair<int, byte> Tier(int dungeonId, byte tier) => new KeyValuePair<int, byte>(dungeonId, tier);

    /// <summary>
    /// 保护:权限表的 <c>DifficultyLimit(dungeonId)</c> 省略第二个参数时按"有终极难度"计算(与显式传 true 同值),
    /// 而不是按"没有终极难度"(显式传 false)计算;并且缺省口径跟随后续 (0,5) 通知与重新装载的结果。
    /// 推导:档位 3 恰好等于有终极难度的 cap → 缺省 3、显式 false 被夹到 2;档位 4 刚超过 cap、档位 255 是字节上限,结果相同;
    /// 档位 0/1/2 两种口径同值(0/1/2),用来确认只有缺省值被钉住、其余行为没被改动。
    /// </summary>
    [Fact]
    public void PermissionTableLimitAssumesTheUltimateDifficultyWhenTheFlagIsOmitted()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[]
        {
            Tier(7101, 0), Tier(7102, 1), Tier(7103, 2), Tier(7104, 3), Tier(7105, 4), Tier(7106, 255),
        }, hasSnapshot: true);
        Assert.True(table.HasSnapshot);
        Assert.Equal(6, table.EntryCount);

        // 三个数依次是:省略第二个参数 / 显式 true / 显式 false
        string Limits(int dungeonId) =>
            table.DifficultyLimit(dungeonId) + "/" + table.DifficultyLimit(dungeonId, true) + "/" + table.DifficultyLimit(dungeonId, false);

        Assert.Equal("0/0/0", Limits(7101)); // 档位 0:不论终极难度都是 0
        Assert.Equal("1/1/1", Limits(7102)); // 档位 1:两种 cap 都放得下
        Assert.Equal("2/2/2", Limits(7103)); // 档位 2:cap 3 时是 2;cap 2 时恰好卡在 cap 上,也是 2
        Assert.Equal("3/3/2", Limits(7104)); // 档位 3:恰好等于有终极难度的 cap;没有终极难度时被夹到 2
        Assert.Equal("3/3/2", Limits(7105)); // 档位 4:刚超过 cap
        Assert.Equal("3/3/2", Limits(7106)); // 档位 255:字节上限
        Assert.Equal("0/0/0", Limits(7199)); // 表里没有条目:当 0(不是"不许可")

        // 缺省口径与"GetTier 直通规则函数的缺省口径"一致,并且与显式 false 的结果不同(没有终极难度时上限少 1)
        Assert.Equal((byte)3, table.GetTier(7104));
        Assert.Equal(OnlineDungeonPermissionRules.DifficultyLimitForTier(table.GetTier(7104)), table.DifficultyLimit(7104));
        Assert.NotEqual(table.DifficultyLimit(7104, false), table.DifficultyLimit(7104));

        // 之后推来的 (0,5) 通知改了档位:缺省上限同步变化
        int versionBeforeUpdate = table.Version;
        table.ApplyUpdate(new[] { Tier(7103, 3), Tier(7106, 1) });
        Assert.Equal(versionBeforeUpdate + 1, table.Version);
        Assert.Equal("3/3/2", Limits(7103)); // 档位 2 → 3:缺省上限 2 → 3,显式 false 仍是 2
        Assert.Equal("1/1/1", Limits(7106)); // 档位 255 → 1:缺省上限 3 → 1
        Assert.Equal("3/3/2", Limits(7104)); // 没被更新的条目不变

        // 重新装载一份空快照:所有条目消失,缺省口径下全部回到 0
        table.LoadInitial(null, hasSnapshot: false);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal("0/0/0", Limits(7104));
        Assert.Equal("0/0/0", Limits(7103));
    }

    /// <summary>
    /// 保护:档位 → 可选最高难度的规则函数 <c>DifficultyLimitForTier(tier)</c> 省略第二个参数时按"有终极难度"计算,
    /// 在分叉点(档位 2 与 3)两侧、负数/0 的下界两侧与整型极值上各取一值,逐行对照显式 true / 显式 false 的结果。
    /// 推导:cap 有终极难度时 3、否则 2;档位 &lt;= 0 → 0,其余取 min(档位, cap);如 (3, 3, 2) = 档位 3 缺省上限 3、显式 false 上限 2。
    /// </summary>
    [Theory]
    [InlineData(int.MinValue, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 3, 2)]
    [InlineData(4, 3, 2)]
    [InlineData(255, 3, 2)]
    [InlineData(int.MaxValue, 3, 2)]
    public void TierToLimitRuleAssumesTheUltimateDifficultyWhenTheFlagIsOmitted(int tier, int withUltimate, int withoutUltimate)
    {
        Assert.Equal(withUltimate, OnlineDungeonPermissionRules.DifficultyLimitForTier(tier));
        Assert.Equal(withUltimate, OnlineDungeonPermissionRules.DifficultyLimitForTier(tier, true));
        Assert.Equal(withoutUltimate, OnlineDungeonPermissionRules.DifficultyLimitForTier(tier, false));
    }
}

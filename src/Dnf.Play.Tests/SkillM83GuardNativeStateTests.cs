namespace Dnf.Play.Tests;

/// <summary>
/// M83 / native 状态 0x13 防御姿态类：行为由 E-164 与 <see cref="SkillGuardTests"/> 覆盖(合成技能 914)。
/// 903/904 已用于波系/ Hop 合成数据;本文件只钉住读码状态号,避免与任务书编号冲突的重复实现。
/// </summary>
public sealed class SkillM83GuardNativeStateTests
{
    /// <summary>客户端 native 状态号(观察 E-164 / enter 0x00ccd2a0 guard 分支,静态读码,非 REA)。</summary>
    public const int GuardNativeState = 0x13;

    [Fact]
    public void ClientGuardNativeStateIsThirteen()
    {
        Assert.Equal(19, GuardNativeState);
    }

}

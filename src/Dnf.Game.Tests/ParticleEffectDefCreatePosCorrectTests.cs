using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// <c>[create pos correct]</c> 解析为 <see cref="ParticleEffectDef.CreatePosCorrect"/> 的取值边界。
/// 证据与未知项:读码笔记(粒子逆向与仿真开发笔记 2.1 / 2.3 节)写明该标签落在发射器 +0xb0、类型 bool,
/// 发射侧判定写作"非 0 即插值起步坐标"(<c>!= 0</c>);当前按"非 0 即真"实现。
/// 2 与 -1 的低字节都不为 0,无论加载侧是整数归一还是取低字节,发射侧都判真;
/// 但客户端加载侧对 2 与 -1 的具体处理(以及 256 这类低字节为 0 的值)没有单独反汇编验证,属于未验证项。
/// </summary>
public class ParticleEffectDefCreatePosCorrectTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(-1, true)]
    public void CreatePosCorrectIsTrueForEveryNonZeroInteger(int value, bool expected)
    {
        var b = new TokenStreamBuilder();
        b.Tag("[create pos correct]").Int(value);
        ScriptLine[] lines = ScriptDocument.Parse(b.Bytes(), new PvfStringTable(b.StringTableBytes()));

        Assert.Equal(expected, ParticleEffectDef.Parse("synthetic/pos_correct.ptl", lines).CreatePosCorrect);
    }
}

using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// <see cref="ParticleEffectMapper.ToSimDef"/> 用 <c>(Sim枚举)(int)游戏枚举</c> 的数值强转把 Game 层定义转成 Render 层规格,
/// 共 7 族枚举(ChangingType、Landing、RotationAngleRangeType、HowToCreateObject、ObjectType、Layer、各轴 MoveType)。
/// 两边的枚举数值必须逐项对齐:本类对每族枚举的每个成员造一份"只改该字段"的合成定义过一遍映射,
/// 断言映射后的 Sim 成员与原成员同名(数值错位时 <c>ToString()</c> 会退化成数字或别的名字),并断言两边成员个数相等
/// (以后谁加了成员只改一边会立刻变红)。全部数据为自包含合成数据。
/// </summary>
public class ParticleEffectMapperEnumAlignmentTests
{
    private static ParticleEffectDef MakeGameDef(
        ParticleMoveType moveX = ParticleMoveType.Stop,
        ParticleMoveType moveY = ParticleMoveType.Stop,
        ParticleMoveType moveZ = ParticleMoveType.Stop,
        ParticleChanging changingType = ParticleChanging.KeepChanging,
        ParticleLanding landing = ParticleLanding.Pierce,
        ParticleRotationRangeType rangeType = ParticleRotationRangeType.Rectangle,
        ParticleHowToCreate howToCreate = ParticleHowToCreate.Randomize,
        ParticleObjectKind objectType = ParticleObjectKind.Animation,
        ParticleLayer layer = ParticleLayer.Normal)
    {
        return new ParticleEffectDef(
            path: "synthetic/align.ptl",
            creationPos: new ParticleVec3i(0, 0, 0),
            x: new ParticleEffectAxis(moveX, 1, 2, 3, 4),
            y: new ParticleEffectAxis(moveY, 5, 6, 7, 8),
            z: new ParticleEffectAxis(moveZ, 9, 10, 11, 12),
            changingTermMs: 0,
            changingType: changingType,
            gravity: 0,
            rotationAngle: new ParticleVec3i(0, 0, 0),
            lifeTimeMs: 0,
            landing: landing,
            transparentStart: 100,
            transparentEnd: 100,
            attackBetweenTimeMs: 1000,
            expansionRate: 1000,
            attackBonusRate: 0,
            creationFrequency: -1f,
            maximumCreateNumber: 1,
            maximumCreateTimeMs: 0,
            createAreaSize: new ParticleVec3i(0, 0, 0),
            createAreaRotationAngle: new ParticleVec3i(0, 0, 0),
            rotationAngleRangeType: rangeType,
            rotationAngleVar0: 0,
            rotationAngleVar1: 0,
            randomRange: 0,
            createPosCorrect: false,
            displayEffectRateLow: 100,
            displayEffectRateMid: 100,
            displayEffectRateHigh: 100,
            howToCreateObject: howToCreate,
            objectType: objectType,
            objectTypeRaw: "[animation]",
            layer: layer,
            objects: new[] { new ParticleObjectEntry("fx/dot.img", 0) },
            startSound: string.Empty);
    }

    /// <summary>成员同名 + 两边成员个数与成员名集合一致。</summary>
    private static void AssertAlignedByName<TGame, TSim>(TGame member, TSim mapped)
        where TGame : struct, Enum
        where TSim : struct, Enum
    {
        // 成员个数相等:任何一边增删成员而另一边没跟上,立刻变红
        Assert.Equal(Enum.GetValues<TGame>().Length, Enum.GetValues<TSim>().Length);
        Assert.Equal(
            Enum.GetNames<TGame>().OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            Enum.GetNames<TSim>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // 同名:数值错位时 mapped 会是别的成员或没有名字的裸数字
        Assert.Equal(member.ToString(), mapped.ToString());
    }

    public static IEnumerable<object[]> ChangingMembers() => Enum.GetValues<ParticleChanging>().Select(m => new object[] { m });

    public static IEnumerable<object[]> LandingMembers() => Enum.GetValues<ParticleLanding>().Select(m => new object[] { m });

    public static IEnumerable<object[]> RotationRangeMembers() => Enum.GetValues<ParticleRotationRangeType>().Select(m => new object[] { m });

    public static IEnumerable<object[]> HowToCreateMembers() => Enum.GetValues<ParticleHowToCreate>().Select(m => new object[] { m });

    public static IEnumerable<object[]> ObjectKindMembers() => Enum.GetValues<ParticleObjectKind>().Select(m => new object[] { m });

    public static IEnumerable<object[]> LayerMembers() => Enum.GetValues<ParticleLayer>().Select(m => new object[] { m });

    /// <summary>X、Y、Z 三条轴 × MoveType 每个成员。</summary>
    public static IEnumerable<object[]> MoveTypeMembersPerAxis()
    {
        foreach (int axis in new[] { 0, 1, 2 })
        {
            foreach (ParticleMoveType m in Enum.GetValues<ParticleMoveType>())
            {
                yield return new object[] { axis, m };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ChangingMembers))]
    public void ChangingTypeMapsEveryMemberToTheSameNamedSimMember(ParticleChanging member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(changingType: member));
        AssertAlignedByName(member, sim.ChangingType);
    }

    [Theory]
    [MemberData(nameof(LandingMembers))]
    public void LandingMapsEveryMemberToTheSameNamedSimMember(ParticleLanding member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(landing: member));
        AssertAlignedByName(member, sim.Landing);
    }

    [Theory]
    [MemberData(nameof(RotationRangeMembers))]
    public void RotationAngleRangeTypeMapsEveryMemberToTheSameNamedSimMember(ParticleRotationRangeType member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(rangeType: member));
        AssertAlignedByName(member, sim.RotationAngleRangeType);
    }

    [Theory]
    [MemberData(nameof(HowToCreateMembers))]
    public void HowToCreateObjectMapsEveryMemberToTheSameNamedSimMember(ParticleHowToCreate member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(howToCreate: member));
        AssertAlignedByName(member, sim.HowToCreateObject);
    }

    [Theory]
    [MemberData(nameof(ObjectKindMembers))]
    public void ObjectTypeMapsEveryMemberToTheSameNamedSimMember(ParticleObjectKind member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(objectType: member));
        AssertAlignedByName(member, sim.ObjectType);
    }

    [Theory]
    [MemberData(nameof(LayerMembers))]
    public void LayerMapsEveryMemberToTheSameNamedSimMember(ParticleLayer member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(layer: member));
        AssertAlignedByName(member, sim.Layer);
    }

    [Theory]
    [MemberData(nameof(MoveTypeMembersPerAxis))]
    public void MoveTypeOfEachAxisMapsEveryMemberToTheSameNamedSimMember(int axis, ParticleMoveType member)
    {
        ParticleSimDef sim = ParticleEffectMapper.ToSimDef(MakeGameDef(
            moveX: axis == 0 ? member : ParticleMoveType.Stop,
            moveY: axis == 1 ? member : ParticleMoveType.Stop,
            moveZ: axis == 2 ? member : ParticleMoveType.Stop));

        ParticleSimAxis[] axes = { sim.X, sim.Y, sim.Z };
        AssertAlignedByName(member, axes[axis].Type);
        // 另外两条轴保持 Stop,防止轴间接线错位被"同名"掩盖
        for (int other = 0; other < 3; other++)
        {
            if (other != axis)
            {
                Assert.Equal(ParticleSimMoveType.Stop, axes[other].Type);
            }
        }
    }
}

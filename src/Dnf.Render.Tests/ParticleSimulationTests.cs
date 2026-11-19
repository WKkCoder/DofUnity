using System.Buffers.Binary;
using System.Text;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>
/// 纯函数确定性粒子仿真器 <see cref="ParticleSimulation"/> 与 <see cref="ParticleEmitter"/> 的合成单测。
/// 全部期望值均按公式手算自合成参数，无真库文件、无文件 I/O、无随机时钟依赖。
/// </summary>
public class ParticleSimulationTests
{
    private static ParticleSimDef MakeDef(
        ParticleSimVec3 creationPos = default,
        ParticleSimAxis x = default,
        ParticleSimAxis y = default,
        ParticleSimAxis z = default,
        int changingTermMs = 0,
        ParticleSimChanging changingType = ParticleSimChanging.KeepChanging,
        int gravity = 0,
        ParticleSimVec3 rotationAngle = default,
        int lifeTimeMs = 0,
        ParticleSimLanding landing = ParticleSimLanding.Pierce,
        int transparentStart = 100,
        int transparentEnd = 100,
        float creationFrequency = -1f,
        int maximumCreateNumber = 1,
        int maximumCreateTimeMs = 0,
        ParticleSimVec3 createAreaSize = default,
        ParticleSimVec3 createAreaRotationAngle = default,
        ParticleSimRotationRange rotationAngleRangeType = ParticleSimRotationRange.Rectangle,
        int rotationAngleVar0 = 0,
        int rotationAngleVar1 = 0,
        int randomRange = 0,
        bool createPosCorrect = false,
        int rateLow = 100,
        int rateMid = 100,
        int rateHigh = 100,
        ParticleSimHowToCreate howToCreateObject = ParticleSimHowToCreate.Randomize,
        ParticleSimObjectKind objectType = ParticleSimObjectKind.Animation,
        ParticleSimLayer layer = ParticleSimLayer.Normal,
        params ParticleSimObject[] objects)
    {
        return new ParticleSimDef(
            path: "synthetic/test.ptl",
            creationPos: creationPos,
            x: x,
            y: y,
            z: z,
            changingTermMs: changingTermMs,
            changingType: changingType,
            gravity: gravity,
            rotationAngle: rotationAngle,
            lifeTimeMs: lifeTimeMs,
            landing: landing,
            transparentStart: transparentStart,
            transparentEnd: transparentEnd,
            creationFrequency: creationFrequency,
            maximumCreateNumber: maximumCreateNumber,
            maximumCreateTimeMs: maximumCreateTimeMs,
            createAreaSize: createAreaSize,
            createAreaRotationAngle: createAreaRotationAngle,
            rotationAngleRangeType: rotationAngleRangeType,
            rotationAngleVar0: rotationAngleVar0,
            rotationAngleVar1: rotationAngleVar1,
            randomRange: randomRange,
            createPosCorrect: createPosCorrect,
            displayEffectRateLow: rateLow,
            displayEffectRateMid: rateMid,
            displayEffectRateHigh: rateHigh,
            howToCreateObject: howToCreateObject,
            objectType: objectType,
            layer: layer,
            objects: objects.Length == 0 ? new[] { new ParticleSimObject("fx/dot.img", 0) } : objects);
    }

    private sealed class MemorySpriteSource : ISpriteSource
    {
        private readonly Dictionary<string, SpriteFrame> _frames = new(StringComparer.Ordinal);

        public void Add(string path, int frameIndex, SpriteFrame frame)
        {
            _frames[path + "#" + frameIndex] = frame;
        }

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            return _frames.TryGetValue(imageName + "#" + frame, out sprite!);
        }
    }

    private static AniFile BuildSyntheticAni(
        string[] imagePaths,
        (int imgIdx, int frameIdx, int x, int y, int delayMs, float? scaleX, float? scaleY, float? rotRad, (byte r, byte g, byte b, byte a)? tint, bool interp, ushort? graphicEffect)[] frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        w.Write((ushort)frames.Length);
        w.Write((ushort)imagePaths.Length);
        foreach (string p in imagePaths)
        {
            byte[] b = Encoding.Latin1.GetBytes(p);
            w.Write((uint)b.Length);
            w.Write(b);
        }
        w.Write((ushort)0); // global props count
        foreach (var f in frames)
        {
            w.Write((ushort)0); // boxCount
            if (f.imgIdx < 0)
            {
                w.Write((ushort)0xFFFF);
            }
            else
            {
                w.Write((ushort)f.imgIdx);
                w.Write((ushort)f.frameIdx);
            }
            w.Write(f.x);
            w.Write(f.y);

            var props = new List<(ushort id, byte[] payload)>();
            props.Add((AniFrameProperty.Delay, BitConverter.GetBytes(f.delayMs)));
            if (f.scaleX.HasValue && f.scaleY.HasValue)
            {
                byte[] buf = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), BitConverter.SingleToInt32Bits(f.scaleX.Value));
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), BitConverter.SingleToInt32Bits(f.scaleY.Value));
                props.Add((AniFrameProperty.ImageRate, buf));
            }
            if (f.rotRad.HasValue)
            {
                props.Add((AniFrameProperty.ImageRotate, BitConverter.GetBytes(BitConverter.SingleToInt32Bits(f.rotRad.Value))));
            }
            if (f.tint.HasValue)
            {
                var c = f.tint.Value;
                props.Add((AniFrameProperty.Rgba, new[] { c.r, c.g, c.b, c.a }));
            }
            if (f.interp)
            {
                props.Add((AniFrameProperty.Interpolation, new byte[] { 1 }));
            }
            if (f.graphicEffect.HasValue)
            {
                byte[] buf = new byte[f.graphicEffect.Value == 5 ? 5 : 2];
                BinaryPrimitives.WriteUInt16LittleEndian(buf, f.graphicEffect.Value);
                props.Add((AniFrameProperty.GraphicEffect, buf));
            }

            w.Write((ushort)props.Count);
            foreach ((ushort id, byte[] payload) in props)
            {
                w.Write(id);
                w.Write(payload);
            }
        }
        w.Flush();
        return AniFile.Parse(ms.ToArray());
    }

    [Fact]
    public void IdenticalSeedIsBitIdenticalAndSeedChangeOnlyPerturbsRandomFields()
    {
        var randomDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 120, 120, 0, 0),
            y: new ParticleSimAxis(ParticleSimMoveType.Sin, 40, 40, 400, 400),
            creationFrequency: -1f,
            maximumCreateNumber: 5,
            createAreaSize: new ParticleSimVec3(120, 80, 60),
            rotationAngleVar0: 50,
            rotationAngleVar1: 30,
            randomRange: 80,
            lifeTimeMs: 1000,
            howToCreateObject: ParticleSimHowToCreate.Randomize,
            objects: new[]
            {
                new ParticleSimObject("sprite/a.img", 0),
                new ParticleSimObject("sprite/b.img", 1),
                new ParticleSimObject("sprite/c.img", 2),
            });

        ParticleSimulationResult runA1 = ParticleSimulation.Simulate(randomDef, 12345u, 8, 50);
        ParticleSimulationResult runA2 = ParticleSimulation.Simulate(randomDef, 12345u, 8, 50);
        Assert.Equal(runA1.Steps.Count, runA2.Steps.Count);
        for (int f = 0; f < runA1.Steps.Count; f++)
        {
            Assert.Equal(runA1.Steps[f].TimeMs, runA2.Steps[f].TimeMs);
            Assert.Equal(runA1.Steps[f].Sprites.Count, runA2.Steps[f].Sprites.Count);
            for (int i = 0; i < runA1.Steps[f].Sprites.Count; i++)
            {
                ParticleSprite s1 = runA1.Steps[f].Sprites[i];
                ParticleSprite s2 = runA2.Steps[f].Sprites[i];
                Assert.Equal(
                    (s1.ParticleId, s1.WorldX, s1.WorldY, s1.WorldZ, s1.Alpha, s1.ImagePath, s1.FrameIndex),
                    (s2.ParticleId, s2.WorldX, s2.WorldY, s2.WorldZ, s2.Alpha, s2.ImagePath, s2.FrameIndex));
            }
        }

        // 改种子会改变带有随机项的粒子落点/对象，但不改变非随机项（帧步进耗时、数量、寿命配置）
        ParticleSimulationResult runB = ParticleSimulation.Simulate(randomDef, 99999u, 8, 50);
        Assert.Equal(runA1.Steps[0].Sprites.Count, runB.Steps[0].Sprites.Count);
        bool anyPosDiff = false;
        for (int i = 0; i < runA1.Steps[4].Sprites.Count; i++)
        {
            if (runA1.Steps[4].Sprites[i].WorldX != runB.Steps[4].Sprites[i].WorldX ||
                runA1.Steps[4].Sprites[i].WorldY != runB.Steps[4].Sprites[i].WorldY)
            {
                anyPosDiff = true;
                break;
            }
        }
        Assert.True(anyPosDiff);

        // 纯确定性粒子（无 randomRange、无区域尺寸、无随机旋转、InOrder）换任意种子结果完全一致
        var deterministicDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 100, 0, 0),
            lifeTimeMs: 500,
            transparentStart: 100,
            transparentEnd: 20,
            creationFrequency: -1f,
            maximumCreateNumber: 2,
            howToCreateObject: ParticleSimHowToCreate.InOrder,
            objects: new[] { new ParticleSimObject("sprite/p0.img", 0), new ParticleSimObject("sprite/p1.img", 1) });

        ParticleSimulationResult d1 = ParticleSimulation.Simulate(deterministicDef, 1u, 5, 100);
        ParticleSimulationResult d2 = ParticleSimulation.Simulate(deterministicDef, 777777u, 5, 100);
        for (int f = 0; f < 5; f++)
        {
            Assert.Equal(d1.Steps[f].Sprites.Count, d2.Steps[f].Sprites.Count);
            for (int i = 0; i < d1.Steps[f].Sprites.Count; i++)
            {
                Assert.Equal(d1.Steps[f].Sprites[i].WorldX, d2.Steps[f].Sprites[i].WorldX);
                Assert.Equal(d1.Steps[f].Sprites[i].ImagePath, d2.Steps[f].Sprites[i].ImagePath);
                Assert.Equal(d1.Steps[f].Sprites[i].Alpha, d2.Steps[f].Sprites[i].Alpha);
            }
        }
    }

    [Fact]
    public void AxisMovementAndChangingModesProduceExactHandCalculatedPositions()
    {
        static int SimXAt(ParticleSimAxis axis, int changingTermMs, ParticleSimChanging changing, int tMs)
        {
            var def = MakeDef(x: axis, changingTermMs: changingTermMs, changingType: changing);
            ParticleSimulationResult res = ParticleSimulation.Simulate(def, 1u, 2, tMs);
            return res.Steps[1].Sprites[0].WorldX;
        }

        // Stop
        Assert.Equal(0, SimXAt(new ParticleSimAxis(ParticleSimMoveType.Stop, 500, 900, 100, 200), 1000, ParticleSimChanging.KeepChanging, 500));

        // StraightLine + changingTerm=0: disp = v0 * t * 0.001
        var slConst = new ParticleSimAxis(ParticleSimMoveType.StraightLine, 200, 999, 0, 0);
        Assert.Equal(100, SimXAt(slConst, 0, ParticleSimChanging.KeepChanging, 500));
        Assert.Equal(300, SimXAt(slConst, 0, ParticleSimChanging.KeepChanging, 1500));

        // StraightLine + changingTerm=1000, v0=100, v1=300:
        var sl = new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 300, 0, 0);
        // KeepChanging: t=500 -> 75; t=2000(不截断继续外推!) -> 600
        Assert.Equal(75, SimXAt(sl, 1000, ParticleSimChanging.KeepChanging, 500));
        Assert.Equal(600, SimXAt(sl, 1000, ParticleSimChanging.KeepChanging, 2000));

        // KeepLastValue: t=500 -> 75; t=2000 -> 500
        Assert.Equal(75, SimXAt(sl, 1000, ParticleSimChanging.KeepLastValue, 500));
        Assert.Equal(500, SimXAt(sl, 1000, ParticleSimChanging.KeepLastValue, 2000));

        // PingPong: t=1000 -> 200; t=1500 -> 325; t=2500 -> 475
        Assert.Equal(200, SimXAt(sl, 1000, ParticleSimChanging.PingPong, 1000));
        Assert.Equal(325, SimXAt(sl, 1000, ParticleSimChanging.PingPong, 1500));
        Assert.Equal(475, SimXAt(sl, 1000, ParticleSimChanging.PingPong, 2500));

        // Sin / Cos + changingTerm=0: amp=100, period=1000 -> t=250 (pi/2) sin=100, cos=0; t=500 (pi) sin=0, cos=-100
        var sinConst = new ParticleSimAxis(ParticleSimMoveType.Sin, 100, 200, 1000, 2000);
        var cosConst = new ParticleSimAxis(ParticleSimMoveType.Cos, 100, 200, 1000, 2000);
        Assert.Equal(100, SimXAt(sinConst, 0, ParticleSimChanging.KeepChanging, 250));
        Assert.Equal(0, SimXAt(cosConst, 0, ParticleSimChanging.KeepChanging, 250));
        Assert.Equal(-100, SimXAt(cosConst, 0, ParticleSimChanging.KeepChanging, 500));
        // period=0 安全返回 0
        Assert.Equal(0, SimXAt(new ParticleSimAxis(ParticleSimMoveType.Sin, 100, 0, 0, 0), 0, ParticleSimChanging.KeepChanging, 250));

        // Sin / Cos + KeepChanging/KeepLastValue/PingPong
        var cosVar = new ParticleSimAxis(ParticleSimMoveType.Cos, 100, 200, 400, 800);
        // KeepChanging t=500: pMid=500 -> theta=2pi -> cos=1, A=150 -> 150
        Assert.Equal(150, SimXAt(cosVar, 1000, ParticleSimChanging.KeepChanging, 500));
        // KeepLastValue t=1200: theta=2pi*(23/12), cos=0.866025, A=200 -> 173
        Assert.Equal(173, SimXAt(cosVar, 1000, ParticleSimChanging.KeepLastValue, 1200));
        // PingPong t=1400: A=160, cos(4*pi/9)*160 = 27.78 -> (int)27
        Assert.Equal(27, SimXAt(cosVar, 1000, ParticleSimChanging.PingPong, 1400));
    }

    [Fact]
    public void RotationOrder3DAndPerspectiveCompressionScaleYTo29Percent()
    {
        // 验证固定旋转顺序 rot[2](ZY) -> rot[1](XZ) -> rot[0](XY) 与非交换性
        var xzRotDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 100, 0, 0),
            rotationAngle: new ParticleSimVec3(0, 90, 0));
        ParticleSprite xzSp = ParticleSimulation.Simulate(xzRotDef, 1u, 2, 1000).Steps[1].Sprites[0];
        Assert.Equal((0, 0, 100), (xzSp.WorldX, xzSp.WorldY, xzSp.WorldZ));

        // Z 运动先经过 rotZ=90 (ZY -> Y=1000)，再经 rotX=90 (XY -> X=-1000)
        var nonCommDef = MakeDef(
            z: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            rotationAngle: new ParticleSimVec3(90, 0, 90));
        ParticleSprite ncSp = ParticleSimulation.Simulate(nonCommDef, 1u, 2, 1000).Steps[1].Sprites[0];
        Assert.Equal((-1000, 0, 0), (ncSp.WorldX, ncSp.WorldY, ncSp.WorldZ));

        // 仅 rotZ=90 将 Z=1000 转到 Y 轴，再按 2.5D 透视系数 0.29 压缩 -> WorldY = 290
        var zyDef = MakeDef(
            z: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            rotationAngle: new ParticleSimVec3(0, 0, 90));
        ParticleSprite zySp = ParticleSimulation.Simulate(zyDef, 1u, 2, 1000).Steps[1].Sprites[0];
        Assert.Equal((0, 290, 0), (zySp.WorldX, zySp.WorldY, zySp.WorldZ));
    }

    [Fact]
    public void GravityAndAllFourLandingPoliciesBehaveAsDisassembled()
    {
        // startZ=100, gravity=1000 -> t=400: 100 - 0.5*1000*0.16 = 20; t=500: 100 - 125 = -25 < 0
        ParticleSimulationResult RunWithLanding(ParticleSimLanding landing)
        {
            var def = MakeDef(
                creationPos: new ParticleSimVec3(0, 0, 100),
                x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 100, 0, 0),
                gravity: 1000,
                lifeTimeMs: 700,
                landing: landing,
                transparentStart: 100,
                transparentEnd: 0);
            return ParticleSimulation.Simulate(def, 1u, 7, 100); // t=0,100,200,300,400,500,600
        }

        // Pierce: 允许穿过地面 (Z=-25 at 500ms, -80 at 600ms) 且 XY/Alpha 继续推
        ParticleSimulationResult pierce = RunWithLanding(ParticleSimLanding.Pierce);
        Assert.Equal((40, 20), (pierce.Steps[4].Sprites[0].WorldX, pierce.Steps[4].Sprites[0].WorldZ));
        Assert.Equal((50, -25), (pierce.Steps[5].Sprites[0].WorldX, pierce.Steps[5].Sprites[0].WorldZ));
        Assert.Equal((60, -80), (pierce.Steps[6].Sprites[0].WorldX, pierce.Steps[6].Sprites[0].WorldZ));

        // Slide: Z 钳位到 0，下一帧 t=600 仍继续沿 X 滑动且透明度继续衰减 (100 - 100*500/700 = 28; 100 - 100*600/700 = 14)
        ParticleSimulationResult slide = RunWithLanding(ParticleSimLanding.Slide);
        Assert.Equal((50, 0, 28), (slide.Steps[5].Sprites[0].WorldX, slide.Steps[5].Sprites[0].WorldZ, slide.Steps[5].Sprites[0].AlphaPercent));
        Assert.Equal((60, 0, 14), (slide.Steps[6].Sprites[0].WorldX, slide.Steps[6].Sprites[0].WorldZ, slide.Steps[6].Sprites[0].AlphaPercent));

        // Stop: t=500 钳位 Z=0，t=600 发现 Z==0 提前冻结坐标与透明度
        ParticleSimulationResult stop = RunWithLanding(ParticleSimLanding.Stop);
        Assert.Equal((50, 0, 28), (stop.Steps[5].Sprites[0].WorldX, stop.Steps[5].Sprites[0].WorldZ, stop.Steps[5].Sprites[0].AlphaPercent));
        Assert.Equal((50, 0, 28), (stop.Steps[6].Sprites[0].WorldX, stop.Steps[6].Sprites[0].WorldZ, stop.Steps[6].Sprites[0].AlphaPercent));

        // Vanish: t=500 Z<0 时立即死亡消失
        ParticleSimulationResult vanish = RunWithLanding(ParticleSimLanding.Vanish);
        Assert.Single(vanish.Steps[4].Sprites);
        Assert.Empty(vanish.Steps[5].Sprites);
    }

    [Fact]
    public void LifetimeZeroPersistsForeverAndTransparencySaturatesAbove100Percent()
    {
        var infiniteDef = MakeDef(lifeTimeMs: 0, transparentStart: 80, transparentEnd: 10);
        ParticleSimulationResult inf = ParticleSimulation.Simulate(infiniteDef, 1u, 3, 5000);
        Assert.Single(inf.Steps[2].Sprites);
        Assert.Equal((80, (byte)204), (inf.Steps[2].Sprites[0].AlphaPercent, inf.Steps[2].Sprites[0].Alpha));

        // transparentStart=255 -> 0x007fccc5 在 >=100 时跳过乘算，保持 255 不溢出；t=90 时插值到 70% -> round(255*0.7)=179；t=100 死亡
        var fadeDef = MakeDef(lifeTimeMs: 100, transparentStart: 255, transparentEnd: 50);
        ParticleSimulationResult fade = ParticleSimulation.Simulate(fadeDef, 1u, 11, 10);
        Assert.Equal((255, (byte)255), (fade.Steps[0].Sprites[0].AlphaPercent, fade.Steps[0].Sprites[0].Alpha));
        Assert.Equal((70, (byte)179), (fade.Steps[9].Sprites[0].AlphaPercent, fade.Steps[9].Sprites[0].Alpha));
        Assert.Empty(fade.Steps[10].Sprites);

        // 负透明值收敛到 0
        var zeroAlphaDef = MakeDef(lifeTimeMs: 100, transparentStart: -20, transparentEnd: 0);
        ParticleSimulationResult zSim = ParticleSimulation.Simulate(zeroAlphaDef, 1u, 1, 0);
        Assert.Equal((byte)0, zSim.Steps[0].Sprites[0].Alpha);
    }

    [Fact]
    public void EmitterFrequencyBurstAccumulationCapsAndEffectiveMaxTimeWorkAsClient()
    {
        // freq=0 或负非 -1 或 Unknown 类型或空对象均立即停用不发粒子
        Assert.Empty(ParticleSimulation.Simulate(MakeDef(creationFrequency: 0f, maximumCreateNumber: 5), 1u, 3, 50).Steps[0].Sprites);
        Assert.Empty(ParticleSimulation.Simulate(MakeDef(creationFrequency: -2f, maximumCreateNumber: 5), 1u, 3, 50).Steps[0].Sprites);
        Assert.Empty(ParticleSimulation.Simulate(MakeDef(objectType: ParticleSimObjectKind.PassiveObject, maximumCreateNumber: 5), 1u, 3, 50).Steps[0].Sprites);
        var noObjs = new ParticleSimDef(creationFrequency: -1f, maximumCreateNumber: 5, objects: Array.Empty<ParticleSimObject>());
        Assert.Empty(ParticleSimulation.Simulate(noObjs, 1u, 3, 50).Steps[0].Sprites);

        // 质量档位比例与单帧 50 上限，以及 InOrder 钳位到末项
        var burstDef = MakeDef(
            creationFrequency: -1f,
            maximumCreateNumber: 10,
            rateLow: 50,
            howToCreateObject: ParticleSimHowToCreate.InOrder,
            objects: new[]
            {
                new ParticleSimObject("sprite/a.img", 0),
                new ParticleSimObject("sprite/b.img", 1),
            });
        ParticleSimulationResult lowQ = ParticleSimulation.Simulate(burstDef, 1u, 2, 100, quality: 0);
        Assert.Equal(5, lowQ.Steps[0].Sprites.Count);
        Assert.Equal("sprite/a.img", lowQ.Steps[0].Sprites[0].ImagePath);
        Assert.Equal("sprite/b.img", lowQ.Steps[0].Sprites[1].ImagePath);
        Assert.Equal("sprite/b.img", lowQ.Steps[0].Sprites[4].ImagePath);
        Assert.Equal(5, lowQ.Steps[1].Sprites.Count); // 不再追加

        // 单帧 50 颗硬上限
        var hugeBurst = MakeDef(creationFrequency: -1f, maximumCreateNumber: 80);
        Assert.Equal(50, ParticleSimulation.Simulate(hugeBurst, 1u, 1, 0).Steps[0].Sprites.Count);

        // rate==0 不发射
        Assert.Empty(ParticleSimulation.Simulate(MakeDef(creationFrequency: -1f, maximumCreateNumber: 10, rateLow: 0), 1u, 1, 0, quality: 0).Steps[0].Sprites);

        // 连续发射器: Restart 预装 accum=1.0 -> t=0 立刻出 1 颗；freq=10, maxNum=3 -> effMax=(3000-1000)/10=200ms
        var contDef = MakeDef(
            creationFrequency: 10f,
            maximumCreateNumber: 3,
            maximumCreateTimeMs: 0,
            lifeTimeMs: 5000);
        var emitter = new ParticleEmitter(contDef, 1u);
        Assert.Single(emitter.Step(0, default, false)); // t=0 预装发射 1 颗
        Assert.False(emitter.IsEmissionFinished);
        Assert.Equal(2, emitter.Step(100, default, false).Count); // t=100 accum += 1.0 -> 第 2 颗
        Assert.Equal(3, emitter.Step(100, default, false).Count); // t=200 == effMax -> 第 3 颗并 finished=true
        Assert.True(emitter.IsEmissionFinished);
        Assert.Equal(3, emitter.Step(100, default, false).Count); // finished 后不再产生

        // maxTime - 1 < fromNum 时取 min(maxTime - 1, fromNum)
        var timeBounded = MakeDef(creationFrequency: 10f, maximumCreateNumber: 10, maximumCreateTimeMs: 150, lifeTimeMs: 5000);
        var emTime = new ParticleEmitter(timeBounded, 1u);
        Assert.Single(emTime.Step(0, default, false));
        Assert.Equal(2, emTime.Step(200, default, false).Count); // effMax = min(149, 900) = 149 -> accum += 1.49 -> 仅 1 颗共 2 颗并停止
        Assert.True(emTime.IsEmissionFinished);
    }

    [Fact]
    public void SpatialAreaCircleDistributionRandomRangeCreatePosCorrectAndMirrorWork()
    {
        // 发射区 + 圆形角度扰动 + RandomRange(含 lifeTime>=1 下限)
        var circleDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 100, 0, 0),
            y: new ParticleSimAxis(ParticleSimMoveType.Sin, 50, 50, 200, 200),
            creationFrequency: -1f,
            maximumCreateNumber: 12,
            createAreaSize: new ParticleSimVec3(60, 40, 20),
            createAreaRotationAngle: new ParticleSimVec3(0, 0, 90),
            rotationAngleRangeType: ParticleSimRotationRange.Circle,
            rotationAngleVar0: 90,
            randomRange: 200,
            lifeTimeMs: 1);

        ParticleSimulationResult cSim = ParticleSimulation.Simulate(circleDef, 77u, 2, 10);
        Assert.Equal(12, cSim.Steps[0].Sprites.Count); // t=0 刚出生在范围内

        // CreatePosCorrect: 移动发射器一次连发 2 颗时插值起步位置
        var moveCorrectDef = MakeDef(
            creationFrequency: 20f, // 100ms 产生 2 颗
            maximumCreateNumber: 10,
            createPosCorrect: true,
            lifeTimeMs: 1000);
        var emMove = new ParticleEmitter(moveCorrectDef, 1u, new ParticleSimVec3(0, 0, 0));
        emMove.Step(0, new ParticleSimVec3(0, 0, 0), false); // accum=1 -> 第1颗(在0,0,0), accum=0
        IReadOnlyList<ParticleSprite> at100 = emMove.Step(100, new ParticleSimVec3(100, 40, 20), false); // spawnCount=2
        Assert.Equal(3, at100.Count);
        // i=0 -> IntLerp(100, 0, 1, 2) = 50, Y=20, Z=10; i=1 -> IntLerp(100, 0, 2, 2) = 0, Y=0, Z=0
        Assert.Equal((50, 20, 10), (at100[1].WorldX, at100[1].WorldY, at100[1].WorldZ));
        Assert.Equal((0, 0, 0), (at100[2].WorldX, at100[2].WorldY, at100[2].WorldZ));

        // 水平镜像: facingLeft=true 翻转运动 X 位移与区域 X 偏移，且 Mirrored=true
        var mirrorDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 200, 200, 0, 0),
            lifeTimeMs: 1000);
        ParticleSimulationResult rightSim = ParticleSimulation.Simulate(mirrorDef, 1u, 2, 500, facingLeft: false);
        ParticleSimulationResult leftSim = ParticleSimulation.Simulate(mirrorDef, 1u, 2, 500, facingLeft: true);
        Assert.Equal((100, false), (rightSim.Steps[1].Sprites[0].WorldX, rightSim.Steps[1].Sprites[0].Mirrored));
        Assert.Equal((-100, true), (leftSim.Steps[1].Sprites[0].WorldX, leftSim.Steps[1].Sprites[0].Mirrored));
    }

    [Fact]
    public void SyntheticAniInterpolationAdditiveBlendAndContactSheetRenderExactRgba()
    {
        // 两帧合成 .ani: 帧0->偏移(0,0), scale(-2, 2), rot=0, tint(100,200,100,200), 开启插值, 加法混合(2), delay=100ms
        // 帧1->偏移(20,10), scale(-1, 1), rot=1.0f, tint(200,100,200,100), delay=100ms
        AniFile ani = BuildSyntheticAni(
            new[] { "Effect/SynthParticle.img" },
            new (int, int, int, int, int, float?, float?, float?, (byte, byte, byte, byte)?, bool, ushort?)[]
            {
                (0, 0, 0, 0, 100, -2f, 2f, 0f, (100, 200, 100, 200), true, (ushort)AniGraphicEffect.Additive),
                (0, 0, 20, 10, 100, -1f, 1f, 1f, (200, 100, 200, 100), false, (ushort)AniGraphicEffect.Additive),
            });

        var def = MakeDef(
            lifeTimeMs: 500,
            transparentStart: 50,
            transparentEnd: 50,
            objects: new[] { new ParticleSimObject("fx/synth.ani", -1) });

        ParticleSimulationResult sim = ParticleSimulation.Simulate(
            def, 1u, 2, 50, facingLeft: true,
            resolveAni: p => p == "fx/synth.ani" ? ani : null);

        // t=50 (第0帧一半): offset=(10,5), scale=(-1.5, 1.5), rot=0.5f, tint=(150,150,150,150)
        // alphaPct=50 -> finalAlpha = round(150 * 0.5) = 75
        // Mirrored = facingLeft(true) ^ (scaleX < 0)(true) = false!
        ParticleSprite sp = sim.Steps[1].Sprites[0];
        Assert.Equal("sprite/effect/synthparticle.img", sp.ImagePath);
        Assert.Equal(0, sp.FrameIndex);
        Assert.Equal((10, 5), (sp.FrameOffsetX, sp.FrameOffsetY));
        Assert.Equal(-1.5f, sp.ScaleX, 3);
        Assert.Equal(1.5f, sp.ScaleY, 3);
        Assert.Equal(0.5f, sp.RotationRadians, 3);
        Assert.Equal((byte)150, sp.ColorR);
        Assert.Equal((byte)75, sp.Alpha);
        Assert.False(sp.Mirrored);
        Assert.Equal(SpriteBlend.Additive, sp.Blend);

        // 用简单未缩放/未旋转 2x2 像素验证 RenderFrame 与 RenderContactSheet 像素合成
        var sprites = new MemorySpriteSource();
        byte[] rgba =
        {
            80, 40, 20, 255,   80, 40, 20, 255,
            80, 40, 20, 255,   80, 40, 20, 255,
        };
        sprites.Add("sprite/fx/box.img", 0, new SpriteFrame(2, 2, 0, 0, 2, 2, rgba));

        var boxDef = MakeDef(
            lifeTimeMs: 100,
            transparentStart: 100,
            transparentEnd: 100,
            objects: new[] { new ParticleSimObject("fx/box.img", 0) });
        ParticleSimulationResult boxSim = ParticleSimulation.Simulate(boxDef, 1u, 2, 40);

        var canvas = new RgbaCanvas(32, 32);
        canvas.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(canvas, boxSim.Steps[0].Sprites, sprites, new TownProjection(-10, 10));
        // world=(0,0,0) -> sx = 0 - (-10) = 10, sy = 0 + 10 = 10 -> Pixel(10, 10) 为纯源色 (80,40,20,255)
        int off = (10 * 32 + 10) * 4;
        Assert.Equal((80, 40, 20, 255), (canvas.Pixels[off], canvas.Pixels[off + 1], canvas.Pixels[off + 2], canvas.Pixels[off + 3]));

        RgbaCanvas sheet = ParticleSimulation.RenderContactSheet(boxSim, sprites, cellWidth: 64, cellHeight: 64, columns: 2);
        Assert.Equal((128, 64), (sheet.Width, sheet.Height));
        // 第 0 格中心在 (32, 42)，2x2 方块画在 (32, 42)..(33, 43) 覆盖准星中心 (32, 42)
        int cell0Anchor = (42 * 128 + 32) * 4;
        Assert.Equal((80, 40, 20), (sheet.Pixels[cell0Anchor], sheet.Pixels[cell0Anchor + 1], sheet.Pixels[cell0Anchor + 2]));

        // 参数边界校验
        Assert.Throws<ArgumentNullException>(() => ParticleSimulation.Simulate(null!, 1u, 1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParticleSimulation.Simulate(boxDef, 1u, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParticleSimulation.Simulate(boxDef, 1u, 1, -1));
        Assert.Empty(ParticleSimulation.Simulate(boxDef, 1u, 0, 10).Steps);
    }

    [Fact]
    public void ExactLcgCircleOrientationSmallBoundsPosCorrectAndScaledTintedTransformRenderCleanly()
    {
        // 1) Circle + CreateAreaSize(100,0,0) + rotVar0=0 + Z直线1000，手算 seed=1 两颗粒子精确序列
        var circleExact = MakeDef(
            z: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            creationFrequency: -1f,
            maximumCreateNumber: 2,
            createAreaSize: new ParticleSimVec3(100, 0, 0),
            rotationAngleRangeType: ParticleSimRotationRange.Circle,
            rotationAngleVar0: 0,
            lifeTimeMs: 2000);
        ParticleSimulationResult cr = ParticleSimulation.Simulate(circleExact, 1u, 2, 1000);
        // p0: ax=-15, deg=67, rotZ=90-67=23 -> t=1000: (X=-15, Y=113, Z=920); p1: ax=10
        Assert.Equal((-15, 113, 920), (cr.Steps[1].Sprites[0].WorldX, cr.Steps[1].Sprites[0].WorldY, cr.Steps[1].Sprites[0].WorldZ));
        Assert.Equal(10, cr.Steps[1].Sprites[1].WorldX);

        // 2) LCG 高位左移与 bound=2 分支校验
        var lcgDef = MakeDef(createAreaSize: new ParticleSimVec3(1000000, 0, 0), lifeTimeMs: 100);
        Assert.Equal(207237, ParticleSimulation.Simulate(lcgDef, 1u, 1, 0).Steps[0].Sprites[0].WorldX);

        var twoObjDef = MakeDef(
            creationFrequency: -1f,
            maximumCreateNumber: 8,
            howToCreateObject: ParticleSimHowToCreate.Randomize,
            objects: new[] { new ParticleSimObject("sprite/p0.img", 0), new ParticleSimObject("sprite/p1.img", 1) });
        ParticleSimulationResult twoRes = ParticleSimulation.Simulate(twoObjDef, 1u, 1, 0);
        Assert.Contains(twoRes.Steps[0].Sprites, s => s.ImagePath == "sprite/p0.img");
        Assert.Contains(twoRes.Steps[0].Sprites, s => s.ImagePath == "sprite/p1.img");

        // 3) CreatePosCorrect 在单步只生成 1 颗 (den==1) 时仍返回前一帧位置
        var singleCorrectDef = MakeDef(creationFrequency: 10f, maximumCreateNumber: 5, createPosCorrect: true, lifeTimeMs: 1000);
        var emSingle = new ParticleEmitter(singleCorrectDef, 1u, new ParticleSimVec3(0, 0, 0));
        emSingle.Step(0, new ParticleSimVec3(0, 0, 0), false);
        IReadOnlyList<ParticleSprite> stepOne = emSingle.Step(100, new ParticleSimVec3(100, 0, 0), false);
        Assert.Equal(2, stepOne.Count);
        Assert.Equal(0, stepOne[1].WorldX);

        // 4) RenderContactSheet 空单元格背景、准星范围 (-6..6) 与 RGB 通道 + TransformSprite 缩放染色全像素路径
        var shortDef = MakeDef(
            creationPos: new ParticleSimVec3(15, 0, 0),
            lifeTimeMs: 10,
            objects: new[] { new ParticleSimObject("fx/tiny.img", 0) });
        var tinySrc = new MemorySpriteSource();
        tinySrc.Add("sprite/fx/tiny.img", 0, new SpriteFrame(2, 2, 0, 0, 2, 2, new byte[]
        {
            100, 180, 255, 255,  100, 180, 255, 255,
            100, 180, 255, 255,  100, 180, 255, 255,
        }));
        ParticleSimulationResult threeSteps = ParticleSimulation.Simulate(shortDef, 1u, 3, 20);
        RgbaCanvas grid = ParticleSimulation.RenderContactSheet(threeSteps, tinySrc, cellWidth: 64, cellHeight: 64, columns: 2);
        Assert.Equal((128, 128), (grid.Width, grid.Height));
        // 右下空格 (96, 96) 保持底色 (22, 24, 30, 255)
        int emptyCorner = (96 * 128 + 96) * 4;
        Assert.Equal((22, 24, 30, 255), (grid.Pixels[emptyCorner], grid.Pixels[emptyCorner + 1], grid.Pixels[emptyCorner + 2], grid.Pixels[emptyCorner + 3]));
        // 第 2 格（空粒子，anchor=(96,42)）准星底部 (96, 48) 为 (65,75,95)，越界 (96, 49) 为格底 (28,30,38)
        int crossTip = (48 * 128 + 96) * 4;
        int belowTip = (49 * 128 + 96) * 4;
        Assert.Equal((65, 75, 95), (grid.Pixels[crossTip], grid.Pixels[crossTip + 1], grid.Pixels[crossTip + 2]));
        Assert.Equal((28, 30, 38), (grid.Pixels[belowTip], grid.Pixels[belowTip + 1], grid.Pixels[belowTip + 2]));

        // TransformSprite 触发路径: scale=(2,2) + tint=(200,150,254,255) -> (78, 106, 254, 255)
        AniFile scaledAni = BuildSyntheticAni(
            new[] { "fx/tiny.img" },
            new (int, int, int, int, int, float?, float?, float?, (byte, byte, byte, byte)?, bool, ushort?)[]
            {
                (0, 0, 0, 0, 100, 2f, 2f, 0f, (200, 150, 254, 255), false, null),
            });
        var scaledDef = MakeDef(lifeTimeMs: 100, objects: new[] { new ParticleSimObject("fx/scaled.ani", -1) });
        ParticleSimulationResult scaledSim = ParticleSimulation.Simulate(scaledDef, 1u, 1, 0, resolveAni: _ => scaledAni);
        var scaledCanvas = new RgbaCanvas(32, 32);
        scaledCanvas.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(scaledCanvas, scaledSim.Steps[0].Sprites, tinySrc, new TownProjection(-10, 10));
        int pOff = (10 * 32 + 10) * 4;
        Assert.Equal((78, 106, 254, 255), (scaledCanvas.Pixels[pOff], scaledCanvas.Pixels[pOff + 1], scaledCanvas.Pixels[pOff + 2], scaledCanvas.Pixels[pOff + 3]));
    }

    [Fact]
    public void AlphaOver100SkipsScalingOnSub255TintMirrorFlipsAreaXAndRandomLifetimeClampsToOne()
    {
        // 1) M07: 帧自身 Tint Alpha = 120 (< 255) 时，transparent > 100 跳过乘算（保持 120，而非 120*1.5=180）；
        //    transparent = 99 则乘算 round(120 * 0.99) = 119；transparent = 100 保持 120。
        AniFile tintedAni = BuildSyntheticAni(
            new[] { "fx/tint.img" },
            new (int, int, int, int, int, float?, float?, float?, (byte, byte, byte, byte)?, bool, ushort?)[]
            {
                (0, 0, 0, 0, 200, null, null, null, (200, 150, 100, 120), false, null),
            });

        var over100Def = MakeDef(
            lifeTimeMs: 100,
            transparentStart: 150,
            transparentEnd: 90,
            objects: new[] { new ParticleSimObject("fx/tint.ani", -1) });
        ParticleSimulationResult over100Sim = ParticleSimulation.Simulate(
            over100Def, 1u, 10, 10, resolveAni: p => p == "fx/tint.ani" ? tintedAni : null);
        // t=0: alphaPct=150 >= 100 -> 跳过缩放，维持 tintAlpha=120
        Assert.Equal((150, 120), (over100Sim.Steps[0].Sprites[0].AlphaPercent, over100Sim.Steps[0].Sprites[0].Alpha));
        // t=90: alphaPct=96 < 100 -> round(120 * 0.96) = 115
        Assert.Equal((96, 115), (over100Sim.Steps[9].Sprites[0].AlphaPercent, over100Sim.Steps[9].Sprites[0].Alpha));

        var at99Def = MakeDef(lifeTimeMs: 0, transparentStart: 99, transparentEnd: 99, objects: new[] { new ParticleSimObject("fx/tint.ani", -1) });
        Assert.Equal(119, ParticleSimulation.Simulate(at99Def, 1u, 1, 0, resolveAni: _ => tintedAni).Steps[0].Sprites[0].Alpha);

        var at100Def = MakeDef(lifeTimeMs: 0, transparentStart: 100, transparentEnd: 100, objects: new[] { new ParticleSimObject("fx/tint.ani", -1) });
        Assert.Equal(120, ParticleSimulation.Simulate(at100Def, 1u, 1, 0, resolveAni: _ => tintedAni).Steps[0].Sprites[0].Alpha);

        // 2) M08: facingLeft=true 只镜像 CreateAreaSize 的 X 随机偏移，不镜像 emitterPos 与 CreationPos
        //    seed=1u, areaSize.X=100 时第 0 颗 ax = -15:
        //    右向 startX = 7 + 30 + (-15) = 22；左向 startX = 7 + 30 - (-15) = 52
        var areaXDef = MakeDef(
            creationPos: new ParticleSimVec3(30, 10, 5),
            createAreaSize: new ParticleSimVec3(100, 0, 0),
            lifeTimeMs: 1000);
        var emitterPos = new ParticleSimVec3(7, 3, 2);
        ParticleSprite rightArea = ParticleSimulation.Simulate(areaXDef, 1u, 1, 0, emitterPos: emitterPos, facingLeft: false).Steps[0].Sprites[0];
        ParticleSprite leftArea = ParticleSimulation.Simulate(areaXDef, 1u, 1, 0, emitterPos: emitterPos, facingLeft: true).Steps[0].Sprites[0];
        Assert.Equal((22, 13, 7), (rightArea.WorldX, rightArea.WorldY, rightArea.WorldZ));
        Assert.Equal((52, 13, 7), (leftArea.WorldX, leftArea.WorldY, leftArea.WorldZ));

        // 同时核验 CreateAreaSize Y 的 29% 透视缩放与 Z 的 100% 偏移（seed=1u 首项偏移均为 -15）
        var areaYDef = MakeDef(creationPos: new ParticleSimVec3(30, 10, 5), createAreaSize: new ParticleSimVec3(0, 100, 0), lifeTimeMs: 1000);
        ParticleSprite yArea = ParticleSimulation.Simulate(areaYDef, 1u, 1, 0, emitterPos: emitterPos).Steps[0].Sprites[0];
        // (-15 * 29) / 100 = -4 -> startY = 3 + 10 - 4 = 9
        Assert.Equal((37, 9, 7), (yArea.WorldX, yArea.WorldY, yArea.WorldZ));

        var areaZDef = MakeDef(creationPos: new ParticleSimVec3(30, 10, 5), createAreaSize: new ParticleSimVec3(0, 0, 100), lifeTimeMs: 1000);
        ParticleSprite zArea = ParticleSimulation.Simulate(areaZDef, 1u, 1, 0, emitterPos: emitterPos).Steps[0].Sprites[0];
        Assert.Equal((37, 13, -8), (zArea.WorldX, zArea.WorldY, zArea.WorldZ));

        // 3) M11: RandomRange=200 + lifeTimeMs=1 时 permille < 1000 会把 ScalePermille 缩到 0，必须钳位到 1（不能变 0 成为永生）
        var clampLifeDef = MakeDef(
            creationFrequency: -1f,
            maximumCreateNumber: 20,
            randomRange: 200,
            lifeTimeMs: 1);
        ParticleSimulationResult clampSim = ParticleSimulation.Simulate(clampLifeDef, 77u, 4, 1);
        Assert.Equal(20, clampSim.Steps[0].Sprites.Count); // t=0 全部存活
        Assert.Empty(clampSim.Steps[1].Sprites);           // t=1 钳位为 1ms 的全部按期死亡（若变 0 永生或钳为 2ms 则仍存活）
        Assert.Empty(clampSim.Steps[3].Sprites);

        // 同时验证 lifeTimeMs=100 + randomRange=200 确实产生早于 100ms 死亡与晚于 100ms 仍存活的个体
        var spreadLifeDef = MakeDef(
            creationFrequency: -1f,
            maximumCreateNumber: 20,
            randomRange: 200,
            lifeTimeMs: 100);
        ParticleSimulationResult spreadSim = ParticleSimulation.Simulate(spreadLifeDef, 77u, 3, 60); // t=0, 60, 120
        Assert.Equal(20, spreadSim.Steps[0].Sprites.Count);
        Assert.InRange(spreadSim.Steps[1].Sprites.Count, 1, 19); // t=60 部分已死、部分仍存
        Assert.InRange(spreadSim.Steps[2].Sprites.Count, 1, 19); // t=120 超过原 100ms 的仍存、其余已死
    }

    [Fact]
    public void EnumsDefaultsQualityClampingExactAnglesRandomRangeAndTransformBoundariesArePinned()
    {
        // 1) 枚举整数值与默认 ParticleSimDef / ParticleSimVec3 相等性
        Assert.Equal((0, 1, 0, 2, 33), (
            (int)ParticleSimHowToCreate.InOrder, (int)ParticleSimHowToCreate.Randomize,
            (int)ParticleSimObjectKind.Unknown, (int)ParticleSimObjectKind.Animation, (int)ParticleSimObjectKind.PassiveObject));
        Assert.Equal((0, 1, 2, 3, 4, 5, 6), (
            (int)ParticleSimLayer.Close, (int)ParticleSimLayer.Normal, (int)ParticleSimLayer.Bottom,
            (int)ParticleSimLayer.CloseBack, (int)ParticleSimLayer.MiddleBack, (int)ParticleSimLayer.DistantBack, (int)ParticleSimLayer.Door));

        var v0 = new ParticleSimVec3(1, 2, 3);
        Assert.True(v0.Equals(new ParticleSimVec3(1, 2, 3)));
        Assert.True(v0.Equals((object)new ParticleSimVec3(1, 2, 3)));
        Assert.False(v0.Equals(new ParticleSimVec3(9, 2, 3)));
        Assert.False(v0.Equals(new ParticleSimVec3(1, 9, 3)));
        Assert.False(v0.Equals(new ParticleSimVec3(1, 2, 9)));
        Assert.False(v0.Equals((object)"notvec"));
        Assert.Equal(v0.GetHashCode(), new ParticleSimVec3(1, 2, 3).GetHashCode());

        var defDefault = new ParticleSimDef();
        Assert.Equal(string.Empty, defDefault.Path);
        Assert.Equal(0, defDefault.RandomRange);
        Assert.Equal((100, 100), (defDefault.TransparentStart, defDefault.TransparentEnd));
        Assert.Equal((100, 100, 100), (defDefault.DisplayEffectRateLow, defDefault.DisplayEffectRateMid, defDefault.DisplayEffectRateHigh));
        Assert.Equal((ParticleSimHowToCreate.Randomize, ParticleSimObjectKind.Animation, ParticleSimLayer.Normal),
            (defDefault.HowToCreateObject, defDefault.ObjectType, defDefault.Layer));

        // 2) quality < 0 钳位到 0（走 rateLow 而非 rateMid），quality > 2 钳位到 2（走 rateHigh）
        var qDef = MakeDef(creationFrequency: -1f, maximumCreateNumber: 10, rateLow: 20, rateMid: 60, rateHigh: 100);
        Assert.Equal(2, ParticleSimulation.Simulate(qDef, 1u, 1, 0, quality: -1).Steps[0].Sprites.Count);
        Assert.Equal(6, ParticleSimulation.Simulate(qDef, 1u, 1, 0, quality: 1).Steps[0].Sprites.Count);
        Assert.Equal(10, ParticleSimulation.Simulate(qDef, 1u, 1, 0, quality: 5).Steps[0].Sprites.Count);

        // 3) CreateAreaSize Y 精确除 100（seed=3, areaSize.Y=1000 -> ay=59, 59*29/100=17，若除 101 则为 16）
        var areaY1000 = MakeDef(createAreaSize: new ParticleSimVec3(0, 1000, 0), lifeTimeMs: 100);
        ParticleSprite aySp = ParticleSimulation.Simulate(areaY1000, 3u, 1, 0).Steps[0].Sprites[0];
        Assert.Equal(17, aySp.WorldY);
        // .img 粒子默认染色通道保持 (255, 255, 255)
        Assert.Equal((255, 255, 255), (aySp.ColorR, aySp.ColorG, aySp.ColorB));

        // 4) Rectangle 发射角扰动精算: var0=30, var1=40, X=1000, seed=1 -> rx=-10, ry=16 -> (946, -48, 275)
        var rectDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            rotationAngleRangeType: ParticleSimRotationRange.Rectangle,
            rotationAngleVar0: 30,
            rotationAngleVar1: 40,
            lifeTimeMs: 2000);
        ParticleSprite rectSp = ParticleSimulation.Simulate(rectDef, 1u, 2, 1000).Steps[1].Sprites[0];
        Assert.Equal((946, -48, 275), (rectSp.WorldX, rectSp.WorldY, rectSp.WorldZ));

        // 5) Circle 发射角与 CDF 二分边界精算（覆盖 halfVar0=var0/2、cdf[180]、target==0、target==tot、arr[mid]==target）
        static (int x, int y, int z) CircleAt1000(int var0, uint seed)
        {
            var d = MakeDef(
                z: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
                rotationAngleRangeType: ParticleSimRotationRange.Circle,
                rotationAngleVar0: var0,
                lifeTimeMs: 2000);
            ParticleSprite sp = ParticleSimulation.Simulate(d, seed, 2, 1000).Steps[1].Sprites[0];
            return (sp.WorldX, sp.WorldY, sp.WorldZ);
        }
        Assert.Equal((-490, 202, 523), CircleAt1000(90, 1u));
        Assert.Equal((-822, 96, 461), CircleAt1000(360, 1u));   // 命中 CircleCdf[180]
        Assert.Equal((0, 290, 0), CircleAt1000(2, 638u));       // target==0 -> radius=0（杀 lo=1）
        Assert.Equal((-24, 243, 544), CircleAt1000(10, 1u));    // 杀 NextRandom(totalWeight + 0)
        Assert.Equal((-17, 289, -52), CircleAt1000(10, 95u));   // target==cdf[2]（杀 arr[mid] <= target）

        // 6) RandomRange 精算: rr=1 必须生效 (996)；rr=50 精算 (934)；lifeTimeMs=1, rr=200, seed=761 扰动得 lifeTime=2（t=1 仍存活，t=2 死亡）
        var rr1Def = MakeDef(x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0), randomRange: 1, lifeTimeMs: 2000);
        Assert.Equal(996, ParticleSimulation.Simulate(rr1Def, 1u, 2, 1000).Steps[1].Sprites[0].WorldX);
        var rr50Def = MakeDef(x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0), randomRange: 50, lifeTimeMs: 2000);
        Assert.Equal(934, ParticleSimulation.Simulate(rr50Def, 1u, 2, 1000).Steps[1].Sprites[0].WorldX);
        var life1ExpandDef = MakeDef(randomRange: 200, lifeTimeMs: 1);
        ParticleSimulationResult life1Res = ParticleSimulation.Simulate(life1ExpandDef, 761u, 3, 1); // t=0, 1, 2
        Assert.Single(life1Res.Steps[0].Sprites);
        Assert.Single(life1Res.Steps[1].Sprites); // permille=2000 -> lifeTime=2，t=1 仍存活（杀 lifeTime != 1）
        Assert.Empty(life1Res.Steps[2].Sprites);  // t=2 死亡

        // 7) resolveAni 返回 null 时安全降级为空图不抛异常（杀 ani != null || ani.Frames.Length > 0）
        var missingAniDef = MakeDef(lifeTimeMs: 100, objects: new[] { new ParticleSimObject("fx/missing.ani", -1) });
        ParticleSprite missingSp = ParticleSimulation.Simulate(missingAniDef, 1u, 1, 0, resolveAni: _ => null!).Steps[0].Sprites[0];
        Assert.Equal(string.Empty, missingSp.ImagePath);
        Assert.Equal(-1, missingSp.FrameIndex);

        // 8) RenderContactSheet 最小单元 (1x1)、准星上端 (cy-6 / cy-7) 与边框四边 (52, 58, 72)
        var tinySrc = new MemorySpriteSource();
        var onePx = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[] { 200, 150, 100, 255 });
        tinySrc.Add("sprite/fx/dot.img", 0, onePx);
        ParticleSimulationResult emptyStepSim = ParticleSimulation.Simulate(MakeDef(lifeTimeMs: 1), 1u, 2, 10);
        RgbaCanvas minSheet = ParticleSimulation.RenderContactSheet(emptyStepSim, tinySrc, cellWidth: 1, cellHeight: 1, columns: 1);
        Assert.Equal((1, 2), (minSheet.Width, minSheet.Height));

        RgbaCanvas borderSheet = ParticleSimulation.RenderContactSheet(emptyStepSim, tinySrc, cellWidth: 32, cellHeight: 32, columns: 2);
        // 第 1 格在 x=[32..63], y=[0..31], 空粒子, 准星中心 (48, 21) -> 上端 (48, 15) 准星色，(48, 14) 格底色
        static (byte r, byte g, byte b) Px(RgbaCanvas c, int x, int y)
        {
            int o = (y * c.Width + x) * 4;
            return (c.Pixels[o], c.Pixels[o + 1], c.Pixels[o + 2]);
        }
        Assert.Equal((65, 75, 95), Px(borderSheet, 48, 15));
        Assert.Equal((28, 30, 38), Px(borderSheet, 48, 14));
        // 上边框 (40, 0)、下边框 (40, 31)、左边框 (32, 10)、右边框 (63, 10) 均为 (52, 58, 72)，内侧邻格 (33, 10) 与 (62, 10) 为格底 (28, 30, 38)
        Assert.Equal((52, 58, 72), Px(borderSheet, 40, 0));
        Assert.Equal((52, 58, 72), Px(borderSheet, 40, 31));
        Assert.Equal((52, 58, 72), Px(borderSheet, 32, 10));
        Assert.Equal((52, 58, 72), Px(borderSheet, 63, 10));
        Assert.Equal((28, 30, 38), Px(borderSheet, 33, 10));
        Assert.Equal((28, 30, 38), Px(borderSheet, 62, 10));

        // 9) RenderFrame 零缩放边界、单通道染色 (R/G/B 分别 != 255、254 边界)、旋转包围盒、透明像素 Alpha 读取与 2048/2049 尺寸边界
        static ParticleSprite MakeSprite(string path, float sx, float sy, float rot, byte r, byte g, byte b) =>
            new ParticleSprite(0, 0, 0, 0, 0, false, 100, path, 0, path, 0, 0, 0, sx, sy, rot, r, g, b, 255, 0, SpriteBlend.Normal, ParticleSimLayer.Normal);

        var src = new MemorySpriteSource();
        src.Add("sprite/fx/p.img", 0, new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[] { 255, 255, 254, 255 }));
        // 含透明蓝像素 (0,0,255,0) 与不透明红像素 (200,100,0,255) 的 2x1 图，杀 sp[so+2] 误作 alpha
        src.Add("sprite/fx/alpha.img", 0, new SpriteFrame(2, 1, 0, 0, 2, 1, new byte[]
        {
            0, 0, 255, 0,
            200, 100, 0, 255,
        }));
        // 4x4 不透明图（独特 RGB），用于旋转 pi/4 核验 halfOutW '+'、越界 '||' 与 tint==255 通道直通
        byte[] sq4 = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            sq4[i * 4] = 100; sq4[i * 4 + 1] = 180; sq4[i * 4 + 2] = 220; sq4[i * 4 + 3] = 255;
        }
        src.Add("sprite/fx/sq4.img", 0, new SpriteFrame(4, 4, 0, 0, 4, 4, sq4));

        var proj = new TownProjection(-10, 10);
        // ScaleX == 1e-4f 或 ScaleX == 0f (ScaleY == 1f) 必须跳过不绘制
        foreach (ParticleSprite zeroScale in new[]
        {
            MakeSprite("sprite/fx/p.img", 1e-4f, 1f, 0f, 255, 255, 255),
            MakeSprite("sprite/fx/p.img", 0f, 1f, 0f, 255, 255, 255),
        })
        {
            var c = new RgbaCanvas(24, 24);
            c.Clear(10, 10, 10, 255);
            ParticleSimulation.RenderFrame(c, new[] { zeroScale }, src, proj);
            Assert.Equal((10, 10, 10), Px(c, 10, 10));
        }

        // 单通道染色（无缩放/无旋转），覆盖 R!=255 单独染色、G=254、B=254 及 +127 舍入（spB=254, tintB=128 -> (32512+127)/255=127）
        var cR = new RgbaCanvas(24, 24); cR.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cR, new[] { MakeSprite("sprite/fx/p.img", 1f, 1f, 0f, 128, 255, 255) }, src, proj);
        Assert.Equal((128, 255, 254), Px(cR, 10, 10));

        var cG = new RgbaCanvas(24, 24); cG.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cG, new[] { MakeSprite("sprite/fx/p.img", 1f, 1f, 0f, 255, 254, 255) }, src, proj);
        Assert.Equal((255, 254, 254), Px(cG, 10, 10));

        var cB254 = new RgbaCanvas(24, 24); cB254.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cB254, new[] { MakeSprite("sprite/fx/p.img", 1f, 1f, 0f, 255, 255, 254) }, src, proj);
        Assert.Equal((255, 255, 253), Px(cB254, 10, 10));

        var cB128 = new RgbaCanvas(24, 24); cB128.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cB128, new[] { MakeSprite("sprite/fx/p.img", 1f, 1f, 0f, 255, 255, 128) }, src, proj);
        Assert.Equal((255, 255, 127), Px(cB128, 10, 10));

        // 透明蓝像素不画，不透明红像素画且染色
        var cAlpha = new RgbaCanvas(24, 24); cAlpha.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cAlpha, new[] { MakeSprite("sprite/fx/alpha.img", 1f, 1f, 0f, 200, 255, 255) }, src, proj);
        Assert.Equal((10, 10, 10), Px(cAlpha, 10, 10));   // 左像素 alpha=0 未覆盖底色
        Assert.Equal((157, 100, 0), Px(cAlpha, 11, 10));  // 右像素 alpha=255 -> (200*200+127)/255=157

        // 4x4 旋转 pi/4 且 tint=(255,255,255): 旋转后半宽 2.83 -> dw=6, dh=6, deltaX=-1, deltaY=-1, (11,11) 与 (13,12) 命中源色 (100,180,220)
        var cRot = new RgbaCanvas(24, 24); cRot.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cRot, new[] { MakeSprite("sprite/fx/sq4.img", 1f, 1f, (float)(Math.PI / 4.0), 255, 255, 255) }, src, proj);
        Assert.Equal((100, 180, 220), Px(cRot, 11, 11));
        Assert.Equal((100, 180, 220), Px(cRot, 13, 12)); // 若 halfOutW 写成减号则 dw=1 (只画在 x=12)，此处退回底色

        // 2048 高图走 TransformSprite 允许绘制（杀 dh >= 2048）；2049 高图无变换走快路径可绘制（杀误入 TransformSprite 的 063/066）
        byte[] tall2048 = new byte[1 * 2048 * 4];
        tall2048[0] = 200; tall2048[1] = 100; tall2048[2] = 50; tall2048[3] = 255;
        src.Add("sprite/fx/t2048.img", 0, new SpriteFrame(1, 2048, 0, 0, 1, 2048, tall2048));
        var cTall2048 = new RgbaCanvas(16, 16); cTall2048.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cTall2048, new[] { MakeSprite("sprite/fx/t2048.img", 1f, 1f, 0f, 128, 255, 255) }, src, new TownProjection(-2, 2));
        Assert.Equal((100, 100, 50), Px(cTall2048, 2, 2));

        byte[] tall2049 = new byte[1 * 2049 * 4];
        tall2049[0] = 90; tall2049[1] = 80; tall2049[2] = 70; tall2049[3] = 255;
        src.Add("sprite/fx/t2049.img", 0, new SpriteFrame(1, 2049, 0, 0, 1, 2049, tall2049));
        var cTall2049Fast = new RgbaCanvas(16, 16); cTall2049Fast.Clear(10, 10, 10, 255);
        ParticleSimulation.RenderFrame(cTall2049Fast, new[] { MakeSprite("sprite/fx/t2049.img", 1f, 1f, 0f, 255, 255, 255) }, src, new TownProjection(-2, 2));
        Assert.Equal((90, 80, 70), Px(cTall2049Fast, 2, 2)); // 快路径绘制成功（若 063/066 误判进入 TransformSprite 则被 >2048 拦截）
    }
}

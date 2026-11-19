using Dnf.Data;
using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>
/// 粒子仿真补缺测试:钉住变异测试里"原有测试全绿也杀不死"的 9 处行为
/// (第二段相位的零周期兜底、X 轴旋转的整圈判定、RenderFrame 缺帧跳过、联拍图准星横线与边框首像素、
/// 画质档发射倍率、有效结束时间减 1、连发出生年龄、random range 的振幅与周期分路)。
/// 全部期望值均为按公式手算的字面量(推导写在各用例注释里),无真库文件、无墙钟、无睡眠。
/// </summary>
public class ParticleSimulationGapTests
{
    // ------------------------------------------------------------------ 夹具

    private static ParticleSimDef MakeDef(
        ParticleSimAxis x = default,
        ParticleSimAxis y = default,
        ParticleSimAxis z = default,
        int changingTermMs = 0,
        ParticleSimChanging changingType = ParticleSimChanging.KeepChanging,
        ParticleSimVec3 rotationAngle = default,
        int lifeTimeMs = 0,
        float creationFrequency = -1f,
        int maximumCreateNumber = 1,
        int maximumCreateTimeMs = 0,
        int randomRange = 0,
        int rateLow = 100,
        int rateMid = 100,
        int rateHigh = 100)
    {
        return new ParticleSimDef(
            path: "synthetic/gap.ptl",
            x: x,
            y: y,
            z: z,
            changingTermMs: changingTermMs,
            changingType: changingType,
            rotationAngle: rotationAngle,
            lifeTimeMs: lifeTimeMs,
            creationFrequency: creationFrequency,
            maximumCreateNumber: maximumCreateNumber,
            maximumCreateTimeMs: maximumCreateTimeMs,
            randomRange: randomRange,
            displayEffectRateLow: rateLow,
            displayEffectRateMid: rateMid,
            displayEffectRateHigh: rateHigh,
            objects: new[] { new ParticleSimObject("fx/dot.img", 0) });
    }

    /// <summary>对任何图片名都返回 false 且 <c>sprite</c> 置 null 的精灵源(模拟全部缺帧)。</summary>
    private sealed class NoFrameSource : ISpriteSource
    {
        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            sprite = null;
            return false;
        }
    }

    /// <summary>只认注册过的 (图片名, 帧号);其余一律返回 false 且 <c>sprite</c> 置 null。</summary>
    private sealed class PartialSpriteSource : ISpriteSource
    {
        private readonly Dictionary<string, SpriteFrame> _frames = new(StringComparer.Ordinal);

        public void Add(string imageName, int frame, SpriteFrame sprite)
        {
            _frames[imageName + "#" + frame] = sprite;
        }

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            if (_frames.TryGetValue(imageName + "#" + frame, out sprite))
            {
                return true;
            }
            sprite = null;
            return false;
        }
    }

    private static ParticleSprite MakeSpriteAt(string imagePath, int worldX, int worldY)
    {
        return new ParticleSprite(
            0, 0, worldX, worldY, 0, false, 100, imagePath, 0, imagePath, 0, 0, 0,
            1f, 1f, 0f, 255, 255, 255, 255, 0, SpriteBlend.Normal, ParticleSimLayer.Normal);
    }

    private static (byte r, byte g, byte b) Px(RgbaCanvas c, int x, int y)
    {
        int o = (y * c.Width + x) * 4;
        return (c.Pixels[o], c.Pixels[o + 1], c.Pixels[o + 2]);
    }

    private static ParticleSimulationResult EmptySteps(int count)
    {
        var steps = new ParticleStepFrame[count];
        for (int i = 0; i < count; i++)
        {
            steps[i] = new ParticleStepFrame(i * 10, Array.Empty<ParticleSprite>());
        }
        return new ParticleSimulationResult(steps);
    }

    /// <summary>与产品代码同构的 31 位三步 LCG(按客户端 0x00e50e60 的描述独立重写),用来在测试里推出前几次随机数。</summary>
    private sealed class LcgReplica
    {
        private uint _state;

        public LcgReplica(uint seed)
        {
            _state = seed;
        }

        public int Next(int bound)
        {
            if (bound <= 1)
            {
                return 0; // bound <= 1 时返回 0 且不推进状态
            }
            unchecked
            {
                uint x1 = _state * 0x41c64e6dU + 0x3039U;
                uint x2 = x1 * 0x41c64e6dU + 0x3039U;
                uint x3 = x2 * 0x41c64e6dU + 0x3039U;
                _state = x3;
                uint r = ((x3 >> 16) & 0x3ffU) | (((x2 >> 16) & 0x3ffU) << 10) | (((x1 >> 16) & 0x7ffU) << 20);
                return (int)(r % (uint)bound);
            }
        }

        /// <summary>千分比采样:Next(rr*10+1) - rr*5 + 1000,取值 [1000-5rr, 1000+5rr]。</summary>
        public int Permille(int randomRange)
        {
            return Next(randomRange * 10 + 1) - randomRange * 5 + 1000;
        }
    }

    /// <summary>value × permille / 1000,整数截断(与产品 ScalePermille 同义)。</summary>
    private static int Scale(int value, int permille) => (int)((long)value * permille / 1000L);

    // ------------------------------------------------------------------ 097:第二段相位 Var2B == 0 兜底

    [Fact]
    public void KeepLastValueSinCosWithZeroSecondPeriodKeepsOnlyFirstPhaseAndStaysFinite()
    {
        // X 轴 Sin(Var1B=80, Var2A=1000, Var2B=0)、Z 轴 Cos(Var1B=60, Var2A=500, Var2B=0),changingTerm T=125,KeepLastValue。
        // t >= T 时 amp = Var1B,theta = T·2π/((Var2A+Var2B)/2) + (Var2B == 0 ? 0 : (t-T)·2π/Var2B)。
        // X:avg = 500,part1 = 125·2π/500 = π/2,sin = 1 -> dx = 80;
        // Z:avg = 250,part1 = 125·2π/250 = π,cos = -1 -> dz = -60;Y 轴 Stop -> 0。
        // Var2B = 0 时第二段相位按 0 处理:t = T(此时 0/0)、t = 500(除以 0 得 +Inf)乃至很晚的 t,位置都恒为 (80, 0, -60)。
        var def = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.Sin, 40, 80, 1000, 0),
            z: new ParticleSimAxis(ParticleSimMoveType.Cos, 10, 60, 500, 0),
            changingTermMs: 125,
            changingType: ParticleSimChanging.KeepLastValue);
        var emitter = new ParticleEmitter(def, 1u);
        Assert.Single(emitter.Step(0, default, false)); // t=0 爆发 1 颗

        ParticleSprite atT = Assert.Single(emitter.Step(125, default, false));    // t = 125 = T
        Assert.Equal((80, 0, -60), (atT.WorldX, atT.WorldY, atT.WorldZ));
        ParticleSprite at500 = Assert.Single(emitter.Step(375, default, false));  // t = 500
        Assert.Equal((80, 0, -60), (at500.WorldX, at500.WorldY, at500.WorldZ));
        ParticleSprite at5000 = Assert.Single(emitter.Step(4500, default, false)); // t = 5000
        Assert.Equal((80, 0, -60), (at5000.WorldX, at5000.WorldY, at5000.WorldZ));
    }

    [Fact]
    public void KeepLastValueSecondPhaseUsesTimeSinceChangingTermWhenSecondPeriodIsNonZero()
    {
        // T=125,X 轴 Sin:Var1B=80,Var2A=998,Var2B=2 -> avg = 500,part1 = 125·2π/500 = π/2。
        // 第二段相位 = (t-T)·2π/Var2B = (t-125)·π:
        //   t=126 -> π,theta = 3π/2,sin = -1 -> X = -80
        //   t=127 -> 2π,theta = π/2 + 2π,sin = 1 -> X = 80
        //   t=128 -> 3π,theta = 3π/2 + 2π,sin = -1 -> X = -80
        var twoDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.Sin, 40, 80, 998, 2),
            changingTermMs: 125,
            changingType: ParticleSimChanging.KeepLastValue);
        var em = new ParticleEmitter(twoDef, 1u);
        Assert.Single(em.Step(0, default, false));
        Assert.Equal(80, Assert.Single(em.Step(125, default, false)).WorldX);  // t=125:part2 = 0
        Assert.Equal(-80, Assert.Single(em.Step(1, default, false)).WorldX);   // t=126
        Assert.Equal(80, Assert.Single(em.Step(1, default, false)).WorldX);    // t=127
        Assert.Equal(-80, Assert.Single(em.Step(1, default, false)).WorldX);   // t=128

        // Var2B = 1:第二段相位 = (t-T)·2π/1,整毫秒下恰为 2π 的整数倍,sin 值与只有第一段相位时相同,
        // 所以位置恒为 80(avg = (999+1)/2 = 500,part1 = π/2);这条钉住"周期 1 ms 不被当作 0、不产生 Inf/NaN"。
        var oneDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.Sin, 40, 80, 999, 1),
            changingTermMs: 125,
            changingType: ParticleSimChanging.KeepLastValue);
        var emOne = new ParticleEmitter(oneDef, 1u);
        Assert.Single(emOne.Step(0, default, false));
        Assert.Equal(80, Assert.Single(emOne.Step(125, default, false)).WorldX);  // t=125
        Assert.Equal(80, Assert.Single(emOne.Step(1, default, false)).WorldX);    // t=126:part2 = 2π
        Assert.Equal(80, Assert.Single(emOne.Step(374, default, false)).WorldX);  // t=500:part2 = 375·2π
    }

    // ------------------------------------------------------------------ 101:旋转角整圈判定(X/Y/Z 三轴对照)

    /// <summary>
    /// 位移沿 <paramref name="axis"/>(0=X,1=Y,2=Z)的直线轴(速度 2000 px/s,t=1000 ms,位移约 2000 px),
    /// 发射方向角取 <paramref name="rotation"/>(Rectangle、无随机扰动),返回 t=1000 时的世界坐标。
    /// </summary>
    private static (int X, int Y, int Z) PositionAt1000(int axis, ParticleSimVec3 rotation)
    {
        var line = new ParticleSimAxis(ParticleSimMoveType.StraightLine, 2000, 2000, 0, 0);
        var def = MakeDef(
            x: axis == 0 ? line : default,
            y: axis == 1 ? line : default,
            z: axis == 2 ? line : default,
            rotationAngle: rotation);
        ParticleSprite sp = ParticleSimulation.Simulate(def, 1u, 2, 1000).Steps[1].Sprites[0];
        return (sp.WorldX, sp.WorldY, sp.WorldZ);
    }

    [Fact]
    public void RotationXOf361DegreesActsAsOneDegreeAndFullTurnOf360ActsAsNone()
    {
        // 位移沿 Y 轴,D = 2000;X 角旋转 (X,Y) 平面:atan2(D,0) = 90°,转 θ 后 x' = D·cos(90°+θ) = -D·sinθ,y' = D·sin(90°+θ) = D·cosθ。
        // 不旋转:Y = 2000·29% = 580。
        // θ=1°:x' = -2000·0.0174524 = -34.9048 -> X = -34(向零截断);y' = 2000·0.9998477 = 1999.6954 -> Y = 1999.6954·29% = 579.91 -> 579。
        // 361 % 360 = 1,所以 361° 必须等同 1°;360 % 360 = 0,所以 360° 必须等同不旋转。
        (int, int, int) none = PositionAt1000(1, new ParticleSimVec3(0, 0, 0));
        (int, int, int) oneDeg = PositionAt1000(1, new ParticleSimVec3(1, 0, 0));
        (int, int, int) deg361 = PositionAt1000(1, new ParticleSimVec3(361, 0, 0));
        (int, int, int) deg360 = PositionAt1000(1, new ParticleSimVec3(360, 0, 0));
        (int, int, int) minus361 = PositionAt1000(1, new ParticleSimVec3(-361, 0, 0));

        Assert.Equal((0, 580, 0), none);
        Assert.Equal((-34, 579, 0), oneDeg);
        Assert.Equal(oneDeg, deg361);
        Assert.Equal((-34, 579, 0), deg361);
        Assert.Equal(none, deg360);
        // -361 % 360 = -1 ≠ 0,等同 -1°:x' = -D·sin(-1°) = +34.9048 -> X = 34;y' 同为 579
        Assert.Equal((34, 579, 0), minus361);
    }

    [Fact]
    public void RotationYOf361DegreesActsAsOneDegreeAndFullTurnOf360ActsAsNone()
    {
        // 位移沿 X 轴,D = 2000;Y 角旋转 (X,Z) 平面:atan2(0,D) = 0°,转 θ 后 x' = D·cosθ,z' = D·sinθ。
        // 不旋转:X = 2000。θ=1°:x' = 1999.6954 -> X = 1999;z' = 34.9048 -> Z = 34。
        (int, int, int) none = PositionAt1000(0, new ParticleSimVec3(0, 0, 0));
        (int, int, int) oneDeg = PositionAt1000(0, new ParticleSimVec3(0, 1, 0));
        (int, int, int) deg361 = PositionAt1000(0, new ParticleSimVec3(0, 361, 0));
        (int, int, int) deg360 = PositionAt1000(0, new ParticleSimVec3(0, 360, 0));

        Assert.Equal((2000, 0, 0), none);
        Assert.Equal((1999, 0, 34), oneDeg);
        Assert.Equal(oneDeg, deg361);
        Assert.Equal((1999, 0, 34), deg361);
        Assert.Equal(none, deg360);
    }

    [Fact]
    public void RotationZOf361DegreesActsAsOneDegreeAndFullTurnOf360ActsAsNone()
    {
        // 位移沿 Z 轴,D = 2000;Z 角旋转 (Z,Y) 平面:atan2(0,D) = 0°,转 θ 后 z' = D·cosθ,y' = D·sinθ。
        // 不旋转:Z = 2000。θ=1°:z' = 1999.6954 -> Z = 1999;y' = 34.9048 -> Y = 34.9048·29% = 10.12 -> 10。
        (int, int, int) none = PositionAt1000(2, new ParticleSimVec3(0, 0, 0));
        (int, int, int) oneDeg = PositionAt1000(2, new ParticleSimVec3(0, 0, 1));
        (int, int, int) deg361 = PositionAt1000(2, new ParticleSimVec3(0, 0, 361));
        (int, int, int) deg360 = PositionAt1000(2, new ParticleSimVec3(0, 0, 360));

        Assert.Equal((0, 0, 2000), none);
        Assert.Equal((0, 10, 1999), oneDeg);
        Assert.Equal(oneDeg, deg361);
        Assert.Equal((0, 10, 1999), deg361);
        Assert.Equal(none, deg360);
    }

    // ------------------------------------------------------------------ 112:RenderFrame 缺帧与零尺寸帧

    [Fact]
    public void RenderFrameSkipsSpritesWhoseFrameIsMissingWithoutThrowingAndStillDrawsTheOthers()
    {
        // 精灵 A 的图片查不到(TryGetFrame 返回 false,sprite = null),精灵 B 有 2x2 纯色帧。
        // 投影 (相机 x = -2, 地面 y = 3):左上角 x = worldX + 2,y = worldY + 3。
        //   A 在世界 (4, 4) -> 屏幕 (6, 7):不得画;B 在世界 (12, 6) -> 屏幕 (14, 9):2x2 覆盖 (14..15, 9..10)。
        var source = new PartialSpriteSource();
        source.Add("sprite/fx/present.img", 0, new SpriteFrame(2, 2, 0, 0, 2, 2, new byte[]
        {
            200, 100, 50, 255,  200, 100, 50, 255,
            200, 100, 50, 255,  200, 100, 50, 255,
        }));
        var sprites = new[]
        {
            MakeSpriteAt("sprite/fx/missing.img", 4, 4),   // 缺帧的在前,先跑到它
            MakeSpriteAt("sprite/fx/present.img", 12, 6),
        };
        var canvas = new RgbaCanvas(32, 32);
        canvas.Clear(10, 10, 10, 255);

        Exception ex = Record.Exception(() => ParticleSimulation.RenderFrame(canvas, sprites, source, new TownProjection(-2, 3)));
        Assert.Null(ex);

        // 有帧的 B 画在手算位置
        Assert.Equal((200, 100, 50), Px(canvas, 14, 9));
        Assert.Equal((200, 100, 50), Px(canvas, 15, 9));
        Assert.Equal((200, 100, 50), Px(canvas, 14, 10));
        Assert.Equal((200, 100, 50), Px(canvas, 15, 10));
        // B 周围仍是底色
        Assert.Equal((10, 10, 10), Px(canvas, 13, 9));
        Assert.Equal((10, 10, 10), Px(canvas, 16, 9));
        Assert.Equal((10, 10, 10), Px(canvas, 14, 8));
        Assert.Equal((10, 10, 10), Px(canvas, 14, 11));
        // 缺帧的 A 位置没画
        Assert.Equal((10, 10, 10), Px(canvas, 6, 7));
        Assert.Equal((10, 10, 10), Px(canvas, 7, 7));
        Assert.Equal((10, 10, 10), Px(canvas, 6, 8));
        Assert.Equal((10, 10, 10), Px(canvas, 7, 8));
    }

    [Fact]
    public void RenderFrameWithNoFrameAtAllLeavesCanvasUntouchedAndDoesNotThrow()
    {
        var canvas = new RgbaCanvas(16, 16);
        canvas.Clear(10, 10, 10, 255);
        var sprites = new[] { MakeSpriteAt("sprite/fx/a.img", 4, 4), MakeSpriteAt("sprite/fx/b.img", 8, 8) };

        Exception ex = Record.Exception(() => ParticleSimulation.RenderFrame(canvas, sprites, new NoFrameSource(), new TownProjection(0, 0)));
        Assert.Null(ex);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal((10, 10, 10), Px(canvas, x, y));
            }
        }
    }

    [Fact]
    public void RenderFrameSkipsZeroWidthAndZeroHeightFramesWithoutThrowing()
    {
        // 宽为 0(高 2)与高为 0(宽 2)的帧都按"无内容"跳过;紧随其后的正常 2x2 帧照常画。
        // 投影 (0, 0):左上角 = 世界坐标。零宽帧在 (4, 4),零高帧在 (8, 4),正常帧在 (12, 4) -> 覆盖 (12..13, 4..5)。
        var source = new PartialSpriteSource();
        source.Add("sprite/fx/zero_w.img", 0, new SpriteFrame(0, 2, 0, 0, 0, 2, Array.Empty<byte>()));
        source.Add("sprite/fx/zero_h.img", 0, new SpriteFrame(2, 0, 0, 0, 2, 0, Array.Empty<byte>()));
        source.Add("sprite/fx/ok.img", 0, new SpriteFrame(2, 2, 0, 0, 2, 2, new byte[]
        {
            30, 60, 90, 255,  30, 60, 90, 255,
            30, 60, 90, 255,  30, 60, 90, 255,
        }));
        var sprites = new[]
        {
            MakeSpriteAt("sprite/fx/zero_w.img", 4, 4),
            MakeSpriteAt("sprite/fx/zero_h.img", 8, 4),
            MakeSpriteAt("sprite/fx/ok.img", 12, 4),
        };
        var canvas = new RgbaCanvas(24, 12);
        canvas.Clear(10, 10, 10, 255);

        Exception ex = Record.Exception(() => ParticleSimulation.RenderFrame(canvas, sprites, source, new TownProjection(0, 0)));
        Assert.Null(ex);

        Assert.Equal((30, 60, 90), Px(canvas, 12, 4));
        Assert.Equal((30, 60, 90), Px(canvas, 13, 5));
        Assert.Equal((10, 10, 10), Px(canvas, 4, 4));
        Assert.Equal((10, 10, 10), Px(canvas, 5, 5));
        Assert.Equal((10, 10, 10), Px(canvas, 8, 4));
        Assert.Equal((10, 10, 10), Px(canvas, 9, 5));
    }

    // ------------------------------------------------------------------ 120 / 123:联拍图准星横线颜色与格子边框首像素

    [Fact]
    public void ContactSheetAnchorCrosshairHorizontalAndVerticalArmsAreExactlyTheCrosshairColor()
    {
        // 格宽 70、格高 61:准星中心 cx = 70/2 = 35,cy = (61·2)/3 = 122/3 = 40(整除)。
        // 横线 x ∈ [35-6, 35+6] = [29, 41],y = 40;竖线 x = 35,y ∈ [40-6, 40+6] = [34, 46]。
        // 2 格一行:第 1 格横向偏移 70,准星中心 (105, 40)。
        RgbaCanvas sheet = ParticleSimulation.RenderContactSheet(EmptySteps(2), new NoFrameSource(), cellWidth: 70, cellHeight: 61, columns: 2);
        Assert.Equal((140, 61), (sheet.Width, sheet.Height));

        foreach (int baseX in new[] { 0, 70 })
        {
            int cx = baseX + 35;
            for (int dx = -6; dx <= 6; dx++)
            {
                Assert.Equal((65, 75, 95), Px(sheet, cx + dx, 40));
            }
            for (int dy = -6; dy <= 6; dy++)
            {
                Assert.Equal((65, 75, 95), Px(sheet, cx, 40 + dy));
            }
            // 十字外一格是格子底色 (28, 30, 38)
            Assert.Equal((28, 30, 38), Px(sheet, cx - 7, 40));
            Assert.Equal((28, 30, 38), Px(sheet, cx + 7, 40));
            Assert.Equal((28, 30, 38), Px(sheet, cx, 40 - 7));
            Assert.Equal((28, 30, 38), Px(sheet, cx, 40 + 7));
            Assert.Equal((28, 30, 38), Px(sheet, cx + 1, 41));
            Assert.Equal((28, 30, 38), Px(sheet, cx - 1, 39));
        }
    }

    [Fact]
    public void ContactSheetTopAndBottomBorderRowsCoverEveryColumnIncludingBothCorners()
    {
        // 4 格、每行 2 格,格宽 70、格高 61:整图 140 x 122。格 (col,row) 的左上角 = (col·70, row·61)。
        // 每格首行 y = row·61、末行 y = row·61 + 60,整行 x ∈ [col·70, col·70 + 69] 都是边框色 (52, 58, 72);
        // 内部行 (如 row·61 + 30) 的最左与最右像素是边框色,首行紧邻的下一行中部是格子底色 (28, 30, 38)。
        RgbaCanvas sheet = ParticleSimulation.RenderContactSheet(EmptySteps(4), new NoFrameSource(), cellWidth: 70, cellHeight: 61, columns: 2);
        Assert.Equal((140, 122), (sheet.Width, sheet.Height));

        foreach (int col in new[] { 0, 1 })
        {
            foreach (int row in new[] { 0, 1 })
            {
                int left = col * 70;
                int top = row * 61;
                foreach (int y in new[] { top, top + 60 })
                {
                    Assert.Equal((52, 58, 72), Px(sheet, left, y));          // 左角(x = 0)
                    Assert.Equal((52, 58, 72), Px(sheet, left + 1, y));
                    Assert.Equal((52, 58, 72), Px(sheet, left + 35, y));     // 中点
                    Assert.Equal((52, 58, 72), Px(sheet, left + 68, y));
                    Assert.Equal((52, 58, 72), Px(sheet, left + 69, y));     // 右角(x = cw - 1)
                }
                Assert.Equal((52, 58, 72), Px(sheet, left, top + 30));
                Assert.Equal((52, 58, 72), Px(sheet, left + 69, top + 30));
                Assert.Equal((28, 30, 38), Px(sheet, left + 10, top + 1));
                Assert.Equal((28, 30, 38), Px(sheet, left + 10, top + 59));
            }
        }
    }

    // ------------------------------------------------------------------ H04:画质档发射倍率

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 7)]
    [InlineData(2, 13)]
    [InlineData(-1, 4)] // 负画质钳位到 0(低档)
    [InlineData(7, 13)] // 超界画质钳位到 2(高档)
    public void ContinuousEmissionFirstStepCountFollowsTheQualityTierRate(int quality, int expectedCount)
    {
        // 频率 62、步长 100 ms:步长×频率×0.001 = 6.2;_accum 预置 1.0。
        // 低档倍率 50:1 + 6.2×50% = 4.1 -> 4;中档 100:1 + 6.2 = 7.2 -> 7;高档 200:1 + 12.4 = 13.4 -> 13。
        var def = MakeDef(
            creationFrequency: 62f,
            maximumCreateNumber: 0,
            maximumCreateTimeMs: 0,
            rateLow: 50,
            rateMid: 100,
            rateHigh: 200);
        var emitter = new ParticleEmitter(def, 1u, default, quality);
        Assert.Equal(expectedCount, emitter.Step(100, default, false).Count);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 8)]
    [InlineData(2, 16)]
    public void BurstFirstStepCountFollowsTheQualityTierRate(int quality, int expectedCount)
    {
        // 爆发模式(频率 -1):spawnCount = (int)(MaximumCreateNumber × 倍率 × 0.01) = 8 × {50, 100, 200}% = 4, 8, 16。
        var def = MakeDef(
            creationFrequency: -1f,
            maximumCreateNumber: 8,
            rateLow: 50,
            rateMid: 100,
            rateHigh: 200);
        var emitter = new ParticleEmitter(def, 1u, default, quality);
        Assert.Equal(expectedCount, emitter.Step(0, default, false).Count);
    }

    [Fact]
    public void ZeroRateTierEmitsNothingWhileOtherTiersStillEmit()
    {
        // 倍率 0 的低档一个粒子也不发(连发与爆发都一样),中/高档照常。
        var burst = MakeDef(creationFrequency: -1f, maximumCreateNumber: 8, rateLow: 0, rateMid: 100, rateHigh: 200);
        var lowBurst = new ParticleEmitter(burst, 1u, default, 0);
        Assert.Empty(lowBurst.Step(0, default, false));
        Assert.Empty(lowBurst.Step(100, default, false));
        Assert.Equal(8, new ParticleEmitter(burst, 1u, default, 1).Step(0, default, false).Count);
        Assert.Equal(16, new ParticleEmitter(burst, 1u, default, 2).Step(0, default, false).Count);

        var continuous = MakeDef(creationFrequency: 62f, maximumCreateNumber: 0, rateLow: 0, rateMid: 100, rateHigh: 200);
        var lowCont = new ParticleEmitter(continuous, 1u, default, 0);
        Assert.Empty(lowCont.Step(100, default, false));
        Assert.Empty(lowCont.Step(100, default, false));
        Assert.Equal(7, new ParticleEmitter(continuous, 1u, default, 1).Step(100, default, false).Count);
        Assert.Equal(13, new ParticleEmitter(continuous, 1u, default, 2).Step(100, default, false).Count);
    }

    // ------------------------------------------------------------------ H09:有效结束时间 = MaximumCreateTimeMs - 1

    [Fact]
    public void EmissionFinishesAtMaximumCreateTimeMinusOneMillisecond()
    {
        // MaximumCreateTimeMs = 100、MaximumCreateNumber = 0(不按个数限时)、频率 10 -> 有效结束时间 = 100 - 1 = 99 ms。
        // 首步 _accum = 1.0 预置 + 步长×10×0.001:Step(98) -> 1.98 -> 发 1 颗,未结束;Step(99) -> 到点结束(1.99 -> 发 1 颗)。
        var def = MakeDef(creationFrequency: 10f, maximumCreateNumber: 0, maximumCreateTimeMs: 100);

        var at98 = new ParticleEmitter(def, 1u);
        Assert.Single(at98.Step(98, default, false));
        Assert.False(at98.IsEmissionFinished);

        var at99 = new ParticleEmitter(def, 1u);
        Assert.Single(at99.Step(99, default, false));
        Assert.True(at99.IsEmissionFinished);

        // 同一个发射器:先 98 ms 未结束,再 +1 ms(累计 99 ms)就结束,结束后不再发射(剩余累积 0.98+0.01 = 0.99 不足 1)。
        var stepwise = new ParticleEmitter(def, 1u);
        Assert.Single(stepwise.Step(98, default, false));
        Assert.False(stepwise.IsEmissionFinished);
        Assert.Single(stepwise.Step(1, default, false));
        Assert.True(stepwise.IsEmissionFinished);
        Assert.Single(stepwise.Step(500, default, false)); // 已结束:不再发射
    }

    [Fact]
    public void ZeroMaximumCreateTimeNeverEndsEmissionByTime()
    {
        // MaximumCreateTimeMs = 0 时 fromTime = 0(不是 -1),MaximumCreateNumber = 0 -> 有效结束时间 0 = 不限时,永远不会因时间结束。
        var def = MakeDef(creationFrequency: 10f, maximumCreateNumber: 0, maximumCreateTimeMs: 0);
        var emitter = new ParticleEmitter(def, 1u);
        emitter.Step(99, default, false);
        Assert.False(emitter.IsEmissionFinished);
        emitter.Step(1, default, false);
        Assert.False(emitter.IsEmissionFinished);
        emitter.Step(5000, default, false);
        Assert.False(emitter.IsEmissionFinished);
    }

    // ------------------------------------------------------------------ H11:连发一步内的出生年龄

    [Fact]
    public void ContinuousStepSpawnsParticlesWithEvenlySpreadBirthAges()
    {
        // 首步 Step(100),频率 35:_accum = 1.0 + 100×35×0.001 = 4.5 -> spawnCount = 4,stepSpan = 100。
        // 第 i 个粒子出生年龄 = (int)(i × 100 / 4) = 0, 25, 50, 75 ms。
        // X 轴直线 1000 px/s -> 位置 = 年龄 px:0, 25, 50, 75。
        var def = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            creationFrequency: 35f,
            maximumCreateNumber: 0);
        IReadOnlyList<ParticleSprite> sprites = new ParticleEmitter(def, 1u).Step(100, default, false);

        Assert.Equal(4, sprites.Count);
        Assert.Equal(new[] { 0, 25, 50, 75 }, sprites.Select(s => s.AgeMs).ToArray());
        Assert.Equal(new[] { 0, 25, 50, 75 }, sprites.Select(s => s.WorldX).ToArray());
    }

    [Fact]
    public void ContinuousStepDropsParticlesBornAtOrAfterTheirLifetime()
    {
        // 同上但 LifeTimeMs = 60:第 3 个粒子出生年龄 75 >= 60,一生下就死 -> 只剩 3 个存活(年龄 0, 25, 50;位置 0, 25, 50)。
        var def = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 1000, 1000, 0, 0),
            lifeTimeMs: 60,
            creationFrequency: 35f,
            maximumCreateNumber: 0);
        var emitter = new ParticleEmitter(def, 1u);
        IReadOnlyList<ParticleSprite> sprites = emitter.Step(100, default, false);

        Assert.Equal(3, sprites.Count);
        Assert.Equal(new[] { 0, 25, 50 }, sprites.Select(s => s.AgeMs).ToArray());
        Assert.Equal(new[] { 0, 25, 50 }, sprites.Select(s => s.WorldX).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, sprites.Select(s => s.ParticleId).ToArray());

        // 再推进 30 ms:剩余累积 4.5 - 4 = 0.5,加 30×35×0.001 = 1.05 -> 1.55 -> 再发 1 颗(出生年龄 0)。
        // 老粒子年龄各 +30 -> 30、55、80,80 >= 60 死亡。存活者:编号 0、1 与新粒子。
        // 被丢弃的第 4 颗在创建之前就跳过、不占粒子编号,所以新粒子编号是 3(若没跳过而是创建后才死,新粒子会是 4)。
        sprites = emitter.Step(30, default, false);
        Assert.Equal(new[] { 0, 1, 3 }, sprites.Select(s => s.ParticleId).ToArray());
        Assert.Equal(new[] { 30, 55, 0 }, sprites.Select(s => s.AgeMs).ToArray());
        Assert.Equal(new[] { 30, 55, 0 }, sprites.Select(s => s.WorldX).ToArray());
    }

    // ------------------------------------------------------------------ H24:random range 振幅与周期分路

    [Fact]
    public void RandomRangeScalesSinAmplitudesByFirstPermilleAndPeriodsBySecondPermille()
    {
        // 设置:RandomRange = 100(千分比范围 [500, 1500]),发射区全 0、Rectangle 且角度范围 0、只有 1 个对象(NextRandom(1) 不推进状态),
        // X 轴 Sin,Y/Z 轴 Stop(不额外取随机数),LifeTime 0 -> 前三次 SamplePermille 依次是 permAmp、permPeriod、permChanging。
        // 用测试里自带的 LCG 推出 seed=4 的前三次随机数(另用独立的 Python 实现核对过):permAmp = 1092、permPeriod = 828、permChanging = 1325。
        const uint seed = 4u;
        const int rr = 100;
        var lcg = new LcgReplica(seed);
        int permAmp = lcg.Permille(rr);
        int permPeriod = lcg.Permille(rr);
        int permChanging = lcg.Permille(rr);
        Assert.Equal((1092, 828, 1325), (permAmp, permPeriod, permChanging));
        Assert.NotEqual(permAmp, permPeriod); // 二者必须不同,否则分不清振幅用的是哪一路

        // 原始 (Var1A, Var1B, Var2A, Var2B) = (1000, 700, 2000, 3000),互不相同且非 0。
        // 振幅(Var1A/Var1B)乘 permAmp/1000:1000×1092/1000 = 1092,700×1092/1000 = 764(764.4 截断);
        // 周期(Var2A/Var2B)乘 permPeriod/1000:2000×828/1000 = 1656,3000×828/1000 = 2484。
        int a1 = Scale(1000, permAmp);
        int b1 = Scale(700, permAmp);
        int a2 = Scale(2000, permPeriod);
        int b2 = Scale(3000, permPeriod);
        Assert.Equal((1092, 764, 1656, 2484), (a1, b1, a2, b2));

        // ---- 场景 1:changingTerm = 0 -> 只用 Var1A'、Var2A':X(t) = 1092·sin(2π·t/1656)。
        // t=207(1/8 周期,sin = √2/2)-> 1092×0.70711 = 772.16 -> 772;t=414(1/4 周期)-> 1092;
        // t=828(半周期,sin(π)≈0)-> 0;t=1242(3/4 周期)-> -1092。
        // 若 Var1A 误用 permPeriod(828)缩放,则 t=414 处只有 828。
        var sinDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.Sin, 1000, 700, 2000, 3000),
            randomRange: rr);
        int[] times1 = { 207, 414, 828, 1242 };
        int[] expected1 = { 772, 1092, 0, -1092 };
        int[] actual1 = FirstParticleXAt(sinDef, seed, times1);
        Assert.Equal(expected1, actual1);
        for (int i = 0; i < times1.Length; i++)
        {
            double formula = a1 * Math.Sin(times1[i] * 2.0 * Math.PI / a2);
            Assert.InRange(actual1[i], formula - 1.0, formula + 1.0);
        }

        // ---- 场景 2:KeepLastValue,changingTerm 原值 400 -> T' = 400×1325/1000 = 530,四个扰动值都参与。
        // t < T'(例 265):linear 分支,pMid = (a2 + (a2 + (b2-a2)·t/T'))/2,theta = t·2π/pMid,amp = a1 + (b1-a1)·t/T'。
        // t >= T'(例 530、1060、2000):amp = b1 = 764,theta = T'·2π/((a2+b2)/2) + (t-T')·2π/b2,avg = (1656+2484)/2 = 2070。
        //   X(265) = 723.30 -> 723;X(530) = 763.45 -> 763;X(1060) = 145.97 -> 145;X(2000) = -624.16 -> -624。
        var keepDef = MakeDef(
            x: new ParticleSimAxis(ParticleSimMoveType.Sin, 1000, 700, 2000, 3000),
            changingTermMs: 400,
            changingType: ParticleSimChanging.KeepLastValue,
            randomRange: rr);
        int tPrime = Scale(400, permChanging);
        Assert.Equal(530, tPrime);
        int[] times2 = { 265, 530, 1060, 2000 };
        int[] expected2 = { 723, 763, 145, -624 };
        int[] actual2 = FirstParticleXAt(keepDef, seed, times2);
        Assert.Equal(expected2, actual2);
        for (int i = 0; i < times2.Length; i++)
        {
            int t = times2[i];
            double theta;
            double amp;
            if (t >= tPrime)
            {
                double avg = (a2 + (double)b2) * 0.5;
                theta = tPrime * 2.0 * Math.PI / avg + (t - tPrime) * 2.0 * Math.PI / b2;
                amp = b1;
            }
            else
            {
                double pMid = (a2 + (a2 + ((double)b2 - a2) * t / tPrime)) * 0.5;
                theta = t * 2.0 * Math.PI / pMid;
                amp = a1 + ((double)b1 - a1) * t / tPrime;
            }
            double formula = amp * Math.Sin(theta);
            Assert.InRange(actual2[i], formula - 1.0, formula + 1.0);
        }
    }

    /// <summary>同一发射器按递增的绝对时刻逐步推进,返回首颗粒子在这些时刻的世界 X(爆发 1 颗,寿命 0 永生)。</summary>
    private static int[] FirstParticleXAt(ParticleSimDef def, uint seed, int[] times)
    {
        var emitter = new ParticleEmitter(def, seed);
        Assert.Single(emitter.Step(0, default, false)); // t=0 爆发 1 颗
        int prev = 0;
        var xs = new int[times.Length];
        for (int i = 0; i < times.Length; i++)
        {
            IReadOnlyList<ParticleSprite> sprites = emitter.Step(times[i] - prev, default, false);
            prev = times[i];
            xs[i] = sprites[0].WorldX;
        }
        return xs;
    }
}

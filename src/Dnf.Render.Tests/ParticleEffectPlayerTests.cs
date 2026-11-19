using System.Buffers.Binary;
using System.Text;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>
/// r19-particle 阶段 3:<see cref="ParticleEffectPlayer"/> 播放器、单次发射语义（0x010c6138）、
/// 小数毫秒累积、图层映射与 <see cref="SceneSprite"/> 转换的合成单测。
/// </summary>
public class ParticleEffectPlayerTests
{
    private sealed class DictSprites : ISpriteSource
    {
        private readonly Dictionary<string, SpriteFrame> _map = new(StringComparer.Ordinal);

        public void Add(string path, int frame, SpriteFrame sprite) => _map[path + "#" + frame] = sprite;

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) =>
            _map.TryGetValue(imageName + "#" + frame, out sprite!);
    }

    private static AniFile BuildSingleFrameAni(
        string imgPath,
        int frameIdx,
        int offX,
        int offY,
        float? sx,
        float? sy,
        float? rot,
        (byte r, byte g, byte b, byte a)? tint,
        ushort? effect)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        w.Write((ushort)1); // frames
        w.Write((ushort)1); // images
        byte[] pathBytes = Encoding.Latin1.GetBytes(imgPath);
        w.Write((uint)pathBytes.Length);
        w.Write(pathBytes);
        w.Write((ushort)0); // global props

        w.Write((ushort)0); // boxes
        w.Write((ushort)0); // imgIdx
        w.Write((ushort)frameIdx);
        w.Write(offX);
        w.Write(offY);

        var props = new List<(ushort id, byte[] payload)>
        {
            (AniFrameProperty.Delay, BitConverter.GetBytes(500)),
        };
        if (sx.HasValue && sy.HasValue)
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), BitConverter.SingleToInt32Bits(sx.Value));
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), BitConverter.SingleToInt32Bits(sy.Value));
            props.Add((AniFrameProperty.ImageRate, buf));
        }
        if (rot.HasValue)
        {
            props.Add((AniFrameProperty.ImageRotate, BitConverter.GetBytes(BitConverter.SingleToInt32Bits(rot.Value))));
        }
        if (tint.HasValue)
        {
            var c = tint.Value;
            props.Add((AniFrameProperty.Rgba, new[] { c.r, c.g, c.b, c.a }));
        }
        if (effect.HasValue)
        {
            byte[] buf = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, effect.Value);
            props.Add((AniFrameProperty.GraphicEffect, buf));
        }
        w.Write((ushort)props.Count);
        foreach ((ushort id, byte[] payload) in props)
        {
            w.Write(id);
            w.Write(payload);
        }
        w.Flush();
        return AniFile.Parse(ms.ToArray());
    }

    [Fact]
    public void OneShotEmitsOnceAndStopsFutureSpawnsWhileAdvancingLiveParticlesAndFractionalMs()
    {
        Assert.Throws<ArgumentNullException>(() => new ParticleEffectPlayer(null!, 1u, default, false));

        // 连续发射型 .ptl（freq=10Hz, maxNum=5, lifeTime=200ms），X 速度 +100 px/s
        var contDef = new ParticleSimDef(
            path: "synthetic/oneshot.ptl",
            x: new ParticleSimAxis(ParticleSimMoveType.StraightLine, 100, 100, 0, 0),
            lifeTimeMs: 200,
            creationFrequency: 10f,
            maximumCreateNumber: 5,
            objects: new[] { new ParticleSimObject("fx/dot.img", 0) });

        // oneShot=true（对应 0x010c6138）：t=0 预装 accum=1.0 放出 1 颗后立即停止发射，朝左时 X 向负方向移动
        var oneShot = new ParticleEffectPlayer(contDef, 1u, new ParticleSimVec3(50, 80, 10), facingLeft: true, oneShot: true);
        Assert.Equal("synthetic/oneshot.ptl", oneShot.Path);
        Assert.Equal(new ParticleSimVec3(50, 80, 10), oneShot.Position);
        Assert.True(oneShot.FacingLeft);
        Assert.Equal(1, oneShot.LiveCount);
        Assert.False(oneShot.Finished);
        Assert.Equal((50, 80, 10), (oneShot.Sprites[0].WorldX, oneShot.Sprites[0].WorldY, oneShot.Sprites[0].WorldZ));

        // 非正步长不推进
        oneShot.Advance(0.0);
        oneShot.Advance(-10.0);
        Assert.Equal(0, oneShot.Sprites[0].AgeMs);

        // 小数毫秒累积: 0.4 + 0.4 = 0.8 (< 1ms 不步进); + 0.5 = 1.3 (步进 1ms, 余 0.3); + 0.7 = 1.0 (步进 1ms, 总龄 2ms)
        oneShot.Advance(0.4);
        oneShot.Advance(0.4);
        Assert.Equal(0, oneShot.Sprites[0].AgeMs);
        oneShot.Advance(0.5);
        Assert.Equal(1, oneShot.Sprites[0].AgeMs);
        oneShot.Advance(0.7);
        Assert.Equal(2, oneShot.Sprites[0].AgeMs);

        // 再推 98ms（总 100ms）：仍只有最初那 1 颗（绝不补发第 2 颗），朝左走 -10px -> WorldX = 40
        oneShot.Advance(98.0);
        Assert.Equal(1, oneShot.LiveCount);
        Assert.Equal((100, 40), (oneShot.Sprites[0].AgeMs, oneShot.Sprites[0].WorldX));

        // 再推 100ms（总 200ms == lifeTimeMs）：唯一粒子死亡，Finished 置真
        oneShot.Advance(100.0);
        Assert.Equal(0, oneShot.LiveCount);
        Assert.True(oneShot.Finished);
        Assert.Empty(oneShot.Sprites);

        // Finished 之后再 Advance 为安全空操作
        oneShot.Advance(50.0);
        Assert.True(oneShot.Finished);

        // 缺省参数校验：oneShot 默认 true（推进 100ms 仍只有 1 颗），quality 默认 2（用 rateHigh 而非 rateMid）
        var defaultOneShot = new ParticleEffectPlayer(contDef, 1u, new ParticleSimVec3(10, 20, 0), facingLeft: false);
        defaultOneShot.Advance(100.0);
        Assert.Equal(1, defaultOneShot.LiveCount);

        var qualityDef = new ParticleSimDef(
            path: "synthetic/q.ptl",
            lifeTimeMs: 200,
            creationFrequency: -1f,
            maximumCreateNumber: 10,
            displayEffectRateLow: 20,
            displayEffectRateMid: 50,
            displayEffectRateHigh: 100,
            objects: new[] { new ParticleSimObject("fx/dot.img", 0) });
        var defaultQ = new ParticleEffectPlayer(qualityDef, 1u, default, false);
        Assert.Equal(10, defaultQ.LiveCount);
        Assert.Equal(0, defaultQ.Sprites[0].AgeMs); // 构造时 Step(0)，杀 Step(1)

        // oneShot=false + MoveTo + CreatePosCorrect：小于 1ms 的 Advance 不得提前调 Step(0) 覆盖 _prevEmitterPos
        var moveCorrectDef = new ParticleSimDef(
            path: "synthetic/mc.ptl",
            lifeTimeMs: 500,
            creationFrequency: 20f,
            maximumCreateNumber: 10,
            createPosCorrect: true,
            objects: new[] { new ParticleSimObject("fx/dot.img", 0) });
        var mcPlayer = new ParticleEffectPlayer(moveCorrectDef, 1u, new ParticleSimVec3(0, 0, 0), facingLeft: false, oneShot: false);
        Assert.Equal(1, mcPlayer.LiveCount);
        mcPlayer.MoveTo(new ParticleSimVec3(100, 0, 0));
        mcPlayer.Advance(0.4); // wholeMs == 0，不得刷新 _prevEmitterPos
        mcPlayer.Advance(99.6); // 合并为 100ms，生出 2 颗在 (100,0) 与 (0,0) 间插值 -> 第 1 颗在 X=50
        Assert.Equal(3, mcPlayer.LiveCount);
        Assert.Equal(50, mcPlayer.Sprites[1].WorldX);

        // oneShot=false + MoveTo：连续发射并在新位置生出第 2 颗
        var continuous = new ParticleEffectPlayer(contDef, 1u, new ParticleSimVec3(10, 20, 0), facingLeft: false, oneShot: false);
        Assert.Equal(1, continuous.LiveCount);
        continuous.MoveTo(new ParticleSimVec3(90, 40, 5));
        Assert.Equal(new ParticleSimVec3(90, 40, 5), continuous.Position);
        continuous.Advance(100.0);
        Assert.Equal(2, continuous.LiveCount);
        Assert.Equal(20, continuous.Sprites[0].WorldX); // 第 1 颗在 10 处向右走 +10 = 20
        Assert.Equal(90, continuous.Sprites[1].WorldX); // 第 2 颗在 MoveTo 后的 90 处出生
    }

    [Fact]
    public void LayerRankMappingAndSceneSpriteEmissionCoverFastTransformTintAndSkips()
    {
        // 1) 7 种 ParticleSimLayer 映射逐一钉死
        Assert.Equal(0, ParticleEffectPlayer.LayerRank(ParticleSimLayer.DistantBack));
        Assert.Equal(1, ParticleEffectPlayer.LayerRank(ParticleSimLayer.MiddleBack));
        Assert.Equal(2, ParticleEffectPlayer.LayerRank(ParticleSimLayer.CloseBack));
        Assert.Equal(3, ParticleEffectPlayer.LayerRank(ParticleSimLayer.Bottom));
        Assert.Equal(TownScene.NormalRank, ParticleEffectPlayer.LayerRank(ParticleSimLayer.Normal));
        Assert.Equal(5, ParticleEffectPlayer.LayerRank(ParticleSimLayer.Close));
        Assert.Equal(TownScene.NormalRank, ParticleEffectPlayer.LayerRank(ParticleSimLayer.Door));

        var sprites = new DictSprites();
        var src2x2 = new SpriteFrame(2, 2, 1, -2, 2, 2, new byte[]
        {
            100, 180, 255, 255,  100, 180, 255, 255,
            100, 180, 255, 255,  100, 180, 255, 255,
        });
        sprites.Add("sprite/fx/spark.img", 3, src2x2);
        var src1x1 = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[] { 200, 120, 80, 255 });
        sprites.Add("sprite/fx/one.img", 0, src1x1);
        sprites.Add("", 0, src1x1);
        sprites.Add("sprite/fx/empty.img", 0, new SpriteFrame(0, 0, 0, 0, 0, 0, Array.Empty<byte>()));
        sprites.Add("sprite/fx/zero_w.img", 0, new SpriteFrame(0, 2, 0, 0, 0, 2, Array.Empty<byte>()));
        sprites.Add("sprite/fx/zero_h.img", 0, new SpriteFrame(2, 0, 0, 0, 2, 0, Array.Empty<byte>()));

        var proj = new TownProjection(-10, 20);

        // 2) 快路径（无缩放/无旋转/无染色）：精算 ScreenX/ScreenY/SortKey/LayerRank/Alpha/Mirrored
        var fastDef = new ParticleSimDef(
            path: "synthetic/fast.ptl",
            lifeTimeMs: 500,
            transparentStart: 80,
            transparentEnd: 80,
            creationFrequency: -1f,
            maximumCreateNumber: 1,
            layer: ParticleSimLayer.Close,
            objects: new[] { new ParticleSimObject("fx/spark.img", 3) });
        var fastPlayer = new ParticleEffectPlayer(fastDef, 1u, new ParticleSimVec3(30, 50, 10), facingLeft: false);

        Assert.Throws<ArgumentNullException>(() => fastPlayer.AppendSceneSprites(null!, proj, new List<SceneSprite>()));
        Assert.Throws<ArgumentNullException>(() => fastPlayer.AppendSceneSprites(sprites, proj, null!));

        var fastOut = new List<SceneSprite>();
        int fastAdded = fastPlayer.AppendSceneSprites(sprites, proj, fastOut, sortBias: 7);
        Assert.Equal(1, fastAdded);
        SceneSprite fs = Assert.Single(fastOut);
        // sx = 30 - (-10) + 0 + 1 = 41; sy = 50 + 20 - 10 + 0 + (-2) = 58
        Assert.Equal((41, 58, 57, -1, 5, 3, 204, false, SpriteBlend.Normal, "synthetic/fast.ptl"),
            (fs.ScreenX, fs.ScreenY, fs.SortKey, fs.NpcId, fs.LayerRank, fs.AniFrame, fs.TintAlpha, fs.Mirrored, fs.Blend, fs.AnimationPath));
        Assert.Same(src2x2, fs.Frame);

        // FrameIndex == 0、1x1 尺寸、Alpha == 1、RotationRadians == 1e-3f（不大于 1e-3f 仍走快路径保持同一 Frame 引用）
        AniFile edgeFastAni = BuildSingleFrameAni("fx/one.img", 0, 0, 0, 1f, 1f, 1e-3f, (255, 255, 255, 100), null);
        var edgeFastDef = new ParticleSimDef(path: "synthetic/edge_fast.ptl", lifeTimeMs: 100, transparentStart: 1, transparentEnd: 1,
            creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/ef.ani", -1) });
        var edgeFastOut = new List<SceneSprite>();
        Assert.Equal(1, new ParticleEffectPlayer(edgeFastDef, 1u, default, false, resolveAni: _ => edgeFastAni).AppendSceneSprites(sprites, proj, edgeFastOut));
        Assert.Equal((0, 1), (edgeFastOut[0].AniFrame, edgeFastOut[0].TintAlpha));
        Assert.Same(src1x1, edgeFastOut[0].Frame);

        // 3) 变换 + 染色 + 加法混合路径（scale 2x2 + tint (200,150,254,255) + Additive + Bottom）
        AniFile xformAni = BuildSingleFrameAni("fx/spark.img", 3, 4, -5, 2f, 2f, 0f, (200, 150, 254, 255), (ushort)AniGraphicEffect.Additive);
        var xformDef = new ParticleSimDef(
            path: "synthetic/xform.ptl",
            lifeTimeMs: 500,
            creationFrequency: -1f,
            maximumCreateNumber: 1,
            layer: ParticleSimLayer.Bottom,
            objects: new[] { new ParticleSimObject("fx/xform.ani", -1) });
        var xformPlayer = new ParticleEffectPlayer(xformDef, 1u, new ParticleSimVec3(30, 50, 10), facingLeft: true, resolveAni: _ => xformAni);
        var xformOut = new List<SceneSprite>();
        Assert.Equal(1, xformPlayer.AppendSceneSprites(sprites, proj, xformOut));
        SceneSprite xs = Assert.Single(xformOut);
        // mirrored TopLeft: x = 30 - (-10) - (4 + 1 + 2) = 33, deltaX = round(1 - 2) = -1 -> ScreenX = 32
        // y = 50 + 20 - 10 + (-5) + (-2) = 53, deltaY = -1 -> ScreenY = 52
        Assert.Equal((32, 52, 51, -1, 3, false, SpriteBlend.Additive, 255),
            (xs.ScreenX, xs.ScreenY, xs.SortKey, xs.NpcId, xs.LayerRank, xs.Mirrored, xs.Blend, xs.TintAlpha));
        Assert.Equal((4, 4), (xs.Frame.Width, xs.Frame.Height));
        Assert.Equal((78, 106, 254, 255), (xs.Frame.Rgba[0], xs.Frame.Rgba[1], xs.Frame.Rgba[2], xs.Frame.Rgba[3]));

        // 4) 单轴缩放（仅 ScaleX=2、仅 ScaleY=2）与单通道染色（仅 R=128、仅 B=254）都必须走 TransformSprite 路径
        AniFile sxOnlyAni = BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 2f, 1f, 0f, null, null);
        var sxOut = new List<SceneSprite>();
        new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/sx.ani", -1) }),
            1u, default, false, resolveAni: _ => sxOnlyAni).AppendSceneSprites(sprites, proj, sxOut);
        Assert.Equal((4, 2), (sxOut[0].Frame.Width, sxOut[0].Frame.Height));

        AniFile syOnlyAni = BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 1f, 2f, 0f, null, null);
        var syOut = new List<SceneSprite>();
        new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/sy.ani", -1) }),
            1u, default, false, resolveAni: _ => syOnlyAni).AppendSceneSprites(sprites, proj, syOut);
        Assert.Equal((2, 4), (syOut[0].Frame.Width, syOut[0].Frame.Height));

        AniFile tintROnlyAni = BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 1f, 1f, 0f, (128, 255, 255, 255), null);
        var tintROut = new List<SceneSprite>();
        new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/tr.ani", -1) }),
            1u, default, false, resolveAni: _ => tintROnlyAni).AppendSceneSprites(sprites, proj, tintROut);
        Assert.Equal((50, 180, 255, 255), (tintROut[0].Frame.Rgba[0], tintROut[0].Frame.Rgba[1], tintROut[0].Frame.Rgba[2], tintROut[0].Frame.Rgba[3]));

        AniFile tintOnlyAni = BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 1f, 1f, 0f, (255, 255, 254, 255), null);
        var tintOnlyDef = new ParticleSimDef(path: "synthetic/tint.ptl", lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1,
            objects: new[] { new ParticleSimObject("fx/t.ani", -1) });
        var tintOnlyOut = new List<SceneSprite>();
        new ParticleEffectPlayer(tintOnlyDef, 1u, default, false, resolveAni: _ => tintOnlyAni).AppendSceneSprites(sprites, proj, tintOnlyOut);
        Assert.Equal((100, 180, 254, 255), (tintOnlyOut[0].Frame.Rgba[0], tintOnlyOut[0].Frame.Rgba[1], tintOnlyOut[0].Frame.Rgba[2], tintOnlyOut[0].Frame.Rgba[3]));

        // 5) 各种应跳过的情形：Alpha<=0、空路径且 FrameIndex>=0、缺失帧、0x0/0x2/2x0 尺寸帧、极小 ScaleX/ScaleY、超时结束
        var skipCases = new[]
        {
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, transparentStart: 0, transparentEnd: 0, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/spark.img", 3) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("", 0) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/missing.img", 0) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/empty.img", 0) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/zero_w.img", 0) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/zero_h.img", 0) }), 1u, default, false),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/zx.ani", -1) }), 1u, default, false,
                resolveAni: _ => BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 1e-4f, 1f, 0f, null, null)),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: -1f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/zy.ani", -1) }), 1u, default, false,
                resolveAni: _ => BuildSingleFrameAni("fx/spark.img", 3, 0, 0, 1f, 1e-4f, 0f, null, null)),
            new ParticleEffectPlayer(new ParticleSimDef(lifeTimeMs: 100, creationFrequency: 0f, maximumCreateNumber: 1, objects: new[] { new ParticleSimObject("fx/spark.img", 3) }), 1u, default, false),
        };
        foreach (ParticleEffectPlayer p in skipCases)
        {
            var list = new List<SceneSprite>();
            Assert.Equal(0, p.AppendSceneSprites(sprites, proj, list));
            Assert.Empty(list);
        }
    }
}

using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Play;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// r19-particle 阶段 3:<see cref="ParticleEffectMapper"/> 与 <see cref="RoomCombatSession"/> 对 .act [PARTICLE] 的完整接线测试。
/// 全部测试数据为自包含合成 PVF/NPK（900+ 合成编号），不改动既有测试夹具、不含原版路径。
/// </summary>
public class RoomCombatParticleTests
{
    private sealed class SyntheticParticleRoom : IDisposable
    {
        public string Root { get; }
        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }

        private readonly List<string> _strings = new();
        private readonly Dictionary<string, int> _stringIdx = new(StringComparer.Ordinal);

        public SyntheticParticleRoom(
            string actParticlePath,
            int offX,
            int offY,
            int offZ,
            bool addPtlInActDir = true,
            bool addPtlAtRootFallback = false,
            bool useAniParticle = false,
            bool relativeImgInPtlDir = false,
            ParticleObjectKind ptlObjectKind = ParticleObjectKind.Animation,
            int ptlLifeTimeMs = 200,
            float ptlFreq = -1f,
            int ptlMaxNum = 2,
            int actLimit = 1,
            int repeatsInBehavior = 1,
            string firstParticlePath = null,
            string secondParticlePath = null,
            string customObjectPath = null,
            int customObjectArg = 0,
            string extraAniPvfPath = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-ptl-room-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            const string chrPath = "character/synth/synth.chr";
            const string equipRoot = "equipment/character/synth";
            const string weaponEqu = "equipment/character/synth/weapon/sword/synth_blade.equ";
            const string mobPath = "monster/synth_950/synth_950.mob";
            const string atkActPath = "monster/synth_950/action/atk.act";

            var scripts = new List<(string Name, byte[] Data)>
            {
                (chrPath, BuildScript(w =>
                {
                    w.Tag("[body image path]").Str("Character/Synth/skin/body%04d.img");
                    w.Tag("[rest motion]").Str("animation/rest.ani");
                    w.Tag("[waiting motion]").Str("animation/wait.ani");
                    w.Tag("[move motion]").Str("animation/move.ani");
                    w.Tag("[move speed]").Float(200f);
                    w.Tag("[attack motion]").Str("animation/rest.ani");
                    w.Tag("[default avatar]").Tag("[coat]").Int(9501);
                })),
                (equipRoot + "/avatar/coat/coat_9501.equ", BuildScript(w =>
                {
                    w.Tag("[equipment type]").Str("[coat avatar]").Int(0);
                    w.Tag("[layer variation]").Int(0).Str("coat_a");
                    w.Tag("[variation]").Int(0).Int(0);
                    w.Tag("[equipment ani script]").Str("Character/Synth/coat/coat_9501.img");
                })),
                (equipRoot + "/avatar/coat/coat.lst", Encoding.ASCII.GetBytes("#PVF_File\n9501\t`coat_9501.equ`\n")),
                (weaponEqu, BuildScript(w =>
                {
                    w.Tag("[equipment type]").Str("[weapon]").Int(0);
                    w.Tag("[sub type]").Int(0);
                    w.Tag("[layer variation]").Int(0).Str("blade");
                    w.Tag("[variation]").Int(0).Int(0);
                    w.Tag("[equipment ani script]").Str("Character/Synth/weapon/blade.img");
                })),
                ("character/synth/animation/rest.ani", BuildSimpleAni("Character/Synth/skin/body0000.img", 100, loop: true)),
                ("character/synth/animation/wait.ani", BuildSimpleAni("Character/Synth/skin/body0000.img", 100, loop: true)),
                ("character/synth/animation/move.ani", BuildSimpleAni("Character/Synth/skin/body0000.img", 100, loop: true)),
                ("monster/synth_950/animation/stay.ani", BuildSimpleAni("Monster/Synth950/body.img", 100, loop: true)),
                ("monster/synth_950/animation/move.ani", BuildSimpleAni("Monster/Synth950/body.img", 100, loop: true)),
                ("monster/synth_950/animation/atk.ani", BuildMultiFrameAni("Monster/Synth950/body.img", (0, 60), (0, 60), (0, 60), (0, 60))),
                (mobPath, BuildScript(w =>
                {
                    w.Tag("[name]").Str("SYNTH_MOB_950");
                    w.Tag("[level]").Int(1).Int(1);
                    w.Tag("[waiting motion]").Str("animation/stay.ani");
                    w.Tag("[move motion]").Str("animation/move.ani");
                    w.Tag("[attack action]").Str("action/atk.act");
                })),
                (atkActPath, BuildScript(w =>
                {
                    w.Tag("[MOTION]").Tag("[BASE ANI]").Str("../animation/atk.ani").Tag("[/MOTION]");
                    w.Tag("[TRIGGER]")
                        .Tag("[LIMIT]").Int(actLimit)
                        .Tag("[FRAME]").Int(0).Int(3)
                        .Tag("[DO BEHAVIOR]").Tag("[ME]").Int(0)
                        .Tag("[/TRIGGER]");
                    w.Tag("[BEHAVIOR]");
                    for (int r = 0; r < repeatsInBehavior; r++)
                    {
                        string p = firstParticlePath != null && r == 0
                            ? firstParticlePath
                            : (secondParticlePath != null && r >= repeatsInBehavior - 4 ? secondParticlePath : actParticlePath);
                        w.Tag("[PARTICLE]").Str(p).Int(offX).Int(offY).Int(offZ);
                    }
                    w.Tag("[/BEHAVIOR]");
                })),
            };

            byte[] ptlBytes = BuildScript(w =>
            {
                w.Tag("[x axis]")
                    .Tag("[move type]").Str("[straight line]")
                    .Tag("[move variable 1]").Int(100).Int(100);
                w.Tag("[life time]").Int(ptlLifeTimeMs);
                w.Tag("[transparent value]").Int(80).Int(80);
                w.Tag("[creation frequency]").Float(ptlFreq);
                w.Tag("[maximum create number]").Int(ptlMaxNum);
                w.Tag("[how to create object]").Str("[in order]");
                w.Tag("[display effect rate]").Int(0).Int(50).Int(100);
                w.Tag("[object type]").Str(ptlObjectKind == ParticleObjectKind.Animation ? "[animation]" : "[passive object]");
                w.Tag("[layer]").Str("[close]");
                if (customObjectPath != null)
                {
                    w.Tag("[object]").Str(customObjectPath).Int(customObjectArg).Tag("[/object]");
                }
                else if (useAniParticle)
                {
                    w.Tag("[object]").Str("spark_anim.ani").Int(-1).Tag("[/object]");
                }
                else if (relativeImgInPtlDir)
                {
                    w.Tag("[object]").Str("local_spark.img").Int(0).Tag("[/object]");
                }
                else
                {
                    w.Tag("[object]").Str("effect/synth950/spark.img").Int(0).Tag("[/object]");
                }
            });

            if (addPtlInActDir)
            {
                string target = PvfPath.ResolveRelativeTo(atkActPath, actParticlePath);
                scripts.Add((target, ptlBytes));
                if (firstParticlePath != null)
                {
                    scripts.Add((PvfPath.ResolveRelativeTo(atkActPath, firstParticlePath), ptlBytes));
                }
                if (secondParticlePath != null)
                {
                    scripts.Add((PvfPath.ResolveRelativeTo(atkActPath, secondParticlePath), ptlBytes));
                }
                if (useAniParticle)
                {
                    string aniTarget = PvfPath.ResolveRelativeTo(target, "spark_anim.ani");
                    scripts.Add((aniTarget, BuildParticleAni("Effect/Synth950/spark.img", (ushort)AniGraphicEffect.Additive)));
                }
            }
            else if (addPtlAtRootFallback)
            {
                string target = PvfPath.Normalize(actParticlePath);
                scripts.Add((target, ptlBytes));
            }
            if (extraAniPvfPath != null)
            {
                scripts.Add((extraAniPvfPath, BuildParticleAni("Effect/Synth950/spark.img", (ushort)AniGraphicEffect.Additive)));
            }

            var pvfFiles = new List<TestData.PvfFileSpec>
            {
                new("stringtable.bin", TestData.BuildStringTable(_strings.Select(s => Encoding.ASCII.GetBytes(s)).ToList()), 0),
            };
            foreach ((string name, byte[] data) in scripts)
            {
                pvfFiles.Add(new TestData.PvfFileSpec(name, data, 0));
            }
            (byte[] pvf, int _) = TestData.BuildPvf(pvfFiles, validChecksums: true);
            File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);

            byte[] dummyImg = Build2x2Img(20, 40, 60);
            byte[] sparkImg = Build2x2Img(240, 120, 30);
            var npkEntries = new List<(string, byte[])>
            {
                ("sprite/character/synth/skin/body0000.img", dummyImg),
                ("sprite/character/synth/coat/coat_9501.img", dummyImg),
                ("sprite/character/synth/weapon/blade.img", dummyImg),
                ("sprite/monster/synth950/body.img", dummyImg),
                ("sprite/effect/synth950/spark.img", sparkImg),
                ("sprite/monster/synth_950/action/fx/local_spark.img", sparkImg),
                ("sprite/a/local_spark.img", sparkImg),
            };
            File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "synth_ptl.npk"), TestData.BuildNpk(npkEntries.ToArray()));

            Assets = DnfAssets.Open(Root);
            var tiles = new List<TileFile>
            {
                new("t0", new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]),
                new("t1", new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]),
                new("t2", new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]),
            };
            var map = new RoomMap("map/synth/ptl.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null);
            var content = new RoomContent(map, null, null, null, null);

            AvatarClass cls = AvatarClass.FromChr(Assets, chrPath, "[synth]");
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, equipRoot, weaponEqu);
            TownHero hero = TownHero.Create(Assets, chrPath, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, mobPath);
            var spawn = new RoomMonsterSpawn(950, 0, 0, 340, 100, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, 100, 222.5, 1500);
            var strikes = new[] { new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, strikes, seed: 1);
            Session.AttachActs(new ActLibrary(Assets, new GameData(new PvfScriptSource(Assets.Pvf))));
        }

        public bool AdvanceUntil(Func<bool> predicate, int maxSteps = 200, double stepMs = 10.0)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (predicate())
                {
                    return true;
                }
                Session.Update(stepMs, 0, 0, false, false);
            }
            return predicate();
        }

        private byte[] BuildScript(Action<ScriptWriter> write)
        {
            var w = new ScriptWriter(Intern);
            write(w);
            return w.ToBytes();
        }

        private int Intern(string s)
        {
            if (!_stringIdx.TryGetValue(s, out int idx))
            {
                idx = _strings.Count;
                _strings.Add(s);
                _stringIdx[s] = idx;
            }
            return idx;
        }

        private static byte[] BuildSimpleAni(string img, int delayMs, bool loop)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = 0, Y = 0 };
            f.Props.Add((AniFrameProperty.Delay, TestData.LE32(delayMs)));
            var globals = loop ? new (ushort, byte[])[] { (0, new byte[] { 1 }) } : Array.Empty<(ushort, byte[])>();
            return TestData.BuildAni(new[] { img }, globals, new List<TestData.AniFrameSpec> { f });
        }

        private static byte[] BuildMultiFrameAni(string img, params (int frame, int delayMs)[] frames)
        {
            var specs = new List<TestData.AniFrameSpec>();
            foreach ((int frame, int delayMs) in frames)
            {
                var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frame, X = 0, Y = 0 };
                f.Props.Add((AniFrameProperty.Delay, TestData.LE32(delayMs)));
                specs.Add(f);
            }
            return TestData.BuildAni(new[] { img }, Array.Empty<(ushort, byte[])>(), specs);
        }

        private static byte[] BuildParticleAni(string img, ushort graphicEffect)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = 0, Y = 0 };
            f.Props.Add((AniFrameProperty.Delay, TestData.LE32(500)));
            f.Props.Add((AniFrameProperty.GraphicEffect, new[] { (byte)(graphicEffect & 0xff), (byte)(graphicEffect >> 8) }));
            return TestData.BuildAni(new[] { img }, Array.Empty<(ushort, byte[])>(), new List<TestData.AniFrameSpec> { f });
        }

        private static byte[] Build2x2Img(byte r, byte g, byte b)
        {
            byte[] bgra = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)
            {
                bgra[i * 4] = b;
                bgra[i * 4 + 1] = g;
                bgra[i * 4 + 2] = r;
                bgra[i * 4 + 3] = 255;
            }
            TestData.ImgFrameSpec f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 2, 2, bgra);
            f.FrameWidth = 2;
            f.FrameHeight = 2;
            return TestData.BuildImg(new List<TestData.ImgFrameSpec> { f });
        }

        public void Dispose()
        {
            Assets.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private sealed class ScriptWriter
        {
            private readonly Func<string, int> _intern;
            private readonly MemoryStream _ms = new();

            public ScriptWriter(Func<string, int> intern)
            {
                _intern = intern;
                _ms.WriteByte(0xb0);
                _ms.WriteByte(0xd0);
            }

            public ScriptWriter Tag(string tag)
            {
                WriteToken(5, _intern(tag));
                return this;
            }

            public ScriptWriter Str(string s)
            {
                WriteToken(7, _intern(s));
                return this;
            }

            public ScriptWriter Int(int v)
            {
                WriteToken(2, v);
                return this;
            }

            public ScriptWriter Float(float v)
            {
                WriteToken(4, BitConverter.SingleToInt32Bits(v));
                return this;
            }

            private void WriteToken(byte kind, int val)
            {
                _ms.WriteByte(kind);
                _ms.WriteByte((byte)(val & 0xff));
                _ms.WriteByte((byte)((val >> 8) & 0xff));
                _ms.WriteByte((byte)((val >> 16) & 0xff));
                _ms.WriteByte((byte)((val >> 24) & 0xff));
            }

            public byte[] ToBytes() => _ms.ToArray();
        }
    }

    [Fact]
    public void ParticleEffectMapperMapsAllFieldsAndRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ParticleEffectMapper.ToSimDef(null!));

        var src = new ParticleEffectDef(
            path: "synth/map.ptl",
            creationPos: new ParticleVec3i(1, 2, 3),
            x: new ParticleEffectAxis(ParticleMoveType.StraightLine, 11, 12, 13, 14),
            y: new ParticleEffectAxis(ParticleMoveType.Sin, 21, 22, 23, 24),
            z: new ParticleEffectAxis(ParticleMoveType.Cos, 31, 32, 33, 34),
            changingTermMs: 450,
            changingType: ParticleChanging.PingPong,
            gravity: 777,
            rotationAngle: new ParticleVec3i(10, 20, 30),
            lifeTimeMs: 888,
            landing: ParticleLanding.Slide,
            transparentStart: 95,
            transparentEnd: 15,
            attackBetweenTimeMs: 1000,
            expansionRate: 1000,
            attackBonusRate: 0,
            creationFrequency: 6.5f,
            maximumCreateNumber: 9,
            maximumCreateTimeMs: 1400,
            createAreaSize: new ParticleVec3i(40, 50, 60),
            createAreaRotationAngle: new ParticleVec3i(4, 5, 6),
            rotationAngleRangeType: ParticleRotationRangeType.Circle,
            rotationAngleVar0: 70,
            rotationAngleVar1: 80,
            randomRange: 35,
            createPosCorrect: true,
            displayEffectRateLow: 25,
            displayEffectRateMid: 55,
            displayEffectRateHigh: 85,
            howToCreateObject: ParticleHowToCreate.InOrder,
            objectType: ParticleObjectKind.PassiveObject,
            objectTypeRaw: "[passive object]",
            layer: ParticleLayer.MiddleBack,
            objects: new[] { new ParticleObjectEntry("a.ani", -1), new ParticleObjectEntry("b.img", 2) },
            startSound: "SYNTH_SFX");

        ParticleSimDef dst = ParticleEffectMapper.ToSimDef(src);
        Assert.Equal("synth/map.ptl", dst.Path);
        Assert.Equal(new ParticleSimVec3(1, 2, 3), dst.CreationPos);
        Assert.Equal((ParticleSimMoveType.StraightLine, 11, 12, 13, 14), (dst.X.Type, dst.X.Var1A, dst.X.Var1B, dst.X.Var2A, dst.X.Var2B));
        Assert.Equal((ParticleSimMoveType.Sin, 21, 22, 23, 24), (dst.Y.Type, dst.Y.Var1A, dst.Y.Var1B, dst.Y.Var2A, dst.Y.Var2B));
        Assert.Equal((ParticleSimMoveType.Cos, 31, 32, 33, 34), (dst.Z.Type, dst.Z.Var1A, dst.Z.Var1B, dst.Z.Var2A, dst.Z.Var2B));
        Assert.Equal((450, ParticleSimChanging.PingPong, 777), (dst.ChangingTermMs, dst.ChangingType, dst.Gravity));
        Assert.Equal(new ParticleSimVec3(10, 20, 30), dst.RotationAngle);
        Assert.Equal((888, ParticleSimLanding.Slide, 95, 15), (dst.LifeTimeMs, dst.Landing, dst.TransparentStart, dst.TransparentEnd));
        Assert.Equal((6.5f, 9, 1400), (dst.CreationFrequency, dst.MaximumCreateNumber, dst.MaximumCreateTimeMs));
        Assert.Equal(new ParticleSimVec3(40, 50, 60), dst.CreateAreaSize);
        Assert.Equal(new ParticleSimVec3(4, 5, 6), dst.CreateAreaRotationAngle);
        Assert.Equal((ParticleSimRotationRange.Circle, 70, 80, 35, true),
            (dst.RotationAngleRangeType, dst.RotationAngleVar0, dst.RotationAngleVar1, dst.RandomRange, dst.CreatePosCorrect));
        Assert.Equal((25, 55, 85), (dst.DisplayEffectRateLow, dst.DisplayEffectRateMid, dst.DisplayEffectRateHigh));
        Assert.Equal((ParticleSimHowToCreate.InOrder, ParticleSimObjectKind.PassiveObject, ParticleSimLayer.MiddleBack),
            (dst.HowToCreateObject, dst.ObjectType, dst.Layer));
        Assert.Equal(2, dst.Objects.Count);
        Assert.Equal(("a.ani", -1), (dst.Objects[0].Path, dst.Objects[0].Arg));
        Assert.Equal(("b.img", 2), (dst.Objects[1].Path, dst.Objects[1].Arg));
    }

    [Fact]
    public void MonsterAttackActParticleEmitsMirroredMotionAtUnmirroredOffsetRendersInBuildAndRetiresOnExpiry()
    {
        using var room = new SyntheticParticleRoom(
            actParticlePath: "fx/spark.ptl",
            offX: 15,
            offY: -10,
            offZ: 25,
            addPtlInActDir: true,
            useAniParticle: true,
            ptlLifeTimeMs: 200,
            ptlFreq: -1f,
            ptlMaxNum: 2);

        var observed = new List<ActParticleRequest>();
        room.Session.ActParticleObserver = r => observed.Add(r);

        Assert.True(room.AdvanceUntil(() => room.Session.ActParticleRequests > 0), "怪物应出招并触发 [PARTICLE]");
        Assert.Equal((1, 1, 0, 1), (room.Session.ActParticleRequests, room.Session.ActParticlesSpawned, room.Session.ActParticlesMissing, room.Session.ActiveActParticles));

        ActParticleRequest req = Assert.Single(observed);
        Assert.True(req.FacingLeft); // 怪物在 340 朝左面对 300 处的主角
        Assert.Equal("fx/spark.ptl", req.Path);
        Assert.Equal("monster/synth_950/action/fx/spark.ptl", req.ResolvedPath);
        // 偏移 (15, -10, 25) 绝不随朝向镜像：WorldX = monster.X + 15
        Assert.Equal((float)room.Monster.X + 15f, req.WorldX, 2);
        Assert.Equal((float)room.Monster.Y - 10f, req.WorldY, 2);
        Assert.Equal((float)room.Monster.Z + 25f, req.WorldZ, 2);

        ParticleEffectPlayer player = Assert.Single(room.Session.ActParticles);
        Assert.Equal(2, player.LiveCount); // burst maxNum=2
        int initialWorldX = player.Sprites[0].WorldX;

        // Build() 应包含 2 条粒子精灵（Close 图层 rank=5、Additive 混合、透明百分比 80% -> alpha=204）
        List<SceneSprite> spritesAtSpawn = room.Session.Build(0);
        List<SceneSprite> ptlSprites = spritesAtSpawn.Where(s => s.AnimationPath == "monster/synth_950/action/fx/spark.ptl").ToList();
        Assert.Equal(2, ptlSprites.Count);
        Assert.All(ptlSprites, s =>
        {
            Assert.Equal(5, s.LayerRank);
            Assert.Equal(SpriteBlend.Additive, s.Blend);
            Assert.Equal(204, s.TintAlpha);
            Assert.True(s.Mirrored);
        });

        // 推进 100ms（10 步 × 10ms）：朝左镜像的 X=+100 px/s 应向左飞 -10px
        for (int i = 0; i < 10; i++)
        {
            room.Session.Update(10.0, 0, 0, false, false);
        }
        Assert.Equal(1, room.Session.ActiveActParticles);
        Assert.Equal(initialWorldX - 10, room.Session.ActParticles[0].Sprites[0].WorldX);

        // 再推进 110ms（累计超过 200ms 寿命）：粒子自动回收，Build() 中不再出现
        for (int i = 0; i < 11; i++)
        {
            room.Session.Update(10.0, 0, 0, false, false);
        }
        Assert.Equal(0, room.Session.ActiveActParticles);
        Assert.DoesNotContain(room.Session.Build(300), s => s.AnimationPath == "monster/synth_950/action/fx/spark.ptl");
    }

    [Fact]
    public void HandlesMissingPtlRootFallbackRelativeImgNonVisualKindAndCapacityCapCleanly()
    {
        // 1) 缺失 .ptl：记录 ActParticlesMissing，不抛异常、不入活表
        using (var missing = new SyntheticParticleRoom("fx/nonexistent.ptl", 0, 0, 0, addPtlInActDir: false, actLimit: 2))
        {
            Assert.True(missing.AdvanceUntil(() => missing.Session.ActParticleRequests >= 2));
            Assert.Equal((2, 0, 2, 0), (missing.Session.ActParticleRequests, missing.Session.ActParticlesSpawned, missing.Session.ActParticlesMissing, missing.Session.ActiveActParticles));
        }

        // 2) .act 目录下没有、回落到 PVF 根相对路径命中
        using (var fallback = new SyntheticParticleRoom("shared/root_spark.ptl", 0, 0, 0, addPtlInActDir: false, addPtlAtRootFallback: true, ptlMaxNum: 1))
        {
            Assert.True(fallback.AdvanceUntil(() => fallback.Session.ActParticleRequests > 0));
            Assert.Equal((1, 1, 0, 1), (fallback.Session.ActParticleRequests, fallback.Session.ActParticlesSpawned, fallback.Session.ActParticlesMissing, fallback.Session.ActiveActParticles));
            Assert.Contains(fallback.Session.Build(0), s => s.AnimationPath == "shared/root_spark.ptl");
        }

        // 3) .ptl 同目录相对 .img 解析（含单字符目录 a/ 杀 LastIndexOf('/') > 1）及缺失 .img 在 Build 中安全返回 false（杀 return true）
        using (var localImg = new SyntheticParticleRoom("a/local_img.ptl", 0, 0, 0, addPtlInActDir: false, addPtlAtRootFallback: true, relativeImgInPtlDir: true, ptlMaxNum: 1))
        {
            Assert.True(localImg.AdvanceUntil(() => localImg.Session.ActParticleRequests > 0));
            Assert.Contains(localImg.Session.Build(0), s => s.AnimationPath == "a/local_img.ptl");
        }
        using (var missingImg = new SyntheticParticleRoom("fx/miss_img.ptl", 0, 0, 0, addPtlInActDir: true, ptlMaxNum: 1, customObjectPath: "effect/synth950/not_in_npk.img", customObjectArg: 0))
        {
            Assert.True(missingImg.AdvanceUntil(() => missingImg.Session.ActParticleRequests > 0));
            Assert.Equal(1, missingImg.Session.ActiveActParticles);
            Assert.DoesNotContain(missingImg.Session.Build(0), s => s.AnimationPath == "monster/synth_950/action/fx/miss_img.ptl");
        }

        // 3b) ResolveActParticleAni 的两级回退：PVF 根相对路径（norm）与 sprite/ 前缀路径（"sprite/" + norm）
        using (var rootAni = new SyntheticParticleRoom("fx/root_ani.ptl", 0, 0, 0, addPtlInActDir: true, ptlMaxNum: 1,
            customObjectPath: "shared/root_fx.ani", customObjectArg: -1, extraAniPvfPath: "shared/root_fx.ani"))
        {
            Assert.True(rootAni.AdvanceUntil(() => rootAni.Session.ActParticleRequests > 0));
            Assert.Contains(rootAni.Session.Build(0), s => s.AnimationPath == "monster/synth_950/action/fx/root_ani.ptl" && s.Blend == SpriteBlend.Additive);
        }
        using (var spriteAni = new SyntheticParticleRoom("fx/sprite_ani.ptl", 0, 0, 0, addPtlInActDir: true, ptlMaxNum: 1,
            customObjectPath: "effect/under_sprite.ani", customObjectArg: -1, extraAniPvfPath: "sprite/effect/under_sprite.ani"))
        {
            Assert.True(spriteAni.AdvanceUntil(() => spriteAni.Session.ActParticleRequests > 0));
            Assert.Contains(spriteAni.Session.Build(0), s => s.AnimationPath == "monster/synth_950/action/fx/sprite_ani.ptl" && s.Blend == SpriteBlend.Additive);
        }

        // 4) [object type] [passive object] 视觉发射器产出 0 颗 -> 不占活表也不计 missing
        using (var passiveKind = new SyntheticParticleRoom("fx/passive_kind.ptl", 0, 0, 0, addPtlInActDir: true, ptlObjectKind: ParticleObjectKind.PassiveObject))
        {
            Assert.True(passiveKind.AdvanceUntil(() => passiveKind.Session.ActParticleRequests > 0));
            Assert.Equal((1, 0, 0, 0), (passiveKind.Session.ActParticleRequests, passiveKind.Session.ActParticlesSpawned, passiveKind.Session.ActParticlesMissing, passiveKind.Session.ActiveActParticles));
        }

        // 5) 超过 MaxActiveActParticles(64) 上限时按 FIFO 淘汰最老实例（第 0 项 first.ptl + 63 项 mid.ptl + 后 4 项 new.ptl -> first.ptl 被淘汰，剩 60 mid + 4 new）
        using (var capped = new SyntheticParticleRoom("fx/mid.ptl", 0, 0, 0, addPtlInActDir: true, ptlLifeTimeMs: 0, ptlMaxNum: 1, actLimit: 1, repeatsInBehavior: 68, firstParticlePath: "fx/first.ptl", secondParticlePath: "fx/new.ptl"))
        {
            Assert.True(capped.AdvanceUntil(() => capped.Session.ActParticleRequests >= 68));
            Assert.Equal(68, capped.Session.ActParticlesSpawned);
            Assert.Equal(RoomCombatSession.MaxActiveActParticles, capped.Session.ActiveActParticles);
            Assert.Equal(0, capped.Session.ActParticles.Count(p => p.Path.EndsWith("first.ptl", StringComparison.Ordinal)));
            Assert.Equal(60, capped.Session.ActParticles.Count(p => p.Path.EndsWith("mid.ptl", StringComparison.Ordinal)));
            Assert.Equal(4, capped.Session.ActParticles.Count(p => p.Path.EndsWith("new.ptl", StringComparison.Ordinal)));
        }
    }
}

using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网选角的职业基础形象(<see cref="JobOutfits"/> 与 <see cref="DnfJobAvatars"/>):职业号 → 装备根目录与角色脚本的路径、默认外观的部件挑选(只穿"本职业块里真有图层"的件)、
/// <c>avatar</c> 与 <c>at_avatar</c> 两个子目录里取哪个、读不出形象的职业不画(不拿别的职业冒充)、每个职业只造一次。
/// 数据全是合成的迷你职业脚本(只有目录形状与真实数据一致,里面的编号与图层名是编的),不含任何真实素材。
/// </summary>
public class JobAvatarsTests
{
    /// <summary>合成的职业脚本与装备脚本(只有 PVF 里的脚本,图包里放一张占位图:基础形象只读脚本、不读图)。</summary>
    private sealed class JobFixture : IDisposable
    {
        private readonly List<string> _pool = new();

        public string Root { get; }

        private sealed class Script
        {
            private readonly JobFixture _owner;
            private readonly List<(byte Type, int Value)> _tokens = new();

            public Script(JobFixture owner)
            {
                _owner = owner;
            }

            public Script Tag(string tag)
            {
                _tokens.Add(((byte)TokenType.Tag, _owner.Intern(tag)));
                return this;
            }

            public Script Str(string text)
            {
                _tokens.Add(((byte)TokenType.String, _owner.Intern(text)));
                return this;
            }

            public Script Int(params int[] values)
            {
                foreach (int v in values)
                {
                    _tokens.Add(((byte)TokenType.Int, v));
                }
                return this;
            }

            public byte[] Build() => TestData.BuildTokenScript(_tokens);
        }

        private int Intern(string text)
        {
            int at = _pool.IndexOf(text);
            if (at < 0)
            {
                _pool.Add(text);
                at = _pool.Count - 1;
            }
            return at;
        }

        private byte[] Chr(string bodyImage) => new Script(this).Tag("[body image path]").Str(bodyImage).Build();

        /// <summary>一件装备:<paramref name="blocks"/> 里每项是一个 [animation job] 块(职业标记、变体两个数、层键与层名)。</summary>
        private byte[] Equ(string type, params (string Job, int A, int B, (int Key, string Name)[] Layers)[] blocks)
        {
            Script s = new Script(this).Tag("[equipment type]").Str(type);
            foreach ((string job, int a, int b, (int Key, string Name)[] layers) in blocks)
            {
                s.Tag("[animation job]").Str(job).Tag("[variation]").Int(a, b);
                if (layers.Length > 0)
                {
                    s.Tag("[layer variation]");
                    foreach ((int key, string name) in layers)
                    {
                        s.Int(key).Str(name);
                    }
                }
            }
            return s.Build();
        }

        private static readonly (int Key, string Name)[] None = Array.Empty<(int, string)>();

        private static (int Key, string Name)[] One(int key, string name) => new[] { (key, name) };

        public JobFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-jobavatar-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            const string equ = "equipment/character/";
            var scripts = new List<(string Name, byte[] Data)>
            {
                // ---- 职业 1(格斗家):avatar/ 里有皮肤、头发、上衣(别的职业的块在前、本职业的块在后,两个图层)、鞋(只有别的职业的块)、帽子(本职业的块只有变体、没有图层)
                ("character/fighter/fighter.chr", Chr("Character/Fighter/Equipment/Avatar/skin/fi_body%04d.img")),
                (equ + "fighter/avatar/skin/askin_default.equ", Equ("[skin avatar]", ("[fighter]", 0, 5, None))),
                (equ + "fighter/avatar/hair/ahair_default.equ", Equ("[hair avatar]", ("[fighter]", 0, 0, One(2222, "hair_a")))),
                (equ + "fighter/avatar/coat/acoat_default.equ", Equ("[coat avatar]",
                    ("[other]", 9, 9, One(101, "coat_z")), ("[fighter]", 1, 5, new[] { (1111, "coat_a"), (700, "coat_b") }))),
                (equ + "fighter/avatar/shoes/ashoes_default.equ", Equ("[shoes avatar]", ("[other]", 1, 1, One(5, "shoes_a")))),
                (equ + "fighter/avatar/cap/ahat_default.equ", Equ("[hat avatar]", ("[fighter]", 3, 3, None))),

                // ---- 职业 7(格斗家的升级版 at fighter):脚本在,但格斗家目录里的装备没有它的块 → 没有默认外观
                ("character/fighter/atfighter.chr", Chr("Character/Fighter/Equipment/Avatar/skin/fi_body%04d.img")),

                // ---- 职业 2(枪手)与 5(at gunner)共用 gunner 目录:枪手的默认外观在 avatar/,at gunner 的在 at_avatar/(avatar/ 里没有它的块)
                ("character/gunner/gunner.chr", Chr("Character/Gunner/Equipment/Avatar/skin/gu_body%04d.img")),
                ("character/gunner/atgunner.chr", Chr("Character/Gunner/Equipment/Avatar/skin/gu_body%04d.img")),
                (equ + "gunner/avatar/hair/ahair_default.equ", Equ("[hair avatar]", ("[gunner]", 0, 0, One(2222, "hair_a")))),
                (equ + "gunner/at_avatar/skin/askin_default.equ", Equ("[skin avatar]", ("[at gunner]", 0, 7, None))),
                (equ + "gunner/at_avatar/hair/ahair_default.equ", Equ("[hair avatar]", ("[at gunner]", 0, 0, One(2222, "hair_a")))),
                (equ + "gunner/at_avatar/coat/acoat_default.equ", Equ("[coat avatar]", ("[at gunner]", 0, 0, One(1111, "coat_a")))),

                // ---- 职业 3(法师)与 8(at mage):两个子目录里都有;法师在 at_avatar/ 里图层更多 → 取 at_avatar/;at mage 两边一样多 → 取先试的 avatar/
                ("character/mage/mage.chr", Chr("Character/Mage/Equipment/Avatar/skin/mg_body%04d.img")),
                ("character/mage/atmage.chr", Chr("Character/Mage/Equipment/Avatar/skin/mg_body%04d.img")),
                (equ + "mage/avatar/hair/ahair_default.equ", Equ("[hair avatar]", ("[mage]", 0, 0, One(2222, "hair_a")), ("[at mage]", 0, 1, One(2222, "hair_a")))),
                (equ + "mage/at_avatar/hair/ahair_default.equ", Equ("[hair avatar]", ("[mage]", 0, 2, One(2222, "hair_a")), ("[at mage]", 0, 3, One(2222, "hair_a")))),
                (equ + "mage/at_avatar/coat/acoat_default.equ", Equ("[coat avatar]", ("[mage]", 0, 0, One(1111, "coat_a")))),

                // ---- 职业 4(牧师):脚本在,装备一件也没有 → 没有默认外观
                ("character/priest/priest.chr", Chr("Character/Priest/Equipment/Avatar/skin/pr_body%04d.img")),

                // ---- 职业 6(盗贼):没有角色脚本;职业 9、10 同样没有(用来验证越界之外的"脚本缺失")
            };

            var files = new List<TestData.PvfFileSpec>();
            var strings = new List<byte[]>();
            foreach (string s in _pool)
            {
                strings.Add(Encoding.ASCII.GetBytes(s));
            }
            files.Add(new TestData.PvfFileSpec("stringtable.bin", TestData.BuildStringTable(strings), 0));
            foreach ((string name, byte[] data) in scripts)
            {
                files.Add(new TestData.PvfFileSpec(name, data, 0));
            }
            (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);

            TestData.ImgFrameSpec dot = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, new byte[4]);
            File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_dummy.npk"),
                TestData.BuildNpk(new[] { ("sprite/dummy.img", TestData.BuildImg(new[] { dot })) }));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    // ------------------------------------------------------------------ 路径

    [Theory]
    [InlineData(0, "equipment/character/swordman")]
    [InlineData(1, "equipment/character/fighter")]
    [InlineData(2, "equipment/character/gunner")]
    [InlineData(3, "equipment/character/mage")]
    [InlineData(4, "equipment/character/priest")]
    [InlineData(5, "equipment/character/gunner")]
    [InlineData(6, "equipment/character/thief")]
    [InlineData(7, "equipment/character/fighter")]
    [InlineData(8, "equipment/character/mage")]
    [InlineData(9, "equipment/character/swordman")]
    [InlineData(10, "equipment/character/mage")]
    public void TheEquipmentRootIsTheFolderOfTheJobsCharacterScript(int job, string expected)
    {
        Assert.Equal(expected, JobOutfits.EquipRootOf(job));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(200)]
    public void NoJobNumberOutsideTheElevenHasAnEquipmentRoot(int job)
    {
        Assert.Null(JobOutfits.EquipRootOf(job));
    }

    // ------------------------------------------------------------------ 职业布局

    [Fact]
    public void TheClassLayoutComesFromTheJobsCharacterScript()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = JobOutfits.ClassOf(assets, 1);
        Assert.Equal("[fighter]", cls.JobTag);
        Assert.Equal("sprite/character/fighter/equipment/avatar", cls.AvatarRoot);
        Assert.Equal("fi_", cls.FilePrefix);
        Assert.Equal("[at gunner]", JobOutfits.ClassOf(assets, 5).JobTag);
    }

    [Fact]
    public void AMissingCharacterScriptOrAJobOutOfRangeIsAFormatError()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Throws<DnfFormatException>(() => JobOutfits.ClassOf(assets, 6));          // 盗贼没有脚本
        Assert.Throws<DnfFormatException>(() => JobOutfits.ClassOf(assets, 11));
        Assert.Throws<DnfFormatException>(() => JobOutfits.ClassOf(assets, -1));
    }

    // ------------------------------------------------------------------ 默认外观

    [Fact]
    public void TheBaseOutfitIsTheSkinPlusTheDefaultPartsThatReallyHaveLayersForThisJob()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = JobOutfits.CreateBase(assets, 1);

        Assert.Equal(5, outfit.SkinNumber);                                              // 皮肤取 askin_default 的变体编号(0 × 100 + 5)
        Assert.Equal(new[] { "[hair avatar]", "[coat avatar]" }, outfit.Items.Select(i => i.EquipmentType));   // 鞋(只有别的职业的块)与帽子(没有图层)没穿
        // 图层按顺序键从小到大:皮肤(650)、上衣的 coat_b(700)、coat_a(1111)、头发(2222);图片编号 = 变体 A × 100 + B
        Assert.Equal(
            new[]
            {
                "sprite/character/fighter/equipment/avatar/skin/fi_body0005.img",
                "sprite/character/fighter/equipment/avatar/coat/fi_coat0105b.img",
                "sprite/character/fighter/equipment/avatar/coat/fi_coat0105a.img",
                "sprite/character/fighter/equipment/avatar/hair/fi_hair0000a.img",
            },
            outfit.Layers.Select(l => l.ImagePath));
        Assert.Equal(new[] { 650, 700, 1111, 2222 }, outfit.Layers.Select(l => l.Key));
        Assert.Equal("[fighter]", outfit.Class.JobTag);
    }

    [Fact]
    public void TheOtherJobsBlockInTheSameFileIsNotWorn()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarOutfit outfit = JobOutfits.CreateBase(assets, 1);
        Assert.DoesNotContain(outfit.Layers, l => l.Name == "coat_z");
        Assert.DoesNotContain(outfit.Layers, l => l.Name == "shoes_a");
    }

    [Fact]
    public void AtGunnerTakesItsDefaultLookFromAtAvatarBecauseAvatarHasNoLayersForIt()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        AvatarOutfit atGunner = JobOutfits.CreateBase(assets, 5);
        Assert.Equal(7, atGunner.SkinNumber);
        Assert.Equal(new[] { "[hair avatar]", "[coat avatar]" }, atGunner.Items.Select(i => i.EquipmentType));
        Assert.All(atGunner.Items, i => Assert.Contains("/at_avatar/", i.EquPath));

        AvatarOutfit gunner = JobOutfits.CreateBase(assets, 2);                          // 枪手自己的在 avatar/
        Assert.Equal(0, gunner.SkinNumber);                                              // 没有皮肤件:0 号皮肤
        Assert.Single(gunner.Items);
        Assert.Contains("/avatar/hair/", gunner.Items[0].EquPath);
        Assert.DoesNotContain("at_avatar", gunner.Items[0].EquPath);
    }

    [Fact]
    public void WhenBothSubFoldersHaveLayersTheOneWithMoreWinsAndATieGoesToAvatar()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        AvatarOutfit mage = JobOutfits.CreateBase(assets, 3);                            // avatar/ 一个部件、at_avatar/ 两个
        Assert.Equal(2, mage.Items.Count);
        Assert.All(mage.Items, i => Assert.Contains("/at_avatar/", i.EquPath));

        AvatarOutfit atMage = JobOutfits.CreateBase(assets, 8);                          // 两边各一个部件:平手取先试的 avatar/
        Assert.Single(atMage.Items);
        Assert.DoesNotContain("at_avatar", atMage.Items[0].EquPath);
        Assert.Equal("sprite/character/mage/equipment/avatar/hair/mg_hair0001a.img", atMage.Layers.Single(l => l.Name == "hair_a").ImagePath);   // at mage 的块变体是 (0, 1)
    }

    [Theory]
    [InlineData(4)]                                                                      // 脚本在,装备一件也没有
    [InlineData(7)]                                                                      // 装备文件在,但没有这个职业的块
    public void AJobWithoutAnyDefaultLayersHasNoBaseOutfit(int job)
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        DnfFormatException e = Assert.Throws<DnfFormatException>(() => JobOutfits.CreateBase(assets, job));
        Assert.Contains("没有可用的默认外观", e.Message);
    }

    [Fact]
    public void AJobWithoutAScriptOrOutOfRangeHasNoBaseOutfit()
    {
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Throws<DnfFormatException>(() => JobOutfits.CreateBase(assets, 6));
        Assert.Throws<DnfFormatException>(() => JobOutfits.CreateBase(assets, 99));
        Assert.Throws<ArgumentNullException>(() => JobOutfits.CreateBase(null, 1));
    }

    // ------------------------------------------------------------------ 头像来源按职业

    private static AvatarOutfit MiniOutfit(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        return AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
    }

    private static CharacterSlot SlotOfJob(int job)
    {
        return OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", job: (byte)job), 0);
    }

    [Fact]
    public void EachJobGetsItsOwnAvatarSourceBuiltOnceFromItsOwnCharacterScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var made = new List<(int Job, string Chr)>();
        var avatars = new DnfJobAvatars(assets, (job, chr) =>
        {
            made.Add((job, chr));
            return new DnfCharacterAvatars(assets, TownHero.TownRestTag, AvatarFixture.ChrPath, MiniOutfit);
        });

        Assert.NotEmpty(avatars.Pose(SlotOfJob(1), 0, 100, 200));
        Assert.NotEmpty(avatars.Pose(SlotOfJob(1), 50, 100, 200));
        Assert.NotEmpty(avatars.Pose(SlotOfJob(5), 0, 100, 200));
        Assert.NotEmpty(avatars.Pose(SlotOfJob(1), 99, 100, 200));
        Assert.Equal(new[] { (1, "character/fighter/fighter.chr"), (5, "character/gunner/atgunner.chr") }, made);
        Assert.Null(avatars.Failure);
    }

    [Fact]
    public void ACharacterWithoutOnlineInformationIsDrawnAsTheSwordman()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var made = new List<int>();
        var avatars = new DnfJobAvatars(assets, (job, chr) =>
        {
            made.Add(job);
            return new DnfCharacterAvatars(assets, TownHero.TownRestTag, AvatarFixture.ChrPath, MiniOutfit);
        });
        Assert.NotEmpty(avatars.Pose(new CharacterSlot("名", "职", 1), 0, 10, 10));
        Assert.NotEmpty(avatars.Pose(null, 0, 10, 10));
        Assert.Equal(new[] { 0 }, made);
    }

    [Fact]
    public void AJobNumberOutsideTheElevenDrawsNothingAndNeverBorrowsAnotherJobsFigure()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var made = new List<int>();
        var avatars = new DnfJobAvatars(assets, (job, chr) =>
        {
            made.Add(job);
            return new DnfCharacterAvatars(assets, TownHero.TownRestTag, AvatarFixture.ChrPath, MiniOutfit);
        });

        Assert.Empty(avatars.Pose(SlotOfJob(11), 0, 10, 10));
        Assert.Empty(avatars.Pose(SlotOfJob(255), 0, 10, 10));
        Assert.Empty(made);                                                              // 越界的职业不造任何来源
        Assert.StartsWith("job 255:", avatars.Failure);
        Assert.NotEmpty(avatars.Pose(SlotOfJob(2), 0, 10, 10));                           // 之后合法的职业照常
    }

    [Fact]
    public void AJobWhoseFigureCannotBeReadIsEmptyAndTheReasonCarriesTheJobButNoCharacterInfo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var avatars = new DnfJobAvatars(assets, (job, chr) => new DnfCharacterAvatars(assets, "[no such motion]", AvatarFixture.ChrPath, MiniOutfit));

        Assert.Empty(avatars.Pose(SlotOfJob(3), 0, 10, 10));
        Assert.StartsWith("job 3:", avatars.Failure);
        Assert.Contains("[no such motion]", avatars.Failure);
        Assert.DoesNotContain("名", avatars.Failure);                                    // 名字不进原因
        string first = avatars.Failure;
        Assert.Empty(avatars.Pose(SlotOfJob(3), 100, 10, 10));
        Assert.Same(first, avatars.Failure);                                              // 同一个职业只报一次,不每帧重拼字符串
    }

    [Fact]
    public void TheDefaultFactoryReadsTheBaseOutfitFromTheGameData()
    {
        // 默认工厂:职业没有脚本时(合成图包里没有盗贼)读不出形象,原因里有职业号
        using var fx = new JobFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var avatars = new DnfJobAvatars(assets);
        Assert.Empty(avatars.Pose(SlotOfJob(6), 0, 10, 10));
        Assert.StartsWith("job 6:", avatars.Failure);
        Assert.Throws<ArgumentNullException>(() => new DnfJobAvatars(null));
    }
}

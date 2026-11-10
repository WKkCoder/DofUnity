using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 装扮外观(r14-avatar)的合成夹具:一个"迷你职业"([tester]),皮肤、六件默认外观(头发、上衣、裤子、鞋、帽子)、两把武器,
/// 加十一件可穿的装扮和七件穿不出来的装扮。每个图层的图片都是一块纯色矩形,位置和颜色各不相同,所以"哪一层画在哪里、谁压着谁"能逐像素读出来。
/// 坐标是相对原点的(画布 80×80,原点在 (40, 60));顺序键、颜色、尺寸全是随手取的合成数,与原版数据无关。
/// </summary>
internal sealed class WardrobeFixture : IDisposable
{
    public const string ChrPath = "character/wardtest/wardrobe.chr";
    public const string JobTag = "[tester]";
    public const string EquipRoot = "equipment/character/wardtest";
    public const string AvatarImageRoot = "sprite/character/wardtest/equipment/avatar";
    public const string WeaponImageRoot = "sprite/character/wardtest/equipment/weapon";

    public const int OriginX = 40;
    public const int OriginY = 60;
    public const int CanvasWidth = 80;
    public const int CanvasHeight = 80;

    /// <summary>画布的底色(没有任何图层的地方):黑,没有任何图层用黑色。</summary>
    public static readonly (int R, int G, int B) Background = (0, 0, 0);

    /// <summary>相对原点的矩形:x、y 起点与宽高。</summary>
    public readonly record struct Box(int X, int Y, int W, int H)
    {
        public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
    }

    /// <summary>一块纯色的图片:路径、相对原点的位置与大小、颜色;<paramref name="InNpk"/> 为假表示这张图故意不放进图包(缺图)。</summary>
    public sealed record Art(string Path, int X, int Y, int W, int H, (int R, int G, int B) Color, bool InNpk = true)
    {
        public Box Box => new(X, Y, W, H);
    }

    /// <summary>装备脚本里的一行图层:顺序键、层名、它对应的图片。</summary>
    public sealed record Layer(int Key, string Name, Art Art);

    public enum JobBlock
    {
        /// <summary>有本职业的 [animation job] 块。</summary>
        Own,

        /// <summary>只有别的职业的块。</summary>
        Other,

        /// <summary>没有任何 [animation job] 块。</summary>
        None,
    }

    public sealed class Equ
    {
        public string Path { get; init; }
        public string Type { get; init; }
        public JobBlock Block { get; init; } = JobBlock.Own;
        public bool HasVariation { get; init; } = true;
        public int A { get; init; }
        public int B { get; init; }
        public Layer[] Layers { get; init; } = Array.Empty<Layer>();

        /// <summary>在 PVF 里的完整路径(小写)。</summary>
        public string FullPath => EquipRoot + "/" + Path;

        /// <summary>这件装备所有"真的有图"的图层的矩形。</summary>
        public IEnumerable<Box> Boxes => Layers.Where(l => l.Art.InNpk).Select(l => l.Art.Box);
    }

    // ---------------------------------------------------------------- 图片

    private static Art PartArt(string dir, string file, int x, int y, int w, int h, (int, int, int) color, bool inNpk = true)
    {
        return new Art(AvatarImageRoot + "/" + dir + "/" + file, x, y, w, h, color, inNpk);
    }

    private static Art BladeArt(string file, int x, int y, int w, int h, (int, int, int) color)
    {
        return new Art(WeaponImageRoot + "/blade/" + file, x, y, w, h, color);
    }

    public static readonly Art Skin0 = PartArt("skin", "wd_body0005.img", -10, -40, 20, 40, (180, 140, 120));
    public static readonly Art Skin1 = PartArt("skin", "wd_body0102.img", -10, -40, 20, 40, (90, 60, 40));
    public static readonly Art SkinGone = PartArt("skin", "wd_body0505.img", -10, -40, 20, 40, (1, 2, 3), inNpk: false);

    // 0 号皮肤图(在图包里):没写 variation 的皮肤件编号读出来是 0,指向的就是它——只有 HasVariation 这一关能把那种件挡住
    public static readonly Art SkinZero = PartArt("skin", "wd_body0000.img", -10, -40, 20, 40, (7, 7, 7));

    // 默认外观
    public static readonly Art HairDefaultArt = PartArt("hair", "wd_hair0000a.img", -8, -46, 16, 10, (200, 200, 10));
    public static readonly Art CapDefaultArt = PartArt("cap", "wd_cap0000a.img", -9, -50, 18, 8, (10, 200, 200));
    public static readonly Art CoatDefaultArt = PartArt("coat", "wd_coat0000a.img", -12, -34, 24, 16, (10, 200, 10));
    public static readonly Art PantsDefaultArt = PartArt("pants", "wd_pants0000a.img", -10, -18, 20, 14, (10, 10, 200));
    public static readonly Art ShoesDefaultArt = PartArt("shoes", "wd_shoes0000a.img", -12, -6, 24, 6, (120, 60, 10));
    public static readonly Art BladeStartArt = BladeArt("sword0002b.img", 14, -40, 6, 36, (250, 250, 250));
    public static readonly Art BladeOtherArt = BladeArt("sword0003b.img", 16, -44, 6, 40, (90, 90, 90));

    // 装扮
    public static readonly Art HatArt = PartArt("cap", "wd_cap0101a.img", -10, -52, 20, 10, (60, 250, 250));
    public static readonly Art HairFrontArt = PartArt("hair", "wd_hair0003a.img", -7, -47, 14, 12, (250, 150, 10));
    public static readonly Art HairBackArt = PartArt("hair", "wd_hair0003b.img", -12, -44, 24, 22, (140, 80, 10));
    public static readonly Art FaceArt = PartArt("face", "wd_face0002a.img", -4, -40, 8, 6, (250, 10, 10));
    public static readonly Art CoatOneFrontArt = PartArt("coat", "wd_coat0105a.img", -12, -34, 24, 10, (250, 60, 250));
    public static readonly Art CoatOneBackArt = PartArt("coat", "wd_coat0105b.img", -14, -36, 28, 8, (150, 20, 150));
    public static readonly Art CoatTwoArt = PartArt("coat", "wd_coat0206a.img", -12, -34, 24, 12, (250, 250, 60));
    public static readonly Art PantsArt = PartArt("pants", "wd_pants0207a.img", -10, -18, 20, 8, (30, 120, 250));
    public static readonly Art ShoesArt = PartArt("shoes", "wd_shoes0308a.img", -12, -8, 24, 8, (250, 120, 30));
    public static readonly Art NeckBackArt = PartArt("neck", "wd_neck0109a.img", -14, -36, 28, 6, (60, 60, 250));
    public static readonly Art NeckFrontArt = PartArt("neck", "wd_neck0109b.img", 12, -30, 10, 6, (250, 60, 60));
    public static readonly Art BeltArt = PartArt("belt", "wd_belt0110a.img", -11, -20, 22, 3, (240, 200, 100));
    public static readonly Art BeltExtraArt = PartArt("belt", "wd_belt0110b.img", -3, -38, 6, 6, (100, 240, 200));
    public static readonly Art PantsTieArt = PartArt("pants", "wd_pants0411a.img", -10, -12, 20, 12, (30, 250, 120));

    // 缺图的(故意不放进图包)
    public static readonly Art CoatGoneArt = PartArt("coat", "wd_coat0303a.img", -12, -34, 24, 16, (9, 9, 9), inNpk: false);
    public static readonly Art CoatHalfGoneArt = PartArt("coat", "wd_coat0105c.img", -12, -34, 24, 16, (9, 9, 9), inNpk: false);

    // ---------------------------------------------------------------- 装备脚本

    private static Layer L(int key, string name, Art art) => new(key, name, art);

    // 默认外观:变体 (0, 0);皮肤故意取 (0, 5),基础皮肤编号不是 0
    public static readonly Equ SkinDefault = new() { Path = "avatar/skin/askin_default.equ", Type = "[skin avatar]", A = 0, B = 5 };
    public static readonly Equ HairDefault = new() { Path = "avatar/hair/ahair_default.equ", Type = "[hair avatar]", Layers = new[] { L(2222, "hair_a", HairDefaultArt) } };
    public static readonly Equ CoatDefault = new() { Path = "avatar/coat/acoat_default.equ", Type = "[coat avatar]", Layers = new[] { L(1111, "coat_a", CoatDefaultArt) } };
    public static readonly Equ PantsDefault = new() { Path = "avatar/pants/apants_default.equ", Type = "[pants avatar]", Layers = new[] { L(1313, "pants_a", PantsDefaultArt) } };
    public static readonly Equ ShoesDefault = new() { Path = "avatar/shoes/ashoes_default.equ", Type = "[shoes avatar]", Layers = new[] { L(1414, "shoes_a", ShoesDefaultArt) } };
    public static readonly Equ CapDefault = new() { Path = "avatar/cap/ahat_default.equ", Type = "[hat avatar]", Layers = new[] { L(2400, "cap_a", CapDefaultArt) } };

    // 两把武器(变体第二个数进文件名)
    public static readonly Equ WeaponStart = new() { Path = "weapon/sword/sw_start.equ", Type = "[weapon]", A = 3, B = 2, Layers = new[] { L(3000, "swordb", BladeStartArt) } };
    public static readonly Equ WeaponOther = new() { Path = "weapon/sword/sw_other.equ", Type = "[weapon]", A = 3, B = 3, Layers = new[] { L(3000, "swordb", BladeOtherArt) } };

    // 可穿的装扮,每个部位至少一件
    public static readonly Equ Hat = new() { Path = "avatar/cap/wcap.equ", Type = "[hat avatar]", A = 1, B = 1, Layers = new[] { L(2410, "cap_a", HatArt) } };
    public static readonly Equ Hair = new()
    {
        Path = "avatar/hair/whair.equ", Type = "[hair avatar]", A = 0, B = 3,
        Layers = new[] { L(2200, "hair_a", HairFrontArt), L(100, "hair_b", HairBackArt) },
    };
    public static readonly Equ Face = new() { Path = "avatar/face/wface.equ", Type = "[face avatar]", A = 0, B = 2, Layers = new[] { L(2300, "face_a", FaceArt) } };
    public static readonly Equ CoatOne = new()
    {
        Path = "avatar/coat/wcoat1.equ", Type = "[coat avatar]", A = 1, B = 5,
        Layers = new[] { L(1110, "coat_a", CoatOneFrontArt), L(600, "coat_b", CoatOneBackArt) },
    };
    public static readonly Equ CoatTwo = new() { Path = "avatar/coat/wcoat2.equ", Type = "[coat avatar]", A = 2, B = 6, Layers = new[] { L(1112, "coat_a", CoatTwoArt) } };
    public static readonly Equ Pants = new() { Path = "avatar/pants/wpants.equ", Type = "[pants avatar]", A = 2, B = 7, Layers = new[] { L(1300, "pants_a", PantsArt) } };
    public static readonly Equ Shoes = new() { Path = "avatar/shoes/wshoes.equ", Type = "[shoes avatar]", A = 3, B = 8, Layers = new[] { L(1400, "shoes_a", ShoesArt) } };

    /// <summary>裤子,顺序键 1414 与默认鞋的相同(并且矩形与鞋重叠):用来看"换上的装扮留在被换掉的那件的位置上"。</summary>
    public static readonly Equ PantsTie = new() { Path = "avatar/pants/wpants_tie.equ", Type = "[pants avatar]", A = 4, B = 11, Layers = new[] { L(1414, "pants_a", PantsTieArt) } };
    public static readonly Equ Neck = new()
    {
        Path = "avatar/neck/wneck.equ", Type = "[breast avatar]", A = 1, B = 9,
        Layers = new[] { L(300, "neck_a", NeckBackArt), L(3000, "neck_b", NeckFrontArt) },
    };
    public static readonly Equ Belt = new()
    {
        Path = "avatar/belt/wbelt.equ", Type = "[waist avatar]", A = 1, B = 10,
        Layers = new[] { L(1500, "belt_a", BeltArt), L(2300, "belt_b", BeltExtraArt) },
    };
    public static readonly Equ SkinWorn = new() { Path = "avatar/skin/wskin.equ", Type = "[skin avatar]", A = 1, B = 2 };

    // 穿不出来的装扮
    /// <summary>只有别的职业的块:本职业读不到 [variation],没有图层。</summary>
    public static readonly Equ HatOtherJob = new()
    {
        Path = "avatar/cap/wcap_other.equ", Type = "[hat avatar]", Block = JobBlock.Other, A = 1, B = 2,
        Layers = new[] { L(2410, "cap_a", HatArt) },
    };

    /// <summary>灵气:没有任何 [animation job] 块。</summary>
    public static readonly Equ Aurora = new() { Path = "avatar/aurora/waura.equ", Type = "[aurora avatar]", Block = JobBlock.None, HasVariation = false };

    /// <summary>上衣的图片不在图包里。</summary>
    public static readonly Equ CoatNoImage = new() { Path = "avatar/coat/wcoat_noimg.equ", Type = "[coat avatar]", A = 3, B = 3, Layers = new[] { L(1111, "coat_a", CoatGoneArt) } };

    /// <summary>三层里缺一层(coat_c)。</summary>
    public static readonly Equ CoatHalfImage = new()
    {
        Path = "avatar/coat/wcoat_half.equ", Type = "[coat avatar]", A = 1, B = 5,
        Layers = new[] { L(1110, "coat_a", CoatOneFrontArt), L(600, "coat_b", CoatOneBackArt), L(1115, "coat_c", CoatHalfGoneArt) },
    };

    /// <summary>有本职业的块与 [variation],但没有 [layer variation]。</summary>
    public static readonly Equ BeltNoLayer = new() { Path = "avatar/belt/wbelt_nolayer.equ", Type = "[waist avatar]", A = 1, B = 1 };

    /// <summary>皮肤:有本职业的块,但没有 [variation]。</summary>
    public static readonly Equ SkinNoVariation = new() { Path = "avatar/skin/wskin_novar.equ", Type = "[skin avatar]", HasVariation = false };

    /// <summary>皮肤:指向的皮肤图片不在图包里。</summary>
    public static readonly Equ SkinNoImage = new() { Path = "avatar/skin/wskin_noimg.equ", Type = "[skin avatar]", A = 5, B = 5 };

    /// <summary>写进 PVF 的全部装备脚本。</summary>
    public static readonly IReadOnlyList<Equ> AllEqu = new[]
    {
        SkinDefault, HairDefault, CoatDefault, PantsDefault, ShoesDefault, CapDefault, WeaponStart, WeaponOther,
        Hat, Hair, Face, CoatOne, CoatTwo, Pants, Shoes, PantsTie, Neck, Belt, SkinWorn,
        HatOtherJob, Aurora, CoatNoImage, CoatHalfImage, BeltNoLayer, SkinNoVariation, SkinNoImage,
    };

    /// <summary>装扮部位 0 到 9 各一件可穿的装扮(灵气没有,放 null)与基础外观里同部位的脚本,"每个部位只动自己的区域"的测试用。</summary>
    public static (Equ Worn, Equ[] BaseEqu) PartCase(int part)
    {
        return part switch
        {
            0 => (Hat, new[] { CapDefault }),
            1 => (Hair, new[] { HairDefault }),
            2 => (Face, Array.Empty<Equ>()),
            3 => (CoatOne, new[] { CoatDefault }),
            4 => (Pants, new[] { PantsDefault }),
            5 => (Shoes, new[] { ShoesDefault }),
            6 => (Neck, Array.Empty<Equ>()),
            7 => (Belt, Array.Empty<Equ>()),
            8 => (SkinWorn, Array.Empty<Equ>()),
            _ => (null, Array.Empty<Equ>()),
        };
    }

    // ---------------------------------------------------------------- 造数据

    private const byte TagType = (byte)TokenType.Tag;
    private const byte IntType = (byte)TokenType.Int;
    private const byte StrType = (byte)TokenType.String;

    private readonly List<string> _pool = new();

    public string Root { get; }

    private sealed class Script
    {
        private readonly WardrobeFixture _owner;
        private readonly List<(byte Type, int Value)> _tokens = new();

        public Script(WardrobeFixture owner)
        {
            _owner = owner;
        }

        public Script Tag(string tag)
        {
            _tokens.Add((TagType, _owner.Intern(tag)));
            return this;
        }

        public Script Str(params string[] texts)
        {
            foreach (string text in texts)
            {
                _tokens.Add((StrType, _owner.Intern(text)));
            }
            return this;
        }

        public Script Int(params int[] values)
        {
            foreach (int v in values)
            {
                _tokens.Add((IntType, v));
            }
            return this;
        }

        public byte[] Build()
        {
            return TestData.BuildTokenScript(_tokens);
        }
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

    private byte[] BuildEqu(Equ e)
    {
        var s = new Script(this).Tag("[equipment type]").Str(e.Type);
        if (e.Block == JobBlock.None)
        {
            return s.Build();
        }
        s.Tag("[animation job]").Str(e.Block == JobBlock.Own ? JobTag : "[other]");
        if (e.HasVariation)
        {
            s.Tag("[variation]").Int(e.A, e.B);
        }
        if (e.Layers.Length > 0)
        {
            s.Tag("[layer variation]");
            foreach (Layer layer in e.Layers)
            {
                s.Int(layer.Key).Str(layer.Name);
            }
        }
        return s.Build();
    }

    /// <summary>一块纯色的 IMG:一帧,不透明,帧头的位置与大小就是 <paramref name="art"/> 的矩形。</summary>
    private static byte[] BuildImg(Art art)
    {
        var bgra = new byte[art.W * art.H * 4];
        for (int i = 0; i < art.W * art.H; i++)
        {
            bgra[i * 4] = (byte)art.Color.B;
            bgra[i * 4 + 1] = (byte)art.Color.G;
            bgra[i * 4 + 2] = (byte)art.Color.R;
            bgra[i * 4 + 3] = 255;
        }
        TestData.ImgFrameSpec f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, art.W, art.H, bgra);
        f.X = art.X;
        f.Y = art.Y;
        f.FrameWidth = art.W;
        f.FrameHeight = art.H;
        return TestData.BuildImg(new[] { f });
    }

    public WardrobeFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-wardrobe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var scripts = new List<(string Name, byte[] Data)>
        {
            (ChrPath, new Script(this).Tag("[body image path]").Str("Character/Wardtest/Equipment/Avatar/skin/wd_body%04d.img").Build()),
        };
        foreach (Equ e in AllEqu)
        {
            scripts.Add((e.FullPath, BuildEqu(e)));
        }

        var files = new List<TestData.PvfFileSpec>();
        // 先把所有脚本里的字符串都登记好,再生成字符串表(标记流里存的是字符串表的序号)
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

        // 每张图只放一次(同一张图可以被几件装备引用);缺图的不放
        var images = new List<(string Name, byte[] Data)>();
        var seen = new HashSet<string>();
        foreach (Equ e in AllEqu)
        {
            foreach (Layer layer in e.Layers)
            {
                if (layer.Art.InNpk && seen.Add(layer.Art.Path))
                {
                    images.Add((layer.Art.Path, BuildImg(layer.Art)));
                }
            }
        }
        foreach (Art skin in new[] { Skin0, Skin1, SkinZero })
        {
            images.Add((skin.Path, BuildImg(skin)));
        }
        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_wardrobe.npk"), TestData.BuildNpk(images));
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

/// <summary>一次测试用的整套东西:夹具、打开的资源、职业布局、基础外观、一个空衣柜,外加"读装备"和"拍一张画面"。</summary>
internal sealed class WardrobeRig : IDisposable
{
    public WardrobeFixture Fixture { get; }
    public DnfAssets Assets { get; }
    public AvatarClass Class { get; }
    public AvatarOutfit Base { get; }
    public AvatarWardrobe Wardrobe { get; }

    public WardrobeRig()
    {
        Fixture = new WardrobeFixture();
        Assets = DnfAssets.Open(Fixture.Root);
        Class = AvatarClass.FromChr(Assets, WardrobeFixture.ChrPath, WardrobeFixture.JobTag);
        Base = AvatarOutfit.CreateDefault(Assets, Class, WardrobeFixture.EquipRoot, WardrobeFixture.WeaponStart.Path);
        Wardrobe = new AvatarWardrobe(Assets, Base);
    }

    public AvatarItem Read(WardrobeFixture.Equ equ)
    {
        return AvatarItem.Read(Assets, Class, equ.FullPath);
    }

    /// <summary>把一套穿戴按图层顺序画在 80×80 的黑底画布上(每层取 IMG 的第 0 帧,原点在 (40, 60))。</summary>
    public RgbaCanvas Shot(AvatarOutfit outfit)
    {
        var canvas = new RgbaCanvas(WardrobeFixture.CanvasWidth, WardrobeFixture.CanvasHeight);
        canvas.Clear((byte)WardrobeFixture.Background.R, (byte)WardrobeFixture.Background.G, (byte)WardrobeFixture.Background.B, 255);
        foreach (AvatarLayer layer in outfit.Layers)
        {
            if (Assets.TryGetFrame(layer.ImagePath, 0, out SpriteFrame sprite) && !sprite.IsBlank)
            {
                canvas.Draw(sprite, WardrobeFixture.OriginX + sprite.OffsetX, WardrobeFixture.OriginY + sprite.OffsetY);
            }
        }
        return canvas;
    }

    /// <summary>现在的外观的画面。</summary>
    public RgbaCanvas Shot() => Shot(Wardrobe.Current);

    /// <summary>画面上相对原点 (x, y) 处的颜色。</summary>
    public static (int R, int G, int B) At(RgbaCanvas canvas, int x, int y)
    {
        int cx = WardrobeFixture.OriginX + x;
        int cy = WardrobeFixture.OriginY + y;
        int o = (cy * canvas.Width + cx) * 4;
        return (canvas.Pixels[o], canvas.Pixels[o + 1], canvas.Pixels[o + 2]);
    }

    /// <summary>两张画面不同的像素(相对原点的坐标)。</summary>
    public static List<(int X, int Y)> Diff(RgbaCanvas a, RgbaCanvas b)
    {
        var diff = new List<(int X, int Y)>();
        for (int cy = 0; cy < a.Height; cy++)
        {
            for (int cx = 0; cx < a.Width; cx++)
            {
                int o = (cy * a.Width + cx) * 4;
                if (a.Pixels[o] != b.Pixels[o] || a.Pixels[o + 1] != b.Pixels[o + 1] || a.Pixels[o + 2] != b.Pixels[o + 2])
                {
                    diff.Add((cx - WardrobeFixture.OriginX, cy - WardrobeFixture.OriginY));
                }
            }
        }
        return diff;
    }

    /// <summary>画面上有没有这种颜色的像素。</summary>
    public static bool HasColor(RgbaCanvas canvas, (int R, int G, int B) color)
    {
        for (int o = 0; o < canvas.Pixels.Length; o += 4)
        {
            if (canvas.Pixels[o] == color.R && canvas.Pixels[o + 1] == color.G && canvas.Pixels[o + 2] == color.B)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>外观里各图层的层名,按绘制顺序、空格隔开。</summary>
    public static string Names(AvatarOutfit outfit) => string.Join(" ", outfit.Layers.Select(l => l.Name));

    public void Dispose()
    {
        Assets.Dispose();
        Fixture.Dispose();
    }
}

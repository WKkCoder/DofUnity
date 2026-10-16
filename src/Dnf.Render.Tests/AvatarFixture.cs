using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 在临时目录里造一个"迷你职业":一份角色脚本、几件装备脚本、站立与行走两个 .ani、几张 2×2 的 IMG 和一张带可走区域的地图。
/// 全部由合成数据按规格构造(规格与命名规则见证据 E-065),不含任何原版内容。
/// </summary>
internal sealed class AvatarFixture : IDisposable
{
    public const string ChrPath = "character/test/test.chr";
    public const string JobTag = "[tester]";
    public const string EquipRoot = "equipment/character/test";
    public const string WeaponEqu = "weapon/sword/sw_start.equ";
    public const string AvatarImageRoot = "sprite/character/test/equipment/avatar";
    public const string WeaponImageRoot = "sprite/character/test/equipment/weapon";
    public const string AreaMap = "map/test/area.map";
    public const string RestAni = "character/test/animation/rest.ani";
    public const string MoveAni = "character/test/animation/move.ani";
    public const string WaitAni = "character/test/animation/wait.ani";

    /// <summary>每个 IMG 的第 k 帧,红色通道 = 基色 + k,用来判断精灵取的是哪个 IMG 帧。</summary>
    public static readonly (int R, int G, int B) SkinColor = (200, 10, 10);
    public static readonly (int R, int G, int B) CoatAColor = (10, 200, 10);
    public static readonly (int R, int G, int B) CoatBColor = (10, 10, 200);
    public static readonly (int R, int G, int B) HairColor = (200, 200, 10);
    public static readonly (int R, int G, int B) SwordBColor = (60, 60, 60);
    public static readonly (int R, int G, int B) SwordCColor = (250, 250, 250);

    private const byte TagType = (byte)TokenType.Tag;
    private const byte IntType = (byte)TokenType.Int;
    private const byte StrType = (byte)TokenType.String;

    private readonly List<string> _pool = new();

    public string Root { get; }

    /// <summary>按"标记 / 字符串 / 整数"一个个拼出标记流脚本。</summary>
    private sealed class Script
    {
        private readonly AvatarFixture _owner;
        private readonly List<(byte Type, int Value)> _tokens = new();

        public Script(AvatarFixture owner)
        {
            _owner = owner;
        }

        public Script Tag(string tag)
        {
            _tokens.Add((TagType, _owner.Intern(tag)));
            return this;
        }

        public Script Str(string text)
        {
            _tokens.Add((StrType, _owner.Intern(text)));
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

    private Script NewScript()
    {
        return new Script(this);
    }

    public AvatarFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-avatar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var scripts = new List<(string Name, byte[] Data)>
        {
            (ChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani").Build()),
            (EquipRoot + "/avatar/skin/askin_default.equ", NewScript()
                .Tag("[equipment type]").Str("[skin avatar]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(0, 0).Build()),
            // 本职业的块在前,别的职业的块在后:读到下一个 [animation job] 必须停
            (EquipRoot + "/avatar/hair/ahair_default.equ", NewScript()
                .Tag("[equipment type]").Str("[hair avatar]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(0, 0)
                .Tag("[layer variation]").Int(2222).Str("hair_a")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(7, 7)
                .Tag("[layer variation]").Int(102).Str("hair_q").Build()),
            // 别的职业的块在前,本职业的块在后:要跳过前一个块;一件装备可以有多个图层
            (EquipRoot + "/avatar/coat/acoat_default.equ", NewScript()
                .Tag("[equipment type]").Str("[coat avatar]")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(9, 9)
                .Tag("[layer variation]").Int(101).Str("coat_z")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(1, 5)
                .Tag("[layer variation]").Int(1111).Str("coat_a").Int(700).Str("coat_b").Build()),
            // 武器:变体两个数里只有第二个(B = 2)进文件名;swordd 的图片不存在
            (EquipRoot + "/" + WeaponEqu, NewScript()
                .Tag("[equipment type]").Str("[weapon]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(3, 2)
                .Tag("[layer variation]").Int(650).Str("swordb").Int(2999).Str("swordc").Int(3000).Str("swordd").Build()),
            (EquipRoot + "/weapon/sword/sw_digit.equ", NewScript()
                .Tag("[equipment type]").Str("[weapon]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(3, 2)
                .Tag("[layer variation]").Int(654).Str("swordb1").Build()),
            // 本职业没有块的装备
            (EquipRoot + "/avatar/cap/ahat_other.equ", NewScript()
                .Tag("[equipment type]").Str("[cap avatar]")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(1, 1)
                .Tag("[layer variation]").Int(2150).Str("cap_a").Build()),
            (RestAni, BuildAni(new[] { (0, 100), (1, 300) })),
            (MoveAni, BuildAni(new[] { (0, 100), (1, 100), (0, 100) })),
            (WaitAni, BuildAni(new[] { (1, 100), (0, 100) })),
            // 可走区域:两个矩形 (10,20,100,50) 与 (100,60,30,40),拼成一个 L 形
            (AreaMap, NewScript().Tag("[virtual movable area]").Int(10, 20, 100, 50, 100, 60, 30, 40).Build()),
            ("npc/npc.lst", NewScript().Build()),
        };

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

        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_avatar.npk"), TestData.BuildNpk(new (string, byte[])[]
        {
            (AvatarImageRoot + "/skin/tt_body0000.img", BuildImg(SkinColor, -3, -4)),
            (AvatarImageRoot + "/coat/tt_coat0105a.img", BuildImg(CoatAColor, -3, -4)),
            // coat_b:帧 0 偏移 (1,2);帧 1 是带很远偏移的 1×1 全透明占位帧
            (AvatarImageRoot + "/coat/tt_coat0105b.img", BuildImg(CoatBColor, 1, 2, blankSecondFrame: true)),
            (AvatarImageRoot + "/hair/tt_hair0000a.img", BuildImg(HairColor, 0, 0)),
            // 武器图片在 blade/ 目录里,而装备脚本在 sword/ 里
            (WeaponImageRoot + "/blade/sword0002b.img", BuildImg(SwordBColor, -10, -12)),
            (WeaponImageRoot + "/blade/sword0002c.img", BuildImg(SwordCColor, -10, -12)),
            (WeaponImageRoot + "/blade/sword0002b1.img", BuildImg((90, 90, 90), -10, -12)),
        }));
    }

    /// <summary>每个 (IMG 帧号, 时长毫秒) 一帧;.ani 帧偏移 (-5, -8);循环。</summary>
    private static byte[] BuildAni((int FrameIndex, int DelayMs)[] frames)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            specs.Add(f);
        }
        return TestData.BuildAni(new[] { "Character/Test/Equipment/Avatar/skin/tt_body0000.img" },
            new (ushort, byte[])[] { (0, new byte[] { 1 }) }, specs);
    }

    /// <summary>两帧的 IMG,每帧 2×2 不透明,第 k 帧红色通道 = 基色 + k;帧头偏移 (x, y)。</summary>
    private static byte[] BuildImg((int R, int G, int B) color, int x, int y, bool blankSecondFrame = false)
    {
        var frames = new List<TestData.ImgFrameSpec>();
        for (int k = 0; k < 2; k++)
        {
            if (k == 1 && blankSecondFrame)
            {
                var blank = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, new byte[] { 0, 0, 0, 0 });
                blank.X = 300;
                blank.Y = -200;
                blank.FrameWidth = 1;
                blank.FrameHeight = 1;
                frames.Add(blank);
                continue;
            }
            var bgra = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                bgra[i * 4] = (byte)color.B;
                bgra[i * 4 + 1] = (byte)color.G;
                bgra[i * 4 + 2] = (byte)(color.R + k);
                bgra[i * 4 + 3] = 255;
            }
            var f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 2, 2, bgra);
            f.X = x;
            f.Y = y;
            f.FrameWidth = 20;
            f.FrameHeight = 30;
            frames.Add(f);
        }
        return TestData.BuildImg(frames);
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

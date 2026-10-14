using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>在临时目录里造一份"迷你游戏目录":Script.pvf + ImagePacks2/*.npk,全部由合成数据按规格构造。</summary>
internal sealed class SceneFixture : IDisposable
{
    public string Root { get; }

    // 字符串池:0 [NPC]  1 [/NPC]  2 [field animation]  3 Animation/PostBox.ani  4 postbox.npc  5 other.npc
    //          6 Animation/PostBoxFlip.ani  7 postboxflip.npc  8 [left]  9 [right]
    //          10 [animation]  11 [/animation]  12 Animation/Glow.ani  13 [closeback]  14 [close]  15 Animation/Back.ani  16 [normal]
    private static readonly string[] Pool =
    {
        "[NPC]", "[/NPC]", "[field animation]", "Animation/PostBox.ani", "postbox.npc", "other.npc",
        "Animation/PostBoxFlip.ani", "postboxflip.npc", "[left]", "[right]",
        "[animation]", "[/animation]", "Animation/Glow.ani", "[closeback]", "[close]", "Animation/Back.ani", "[normal]",
    };

    private const byte Tag = (byte)TokenType.Tag;
    private const byte Int = (byte)TokenType.Int;
    private const byte Str = (byte)TokenType.String;

    /// <summary>测试 IMG 里的 4×3 像素(RGBA)。第 0 个像素半透明,第 1 个全透明,其余不透明。</summary>
    public static readonly byte[] SpriteRgba =
    {
        10, 20, 30, 128,    0, 0, 0, 0,        200, 100, 50, 255,   1, 2, 3, 255,
        255, 0, 0, 255,     0, 255, 0, 255,    0, 0, 255, 255,      9, 8, 7, 255,
        11, 12, 13, 255,    14, 15, 16, 255,   17, 18, 19, 255,     20, 21, 22, 255,
    };

    public SceneFixture(bool twoNpks = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-render-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var strings = new List<byte[]>();
        foreach (string s in Pool)
        {
            strings.Add(Encoding.ASCII.GetBytes(s));
        }
        var files = new List<TestData.PvfFileSpec>
        {
            new("stringtable.bin", TestData.BuildStringTable(strings), 0),
            // npc.lst:没有标签,"编号 路径" 一对一对
            new("npc/npc.lst", TestData.BuildTokenScript(new (byte, int)[] { (Int, 32), (Str, 4), (Int, 99), (Str, 5), (Int, 33), (Str, 7) }), 0),
            new("npc/postbox.npc", TestData.BuildTokenScript(new (byte, int)[] { (Tag, 2), (Str, 3) }), 0),
            new("npc/animation/postbox.ani", BuildPostboxAni(flipX: false), 0),
            new("npc/postboxflip.npc", TestData.BuildTokenScript(new (byte, int)[] { (Tag, 2), (Str, 6) }), 0),
            new("npc/animation/postboxflip.ani", BuildPostboxAni(flipX: true), 0),
            // 真实数据的写法:每个 NPC 是 编号、朝向字符串、x、y、z 五个值
            new("map/test/facing.map", TestData.BuildTokenScript(new (byte, int)[]
            {
                (Tag, 0),
                (Int, 32), (Str, 9), (Int, 691), (Int, 163), (Int, 0),    // 朝右,.ani 没有缩放:不翻转
                (Int, 32), (Str, 8), (Int, 691), (Int, 163), (Int, 0),    // 朝左:翻转
                (Int, 33), (Str, 9), (Int, 691), (Int, 163), (Int, 0),    // 朝右,.ani 的 x 缩放为 -1:翻转
                (Int, 33), (Str, 8), (Int, 691), (Int, 163), (Int, 0),    // 朝左,.ani 的 x 缩放为 -1:两次翻转抵消
                (Tag, 1),
            }), 0),
            // 地图图层:每组 动画路径、图层名、x、y、z 五个值;路径相对地图所在目录。图层的书写顺序故意与前后顺序相反
            new("map/test/layers.map", TestData.BuildTokenScript(new (byte, int)[]
            {
                (Tag, 10),
                (Str, 12), (Str, 14), (Int, 5), (Int, 6), (Int, 7),        // 光效,[close]
                (Str, 15), (Str, 13), (Int, 0), (Int, 0), (Int, 0),        // 背景,[closeback]
                (Str, 15), (Str, 16), (Int, 100), (Int, 50), (Int, 0),     // 背景,[normal],世界 y = 50
                (Tag, 11),
                (Tag, 0), (Int, 32), (Str, 9), (Int, 691), (Int, 163), (Int, 0), (Tag, 1),   // 一个 NPC,世界 y = 163
            }), 0),
            new("map/test/animation/glow.ani", BuildGlowAni(), 0),
            new("map/test/animation/back.ani", BuildBackAni(), 0),
            new("map/test/town.map", TestData.BuildTokenScript(new (byte, int)[]
            {
                (Tag, 0), (Int, 32), (Int, 691), (Int, 163), (Int, 0), (Int, 99), (Int, 100), (Int, 100), (Int, 0), (Tag, 1),
            }), 0),
        };
        (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
        File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);

        byte[] img = BuildPostboxImg(0);
        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_first.npk"),
            TestData.BuildNpk(new[] { ("sprite/map/postbox.img", img) }));
        if (twoNpks)
        {
            // 同名 IMG 出现在排序靠后的第二个 NPK 里:颜色不同,用来检验"取最后一个"
            File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "b_second.npk"),
                TestData.BuildNpk(new[] { ("sprite/map/postbox.img", BuildPostboxImg(1)) }));
        }
    }

    /// <summary>
    /// 三帧的加法光效:帧 0 染色 alpha 0、帧 1 没有染色(= 255)、帧 2 染色 alpha 0;帧 0、1 带插值开关,时长各 1000 ms,帧 2 没有时长;循环。
    /// 与城镇里 GateOutLight、GatePillarLight 的结构相同(证据 E-030)。
    /// </summary>
    private static byte[] BuildGlowAni()
    {
        byte[] additive = { 2, 0 };
        var f0 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = -10, Y = -20 };
        f0.Props.Add((9, new byte[] { 255, 255, 255, 0 }));
        f0.Props.Add((10, new byte[] { 1 }));
        f0.Props.Add((11, additive));
        f0.Props.Add((12, TestData.LE32(1000)));
        var f1 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = -10, Y = -20 };
        f1.Props.Add((10, new byte[] { 1 }));
        f1.Props.Add((11, additive));
        f1.Props.Add((12, TestData.LE32(1000)));
        var f2 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = -10, Y = -20 };
        f2.Props.Add((9, new byte[] { 255, 255, 255, 0 }));
        f2.Props.Add((11, additive));
        return TestData.BuildAni(new[] { "Map/PostBox.img" }, new (ushort, byte[])[] { (0, new byte[] { 1 }) }, new[] { f0, f1, f2 });
    }

    private static byte[] BuildBackAni()
    {
        var f0 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = 0, Y = -5 };
        f0.Props.Add((12, TestData.LE32(20)));
        return TestData.BuildAni(new[] { "Map/PostBox.img" }, new (ushort, byte[])[] { (1, new byte[] { 0 }) }, new[] { f0 });
    }

    private static byte[] BuildPostboxAni(bool flipX)
    {
        var f0 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = -45, Y = -55 };
        f0.Props.Add((12, TestData.LE32(200)));
        if (flipX)
        {
            f0.Props.Add((7, BitConverter.GetBytes(-1.0f).Concat(BitConverter.GetBytes(1.0f)).ToArray()));
        }
        return TestData.BuildAni(new[] { "Map/PostBox.img" }, new (ushort, byte[])[] { (0, new byte[] { 1 }) }, new[] { f0 });
    }

    /// <summary>一个 4×3 的 ARGB8888 帧;帧头偏移 (1,1),画布 6×5。<paramref name="variant"/> 为 1 时红色通道整体加 100。</summary>
    private static byte[] BuildPostboxImg(int variant)
    {
        var bgra = new byte[SpriteRgba.Length];
        for (int i = 0; i < SpriteRgba.Length; i += 4)
        {
            byte r = SpriteRgba[i];
            if (variant == 1 && SpriteRgba[i + 3] != 0)
            {
                r = (byte)Math.Min(255, r + 100);
            }
            bgra[i] = SpriteRgba[i + 2];
            bgra[i + 1] = SpriteRgba[i + 1];
            bgra[i + 2] = r;
            bgra[i + 3] = SpriteRgba[i + 3];
        }
        TestData.ImgFrameSpec f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 4, 3, bgra);
        f.X = 1;
        f.Y = 1;
        f.FrameWidth = 6;
        f.FrameHeight = 5;
        return TestData.BuildImg(new[] { f });
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

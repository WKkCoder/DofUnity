using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 在临时目录里造一个"迷你副本":一张两块瓦片的房间地图(带背景、图层动画、出口位置、出生区、被动物件)、
/// 两个 .til、几张小 IMG、几个 .ani、出口与障碍物的 .obj、被动物件目录与副本定义。全部由合成数据按规格构造(规格见证据 E-067、E-068),不含任何原版内容。
/// </summary>
internal sealed class RoomFixture : IDisposable
{
    public const string MapPath = "map/test/room/a.map";
    public const string DungeonPath = "dungeon/test/test.dgn";
    public const string TileImage = "sprite/map/test/tile.img";
    public const string BgImage = "sprite/map/test/bg.img";
    public const string TreeImage = "sprite/map/test/tree.img";
    public const string GateImage = "sprite/map/test/gate01.img";

    /// <summary>障碍物的编号;5 是粒子光源(没有 [basic motion],应被跳过);999 不在目录里。</summary>
    public const int TreeId = 700;
    public const int ParticleId = 5;

    /// <summary>副本定义里十个出口物件的编号:801 左普通、802 左首领、803 左墙、804 右普通、805 右首领、806 右墙、807 上普通、808 上首领、809 下普通、810 下首领。</summary>
    public const int LeftNormalId = 801;
    public const int RightWallId = 806;

    /// <summary>
    /// 带碰撞体的出口物件(不在副本定义的十个编号里):[string data] 是门框动画加一个 .til,[int data] 里 .til 对应的那一对是锚点 (列 13, 行 4);
    /// 这个 .til 是 14 列 × 6 行,第 10 到 13 列在每一行都挡路(通行值 2)。
    /// </summary>
    public const int DoorBodyId = 811;

    public const string DoorBodyTile = "passiveobject/mapobject/pathgate/test/til/wall.til";

    private const byte TagType = (byte)TokenType.Tag;
    private const byte IntType = (byte)TokenType.Int;
    private const byte StrType = (byte)TokenType.String;

    private readonly List<string> _pool = new();

    public string Root { get; }

    private sealed class Script
    {
        private readonly RoomFixture _owner;
        private readonly List<(byte Type, int Value)> _tokens = new();

        public Script(RoomFixture owner)
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
            foreach (string t in texts)
            {
                _tokens.Add((StrType, _owner.Intern(t)));
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

    private Script NewScript()
    {
        return new Script(this);
    }

    /// <summary>14 列 × 6 行的通行表:第 0、1 行与第 5 行不能走(2),第 2 到 4 行能走(0);<paramref name="blocked"/> 是额外不能走的 (列, 行)。</summary>
    public static int[] PassTable(params (int Col, int Row)[] blocked)
    {
        var table = new int[14 * 6];
        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 14; col++)
            {
                table[row * 14 + col] = row >= 2 && row <= 4 ? 0 : 2;
            }
        }
        foreach ((int col, int row) in blocked)
        {
            table[row * 14 + col] = 2;
        }
        return table;
    }

    /// <summary>14 列 × 6 行的通行表:第 10 到 13 列在每一行都挡路(2),其余能走(0)。出口物件自带的 .til 的样子。</summary>
    public static int[] WallTable()
    {
        var table = new int[14 * 6];
        for (int row = 0; row < 6; row++)
        {
            for (int col = 10; col < 14; col++)
            {
                table[row * 14 + col] = 2;
            }
        }
        return table;
    }

    public RoomFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-room-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var scripts = new List<(string Name, byte[] Data)>
        {
            (MapPath, NewScript()
                .Tag("[type]").Str("[normal]")
                .Tag("[greed]").Str("JJ BB")
                .Tag("[tile]").Str("tile/t0.til", "tile/t1.til")
                .Tag("[far sight scroll]").Int(50)
                .Tag("[middle sight scroll]").Int(80)
                .Tag("[near sight scroll]").Int(120)
                .Tag("[background animation]")
                .Tag("[ani info]").Tag("[filename]").Str("animation/far.ani").Tag("[layer]").Str("[distantback]").Tag("[order]").Str("[below]").Tag("[/ani info]")
                .Tag("[ani info]").Tag("[filename]").Str("animation/mid.ani").Tag("[layer]").Str("[middleback]").Tag("[order]").Str("[below]").Tag("[/ani info]")
                .Tag("[/background animation]")
                .Tag("[pathgate pos]").Int(16, 120, 440, 130, 220, 60, 220, 190)
                .Tag("[dungeon start area]").Int(300, 100, 60, 40)
                .Tag("[animation]").Str("animation/glow.ani", "[normal]").Int(100, 100, 0).Tag("[/animation]")
                .Tag("[passive object]").Int(TreeId, 200, 40, 0, ParticleId, 150, 150, 0, 999, 10, 10, 0).Tag("[/passive object]")
                // 怪物摆放:每项 8 个数加 2 个字符串;开头多一个游离的数(要能错位恢复),末尾有一个不完整的残项(要被丢弃)
                .Tag("[monster]").Int(77).Int(1, 0, 1, 300, 200, 0, 1, 0).Str("[fixed]", "[normal]")
                .Int(2, 1, 0, 420, 210, 5, 2, 1).Str("[fixed]", "[boss]").Int(3, 0, 0)
                .Build()),
            // 城镇风格的地图:有 [tile] 与 [virtual movable area],没有 [pathgate pos]
            ("map/test/room/town.map", NewScript()
                .Tag("[tile]").Str("tile/t0.til")
                .Tag("[virtual movable area]").Int(0, 0, 100, 100).Build()),
            ("map/test/room/tile/t0.til", NewScript()
                .Tag("[IMAGE]").Str("Map/Test/Tile.img").Int(0)
                .Tag("[pass type]").Int(PassTable()).Build()),
            ("map/test/room/tile/t1.til", NewScript()
                .Tag("[IMAGE]").Str("Map/Test/Tile.img").Int(1)
                .Tag("[img pos]").Int(90)
                .Tag("[pass type]").Int(PassTable((2, 3))).Build()),
            ("map/test/room/animation/far.ani", BuildAni("Map/Test/Bg.img", 0, 2, -3)),
            ("map/test/room/animation/mid.ani", BuildAni("Map/Test/Bg.img", 0, 0, 0)),
            ("map/test/room/animation/glow.ani", BuildAni("Map/Test/Tile.img", 0, 0, 0)),
            ("map/test/animation/tree.ani", BuildAni("Map/Test/Tree.img", 0, 0, 0)),
            ("passiveobject/passiveobject.lst", NewScript()
                .Int(TreeId).Str("MapObject/Obstacle/Tree.obj")
                .Int(ParticleId).Str("MapObject/ParticleFactory/RayGround.obj")
                .Int(LeftNormalId).Str("MapObject/PathGate/Test/LeftNormal.obj")
                .Int(DoorBodyId).Str("MapObject/PathGate/Test/RightDoor.obj").Build()),
            ("passiveobject/mapobject/obstacle/tree.obj", NewScript()
                .Tag("[width]").Int(40, 20)
                .Tag("[floating height]").Int(0)
                .Tag("[layer]").Str("[normal]")
                .Tag("[pass type]").Str("[do not pass]")
                .Tag("[basic motion]").Str("../../../Map/Test/Animation/Tree.ani").Build()),
            ("passiveobject/mapobject/particlefactory/rayground.obj", NewScript()
                .Tag("[string data]").Str("Particle/rayGround.ptl").Tag("[/string data]")
                .Tag("[int data]").Int(1000).Tag("[/int data]").Build()),
            ("passiveobject/mapobject/pathgate/test/leftnormal.obj", NewScript()
                .Tag("[layer]").Str("[normal]")
                .Tag("[string data]").Str("Animation/Frame.ani", "Animation/Door.ani", "Animation/Open.ani").Tag("[/string data]")
                .Tag("[int data]").Int(0, 1, 1, 1, 2, 1).Tag("[/int data]").Build()),
            // 门框 + 碰撞体 .til;[int data] 的第二对 (13, 4) 是 .til 的锚点,不是 (种类, 变体)
            ("passiveobject/mapobject/pathgate/test/rightdoor.obj", NewScript()
                .Tag("[layer]").Str("[bottom]")
                .Tag("[string data]").Str("Animation/Frame.ani", "Til/Wall.til").Tag("[/string data]")
                .Tag("[int data]").Int(0, 1, 13, 4).Tag("[/int data]").Build()),
            (DoorBodyTile, NewScript().Tag("[pass type]").Int(WallTable()).Build()),
            ("passiveobject/mapobject/pathgate/test/animation/frame.ani", BuildAni("Map/Test/Gate%d%d.img", 0, 0, 0)),
            ("passiveobject/mapobject/pathgate/test/animation/door.ani", BuildAni("Map/Test/Gate%d%d.img", 1, 0, 0)),
            ("passiveobject/mapobject/pathgate/test/animation/open.ani", BuildAni("Map/Test/Gate%d%d.img", 2, 0, 0)),
            (DungeonPath, NewScript()
                .Tag("[pathgate object]").Int(801, 802, 803, 804, 805, 806, 807, 808, 809, 810)
                // 迷宫:一格,出口码 J(右 + 下);起点格是它自己,没有首领格
                .Tag("[maze info]").Tag("[size]").Int(1, 1).Tag("[greed]").Str("JJ")
                .Tag("[start map]").Int(0, 0, 0, 0).Tag("[/start map]").Tag("[boss map]").Tag("[/boss map]").Tag("[/maze info]")
                .Build()),
            // 房间地图目录:带 [greed] 的 a.map 是候选;没有 [greed] 的城镇图被跳过;另一个目录的图不读
            ("map/map.lst", NewScript().Int(1).Str("Test/Room/a.map").Int(2).Str("Test/Room/town.map").Int(3).Str("Other/x.map").Build()),
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

        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_room.npk"), TestData.BuildNpk(new (string, byte[])[]
        {
            // 瓦片:帧 0 是 8×4(帧头偏移 1,2),帧 1 是 6×3(偏移 0,0)
            (TileImage, BuildImg((8, 4, 1, 2, 220, 0), (6, 3, 0, 0, 110, 0))),
            // 背景:20×10 的图放在宽 100 的画布上(重复周期 100),帧头偏移 (3,4)
            (BgImage, BuildImg((20, 10, 3, 4, 50, 100))),
            (TreeImage, BuildImg((10, 20, -5, -18, 30, 0))),
            // 出口:门框(帧 0)、关着的门(帧 1)、开了之后的光(帧 2)
            (GateImage, BuildImg((6, 6, 0, 0, 60, 0), (4, 8, 1, 1, 70, 0), (3, 3, 2, 2, 80, 0))),
        }));
    }

    /// <summary>
    /// 一帧的 .ani(循环、每帧 100 毫秒),图片名模板、IMG 帧号与帧偏移 (x, y) 可调。
    /// </summary>
    private static byte[] BuildAni(string imageName, int frameIndex, int x, int y)
    {
        var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = x, Y = y };
        f.Props.Add((12, TestData.LE32(100)));
        return TestData.BuildAni(new[] { imageName }, new (ushort, byte[])[] { (0, new byte[] { 1 }) }, new[] { f });
    }

    /// <summary>
    /// 每个元组 (宽, 高, 帧头偏移 x, 帧头偏移 y, 红色通道, 画布宽;0 表示取 宽 + 偏移 x) 一帧纯色不透明的 IMG;帧的绿色通道 = 帧号,用来判断精灵取的是哪个 IMG 帧。
    /// </summary>
    private static byte[] BuildImg(params (int W, int H, int X, int Y, int R, int Canvas)[] frames)
    {
        var specs = new List<TestData.ImgFrameSpec>();
        for (int k = 0; k < frames.Length; k++)
        {
            (int w, int h, int x, int y, int r, int canvas) = frames[k];
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                bgra[i * 4] = 10;
                bgra[i * 4 + 1] = (byte)k;
                bgra[i * 4 + 2] = (byte)r;
                bgra[i * 4 + 3] = 255;
            }
            TestData.ImgFrameSpec f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, w, h, bgra);
            f.X = x;
            f.Y = y;
            f.FrameWidth = canvas > 0 ? canvas : w + x;
            f.FrameHeight = h + y;
            specs.Add(f);
        }
        return TestData.BuildImg(specs);
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

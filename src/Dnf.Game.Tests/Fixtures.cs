using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 合成数据构造器:按原版格式的规格(证据 E-009、E-036、E-035)自己造出测试用的标记流脚本、二进制 .ani 与脚本文件,
/// 不含任何原版内容。字符串表在 <see cref="Build"/> 时一次性生成,所以所有文件要先加完再 Build。
/// </summary>
internal sealed class Fixture
{
    private readonly MemoryScriptSource _source = new();
    private readonly List<string> _pool = new();
    private readonly Dictionary<string, int> _poolIndex = new(StringComparer.Ordinal);

    public int Intern(string s)
    {
        if (!_poolIndex.TryGetValue(s, out int i))
        {
            i = _pool.Count;
            _pool.Add(s);
            _poolIndex.Add(s, i);
        }
        return i;
    }

    /// <summary>加一个 .nut 脚本文本(路径相对 PVF 根,例如 sqr/foo.nut)。</summary>
    public Fixture Nut(string path, string text)
    {
        _source.Add(path, text);
        return this;
    }

    /// <summary>加一份标记流脚本(.chr、.skl、.atk、.lst 等)。</summary>
    public Fixture Tokens(string path, Action<TokenWriter> build)
    {
        var w = new TokenWriter(this);
        build(w);
        _source.AddBytes(path, w.ToBytes());
        return this;
    }

    public Fixture Ani(string path, AniBuilder ani)
    {
        _source.AddBytes(path, ani.ToBytes());
        return this;
    }

    /// <summary>加一份"编号 路径"列表(.lst)。</summary>
    public Fixture List(string path, params (int Id, string Path)[] entries)
    {
        return Tokens(path, w =>
        {
            foreach ((int id, string p) in entries)
            {
                w.Int(id).Str(p);
            }
        });
    }

    public MemoryScriptSource Build()
    {
        var s = new MemoryStream();
        void U32(uint v) => s.Write(BitConverter.GetBytes(v));
        U32((uint)_pool.Count);
        uint offset = (uint)((_pool.Count + 1) * 4);
        foreach (string item in _pool)
        {
            U32(offset);
            offset += (uint)item.Length;
        }
        U32(offset);
        foreach (string item in _pool)
        {
            s.Write(Encoding.Latin1.GetBytes(item));
        }
        _source.AddBytes(GameData.StringTablePath, s.ToArray());
        return _source;
    }
}

internal sealed class TokenWriter
{
    private readonly Fixture _fx;
    private readonly MemoryStream _s = new();

    public TokenWriter(Fixture fx)
    {
        _fx = fx;
        _s.WriteByte(TokenScript.Magic0);
        _s.WriteByte(TokenScript.Magic1);
    }

    private TokenWriter Put(TokenType type, int value)
    {
        _s.WriteByte((byte)type);
        _s.Write(BitConverter.GetBytes(value));
        return this;
    }

    public TokenWriter Tag(string name) => Put(TokenType.Tag, _fx.Intern(name));

    public TokenWriter Int(int v) => Put(TokenType.Int, v);

    public TokenWriter Ints(params int[] values)
    {
        foreach (int v in values)
        {
            Int(v);
        }
        return this;
    }

    public TokenWriter Float(float v) => Put(TokenType.Float, BitConverter.SingleToInt32Bits(v));

    public TokenWriter Str(string v) => Put(TokenType.String, _fx.Intern(v));

    public byte[] ToBytes() => _s.ToArray();
}

/// <summary>二进制 .ani 的构造器(无图片、只有判定框与帧属性)。</summary>
internal sealed class AniBuilder
{
    private sealed class FrameSpec
    {
        public int Delay;
        public int? Flag;
        public List<(ushort Type, int[] Values)> Boxes = new();
    }

    private readonly List<FrameSpec> _frames = new();
    private bool _loop;

    public AniBuilder Loop()
    {
        _loop = true;
        return this;
    }

    public AniBuilder Frame(int delayMs, int? flag = null, params (ushort Type, int[] Values)[] boxes)
    {
        _frames.Add(new FrameSpec { Delay = delayMs, Flag = flag, Boxes = boxes.ToList() });
        return this;
    }

    public static (ushort, int[]) Attack(int x, int y, int z, int w, int d, int h) => (AniBoxType.Attack, new[] { x, y, z, w, d, h });

    public static (ushort, int[]) Damage(int x, int y, int z, int w, int d, int h) => (AniBoxType.Damage, new[] { x, y, z, w, d, h });

    public byte[] ToBytes()
    {
        var s = new MemoryStream();
        void U16(int v) => s.Write(BitConverter.GetBytes((ushort)v));
        void I32(int v) => s.Write(BitConverter.GetBytes(v));
        U16(_frames.Count);
        U16(0); // 图片个数
        if (_loop)
        {
            U16(1);
            U16(AniGlobalProperty.Loop);
            s.WriteByte(1);
        }
        else
        {
            U16(0);
        }
        foreach (FrameSpec f in _frames)
        {
            U16(f.Boxes.Count);
            foreach ((ushort type, int[] values) in f.Boxes)
            {
                U16(type);
                foreach (int v in values)
                {
                    I32(v);
                }
            }
            U16(0xFFFF); // 无图
            I32(0);
            I32(0);
            int props = 1 + (f.Flag.HasValue ? 1 : 0);
            U16(props);
            U16(AniFrameProperty.Delay);
            I32(f.Delay);
            if (f.Flag.HasValue)
            {
                U16(AniFrameProperty.KeyFrameFlag);
                I32(f.Flag.Value);
            }
        }
        return s.ToArray();
    }
}

/// <summary>
/// 一个带单个职业(编号 8)的合成世界:角色 .chr、技能列表、技能 .skl、动画与攻击信息,以及登记状态的脚本。
/// 调用方按需往里加状态脚本与动画。
/// </summary>
internal static class World
{
    public const int Job = 8;
    public const int SkillFoo = 15;
    public const int StateFoo = 70;

    public static Fixture Basic()
    {
        var fx = new Fixture();
        fx.List("character/character.lst", (Job, "test/test.chr"));
        fx.Tokens("character/test/test.chr", w => w
            .Tag("[job]").Str("[test mage]")
            .Tag("[waiting motion]").Str("animation/stand.ani")
            .Tag("[etc motion]").Str("animation/foo_start.ani").Str("animation/foo_loop.ani").Str("animation/foo_end.ani")
            .Tag("[etc attack info]").Str("attackinfo/foo.atk"));
        fx.Ani("character/test/animation/stand.ani", new AniBuilder().Loop().Frame(100).Frame(100));
        fx.Tokens("character/test/attackinfo/foo.atk", w => w.Tag("[damage type]").Str("[physical]"));
        fx.List("skill/skilllist.lst", (Job, "testskill.lst"));
        fx.List("skill/testskill.lst", (SkillFoo, "test/foo.skl"));
        fx.Tokens("skill/test/foo.skl", w => w
            .Tag("[executable states]").Ints(0, 8)
            .Tag("[static data]").Ints(300, 150)
            .Tag("[level info]").Ints(2, 100, 5, 120, 7, 140, 9));
        return fx;
    }

    /// <summary>登记链:header 里是常量,状态脚本是 sqr/test/foo.nut(回调后缀 Foo)。</summary>
    public static Fixture WithFooScript(this Fixture fx, string fooScript)
    {
        fx.Nut("sqr/loadstate.nut", @"
IRDSQRCharacter.pushScriptFiles(""test/header.nut"");
IRDSQRCharacter.pushState(JOB_TEST, ""test/foo.nut"", ""Foo"", STATE_FOO, SKILL_FOO);
");
        fx.Nut("sqr/test/header.nut", @"
JOB_TEST <- 8; STATE_FOO <- 70; SKILL_FOO <- 15; STATE_STAND <- 0;
STATE_PRIORITY_USER <- 1; ENUM_DIRECTION_LEFT <- 0; ENUM_DIRECTION_RIGHT <- 1; ENUM_DIRECTION_NEUTRAL <- 4;
CUSTOM_ANI_FOO_START <- 0; CUSTOM_ANI_FOO_LOOP <- 1; CUSTOM_ANI_FOO_END <- 2; CUSTOM_ATTACK_INFO_FOO <- 0;
");
        fx.Nut("sqr/test/foo.nut", fooScript);
        return fx;
    }

    public static Dnf.Game.GameWorld Create(MemoryScriptSource source, out ScriptLoadSummary summary)
    {
        var data = new GameData(source);
        var host = new ScriptHost(source);
        var world = new Dnf.Game.GameWorld(data, host);
        summary = world.LoadScripts();
        return world;
    }
}

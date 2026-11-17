using System.Text;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>联网选角测试用的合成角色名册(名字、职业、装备都是编的;编号 900 起,从不用真实值)。</summary>
internal static class RosterKit
{
    /// <summary>一个角色条目:名字按 UTF-8 编成字节,等级默认 1,其余都是 0;<paramref name="d2"/> 非 0 是待删除。</summary>
    public static OnlineCharacterEntry Entry(string name, byte x1 = 0, byte job = 0, byte growTypeByte = 0, byte level = 1, byte status = 0, uint d2 = 0,
        ushort uid = 900, IEnumerable<OnlineCharacterEquip> equips = null, byte d = 0)
    {
        return EntryBytes(Encoding.UTF8.GetBytes(name), x1, job, growTypeByte, level, status, d2, uid, equips, d);
    }

    /// <summary>名字直接给字节(测试非法序列、过长、空名字)。</summary>
    public static OnlineCharacterEntry EntryBytes(byte[] name, byte x1 = 0, byte job = 0, byte growTypeByte = 0, byte level = 1, byte status = 0, uint d2 = 0,
        ushort uid = 900, IEnumerable<OnlineCharacterEquip> equips = null, byte d = 0)
    {
        return new OnlineCharacterEntry(uid, name, x1, job, growTypeByte, level, d, status, equips, 0, 0, 0, 0, 0, 0, 0, 0, 0, d2);
    }

    public static OnlineCharacterList List(params OnlineCharacterEntry[] entries)
    {
        return new OnlineCharacterList(6, 1, 2, 77u, entries, 0);
    }

    /// <summary>N 个角色:第 i 个叫"角色(i+1)"、编号 900 + i、职业 i % 11、等级 i + 1(最多 255)、都能进入。</summary>
    public static OnlineCharacterList Many(int count)
    {
        var entries = new List<OnlineCharacterEntry>();
        for (int i = 0; i < count; i++)
        {
            entries.Add(Entry("角色" + (i + 1), job: (byte)(i % 11), level: (byte)Math.Min(255, i + 1), uid: (ushort)(900 + i)));
        }
        return List(entries.ToArray());
    }

    public static CharacterRoster Roster(int count, ICharacterTitles titles = null)
    {
        return OnlineCharacterRoster.From(Many(count), titles);
    }

    /// <summary>空图包的选角素材(不画头像):逻辑测试需要 <see cref="CharacterSelectScreen.Click"/> 起作用,它要求有素材。</summary>
    public static CharacterSelectScene EmptyScene() => new CharacterSelectScene(new ShellFakeSprites());
}

/// <summary>记下被问过的 (职业、一转、觉醒) 的称号来源;<see cref="Name"/> 决定返回什么。</summary>
internal sealed class StubTitles : ICharacterTitles
{
    public string DefaultName { get; set; } = "默认名";

    public List<(int Job, int Grow, int Awakening)> Calls { get; } = new();

    public Func<int, int, int, string> Name { get; set; } = (j, g, a) => "职" + j + "转" + g + "觉" + a;

    public string ClassName(int job, int growType, int awakening)
    {
        Calls.Add((job, growType, awakening));
        return Name(job, growType, awakening);
    }
}

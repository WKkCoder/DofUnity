using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dnf.Game;

// 本项目自己的设计(r15-avatarhost):装扮栏的存档字段。原版的角色数据在服务器数据库里,客户端不存;这里只存数字(部位号与物品编号),不含任何原版内容。
// 存的形状按规则层(r14-avatar)的约定:存 AvatarSlots 里每个部位穿着的物品编号,读回用 AvatarSlots.Restore,类型与槽对不上不抛异常。
namespace Dnf.Play
{
    /// <summary>存档里装扮栏的一格:部位号(0 到 9)与物品编号。</summary>
    public readonly struct SavedAvatar
    {
        public SavedAvatar(int part, int code)
        {
            Part = part;
            Code = code;
        }

        public int Part { get; }

        public int Code { get; }
    }

    public sealed partial class HeroSave
    {
        /// <summary>装扮栏穿着的每一件(键 <c>avatar 部位号 物品编号</c>,部位号从小到大,只写有东西的格;旧存档没有这些行)。</summary>
        public List<SavedAvatar> Avatars { get; } = new List<SavedAvatar>();
    }

    /// <summary>装扮存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略它们)。</summary>
    internal static class AvatarSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string Key = "avatar";

        public static bool Handles(string key)
        {
            return key == Key;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            foreach (SavedAvatar avatar in save.Avatars)
            {
                sb.Append(Key).Append(' ').Append(avatar.Part.ToString(Inv)).Append(' ').Append(avatar.Code.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            if (f.Length != 3)
            {
                error = "第 " + no + " 行:" + key + " 需要 2 个值";
                return false;
            }
            if (!long.TryParse(f[1], NumberStyles.Integer, Inv, out long part) || !long.TryParse(f[2], NumberStyles.Integer, Inv, out long code))
            {
                error = "第 " + no + " 行:" + key + " 的值不是整数";
                return false;
            }
            if (part < AvatarSlots.First || part > AvatarSlots.Last || code < 1 || code > int.MaxValue)
            {
                error = "第 " + no + " 行:" + key + " 的值超出范围";
                return false;
            }
            r.Avatars.Add(new SavedAvatar((int)part, (int)code));
            return true;
        }
    }
}

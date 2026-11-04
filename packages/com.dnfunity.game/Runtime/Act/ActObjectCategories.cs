using System;
using System.Collections.Generic;

// 客户端全局类别表(初始化函数 0x0129c140,查名函数 0x01293f10;ADD/REMOVE/[IS CATEGORY] 共用,E-171,静态读码,非 REA)。
namespace Dnf.Game.Act
{
    /// <summary>
    /// 动作脚本里的运行时类别名 → 编号(与客户端全局类别表 0x0129c140 注册次序一致)。编号 <see cref="InvalidId"/>(0x2c) 表示不认识的名字;
    /// <see cref="AnchorCategoryId"/>(0x1a) 在 ADD/REMOVE 时还会写怪物锚点标志(字段 +0x4f94 与坐标 +0x4700/+0x4704,块 0x010bbc21 / 0x010bbd8e)。
    /// </summary>
    public static class ActObjectCategories
    {
        /// <summary>查表失败时 ADD/REMOVE 整段跳过(客户端查名函数 0x01293f10 返回 0x2c)。</summary>
        public const int InvalidId = 0x2c;

        /// <summary>写锚点标志与坐标的特殊类别(客户端只在 <c>catId == 0x1a</c> 时写 +0x4f94,对应脚本标签 [fixture])。</summary>
        public const int AnchorCategoryId = 0x1a;

        private static readonly Dictionary<string, int> ByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static ActObjectCategories()
        {
            // 顺序与客户端 0x0129c140 注册次序一致(共 44 项,编号 0..43);31、32 两档在静态数据里没读到字符串,留空。
            string[] names =
            {
                "human", "beast", "plant", "insect", "machine", "organic", "hybrid", "undead", "devil", "spirit", "dragon", "angel",
                "melee combat", "range combat", "close-passive", "close-carefully", "full-superarmor", "close-tough", "close-manace",
                "oneside move", "human armor", "fort armor", "divine armor", "goblin", "tau", "lugaru", "fixture", "stay", "melee",
                "missile", "energy", null, null, "bullet", "cannon", "explosion", "inflame", "non inflame", "light", "dark", "water",
                "fire", "fort", "unbeatable",
            };
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                {
                    ByName[names[i]] = i;
                }
            }
        }

        /// <summary>把脚本里的类别标签(带或不带方括号)规范成查表键。</summary>
        public static string NormalizeName(string tagOrName)
        {
            if (string.IsNullOrEmpty(tagOrName))
            {
                return string.Empty;
            }
            string s = tagOrName.Trim();
            if (s.Length >= 2 && s[0] == '[' && s[s.Length - 1] == ']')
            {
                s = s.Substring(1, s.Length - 2);
            }
            return s.Trim();
        }

        /// <summary>类别名 → 编号;不认识返回 <see cref="InvalidId"/>。</summary>
        public static int Resolve(string tagOrName)
        {
            string key = NormalizeName(tagOrName);
            return key.Length == 0 || !ByName.TryGetValue(key, out int id) ? InvalidId : id;
        }

        /// <summary>编号是否有效(0..43;与客户端表一致,不含 <see cref="InvalidId"/>)。</summary>
        public static bool IsValidId(int id) => (uint)id < (uint)InvalidId;
    }
}

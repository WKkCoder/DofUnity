using System;
using System.Collections.Generic;

// 证据:脚本里文字的引用是"种类编号 + 键"一对(标记类型 9 的值是种类编号,紧跟的类型 10 是键,见 E-121)。
namespace Dnf.Data
{
    /// <summary>
    /// 所有 *.kor.str 文本表的集合:按需读文件、缓存;脚本里的文字引用(种类编号 + 键)在这里换成文字。
    /// 种类编号到文件的对应是对全库脚本做的统计(每个键都能在对应文件里找到),见 <see cref="PathOfKind"/>。
    /// 文件读不到时对应的查询返回 false/null,不抛异常(用户的数据目录可能没有这份文本)。
    /// </summary>
    public sealed class StrLibrary
    {
        /// <summary>种类编号:脚本里文字引用的种类。</summary>
        public const int KindCharacter = 0;
        public const int KindDungeon = 2;
        public const int KindEquipment = 3;
        public const int KindEtc = 4;
        public const int KindItemShop = 5;
        public const int KindMonster = 7;
        public const int KindNpc = 8;
        public const int KindQuest = 11;
        public const int KindSkill = 12;
        public const int KindStackable = 13;
        public const int KindTown = 14;
        public const int KindWorldMap = 15;
        public const int KindCreature = 16;
        public const int KindAiCharacter = 17;
        public const int KindRandomOption = 22;

        /// <summary>界面文字表(ui/ui.kor.str):界面脚本按键取字,不走种类编号。</summary>
        public const string UiPath = "ui/ui.kor.str";

        private static readonly Dictionary<int, string> KindPaths = new Dictionary<int, string>
        {
            [KindCharacter] = "character/character.kor.str",
            [KindDungeon] = "dungeon/dungeon.kor.str",
            [KindEquipment] = "equipment/equipment.kor.str",
            [KindEtc] = "etc/etc.kor.str",
            [KindItemShop] = "itemshop/itemshop.kor.str",
            [KindMonster] = "monster/monster.kor.str",
            [KindNpc] = "npc/npc.kor.str",
            [KindQuest] = "n_quest/quest.kor.str",
            [KindSkill] = "skill/skill.kor.str",
            [KindStackable] = "stackable/stackable.kor.str",
            [KindTown] = "town/town.kor.str",
            [KindWorldMap] = "worldmap/worldmap.kor.str",
            [KindCreature] = "creature/creature.kor.str",
            [KindAiCharacter] = "aicharacter/aicharacter.kor.str",
            [KindRandomOption] = "etc/randomoption/randomoption.kor.str",
        };

        private readonly Func<string, byte[]> _read;
        private readonly Dictionary<string, StrBook> _books = new Dictionary<string, StrBook>(StringComparer.OrdinalIgnoreCase);

        /// <param name="read">按 PVF 内路径取文件字节,没有这个文件返回 null。</param>
        public StrLibrary(Func<string, byte[]> read)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
        }

        /// <summary>种类编号对应的文本表路径;不认识的编号返回 null。</summary>
        public static string PathOfKind(int kind)
        {
            return KindPaths.TryGetValue(kind, out string path) ? path : null;
        }

        /// <summary>读一份文本表(第一次读后缓存);文件不存在返回 null。</summary>
        public StrBook Book(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (_books.TryGetValue(path, out StrBook book))
            {
                return book;
            }
            byte[] data = _read(path);
            book = data == null ? null : new StrBook(data);
            _books[path] = book;
            return book;
        }

        /// <summary>种类编号对应的文本表;种类不认识或文件不存在返回 null。</summary>
        public StrBook BookOfKind(int kind)
        {
            return Book(PathOfKind(kind));
        }

        /// <summary>脚本里的一个文字引用(种类编号 + 键)换成文字;查不到返回 false。</summary>
        public bool TryGet(int kind, string key, out string text)
        {
            StrBook book = BookOfKind(kind);
            if (book == null)
            {
                text = null;
                return false;
            }
            return book.TryGet(key, out text);
        }

        /// <summary>同 <see cref="TryGet"/>,查不到返回 null。</summary>
        public string Get(int kind, string key)
        {
            return TryGet(kind, key, out string text) ? text : null;
        }

        /// <summary>界面文字表里的一个键;查不到返回 null。</summary>
        public string Ui(string key)
        {
            StrBook book = Book(UiPath);
            return book == null ? null : book.Get(key);
        }
    }
}

using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-125(服务端静态读码,非 REA 的部分另行标注):
//   进副本的难度门槛 CParty::dungeon_start 0x085a0954 与 CParty::checkDungeonDifficulty 0x085be932;
//   通关记录的存取 WongWork::CDungeonClear::getClearedDungeonDiff 0x085bf96c / addClearedDungeon 0x08374f24、CUser::AddDungeonClear 0x086780fa;
//   通关后记录 CParty::clear_reward 0x085ae0d4 → CParty::DungeonPermission 0x085b12f8 与 CParty::addDungeonClear 0x085bddfc;
//   副本种类 CParty::getDungeonTypeKind 0x085bdd2a。
// 存档里的存取格式(HeroSave 的 dclear 行)是本项目自己的设计,原版的角色数据在服务器数据库里。
namespace Dnf.Game
{
    /// <summary>副本种类(<c>CParty::getDungeonTypeKind</c> 的前三类);难度 3 的开放条件按种类不同。其余种类(血系统、绝望塔、锦标赛……)不开放难度 3。</summary>
    public enum DungeonClearKind
    {
        /// <summary>普通副本(服务端的 0)。</summary>
        Standard = 0,

        /// <summary>服务端 <c>CDungeon+0x89c</c> 那个标志置位的副本(返回 1;标志对应哪个 .dgn 标签没有查)。</summary>
        Flagged = 1,

        /// <summary><c>get_dimension_possible</c> 大于 0 的副本(返回 2)。</summary>
        Dimension = 2,

        /// <summary>其余种类:通关不记录难度 3。</summary>
        Other = 3,
    }

    /// <summary>
    /// 角色的副本通关记录与难度门槛(M33,证据 E-125)。
    /// 服务端为每个角色存 <c>map&lt;short 副本编号, char 值&gt;</c>:没有记录是 0;值 v 表示难度 1 到 v 已经开放(难度 0 永远能进)。
    /// 进副本时难度 d 大于 0 要查(游戏管理员、锦标赛副本、冒险副本不查):值是 0 进不了;值小于 d 进不了,只有 d 等于 4 不比较大小(但值仍要大于 0)。
    /// 通关时(队员等级不低于副本最低等级才记)按通关的难度改记录,只升不降:
    /// 难度 0 通关 → 值至少为 1;难度 1 通关且评分不低于 S → 至少为 2;
    /// 难度 2 通关且副本在 <see cref="UltimateDungeonList"/> 里、种类为前三类、评分够、被击次数不超过上限、没用过复活币 → 至少为 3。
    /// 难度 3 与 4 通关不改记录。
    /// </summary>
    public sealed class DungeonClears
    {
        /// <summary>难度个数(0 到 4)。</summary>
        public const int Difficulties = 5;

        /// <summary>界面里能选的最高难度(原版结算与选择界面只有四个字母图,难度 4 不在这里选)。</summary>
        public const int MaxSelectable = 3;

        /// <summary>评分档(与 <c>RankSystem.GradeOf</c> 一致:0 = F……8 = SSS)里难度 1 通关要求的最低档:S。</summary>
        public const int GradeForTier2 = 6;

        /// <summary>难度 2 通关(普通种类)要求的评分档:SSS。</summary>
        public const int GradeForTier3Standard = 8;

        /// <summary>难度 2 通关(另两类)要求的最低评分档:SS。</summary>
        public const int GradeForTier3Other = 7;

        /// <summary>被击次数上限:普通种类 10、标志种类 30、维度种类 50(服务端常量;客户端上报的那个整数的含义没有恢复,按被击次数理解)。</summary>
        public static int HitLimit(DungeonClearKind kind)
        {
            switch (kind)
            {
                case DungeonClearKind.Standard:
                    return 10;
                case DungeonClearKind.Flagged:
                    return 30;
                case DungeonClearKind.Dimension:
                    return 50;
                default:
                    return -1;
            }
        }

        /// <summary>副本定义(.dgn)的最低等级 <c>[minimum required level]</c>(第一个整数;没有这个标签是 0)。</summary>
        public static int MinimumLevel(ScriptLine[] dungeon)
        {
            if (dungeon != null)
            {
                foreach (ScriptLine line in dungeon)
                {
                    if (line.TagIs("[minimum required level]"))
                    {
                        List<int> numbers = line.Ints();
                        return numbers.Count > 0 ? numbers[0] : 0;
                    }
                }
            }
            return 0;
        }

        private readonly Dictionary<int, int> _opened = new Dictionary<int, int>();

        /// <summary>有记录的副本数。</summary>
        public int Count => _opened.Count;

        /// <summary>记录值(0 = 没有记录)。</summary>
        public int Opened(int dungeonId)
        {
            return _opened.TryGetValue(dungeonId, out int value) ? value : 0;
        }

        /// <summary>
        /// 抬高记录(<c>CUser::AddDungeonClear</c>:只在新值更大时写),返回是不是写了。<paramref name="value"/> 夹在 0 到 <see cref="Difficulties"/> − 1。
        /// </summary>
        public bool Raise(int dungeonId, int value)
        {
            int clamped = Math.Max(0, Math.Min(Difficulties - 1, value));
            if (clamped <= Opened(dungeonId))
            {
                return false;
            }
            _opened[dungeonId] = clamped;
            return true;
        }

        /// <summary>存档还原用:直接设记录值(0 或越界的丢掉)。</summary>
        public void Set(int dungeonId, int value)
        {
            if (value >= 1 && value < Difficulties)
            {
                _opened[dungeonId] = value;
            }
        }

        public void Clear()
        {
            _opened.Clear();
        }

        /// <summary>全部记录,按副本编号从小到大(存档用)。</summary>
        public List<KeyValuePair<int, int>> Entries()
        {
            var list = new List<KeyValuePair<int, int>>(_opened);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        /// <summary>
        /// 能不能以难度 <paramref name="difficulty"/> 进这个副本(<c>dungeon_start</c> 的门槛):难度 0 永远能;
        /// <paramref name="exempt"/> 为真(游戏管理员、锦标赛副本、冒险副本)不查;难度 1 到 3 要求记录值不小于难度;难度 4 只要求有记录。越界难度进不了。
        /// </summary>
        public bool CanEnter(int dungeonId, int difficulty, bool exempt = false)
        {
            if (difficulty < 0 || difficulty >= Difficulties)
            {
                return false;
            }
            if (difficulty == 0 || exempt)
            {
                return true;
            }
            int value = Opened(dungeonId);
            if (value == 0)
            {
                return false;
            }
            return value >= difficulty || difficulty == Difficulties - 1;
        }

        /// <summary>
        /// 选择界面里这个副本能选到的最高难度(0 到 <see cref="MaxSelectable"/>):记录值夹在 <see cref="MaxSelectable"/> 以内;
        /// 没有难度 3 的副本(<paramref name="hasUltimate"/> 为假)最多到 2。
        /// </summary>
        public int HighestSelectable(int dungeonId, bool hasUltimate)
        {
            int cap = hasUltimate ? MaxSelectable : MaxSelectable - 1;
            return Math.Min(cap, Opened(dungeonId));
        }

        /// <summary>
        /// 通关一个副本后记录(<c>DungeonPermission</c> 与 <c>addDungeonClear</c>):<paramref name="grade"/> 是评分档(0 = F……8 = SSS),
        /// <paramref name="hitsTaken"/> 被击次数,<paramref name="coinsUsed"/> 用掉的复活币数。队员等级低于 <paramref name="minLevel"/> 不记。
        /// 返回新写下的记录值;没有改动返回 0(这个值同时是客户端收到的"某难度开放"通知里的数)。
        /// </summary>
        public int Record(int dungeonId, int difficulty, int grade, int hitsTaken, int coinsUsed, int heroLevel, int minLevel, bool hasUltimate,
            DungeonClearKind kind = DungeonClearKind.Standard)
        {
            if (heroLevel < minLevel)
            {
                return 0;
            }
            int value;
            switch (difficulty)
            {
                case 0:
                    value = 1;
                    break;
                case 1:
                    value = grade >= GradeForTier2 ? 2 : 0;
                    break;
                case 2:
                    value = hasUltimate && OpensTier3(kind, grade, hitsTaken, coinsUsed) ? 3 : 0;
                    break;
                default:
                    value = 0;
                    break;
            }
            return value > 0 && Raise(dungeonId, value) ? value : 0;
        }

        private static bool OpensTier3(DungeonClearKind kind, int grade, int hitsTaken, int coinsUsed)
        {
            int limit = HitLimit(kind);
            if (limit < 0 || coinsUsed != 0 || hitsTaken > limit)
            {
                return false;
            }
            return grade >= (kind == DungeonClearKind.Standard ? GradeForTier3Standard : GradeForTier3Other);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace Dnf.Play
{
    /// <summary>
    /// 界面占位文字的中文版:窗口代码里写的是英文占位(点阵字库只有大写字母与数字),宿主装上字体之后,
    /// <see cref="OverlayArt.Text"/> 把这些占位词换成下面的中文再用字体画。中文是本项目自己写的(常见的界面用语),
    /// 不是原版字符串表里的文字;原版的物品名、说明等数据文字另走字符串表(见 <see cref="ItemTexts"/>)。
    /// 两种写法:整句相同的(<see cref="Exact"/>),和带 <c>{0}</c> <c>{1}</c> 空位的句式(<see cref="Patterns"/>,空位里的文字原样带过去)。
    /// 认不出来的文字原样返回,所以物品名一类的数据文字经过这里不受影响。
    /// </summary>
    public static class UiText
    {
        private static readonly string[,] ExactPairs =
        {
            // 背包
            { "INVENTORY", "背包" },
            { "EQUIPMENT", "装备栏" },
            { "EQUIP", "装备" },
            { "USE", "消耗" },
            { "MATERIAL", "材料" },
            { "QUEST", "任务" },
            { "WPN", "武器" },
            { "TITLE", "称号" },
            { "COAT", "上衣" },
            { "SHLD", "肩膀" },
            { "PANT", "下装" },
            { "SHOE", "鞋" },
            { "BELT", "腰带" },
            { "NECK", "项链" },
            { "WRST", "手镯" },
            { "RING", "戒指" },
            { "SUPT", "辅助" },
            { "STONE", "魔法石" },
            { "EMPTY SLOT", "空槽位" },
            { "CAN USE", "可以使用" },
            { "CAN EQUIP", "可以装备" },
            { "EQUIPPED", "已装备" },
            { "UNEQUIPPED", "已卸下" },
            { "BAG FULL", "背包已满" },
            { "CANNOT USE YET", "暂时不能使用" },
            { "NO SLOT FOR THIS", "这件物品没有装备栏" },
            { "WRONG SLOT", "装备栏不对" },
            { "WRONG CLASS", "职业不符" },
            { "LEVEL TOO LOW", "等级不足" },
            { "NO ANIMATION FOR THIS", "这件装备还没有外观" },
            { "USED", "已使用" },
            { "COOLING DOWN", "冷却中" },
            { "CANNOT USE HERE", "这里不能使用" },
            { "NONE LEFT", "已经没有了" },
            { "ARROWS MOVE   ENTER EQUIP OR USE   Q E PAGE   I CLOSE", "方向键 移动   回车 装备或使用   Q E 翻页   I 关闭" },

            // 商店
            { "SHOP", "商店" },
            { "WEAPON SHOP", "武器商店" },
            { "GENERAL SHOP", "杂货商店" },
            { "BUY", "买入" },
            { "SELL", "卖出" },
            { "SPACE SHOP", "按空格键 打开商店" },
            { "NO SALE", "不可出售" },
            { "BUY HOW MANY", "买入几件" },
            { "SELL HOW MANY", "卖出几件" },
            { "MAX", "最大" },
            { "OK", "确定" },
            { "CANCEL", "取消" },
            { "DONE", "完成" },
            { "NOTHING THERE", "这里什么也没有" },
            { "NOT SOLD HERE", "这里不卖这个" },
            { "BAD COUNT", "数量不对" },
            { "CANNOT BUY THIS", "不能买这个" },
            { "NOT ENOUGH GOLD", "金币不够" },
            { "BAG IS FULL", "背包已满" },
            { "SHOP WILL NOT TAKE THIS", "商店不收这个" },
            { "ARROWS MOVE   ENTER BUY   Q E PAGE   TAB SELL MODE   ESC CLOSE", "方向键 移动   回车 买入   Q E 翻页   Tab 切到卖出   Esc 关闭" },
            { "ARROWS MOVE   ENTER SELL   Q E PAGE   TAB BUY MODE   ESC CLOSE", "方向键 移动   回车 卖出   Q E 翻页   Tab 切到买入   Esc 关闭" },
            { "UP DOWN +1 -1   LEFT RIGHT -10 +10   ENTER OK   ESC CANCEL", "上下 ±1   左右 ±10   回车 确定   Esc 取消" },

            // 任务(M31)
            { "QUESTS", "任务" },
            { "NO QUESTS", "没有任务" },
            { "SPACE TALK", "按空格键 对话" },
            { "ACCEPT", "接受" },
            { "COMPLETE", "完成" },
            { "ABANDON", "放弃" },
            { "CLOSE", "关闭" },
            { "REWARD", "奖励" },
            { "CHOOSE ONE", "选择一件奖励" },
            { "QUEST ACCEPTED", "任务已接取" },
            { "QUEST READY", "任务可以交了" },
            { "QUEST COMPLETE", "任务完成" },
            { "QUEST ABANDONED", "任务已放弃" },
            { "QUEST ITEM", "任务物品" },
            { "NOT AVAILABLE", "现在不能接" },
            { "NOT DONE YET", "还没完成" },
            { "CANNOT ABANDON", "这个任务不能放弃" },
            { "PRESS ABANDON AGAIN", "再按一次放弃" },
            { "NO SUCH QUEST", "没有这个任务" },
            { "UP DOWN SELECT   DEL ABANDON   L ESC CLOSE", "上下 选择   Delete 放弃   L Esc 关闭" },
            { "UP DOWN SELECT   ENTER ACCEPT OR COMPLETE   LEFT RIGHT REWARD   TAB SHOP   ESC CLOSE", "上下 选择   回车 接受或完成   左右 选奖励   Tab 商店   Esc 关闭" },
            { "UP DOWN SELECT   ENTER ACCEPT OR COMPLETE   LEFT RIGHT REWARD   ESC CLOSE", "上下 选择   回车 接受或完成   左右 选奖励   Esc 关闭" },

            // 技能窗口(M35)
            { "SKILLS", "技能" },
            { "COMMON", "通用" },
            { "CLASS", "职业" },
            { "OTHER", "其他" },
            { "AWAKEN", "觉醒" },
            { "NO SKILLS", "没有技能" },
            { "AWAKENING SKILL", "觉醒技能" },
            { "ACTIVE SKILL", "主动技能" },
            { "PASSIVE SKILL", "被动技能" },
            { "REQUIRES", "前置技能" },
            { "LEARN", "学习" },
            { "REFUND", "退还" },
            { "RESET", "重置" },
            { "SKILL LEARNED", "技能已学习" },
            { "SKILL REFUNDED", "技能已退还" },
            { "SKILLS RESET", "技能已重置" },
            { "PRESS RESET AGAIN", "再按一次重置" },
            { "SKILL SLOTTED", "已放进快捷栏" },
            { "CANNOT SLOT THIS", "这个技能不能放进快捷栏" },
            { "SLOT CLEARED", "已从快捷栏拿掉" },
            { "NOT IN A SLOT", "它不在快捷栏里" },
            { "CANNOT LEARN YET", "暂时学不了" },
            { "NOT ENOUGH POINTS", "点数不够" },
            { "NOT LEARNED", "还没学" },
            { "BELOW STARTING LEVEL", "不能低于初始等级" },
            { "ANOTHER SKILL NEEDS IT", "别的技能需要它" },
            { "NO COST DATA", "没有花费数据" },
            { "NO SUCH SKILL", "没有这个技能" },
            { "UP DOWN SKILL   LEFT RIGHT PAGE   ENTER LEARN   BACK REFUND   R RESET   ASDFGH SLOT   DEL UNSLOT   K CLOSE", "上下 选技能   左右 换页   回车 学习   退格 退还   R 重置   ASDFGH 放快捷栏   Delete 摘下   K 关闭" },

            // 系统菜单、复活、结算
            { "SYSTEM MENU", "系统菜单" },
            { "RESUME", "继续游戏" },
            { "RETURN TO TOWN", "返回城镇" },
            { "CHARACTER SELECT", "返回选角" },
            { "QUIT GAME", "退出游戏" },
            { "NO REVIVE COIN", "没有复活币" },
            { "COIN LIMIT REACHED", "复活币已用到上限" },
            { "PRESS X TO REVIVE", "按 X 键复活" },
            { "FREE", "免费" },
            { "CLEAR", "通关" },
            { "CLEAR!", "通关!" },
            { "RETRY", "再次挑战" },
            { "PARTY", "组队" },
            { "SELECT", "选择地下城" },
            { "TOWN", "返回城镇" },

            // 选角与副本选择
            { "SELECT CHARACTER", "选择角色" },
            { "NO CHARACTER", "没有角色" },
            { "ARROWS SELECT   ENTER START   ESC QUIT", "方向键 选择   回车 开始   Esc 退出" },
            { "SELECT DUNGEON", "选择地下城" },
            { "NO DUNGEON", "没有地下城" },
            { "UP DOWN SELECT   ENTER GO   ESC TOWN", "上下 选择   回车 进入   Esc 返回城镇" },
            { "UP DOWN SELECT   LEFT RIGHT LEVEL   ENTER GO   ESC TOWN", "上下 选择   左右 难度   回车 进入   Esc 返回城镇" },
            { "NORMAL", "普通" },
            { "EXPERT", "冒险" },
            { "MASTER", "勇士" },
            { "KING", "王者" },
            { "HERO", "英雄" },
            { "DIFFICULTY NORMAL", "难度 普通" },
            { "DIFFICULTY EXPERT", "难度 冒险" },
            { "DIFFICULTY MASTER", "难度 勇士" },
            { "DIFFICULTY KING", "难度 王者" },
            { "DIFFICULTY HERO", "难度 英雄" },
            { "NEW LEVEL OPENED NORMAL", "新开放难度 普通" },
            { "NEW LEVEL OPENED EXPERT", "新开放难度 冒险" },
            { "NEW LEVEL OPENED MASTER", "新开放难度 勇士" },
            { "NEW LEVEL OPENED KING", "新开放难度 王者" },
            { "NEW LEVEL OPENED HERO", "新开放难度 英雄" },
            { "CREATE", "创建" },
            { "DELETE", "删除" },
            { "CHANNEL", "频道" },
            { "EXIT", "退出" },
            { "QUICK PARTY", "快速组队" },
            { "ABYSS PARTY", "深渊组队" },
        };

        private static readonly string[,] PatternPairs =
        {
            { "EXP {0}  GOLD {1}", "经验 {0}  金币 {1}" },
            { "PROGRESS {0}", "进度 {0}" },
            { "SP {0}  AP {1}", "技能点 {0}  觉醒点 {1}" },
            { "NEXT COST SP {0}", "下一级花费 技能点 {0}" },
            { "NEXT COST AP {0}", "下一级花费 觉醒点 {0}" },
            { "GOLD {0}", "金币 {0}" },
            { "COINS {0}", "复活币 {0}" },
            { "LEVEL {0}", "等级 {0}" },
            { "ATK P +{0}", "物理攻击 +{0}" },
            { "ATK M +{0}", "魔法攻击 +{0}" },
            { "LV {0}  GRADE {1}  RARITY {2}  PRICE {3}", "等级 {0}  品级 {1}  稀有度 {2}  价格 {3}" },
            { "NEED LEVEL {0}", "需要等级 {0}" },
            { "PAGE {0}", "第 {0} 页" },
            { "LV {0}", "等级 {0}" },
            { "BUY {0}", "买入价 {0}" },
            { "SELL {0}", "收购价 {0}" },
            { "OWN {0}", "持有 {0}" },
            { "MAX {0}", "最多 {0}" },
            { "UNIT {0}  COST {1}", "单价 {0}  花费 {1}" },
            { "UNIT {0}  GET {1}", "单价 {0}  获得 {1}" },
            { "BOUGHT {0} FOR {1}", "买入 {0} 件,花费 {1} 金币" },
            { "SOLD {0} FOR {1}", "卖出 {0} 件,获得 {1} 金币" },
            { "CLEAR TIME {0}", "通关用时 {0}" },
            { "KILLS {0}", "击杀 {0}" },
            { "EXP +{0}", "经验 +{0}" },
            { "GOLD +{0}", "金币 +{0}" },
            { "ITEMS {0}", "物品 {0}" },

            // 通关结算(M32;"RANK BONUS" 要排在 "RANK {0}" 前面,句式按字面开头逐条比)
            { "CLEAR EXP +{0}", "通关经验 +{0}" },
            { "RANK BONUS +{0}", "评分加成 +{0}" },
            { "RANK {0}", "评分 {0}" },
            { "LEVELED UP TO {0}", "升到 {0} 级" },
        };

        private static readonly Dictionary<string, string> Exact = BuildExact();
        private static readonly Pattern[] Patterns = BuildPatterns();

        /// <summary>整句对照表的条数(测试用)。</summary>
        public static int ExactCount => Exact.Count;

        /// <summary>句式对照表的条数(测试用)。</summary>
        public static int PatternCount => Patterns.Length;

        /// <summary>占位英文换成中文;认不出来(含已经是中文、数据文字、数字)原样返回。</summary>
        public static string Translate(string english)
        {
            if (string.IsNullOrEmpty(english) || !HasAsciiLetter(english))
            {
                return english;
            }
            if (Exact.TryGetValue(english, out string chinese))
            {
                return chinese;
            }
            foreach (Pattern p in Patterns)
            {
                if (p.TryTranslate(english, out chinese))
                {
                    return chinese;
                }
            }
            return english;
        }

        /// <summary>这句话有没有中文版(整句或句式)。</summary>
        public static bool HasTranslation(string english)
        {
            return !string.IsNullOrEmpty(english) && !string.Equals(Translate(english), english, StringComparison.Ordinal);
        }

        /// <summary>纯数字与符号的文字(伤害数字、倒计时一类,每帧都变)不可能是占位词,先挡掉,省得每帧去比对表。</summary>
        private static bool HasAsciiLetter(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, string> BuildExact()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ExactPairs.GetLength(0); i++)
            {
                map.Add(ExactPairs[i, 0], ExactPairs[i, 1]);
            }
            return map;
        }

        private static Pattern[] BuildPatterns()
        {
            var list = new Pattern[PatternPairs.GetLength(0)];
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new Pattern(PatternPairs[i, 0], PatternPairs[i, 1]);
            }
            return list;
        }

        /// <summary>带 {0} {1} 空位的句式:英文句式拆成"字面段"和"空位",依次在文字里找,空位取两段字面之间的内容(不能为空)。</summary>
        private sealed class Pattern
        {
            private readonly string[] _literals;
            private readonly string _chinese;

            public Pattern(string english, string chinese)
            {
                _chinese = chinese;
                var parts = new List<string>();
                var current = new StringBuilder();
                int i = 0;
                while (i < english.Length)
                {
                    if (english[i] == '{' && i + 2 < english.Length && english[i + 2] == '}' && char.IsDigit(english[i + 1]))
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                        i += 3;
                        continue;
                    }
                    current.Append(english[i]);
                    i++;
                }
                parts.Add(current.ToString());
                _literals = parts.ToArray();
            }

            public bool TryTranslate(string text, out string chinese)
            {
                chinese = null;
                int holes = _literals.Length - 1;
                if (holes < 1 || !text.StartsWith(_literals[0], StringComparison.Ordinal))
                {
                    return false;
                }
                var values = new string[holes];
                int pos = _literals[0].Length;
                for (int h = 0; h < holes; h++)
                {
                    string next = _literals[h + 1];
                    bool last = h == holes - 1;
                    int end;
                    if (last)
                    {
                        if (next.Length == 0)
                        {
                            end = text.Length;
                        }
                        else if (text.EndsWith(next, StringComparison.Ordinal) && text.Length - next.Length > pos)
                        {
                            end = text.Length - next.Length;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (next.Length == 0 || pos + 1 > text.Length)
                        {
                            return false;
                        }
                        end = text.IndexOf(next, pos + 1, StringComparison.Ordinal);
                    }
                    if (end <= pos)
                    {
                        return false;
                    }
                    values[h] = text.Substring(pos, end - pos);
                    pos = end + next.Length;
                }
                // 一遍扫完中文模板,避免替换进去的值里恰好含有 {n} 时被再替换一次
                var result = new StringBuilder(_chinese.Length + 16);
                for (int i = 0; i < _chinese.Length; i++)
                {
                    char c = _chinese[i];
                    if (c == '{' && i + 2 < _chinese.Length && _chinese[i + 2] == '}' && char.IsDigit(_chinese[i + 1]) && _chinese[i + 1] - '0' < holes)
                    {
                        result.Append(values[_chinese[i + 1] - '0']);
                        i += 2;
                        continue;
                    }
                    result.Append(c);
                }
                chinese = result.ToString();
                return true;
            }
        }
    }
}

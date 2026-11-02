using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一对"最小值、最大值":.mob 里按怪物等级在 [level] 范围内线性插值的取值(见 <see cref="MonsterAbility.Lerp"/>)。
    /// </summary>
    public readonly struct IntPair
    {
        public IntPair(int min, int max)
        {
            Min = min;
            Max = max;
        }

        public int Min { get; }
        public int Max { get; }

        public override string ToString()
        {
            return Min + ".." + Max;
        }
    }

    /// <summary>
    /// 一个怪物脚本 .mob 的定义。编号经 monster/monster.lst 找到 .mob;动画与攻击信息路径相对 .mob 所在目录。
    /// 取值来源与默认值:服务端加载函数(ImportMonsterScript)按固定偏移把标签存进脚本结构,
    /// 结构的初始化函数(Clear)给出每个字段在标签缺省时的取值——[sight] 200、[warlike] 50、[attack delay] 1000、
    /// [targeting time term] 1000、[width] 40 20、[jump speed] 100 100,其余成对的数值都是 0(证据 E-062)。
    /// 本类只做"读",不解释:怪物的最终属性由 <see cref="MonsterAbility"/> 计算,行为由 .act 与 .ai 决定。
    /// 全库 2,070 个 .mob 里有 272 种标签;没有列在这里的标签仍可以通过 <see cref="ObjectDef.Tags"/> 读到。
    /// </summary>
    public sealed class MonsterDef : ObjectDef
    {
        /// <summary>四种元素抗性的标签,下标 0..3 依次是 火、水、暗、光(服务端存放顺序;与 <see cref="MonsterAbility"/> 的元素下标一致)。</summary>
        public static readonly string[] ElementResistanceTags =
        {
            "[fire resistance]", "[water resistance]", "[dark resistance]", "[light resistance]",
        };

        /// <summary>四种元素攻击的标签,顺序同 <see cref="ElementResistanceTags"/>。</summary>
        public static readonly string[] ElementAttackTags =
        {
            "[fire attack]", "[water attack]", "[dark attack]", "[light attack]",
        };

        /// <summary>
        /// 17 种异常状态抗性的标签,按服务端存放顺序(下标 13 的 [stuck resistance] 在按等级加成时被排除,见 <see cref="MonsterAbility"/>)。
        /// 顺序取自服务端二进制里的字符串表(缓慢、冰冻、中毒、眩晕、诅咒、致盲、感电、石化、睡眠、灼烧、武器破坏、流血、穿刺、硬直、混乱、束缚、防具破坏)。
        /// </summary>
        public static readonly string[] StatusResistanceTags =
        {
            "[slow resistance]", "[freeze resistance]", "[poison resistance]", "[stun resistance]", "[curse resistance]",
            "[blind resistance]", "[lightning resistance]", "[stone resistance]", "[sleep resistance]", "[burn resistance]",
            "[weapon break resistance]", "[bleeding resistance]", "[piercing resistance]", "[stuck resistance]",
            "[confuse resistance]", "[hold resistance]", "[armor break resistance]",
        };

        /// <summary>[ai pattern] 里按难度分的子块名(服务端同样只认这五个):easy、medium、hard、ultimate、hero。</summary>
        public static readonly string[] AiDifficultyNames = { "easy", "medium", "hard", "ultimate", "hero" };

        public MonsterDef(int id, string path, ScriptLine[] lines)
            : base(path, lines)
        {
            Id = id;
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            BaseName = dot > 0 ? file.Substring(0, dot) : file;

            Level = Pair("[level]", 0, 0);
            ElementResistance = PairArray(ElementResistanceTags);
            StatusResistance = PairArray(StatusResistanceTags);
            ElementAttack = PairArray(ElementAttackTags);
            HpRegenSpeed = Pair("[HP regen speed]", 0, 0);
            MpRegenSpeed = Pair("[MP regen speed]", 0, 0);
            MoveSpeed = Pair("[move speed]", 0, 0);
            AttackSpeed = Pair("[attack speed]", 0, 0);
            CastSpeed = Pair("[cast speed]", 0, 0);
            HitRecovery = Pair("[hit recovery]", 0, 0);
            JumpPower = Pair("[jump power]", 0, 0);
            JumpSpeed = Pair("[jump speed]", 100, 100);
            NormalDashSpeed = Pair("[normal dash speed]", 0, 0);
            SlantDashSpeed = Pair("[slant dash speed]", 0, 0);
            Weight = Pair("[weight]", 0, 0);
            Rigidity = Pair("[rigidity]", 0, 0);
            Width = Pair("[width]", 40, 20);
            AbilityTable = Tags.IntOr("[ability table]", 0);
            AbilityCategories = ParseAbilityCategories(lines);
            BaseTablePaths = Tags.Strings("[base table]");
            CategoryNames = BlockStrings("[category]", "[/category]");
            AttackActions = BlockStrings("[attack action]", "[/attack action]");
            ActionList = BlockStrings("[action list]", "[/action list]");
            EtcActions = BlockStrings("[etc action]", "[/etc action]");
            Cooltimes = BlockInts("[cooltime]", "[/cooltime]");
            AttackMotions = BlockStrings("[attack motion]", "[/attack motion]");
            AttackInfos = BlockStrings("[attack info]", "[/attack info]");
            AiPatterns = ParseAiPatterns(lines);
        }

        public int Id { get; }

        /// <summary>文件名去掉扩展名(规范化后为小写)。</summary>
        public string BaseName { get; }

        /// <summary>[name] 的字符串键(形如 "name_660",取自字符串引用的键部分);没有则 null。名字文本在另一份语言表里,这里不解析。</summary>
        public string NameKey
        {
            get
            {
                if (Tags.TryGetLine("[name]", out ScriptLine line))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.Keyed || v.Kind == ScriptValueKind.String)
                        {
                            return v.Text;
                        }
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// [face image]:怪物头像所在的图(形如 "Monster/MonsterFace.img",原样保留大小写)与帧号(证据 E-106:目标血条窗口里的 24×24 小头像);
        /// 没写这个标签则 <see cref="FaceImage"/> 为 null、<see cref="FaceIndex"/> 为 -1。
        /// </summary>
        public string FaceImage => Tags.FirstString("[face image]");

        public int FaceIndex
        {
            get
            {
                int[] v = Tags.Ints("[face image]");
                return FaceImage != null && v.Length > 0 ? v[0] : -1;
            }
        }

        /// <summary>[level]:怪物等级范围(缺省 0 0)。怪物实际等级由出场它的地图给出,属性在这个范围内插值。</summary>
        public IntPair Level { get; }

        /// <summary>火、水、暗、光四种元素抗性(每项一对最小/最大),下标见 <see cref="ElementResistanceTags"/>。</summary>
        public IntPair[] ElementResistance { get; }

        /// <summary>17 种异常状态抗性,下标见 <see cref="StatusResistanceTags"/>。</summary>
        public IntPair[] StatusResistance { get; }

        /// <summary>火、水、暗、光四种元素攻击,下标同 <see cref="ElementResistance"/>。</summary>
        public IntPair[] ElementAttack { get; }

        public IntPair HpRegenSpeed { get; }
        public IntPair MpRegenSpeed { get; }
        public IntPair MoveSpeed { get; }
        public IntPair AttackSpeed { get; }
        public IntPair CastSpeed { get; }

        /// <summary>[hit recovery]:受击恢复(名字取自标签;数值的单位与对硬直时间的作用属推断,见 docs/hit-pipeline.md)。</summary>
        public IntPair HitRecovery { get; }

        public IntPair JumpPower { get; }
        public IntPair JumpSpeed { get; }
        public IntPair NormalDashSpeed { get; }
        public IntPair SlantDashSpeed { get; }
        public IntPair Weight { get; }
        public IntPair Rigidity { get; }

        /// <summary>[width]:两个整数(服务端缺省 40 与 20);取向推断为横向与纵深方向的碰撞尺寸。</summary>
        public IntPair Width { get; }

        /// <summary>
        /// [life max]:服务端把它的值取低字节存进脚本结构 +0x3B8(缺省 1),数值块的字节标志(<see cref="MonsterStats.IxFlag"/>)从这里来。
        /// 名字取自标签,字节的含义没有读出(全库只有 3 个怪物写了它)。
        /// </summary>
        public int LifeMax => Tags.IntOr("[life max]", 1);

        /// <summary>[ability table]:选择基础属性表的编号(0 = 缺省;1 = 召唤物表,见 <see cref="MonsterAbility.SelectTable"/>)。</summary>
        public int AbilityTable { get; }

        /// <summary>[ability category]:对基础属性的百分比或加法修正,按出现顺序。</summary>
        public AbilityCategoryEntry[] AbilityCategories { get; }

        /// <summary>[base table]:这个怪物自己的基础属性表路径(相对 monster/ 目录),可有多个;没有则为空数组。</summary>
        public string[] BaseTablePaths { get; }

        /// <summary>[category] 块里的类别字符串,如 "[melee combat]"(块外的值不算)。</summary>
        public string[] CategoryNames { get; }

        /// <summary>[attack action] 块里的 .act 路径(原样,相对 .mob 目录)。</summary>
        public string[] AttackActions { get; }

        /// <summary>[action list] 块里的 .act 路径(原样):与 [attack action] 二选一的另一种写法,脚本怪物里 214 个用它(证据 E-128)。</summary>
        public string[] ActionList { get; }

        /// <summary>
        /// [etc action] 块里的 .act 路径(原样,相对 .mob 目录):怪物的自定义动作列表,脚本 [SET ACTION] [CUSTOM] n 与 [TRIGGER] [CUSTOM] a i 的 n、a 就是它的下标
        /// (M62-b,证据 E-159;对应关系是推断,依据全库扫描:怪物自己的 .act 里 [CUSTOM] n 的 n 落在这张表里的占绝大多数)。没有这个块是空数组。
        /// </summary>
        public string[] EtcActions { get; }

        /// <summary>规范化的 [etc action] 路径;空串或越界返回 null。</summary>
        public string EtcActionPath(int index)
        {
            return ResolveAt(EtcActions, index);
        }

        /// <summary>
        /// 脚本怪物的招式列表:[attack action] 与 [action list] 二选一(两个标签不会同时出现),.ai 的动作脚本返回的就是这个列表的下标;
        /// [attack info]、[cooltime] 与它同下标(证据 E-128)。
        /// </summary>
        public string[] AttackList => AttackActions.Length > 0 ? AttackActions : ActionList;

        /// <summary>[cooltime] 块里的整数(毫秒,与招式列表同下标;0 表示没有冷却);没有该块时为空数组。</summary>
        public int[] Cooltimes { get; }

        /// <summary>[attack motion] 块里的 .ani 路径(原样)。</summary>
        public string[] AttackMotions { get; }

        /// <summary>[attack info] 块里的 .atk 路径(原样;空字符串表示该位没有攻击信息)。</summary>
        public string[] AttackInfos { get; }

        /// <summary>[ai pattern] 里各难度的 .ai 路径列表(键为 easy、medium、hard、ultimate、hero;没有该难度时不含键)。</summary>
        public Dictionary<string, string[]> AiPatterns { get; }

        /// <summary>[sight]:服务端缺省 200。</summary>
        public int Sight => Tags.IntOr("[sight]", 200);

        /// <summary>[vision]:全库 1,257 个 .mob 使用(与 [sight] 不同写法,含义属推断:视野距离);没有则 0。</summary>
        public int Vision => Tags.IntOr("[vision]", 0);

        /// <summary>
        /// [superarmor on attack](E-143):第一个整数非 0 表示这个怪物出招期间(客户端 STATE_ATTACK = 8)带档位 1 的霸体——客户端在进入出招状态时打开、
        /// 离开出招状态时关闭;这一档霸体被攻击信息的 [ignore super armor] 无视(见 <c>docs/hit-pipeline.md</c> §26)。
        /// 第二个整数(<see cref="SuperArmorValue"/>)在打开时加进怪物属性组里的一个数值槽,哪个属性没读出来。全库 34 个怪物写了它(本机脚本扫描汇总,非 REA)。
        /// </summary>
        public bool SuperArmorOnAttack
        {
            get
            {
                int[] v = Tags.Ints("[superarmor on attack]");
                return v.Length > 0 && v[0] != 0;
            }
        }

        /// <summary>[superarmor on attack] 的第二个整数(缺省 0;含义没读出来,不参与运行期)。</summary>
        public int SuperArmorValue
        {
            get
            {
                int[] v = Tags.Ints("[superarmor on attack]");
                return v.Length > 1 ? v[1] : 0;
            }
        }

        /// <summary>[warlike]:服务端缺省 50(好战程度,名字取自标签,数值含义属推断)。</summary>
        public int Warlike => Tags.IntOr("[warlike]", 50);

        /// <summary>[attack delay]:服务端缺省 1000(毫秒,推断)。</summary>
        public int AttackDelay => Tags.IntOr("[attack delay]", 1000);

        /// <summary>[think term]:没有则 0(毫秒,推断)。</summary>
        public int ThinkTerm => Tags.IntOr("[think term]", 0);

        /// <summary>[destination change term]:重选目的地的最短间隔(毫秒);没有则 0(证据 E-128,控制器里是 [destination change term] 门控)。</summary>
        public int DestinationChangeTerm => Tags.IntOr("[destination change term]", 0);

        /// <summary>[keep distance with target]:选目的地方案 1 里与目标保持的水平距离(像素,推断);没有则 0。</summary>
        public int KeepDistanceWithTarget => Tags.IntOr("[keep distance with target]", 0);

        /// <summary>[floating height]:离地高度,没有则 0。</summary>
        public int FloatingHeight => Tags.IntOr("[floating height]", 0);

        /// <summary>[appear sound]:出场时放的声音标签(audio.xml 里的 ID,证据 E-089);没写为 null。出场的确切时刻没有读,按进房间时放处理。</summary>
        public string AppearSound => Tags.FirstString("[appear sound]");

        /// <summary>[damage sound]:受击时放的声音标签;没写为 null。</summary>
        public string DamageSound => Tags.FirstString("[damage sound]");

        /// <summary>[die sound]:死亡时放的声音标签;没写为 null。</summary>
        public string DieSound => Tags.FirstString("[die sound]");

        /// <summary>[attack sound]:出招时放的声音标签;没写为 null。</summary>
        public string AttackSound => Tags.FirstString("[attack sound]");

        /// <summary>[ambient sound]:活着时偶尔放的环境声音标签;没写为 null。</summary>
        public string AmbientSound => Tags.FirstString("[ambient sound]");

        /// <summary>[stuckbonus on damage] 的四个整数(没有则四个 0)。</summary>
        public int[] StuckBonusOnDamage
        {
            get
            {
                int[] v = Tags.Ints("[stuckbonus on damage]");
                var r = new int[4];
                for (int i = 0; i < 4 && i < v.Length; i++)
                {
                    r[i] = v[i];
                }
                return r;
            }
        }

        /// <summary>某个单项动画标签(如 "[waiting motion]")的路径(相对 .mob 解析为规范化路径);没有则 null。</summary>
        public string ActionPath(string tag)
        {
            return MotionPath(tag);
        }

        /// <summary>[waiting action]、[move action] 等 .act 路径(规范化);没有则 null。</summary>
        public string WaitingActionPath => MotionPath("[waiting action]");
        public string MoveActionPath => MotionPath("[move action]");
        public string SitActionPath => MotionPath("[sit action]");
        public string Damage1ActionPath => MotionPath("[damage action 1]");
        public string Damage2ActionPath => MotionPath("[damage action 2]");
        public string DownActionPath => MotionPath("[down action]");
        public string OverturnActionPath => MotionPath("[overturn action]");

        /// <summary>规范化的 [attack action] 路径(跳过空串)。</summary>
        public string AttackActionPath(int index)
        {
            return ResolveAt(AttackActions, index);
        }

        /// <summary>规范化的 [attack info] 路径;空串或越界返回 null。</summary>
        public string AttackInfoPath(int index)
        {
            return ResolveAt(AttackInfos, index);
        }

        /// <summary>规范化的 [attack motion] 路径;空串或越界返回 null。</summary>
        public string AttackMotionPath(int index)
        {
            return ResolveAt(AttackMotions, index);
        }

        /// <summary>[ai pattern] 在某个难度下的规范化 .ai 路径列表;没有该难度时返回空数组。</summary>
        public string[] AiPaths(string difficulty)
        {
            if (!AiPatterns.TryGetValue(difficulty, out string[] raw))
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(raw.Length);
            foreach (string s in raw)
            {
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(PvfPath.ResolveRelativeTo(Path, s));
                }
            }
            return list.ToArray();
        }

        private string ResolveAt(string[] items, int index)
        {
            if ((uint)index >= (uint)items.Length || string.IsNullOrEmpty(items[index]))
            {
                return null;
            }
            return PvfPath.ResolveRelativeTo(Path, items[index]);
        }

        /// <summary>读一个成对标签的两个整数;标签不存在用缺省;只有一个整数时最大值取最小值。</summary>
        private IntPair Pair(string tag, int defMin, int defMax)
        {
            int[] v = Tags.Ints(tag);
            if (v.Length == 0)
            {
                return new IntPair(defMin, defMax);
            }
            return new IntPair(v[0], v.Length > 1 ? v[1] : v[0]);
        }

        private IntPair[] PairArray(string[] tags)
        {
            var r = new IntPair[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                r[i] = Pair(tags[i], 0, 0);
            }
            return r;
        }

        /// <summary>取"开标签"到"闭标签"之间每一行里的所有字符串值;块内的值也包括开标签那一行自带的值。</summary>
        private string[] BlockStrings(string open, string close)
        {
            var list = new List<string>();
            ScriptLine[] lines = Tags.Lines;
            int i = 0;
            while (i < lines.Length && !lines[i].TagIs(open))
            {
                i++;
            }
            if (i == lines.Length)
            {
                return Array.Empty<string>();
            }
            for (; i < lines.Length && !lines[i].TagIs(close); i++)
            {
                foreach (ScriptValue v in lines[i].Values)
                {
                    if (v.Kind == ScriptValueKind.String && v.Text != null)
                    {
                        list.Add(v.Text);
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>取"开标签"到"闭标签"之间每一行里的所有整数值;没有开标签时返回空数组。</summary>
        private int[] BlockInts(string open, string close)
        {
            var list = new List<int>();
            ScriptLine[] lines = Tags.Lines;
            int i = 0;
            while (i < lines.Length && !lines[i].TagIs(open))
            {
                i++;
            }
            if (i == lines.Length)
            {
                return Array.Empty<int>();
            }
            for (; i < lines.Length && !lines[i].TagIs(close); i++)
            {
                foreach (ScriptValue v in lines[i].Values)
                {
                    if (v.IsNumber)
                    {
                        list.Add(v.Int);
                    }
                }
            }
            return list.ToArray();
        }

        private static AbilityCategoryEntry[] ParseAbilityCategories(ScriptLine[] lines)
        {
            var list = new List<AbilityCategoryEntry>();
            foreach (ScriptLine line in lines)
            {
                if (!line.TagIs("[ability category]"))
                {
                    continue;
                }
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    if (v[i].Kind != ScriptValueKind.String || v[i + 1].Kind != ScriptValueKind.String || !v[i + 2].IsNumber)
                    {
                        break;
                    }
                    int type = MonsterAbility.CategoryIndex(v[i].Text);
                    if (type < 0)
                    {
                        continue;
                    }
                    string op = v[i + 1].Text;
                    bool multiply = op != "+";
                    list.Add(new AbilityCategoryEntry(type, multiply, v[i + 2].Int));
                }
                break;
            }
            return list.ToArray();
        }

        private static Dictionary<string, string[]> ParseAiPatterns(ScriptLine[] lines)
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            int i = 0;
            while (i < lines.Length && !lines[i].TagIs("[ai pattern]"))
            {
                i++;
            }
            for (i++; i < lines.Length && !lines[i].TagIs("[/ai pattern]"); i++)
            {
                string tag = lines[i].Tag;
                if (tag == null || tag.Length < 3 || tag[0] != '[' || tag[1] == '/')
                {
                    continue;
                }
                string name = tag.Substring(1, tag.Length - 2).ToLowerInvariant();
                var paths = new List<string>();
                foreach (ScriptValue v in lines[i].Values)
                {
                    if (v.Kind == ScriptValueKind.String && v.Text != null)
                    {
                        paths.Add(v.Text);
                    }
                }
                if (!result.ContainsKey(name))
                {
                    result.Add(name, paths.ToArray());
                }
            }
            return result;
        }
    }

    /// <summary>[ability category] 里的一项:修正哪个属性(<see cref="MonsterAbility.CategoryNames"/> 的下标)、乘还是加、数值(百分数)。</summary>
    public readonly struct AbilityCategoryEntry
    {
        public AbilityCategoryEntry(int type, bool multiply, int value)
        {
            Type = type;
            Multiply = multiply;
            Value = value;
        }

        public int Type { get; }

        /// <summary>真:字段 = 字段 × (Value / 100);假:字段 += Value / 100(整数除法)。</summary>
        public bool Multiply { get; }

        public int Value { get; }
    }
}

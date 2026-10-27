using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game.Ai;
using Dnf.Script;

// 证据:脚本与数据文件(.chr、.skl、.atk、.ani、.lst)之间的契约见 E-036;.atk 的读法见 E-047。
namespace Dnf.Game
{
    /// <summary>
    /// 游戏数据的读取与缓存:角色(.chr)、技能(.skl)、攻击信息(.atk)、动画(.ani)与几张编号表(.lst)。
    /// 数据来自 <see cref="IScriptSource"/>(用户自备的 Script.pvf 或测试用的内存数据);读不到或格式不符时返回 null,不抛异常。
    /// 路径一律规范化(小写、'/' 分隔)。本类不含任何原版内容。
    /// </summary>
    public sealed class GameData
    {
        public const string StringTablePath = "stringtable.bin";
        public const string CharacterListPath = "character/character.lst";
        public const string SkillListPath = "skill/skilllist.lst";
        public const string PassiveObjectListPath = "passiveobject/passiveobject.lst";
        public const string MonsterListPath = "monster/monster.lst";

        private readonly IScriptSource _source;
        private PvfStringTable _strings;
        private bool _stringsTried;

        private readonly Dictionary<string, ScriptLine[]> _scripts = new Dictionary<string, ScriptLine[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, string>> _lists = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, AniFile> _anis = new Dictionary<string, AniFile>(StringComparer.Ordinal);
        private readonly Dictionary<int, CharacterDef> _characters = new Dictionary<int, CharacterDef>();
        private readonly Dictionary<long, SkillDef> _skills = new Dictionary<long, SkillDef>();
        private readonly Dictionary<string, AttackInfoDef> _attackInfos = new Dictionary<string, AttackInfoDef>(StringComparer.Ordinal);

        public GameData(IScriptSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public IScriptSource Source => _source;

        public bool TryReadBytes(string path, out byte[] data)
        {
            return _source.TryRead(PvfPath.Normalize(path), out data);
        }

        /// <summary>字符串表;读不到时为 null。</summary>
        public PvfStringTable Strings
        {
            get
            {
                if (!_stringsTried)
                {
                    _stringsTried = true;
                    if (_source.TryRead(StringTablePath, out byte[] data))
                    {
                        try
                        {
                            _strings = new PvfStringTable(data);
                        }
                        catch (DnfFormatException)
                        {
                            _strings = null;
                        }
                    }
                }
                return _strings;
            }
        }

        /// <summary>读一份标记流脚本并按标签行拆开;文件不存在、不是标记流或没有字符串表时返回 null。结果会缓存。</summary>
        public ScriptLine[] ReadScript(string path)
        {
            string key = PvfPath.Normalize(path);
            if (_scripts.TryGetValue(key, out ScriptLine[] cached))
            {
                return cached;
            }
            ScriptLine[] lines = null;
            PvfStringTable strings = Strings;
            if (strings != null && _source.TryRead(key, out byte[] data))
            {
                try
                {
                    lines = ScriptDocument.Parse(data, strings);
                }
                catch (DnfFormatException)
                {
                    lines = null;
                }
            }
            _scripts[key] = lines;
            return lines;
        }

        /// <summary>读一份"编号 路径"列表(*.lst)。</summary>
        public Dictionary<int, string> ReadList(string path)
        {
            string key = PvfPath.Normalize(path);
            if (_lists.TryGetValue(key, out Dictionary<int, string> cached))
            {
                return cached;
            }
            ScriptLine[] lines = ReadScript(key);
            Dictionary<int, string> list = lines == null ? null : TagTable.ParseList(lines);
            _lists[key] = list;
            return list;
        }

        /// <summary>读一个二进制 .ani;不存在或不是二进制格式(全库有 70 个不是)时返回 null。结果会缓存。</summary>
        public AniFile GetAnimation(string path)
        {
            if (path == null)
            {
                return null;
            }
            string key = PvfPath.Normalize(path);
            if (_anis.TryGetValue(key, out AniFile cached))
            {
                return cached;
            }
            AniFile ani = null;
            if (_source.TryRead(key, out byte[] data))
            {
                try
                {
                    ani = AniFile.Parse(data);
                }
                catch (DnfFormatException)
                {
                    ani = null;
                }
            }
            _anis[key] = ani;
            return ani;
        }

        /// <summary>职业编号对应的角色定义(经 character/character.lst 找到 .chr);找不到返回 null。</summary>
        public CharacterDef GetCharacter(int job)
        {
            if (_characters.TryGetValue(job, out CharacterDef cached))
            {
                return cached;
            }
            CharacterDef def = null;
            Dictionary<int, string> list = ReadList(CharacterListPath);
            if (list != null && list.TryGetValue(job, out string relative))
            {
                string path = PvfPath.ResolveRelativeTo(CharacterListPath, relative);
                ScriptLine[] lines = ReadScript(path);
                if (lines != null)
                {
                    def = new CharacterDef(job, path, lines);
                }
            }
            _characters[job] = def;
            return def;
        }

        /// <summary>某职业的某个技能编号对应的技能定义(经 skill/skilllist.lst 找到该职业的列表,再找到 .skl);找不到返回 null。</summary>
        public SkillDef GetSkill(int job, int skillId)
        {
            long key = ((long)job << 32) | (uint)skillId;
            if (_skills.TryGetValue(key, out SkillDef cached))
            {
                return cached;
            }
            SkillDef def = null;
            Dictionary<int, string> perJob = ReadList(SkillListPath);
            if (perJob != null && perJob.TryGetValue(job, out string listFile))
            {
                string listPath = PvfPath.ResolveRelativeTo(SkillListPath, listFile);
                Dictionary<int, string> skills = ReadList(listPath);
                if (skills != null && skills.TryGetValue(skillId, out string relative))
                {
                    string path = PvfPath.ResolveRelativeTo(SkillListPath, relative);
                    ScriptLine[] lines = ReadScript(path);
                    if (lines != null)
                    {
                        def = new SkillDef(skillId, path, lines);
                    }
                }
            }
            _skills[key] = def;
            return def;
        }

        /// <summary>某职业技能列表里所有的技能编号(经 skill/skilllist.lst 找到该职业的列表),从小到大;找不到列表返回空数组。</summary>
        public int[] GetSkillIds(int job)
        {
            Dictionary<int, string> perJob = ReadList(SkillListPath);
            if (perJob == null || !perJob.TryGetValue(job, out string listFile))
            {
                return Array.Empty<int>();
            }
            Dictionary<int, string> skills = ReadList(PvfPath.ResolveRelativeTo(SkillListPath, listFile));
            if (skills == null)
            {
                return Array.Empty<int>();
            }
            var ids = new int[skills.Count];
            skills.Keys.CopyTo(ids, 0);
            Array.Sort(ids);
            return ids;
        }

        /// <summary>读一个 .atk 攻击信息(规范化路径或相对路径均可,调用方应先解析成完整路径);找不到返回 null。</summary>
        public AttackInfoDef GetAttackInfo(string path)
        {
            if (path == null)
            {
                return null;
            }
            string key = PvfPath.Normalize(path);
            if (_attackInfos.TryGetValue(key, out AttackInfoDef cached))
            {
                return cached;
            }
            AttackInfoDef def = null;
            ScriptLine[] lines = ReadScript(key);
            if (lines != null)
            {
                AttackInfoData data = null;
                AttackInfoLoadReport report = null;
                PvfStringTable strings = Strings;
                if (strings != null && _source.TryRead(key, out byte[] raw) && TokenScript.IsTokenScript(raw))
                {
                    try
                    {
                        data = AttackInfoLoader.Load(new ScriptCursor(TokenScript.Parse(raw), strings), out report);
                    }
                    catch (DnfFormatException)
                    {
                        data = null;
                        report = null;
                    }
                }
                def = new AttackInfoDef(key, lines, data, report);
            }
            _attackInfos[key] = def;
            return def;
        }

        /// <summary>被动对象编号对应的 .obj 路径(经 passiveobject/passiveobject.lst);找不到返回 null。</summary>
        public string GetPassiveObjectPath(int id)
        {
            Dictionary<int, string> list = ReadList(PassiveObjectListPath);
            if (list != null && list.TryGetValue(id, out string relative))
            {
                return PvfPath.ResolveRelativeTo(PassiveObjectListPath, relative);
            }
            return null;
        }

        private readonly Dictionary<int, PassiveObjectDef> _passiveObjects = new Dictionary<int, PassiveObjectDef>();

        /// <summary>被动对象编号对应的 .obj 定义;找不到返回 null。结果会缓存。</summary>
        public PassiveObjectDef GetPassiveObject(int id)
        {
            if (_passiveObjects.TryGetValue(id, out PassiveObjectDef cached))
            {
                return cached;
            }
            PassiveObjectDef def = null;
            string path = GetPassiveObjectPath(id);
            if (path != null)
            {
                ScriptLine[] lines = ReadScript(path);
                if (lines != null)
                {
                    def = new PassiveObjectDef(id, path, lines);
                }
            }
            _passiveObjects[id] = def;
            return def;
        }

        private readonly Dictionary<string, ParticleDef> _particles = new Dictionary<string, ParticleDef>(StringComparer.Ordinal);

        /// <summary>读一个 .ptl 粒子定义(完整路径;相对路径调用方先用 <see cref="PvfPath.ResolveRelativeTo"/> 解析);找不到返回 null。结果会缓存。</summary>
        public ParticleDef GetParticle(string path)
        {
            if (path == null)
            {
                return null;
            }
            string key = PvfPath.Normalize(path);
            if (_particles.TryGetValue(key, out ParticleDef cached))
            {
                return cached;
            }
            ScriptLine[] lines = ReadScript(key);
            ParticleDef def = lines == null ? null : ParticleDef.Parse(key, lines);
            _particles[key] = def;
            return def;
        }

        /// <summary>
        /// 读 .mob 里 [passive object filename] 写的 .ptl:先按相对 .mob 所在目录解析,没有再按 PVF 根目录下的完整路径找(两种写法在数据里都有,同一个引用不会两种都存在,证据 E-103)。
        /// 读不到返回 null。
        /// </summary>
        public ParticleDef GetParticleFor(string mobPath, string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }
            return GetParticle(PvfPath.ResolveRelativeTo(mobPath, file)) ?? GetParticle(PvfPath.Normalize(file));
        }

        /// <summary>怪物编号对应的 .mob 路径(经 monster/monster.lst);找不到返回 null。</summary>
        public string GetMonsterPath(int id)
        {
            Dictionary<int, string> list = ReadList(MonsterListPath);
            if (list != null && list.TryGetValue(id, out string relative))
            {
                return PvfPath.ResolveRelativeTo(MonsterListPath, relative);
            }
            return null;
        }

        /// <summary>monster/monster.lst 里的全部怪物编号(按编号升序);读不到列表时为空。</summary>
        public int[] MonsterIds()
        {
            Dictionary<int, string> list = ReadList(MonsterListPath);
            if (list == null)
            {
                return Array.Empty<int>();
            }
            var ids = new List<int>(list.Keys);
            ids.Sort();
            return ids.ToArray();
        }

        private readonly Dictionary<int, MonsterDef> _monsters = new Dictionary<int, MonsterDef>();

        /// <summary>怪物编号对应的 .mob 定义;找不到返回 null。结果会缓存。</summary>
        public MonsterDef GetMonster(int id)
        {
            if (_monsters.TryGetValue(id, out MonsterDef cached))
            {
                return cached;
            }
            MonsterDef def = null;
            string path = GetMonsterPath(id);
            if (path != null)
            {
                ScriptLine[] lines = ReadScript(path);
                if (lines != null)
                {
                    def = new MonsterDef(id, path, lines);
                }
            }
            _monsters[id] = def;
            return def;
        }

        // ------------------------------------------------------------------ .ai 决策树脚本(证据 E-052、E-128)

        private AiConstants _aiConstants;
        private bool _aiConstantsTried;
        private readonly Dictionary<string, AiScript> _aiScripts = new Dictionary<string, AiScript>(StringComparer.Ordinal);

        /// <summary>AICharacter/OperatingValue.lst 里的"名字 → 数值"名表;读不到时为 null(这时脚本里的名字参数不查表)。</summary>
        public AiConstants AiConstants
        {
            get
            {
                if (!_aiConstantsTried)
                {
                    _aiConstantsTried = true;
                    PvfStringTable strings = Strings;
                    if (strings != null && _source.TryRead(AiConstants.ListPath, out byte[] data))
                    {
                        try
                        {
                            _aiConstants = AiConstants.Parse(data, strings);
                        }
                        catch (DnfFormatException)
                        {
                            _aiConstants = null;
                        }
                    }
                }
                return _aiConstants;
            }
        }

        /// <summary>读并解析一份 .ai 决策树;文件不存在、不是标记流或没有字符串表时返回 null。结果会缓存。</summary>
        public AiScript GetAiScript(string path)
        {
            if (path == null)
            {
                return null;
            }
            string key = PvfPath.Normalize(path);
            if (_aiScripts.TryGetValue(key, out AiScript cached))
            {
                return cached;
            }
            AiScript script = null;
            PvfStringTable strings = Strings;
            if (strings != null && _source.TryRead(key, out byte[] data) && TokenScript.IsTokenScript(data))
            {
                try
                {
                    script = AiParser.Parse(data, strings, AiConstants);
                }
                catch (DnfFormatException)
                {
                    script = null;
                }
            }
            _aiScripts[key] = script;
            return script;
        }

        private MonsterBaseTables _monsterBaseTables;
        private HpGaugeFactors _hpGaugeFactors;
        private readonly Dictionary<MonsterDef, MonsterBaseTable> _ownTables = new Dictionary<MonsterDef, MonsterBaseTable>();

        /// <summary>目标怪物血条长度的三个系数(monster/monsterapcdifficultybonus.tbl 的 [adjust hp gauge];缺文件或缺块全是 1;证据 E-106)。</summary>
        public HpGaugeFactors HpGaugeFactors
        {
            get
            {
                if (_hpGaugeFactors == null)
                {
                    _hpGaugeFactors = HpGaugeFactors.Parse(ReadScript(HpGaugeFactors.Path));
                }
                return _hpGaugeFactors;
            }
        }

        /// <summary>五张公共怪物基础属性表(第一次用到时读取并缓存;缺的表为空表)。</summary>
        public MonsterBaseTables MonsterBaseTables
        {
            get
            {
                if (_monsterBaseTables == null)
                {
                    _monsterBaseTables = MonsterBaseTables.Load(this);
                }
                return _monsterBaseTables;
            }
        }

        /// <summary>
        /// 怪物自带的基础属性表([base table] 指向的 .tbl,路径相对 monster/ 目录)。有多个时按出现顺序加载,
        /// 后加载的覆盖相同行号的行(服务端每次从行 0 开始写)。没有 [base table] 或一张也读不到时返回 null。
        /// </summary>
        public MonsterBaseTable GetMonsterOwnTable(MonsterDef def)
        {
            if (def == null)
            {
                return null;
            }
            if (_ownTables.TryGetValue(def, out MonsterBaseTable cached))
            {
                return cached;
            }
            var rows = new List<MonsterBaseRow>();
            foreach (string relative in def.BaseTablePaths)
            {
                ScriptLine[] lines = ReadScript(PvfPath.ResolveRelativeTo(MonsterListPath, relative));
                if (lines == null)
                {
                    continue;
                }
                MonsterBaseTable table = MonsterBaseTable.Parse(lines);
                for (int i = 0; i < table.Count; i++)
                {
                    if (i < rows.Count)
                    {
                        rows[i] = table.Rows[i];
                    }
                    else
                    {
                        rows.Add(table.Rows[i]);
                    }
                }
            }
            MonsterBaseTable own = rows.Count > 0 ? new MonsterBaseTable(rows) : null;
            _ownTables[def] = own;
            return own;
        }

        // ------------------------------------------------------------------ 经验与升级(证据 E-111)

        private ExpTable _expTable;
        private SpTable _spTable;
        private ExpRates _expRates;
        private readonly Dictionary<CharacterDef, CharacterGrowth> _growths = new Dictionary<CharacterDef, CharacterGrowth>();

        /// <summary>升级经验表(character/exptable.tbl);缺文件是空表(没有下一级,升不了级)。</summary>
        public ExpTable ExpTable
        {
            get
            {
                if (_expTable == null)
                {
                    _expTable = ExpTable.Parse(ReadScript(ExpTable.Path));
                }
                return _expTable;
            }
        }

        /// <summary>每级技能点表(etc/sptable.etc 的 [sp table]);缺文件是空表(每级 0 点)。</summary>
        public SpTable SpTable
        {
            get
            {
                if (_spTable == null)
                {
                    _spTable = SpTable.Parse(ReadScript(SpTable.Path));
                }
                return _spTable;
            }
        }

        /// <summary>击杀经验的三张倍率表(etc/serverparameter.etc);缺文件或缺标签是全 1 的中性值。</summary>
        public ExpRates ExpRates
        {
            get
            {
                if (_expRates == null)
                {
                    _expRates = ExpRates.Parse(ReadScript(ExpRates.Path));
                }
                return _expRates;
            }
        }

        private RankSystem _rankSystem;

        /// <summary>副本评分的数据(etc/ranksysteminfo.etc,证据 E-124);缺文件是空数据(没有评分门槛、没有平均用时)。</summary>
        public RankSystem RankSystem
        {
            get
            {
                if (_rankSystem == null)
                {
                    _rankSystem = RankSystem.Parse(ReadScript(RankSystem.Path));
                }
                return _rankSystem;
            }
        }

        private MonsterDifficultyTable _monsterDifficulty;

        /// <summary>副本难度对怪物数值的加成表(monster/monsterapcdifficultybonus.tbl,证据 E-125);缺文件是空表(<see cref="MonsterDifficultyTable.Apply"/> 什么也不改)。</summary>
        public MonsterDifficultyTable MonsterDifficulty
        {
            get
            {
                if (_monsterDifficulty == null)
                {
                    _monsterDifficulty = MonsterDifficultyTable.Parse(ReadScript(MonsterDifficultyTable.Path));
                }
                return _monsterDifficulty;
            }
        }

        private UltimateDungeonList _ultimateDungeons;

        /// <summary>有"难度 3"的副本清单(etc/ultimatedungeonlist.etc,证据 E-125);缺文件是空清单(没有副本能开放难度 3)。</summary>
        public UltimateDungeonList UltimateDungeons
        {
            get
            {
                if (_ultimateDungeons == null)
                {
                    _ultimateDungeons = UltimateDungeonList.Parse(ReadScript(UltimateDungeonList.Path));
                }
                return _ultimateDungeons;
            }
        }

        /// <summary>角色的等级成长(.chr 的 [initial value] 与 [growtype N] 块);<paramref name="chr"/> 为 null 返回没有数据的成长。</summary>
        public CharacterGrowth GetGrowth(CharacterDef chr)
        {
            if (chr == null)
            {
                return CharacterGrowth.Parse(null);
            }
            if (!_growths.TryGetValue(chr, out CharacterGrowth growth))
            {
                growth = CharacterGrowth.Parse(chr.Tags.Lines);
                _growths[chr] = growth;
            }
            return growth;
        }
    }
}

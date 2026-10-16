using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Script;

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

        private MonsterBaseTables _monsterBaseTables;
        private readonly Dictionary<MonsterDef, MonsterBaseTable> _ownTables = new Dictionary<MonsterDef, MonsterBaseTable>();

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
    }
}

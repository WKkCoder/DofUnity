using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 按怪物编号造出房间里的一只怪物(M41 从 <see cref="RoomCombatSession.Create"/> 里原样抽出来):地图出生点与脚本召唤([SUMMON MONSTER],E-134)走同一条路,
    /// 读 .mob 模型、算数值(<see cref="MonsterAbility.Compute"/>,难度加成接在后面,E-125)、装攻击信息、默认 AI、投掷、.ai 脚本与面板。
    /// 模型按编号缓存(缺图或缺脚本的记成 null,不再重读)。出生点怪物的等级取 .mob 的 [level];召唤物的等级由脚本给。
    /// </summary>
    public sealed class RoomMonsterBuilder
    {
        /// <summary>召唤物等级的上限:客户端的创建槽把等级夹到 200 以内(静态读码,E-134)。</summary>
        public const int MaxSummonLevel = 200;

        private readonly DnfAssets _assets;
        private readonly GameData _data;
        private readonly MonsterCatalog _catalog;
        private readonly TownHero _hero;
        private readonly int _roomWidth;
        private readonly int _seed;
        private readonly int _difficulty;
        private readonly DungeonDifficultyBonus _dungeonBonus;
        private readonly int[] _heroWidth;
        private readonly Dictionary<int, MonsterModel> _models = new Dictionary<int, MonsterModel>();

        /// <param name="hero">主角(怪物的步速按他的速度换算)。</param>
        /// <param name="roomWidth">房间宽度(默认 AI 的游荡范围)。</param>
        /// <param name="heroWidth">主角 .chr 的 [width](碰撞宽度与进深),没有就是 null。</param>
        public RoomMonsterBuilder(DnfAssets assets, GameData data, MonsterCatalog catalog, TownHero hero, int roomWidth, int seed, int difficulty,
            DungeonDifficultyBonus dungeonBonus, int[] heroWidth)
        {
            _assets = assets;
            _data = data;
            _catalog = catalog;
            _hero = hero;
            _roomWidth = roomWidth;
            _seed = seed;
            _difficulty = difficulty;
            _dungeonBonus = dungeonBonus;
            _heroWidth = heroWidth;
        }

        private MonsterModel ModelOf(int code)
        {
            if (!_models.TryGetValue(code, out MonsterModel model))
            {
                string mobPath = _catalog.PathOf(code);
                model = mobPath == null ? null : MonsterModel.Read(_assets, mobPath);
                _models[code] = model;
            }
            return model;
        }

        /// <summary>这个编号能不能造出来(有模型、有脚本、有待机动画),顺便把模型读进缓存:预读召唤物用,免得第一次召唤那一帧才去读文件。</summary>
        public bool CanBuild(int code)
        {
            MonsterModel model = ModelOf(code);
            return model != null && model.Waiting != null && _data.GetMonster(code) != null;
        }

        /// <summary>
        /// 召唤物的等级夹到难度表这一档的行数以内(客户端 0x010b6e77 起:行数取自按难度分的五张表,超过就取行数),再不超过 <see cref="MaxSummonLevel"/>;
        /// 没有难度表(行数 0)时只受后一个上限(E-134)。
        /// </summary>
        public int ClampSummonLevel(int level)
        {
            return ClampSummonLevel(level, _data.MonsterDifficulty.RowCount(_difficulty));
        }

        /// <summary><see cref="ClampSummonLevel(int)"/> 的规则本身:<paramref name="tableRows"/> 是难度表这一档的行数(0 表示没有表)。</summary>
        public static int ClampSummonLevel(int level, int tableRows)
        {
            if (tableRows > 0 && level > tableRows)
            {
                level = tableRows;
            }
            return level > MaxSummonLevel ? MaxSummonLevel : level;
        }

        /// <summary>
        /// 造出下标为 <paramref name="index"/> 的怪物;缺图、缺脚本或没有待机动画的返回 null。<paramref name="levelOverride"/> 大于 0 时用它做等级(召唤物),
        /// 否则取 .mob 的 [level]。
        /// </summary>
        public MonsterActor Build(int index, RoomMonsterSpawn spawn, int levelOverride = 0)
        {
            MonsterModel model = ModelOf(spawn.Code);
            MonsterDef def = _data.GetMonster(spawn.Code);
            if (model == null || def == null || model.Waiting == null)
            {
                return null;
            }
            GameData data = _data;
            int seed = _seed;
            int difficulty = _difficulty;
            int level = levelOverride > 0 ? levelOverride : def.Tags.IntOr("[level]", 1);
            MonsterKind kind = spawn.IsBoss ? MonsterKind.Boss : MonsterKind.Normal;
            MonsterStats stats = MonsterAbility.Compute(def, data.MonsterBaseTables, data.GetMonsterOwnTable(def), level, 0, kind,
                data.MonsterDifficulty, difficulty, _dungeonBonus);                  // 难度加成接在 [ability category] 之后(证据 E-125)
            var actor = new MonsterActor(index, spawn, model, level, stats.HpMax,
                RoomCombatSession.WalkSpeed(_hero, stats.MoveSpeed > 0 ? stats.MoveSpeed : def.Tags.IntOr("[move speed]", 0)),
                def.Tags.IntOr("[attack delay]", 0), def.Tags.IntOr("[attack speed]", 0), stats.HitRecovery, stats.Weight);
            // 出招的攻击信息:[attack info] 与 [attack motion] 同下标(证据 E-086);出哪一招由 [attack kind] 的记录决定(证据 E-100、E-101),
            // 没有记录的怪物仍固定用第 0 个动作
            var attackStrikes = new HeroStrikeInfo[model.AttackInfoPaths.Count];
            for (int a = 0; a < attackStrikes.Length; a++)
            {
                attackStrikes[a] = !string.IsNullOrEmpty(model.AttackInfoPaths[a])
                    ? HeroStrikeInfo.Read(data, model.AttackInfoPaths[a])
                    : new HeroStrikeInfo(0, 0, false);
            }
            // 出生悬停(.mob 的 [floating height],MonsterActor.SetBaseFloatingHeight)暂不接线(E-133):有 36 种怪物的基准高度在 150 以上,
            // 地面攻击框够不着,自动机清不掉这些房间(全库扫描里 5 个副本会因此卡住);要先读清楚原版的命中高度规则与跳击再接
            actor.Strike = attackStrikes.Length > 0 ? attackStrikes[0] : new HeroStrikeInfo(0, 0, false);
            actor.SuperArmorOnAttack = def.SuperArmorOnAttack;              // [superarmor on attack]:出招期间带档位 1 的霸体(E-143)
            actor.SetStrikes(attackStrikes);
            actor.SetAttackKinds(AttackKindRecord.Parse(def.Tags), unchecked((uint)seed * 2654435761u + (uint)actor.Index * 40503u + 12345u));
            // 默认 AI 的走位:游荡、发现主角、按节拍朝主角走(证据 E-114);没有 [melee combat]/[range combat]/[stay] 类别的怪物仍走占位
            actor.SetDefaultAi(MonsterBrain.For(def, _roomWidth, unchecked((uint)seed * 2246822519u + (uint)actor.Index * 3266489917u + 374761393u)));
            actor.SetThrows(MonsterThrows.Load(_assets, data, def.Path, ThrowAttackRecord.Parse(def.Tags)));   // [throw attack]:出招动画到发射帧时扔东西(证据 E-103)
            // .ai 脚本驱动(证据 E-128):带 [ai pattern] 的怪物出招、走位交给四个 .ai 脚本;读不到脚本的仍走上面的默认 AI 与占位
            uint aiState = unchecked((uint)seed * 3266489917u + (uint)actor.Index * 668265263u + 2246822519u);
            actor.SetScript(MonsterScript.Create(data, def, model, difficulty, () => ClientRandom.Next(ref aiState)));
            if (_heroWidth != null && _heroWidth.Length >= 2)
            {
                actor.TargetWidth = _heroWidth[0];
                actor.TargetDepth = _heroWidth[1];
            }
            actor.PhysicalAttack = stats.PhysicalAttack;
            actor.MagicalAttack = stats.MagicalAttack;
            actor.Defense = DefenderPanel.FromMonster(stats, level);                    // 挨打时的防御面板(证据 E-110)
            actor.Panel = MonsterPanelInfo.From(def, kind, level, stats.HpMax, data);   // 目标血条窗口的资料(证据 E-106)
            actor.Sounds = new MonsterSounds(def.AppearSound, def.DamageSound, def.DieSound, def.AttackSound, def.AmbientSound);
            return actor;
        }
    }
}

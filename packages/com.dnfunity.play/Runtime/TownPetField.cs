using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

// 城镇里的宠物实体:出战的宠物(状态层 PetSession 的宠物格)在城镇里生成一个跟着主角走的实体,收起、换宠物、饿死时撤掉。
// "在不在场"每次都直接问状态层(装备着谁、饿没饿死),这里不另记一份"出战了没有"的标志,所以窗口、存档还原、别的接线改了宠物格,下一帧就如实反映;
// "在哪里"才由 PetFollower 算。宠物不进副本战斗:本类只在城镇会话里被推进(见 TownPetScene),副本阶段没有实体、也不画。
// 饱食随时间的流逝由状态层按秒结算(PetInstance.Settle);城镇这一侧在每次推进、每次出图时用同一个时间源(IPetClock)问一次,
// 所以时间源是可以注入的:测试拨手动钟,宿主给墙钟。
namespace Dnf.Play
{
    /// <summary>
    /// 能按宠物外观与动作给出这一刻的精灵的东西(渲染接缝:生产版 <see cref="PetSprites"/> 走现成的 ANI 构建口,测试里用假的)。
    /// 当前没有图(动画读不到、空帧)时返回 null。
    /// </summary>
    public interface IPetSpriteSource
    {
        SceneSprite Build(PetLook look, PetMotion motion, long animMs, int worldX, int worldY, int worldZ, bool facingLeft, TownProjection projection, int objectId);
    }

    /// <summary>城镇里的一个宠物实体:对应状态层里的一只宠物实例,带着外观、位置跟随器与当前动作已经播放的时间。</summary>
    public sealed class TownPetEntity
    {
        private double _animMs;

        internal TownPetEntity(PetInstance pet, PetLook look, PetFollower follower)
        {
            Pet = pet;
            Look = look;
            Follower = follower;
            Last = new PetFollowStep(PetFollowPhase.Idle, follower.X, follower.Y, follower.X, follower.Y, follower.FacingLeft, false, PetMotion.Stand);
        }

        /// <summary>状态层里的那只宠物实例(同一个对象;存档还原换了实例对象,实体就会随之重建)。</summary>
        public PetInstance Pet { get; }

        public int Uid => Pet.Uid;

        public PetLook Look { get; }

        public PetFollower Follower { get; }

        /// <summary>跟随状态机上一步的输出(刚出现时是"站在停靠点上")。</summary>
        public PetFollowStep Last { get; private set; }

        public PetMotion Motion => Last.Motion;

        public double X => Follower.X;

        public double Y => Follower.Y;

        public bool FacingLeft => Follower.FacingLeft;

        /// <summary>离地高度(世界 z),取自外观的 [floating height]。</summary>
        public int Z => Look.FloatingHeight;

        /// <summary>当前动作已经播放的毫秒数(动作换了从 0 起)。</summary>
        public long AnimMs => (long)_animMs;

        /// <summary>精灵上带的对象编号(<see cref="TownPetField.ObjectIdBase"/> 加实例号)。</summary>
        public int ObjectId => TownPetField.ObjectIdBase + Pet.Uid;

        internal void Apply(PetFollowStep step, double deltaMs)
        {
            if (step.Motion != Last.Motion)
            {
                _animMs = 0.0;
            }
            else if (deltaMs > 0.0)
            {
                _animMs += deltaMs;
            }
            Last = step;
        }

        internal void Reset(PetFollowStep placed)
        {
            _animMs = 0.0;
            Last = placed;
        }
    }

    /// <summary>
    /// 城镇宠物场:状态层的出战宠物 → 城镇里的跟随实体。<see cref="Sync"/> 把实体与状态层对齐(没出战或饿死的不在场、出战的在场、换了一只就换实体),
    /// <see cref="Advance"/> 每帧先 <see cref="Sync"/> 再推进跟随,<see cref="BuildSprites"/> 出图前也 <see cref="Sync"/>(游戏暂停、窗口里换了宠物时,下一帧画面照样如实)。
    /// </summary>
    public sealed class TownPetField
    {
        /// <summary>宠物精灵上的对象编号起点(加实例号);与主角(-2)、怪物(1000 起)错开。</summary>
        public const int ObjectIdBase = 5000;

        private readonly PetSession _session;
        private readonly IPetClock _clock;
        private readonly PetLookCatalog _looks;
        private readonly IPetSpriteSource _sprites;
        private readonly PetFollowRules _rules;
        private readonly List<TownPetEntity> _entities = new List<TownPetEntity>();

        /// <param name="session">状态层的宠物会话(出战谁、饱食多少都问它)。</param>
        /// <param name="clock">时间源(注入):所有取饱食的地方都用它给的秒数。</param>
        /// <param name="looks">宠物外观目录;不给时宠物没有外观(照样在场、照样跟随,只是画不出来)。</param>
        /// <param name="sprites">精灵来源;不给时 <see cref="BuildSprites"/> 返回空表。</param>
        /// <param name="rules">跟随规则;不给用缺省。</param>
        public TownPetField(PetSession session, IPetClock clock, PetLookCatalog looks = null, IPetSpriteSource sprites = null, PetFollowRules rules = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _looks = looks;
            _sprites = sprites;
            _rules = rules ?? PetFollowRules.Default;
        }

        public PetSession Session => _session;

        public IPetClock Clock => _clock;

        /// <summary>现在在场的实体(状态层只有一个宠物格,所以至多一个)。</summary>
        public IReadOnlyList<TownPetEntity> Entities => _entities;

        /// <summary>累计出现过几个实体、撤掉过几个(换一只宠物算撤一个再出一个)。</summary>
        public int Spawned { get; private set; }

        public int Despawned { get; private set; }

        /// <summary>某个实例号的实体;不在场返回 null。</summary>
        public TownPetEntity Find(int uid)
        {
            foreach (TownPetEntity entity in _entities)
            {
                if (entity.Uid == uid)
                {
                    return entity;
                }
            }
            return null;
        }

        /// <summary>
        /// 让实体与状态层对齐:宠物格里有宠物、并且它此刻没有饿死,实体才在场;宠物换了(按实例对象判断)旧实体撤掉、新实体出现在 <paramref name="master"/> 身边的停靠点上。
        /// 饿死的宠物不在城镇里露面是本项目自己的设计(原版怎么表现没有样本);喂食复活或回城复活之后下一次对齐就会重新出现。
        /// 用到的"现在"取自时间源,会把这只宠物的饱食结算到此刻。
        /// </summary>
        public void Sync(PetMaster master)
        {
            PetInstance equipped = _session.Equipped;
            PetInstance want = equipped != null && !equipped.IsStarved(_clock.NowSeconds) ? equipped : null;
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_entities[i].Pet, want))
                {
                    _entities.RemoveAt(i);
                    Despawned++;
                }
            }
            if (want != null && _entities.Count == 0)
            {
                PetLook look = _looks != null ? _looks.Get(want.Def) : PetLookScript.Parse(null, want.Def.Id, want.Def.Path);
                _entities.Add(new TownPetEntity(want, look, PetFollower.For(_rules, look, master)));
                Spawned++;
            }
        }

        /// <summary>每帧推进:先与状态层对齐,再让每个实体的跟随状态机走 <paramref name="deltaMs"/> 毫秒。</summary>
        public void Advance(double deltaMs, PetMaster master)
        {
            Sync(master);
            foreach (TownPetEntity entity in _entities)
            {
                entity.Apply(entity.Follower.Step(deltaMs, master), deltaMs);
            }
        }

        /// <summary>主角换了场景(进新区域、从副本回来):现有的实体直接落在新位置的停靠点上,不追。</summary>
        public void Arrive(PetMaster master)
        {
            foreach (TownPetEntity entity in _entities)
            {
                entity.Follower.Place(master);
                entity.Reset(new PetFollowStep(PetFollowPhase.Idle, entity.Follower.X, entity.Follower.Y, entity.Follower.X, entity.Follower.Y,
                    entity.Follower.FacingLeft, false, PetMotion.Stand));
            }
        }

        /// <summary>这一刻要画的宠物精灵(没有精灵来源或这一帧没有图时不含对应实体);出图前先与状态层对齐。</summary>
        public List<SceneSprite> BuildSprites(TownProjection projection, PetMaster master)
        {
            Sync(master);
            var sprites = new List<SceneSprite>(_entities.Count);
            if (_sprites == null)
            {
                return sprites;
            }
            foreach (TownPetEntity entity in _entities)
            {
                SceneSprite sprite = _sprites.Build(entity.Look, entity.Motion, entity.AnimMs, (int)Math.Floor(entity.X), (int)Math.Floor(entity.Y), entity.Z,
                    entity.FacingLeft, projection, entity.ObjectId);
                if (sprite != null)
                {
                    sprites.Add(sprite);
                }
            }
            return sprites;
        }

        /// <summary>
        /// 进了城镇(不在战斗里):装备着的宠物从"消耗中"切回"回补"(饿死的宠物此时复活,状态层的规则)。已经是回补或没有宠物时什么也不变。
        /// 宿主在阶段切到城镇时调(<see cref="PetTownWorld"/> 造会话时会调一次)。
        /// </summary>
        public PetStateChange EnterTown()
        {
            return _session.SetFighting(false, _clock.NowSeconds);
        }

        /// <summary>进了副本(战斗开始):装备着的宠物从"回补"切到"消耗中"。宿主在阶段切到副本时调;本类不碰副本里的任何东西。</summary>
        public PetStateChange EnterDungeon()
        {
            return _session.SetFighting(true, _clock.NowSeconds);
        }

        /// <summary>按流程的阶段切换:副本阶段在战斗,其余阶段(城镇、副本选择、加载、选角)都不在战斗。</summary>
        public PetStateChange OnStage(GameStage stage)
        {
            return stage == GameStage.Dungeon ? EnterDungeon() : EnterTown();
        }
    }
}

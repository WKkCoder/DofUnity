using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:E-103(怪物投掷的发射条件、创建位置与 .ptl 的字段取自客户端;飞行的运行时语义——速度单位、寿命、命中后消失——是推断,见 E-103 的未决项)。
namespace Dnf.Play
{
    /// <summary>
    /// 一种投射物的"配方":被动对象 .obj 的基础动画([basic motion])与攻击信息([attack info]),加上 .ptl 的飞行定义。
    /// 数据一律从用户自备的 PVF 里读,本类不含原版内容。
    /// </summary>
    public sealed class ProjectileTemplate
    {
        /// <summary>读不出寿命时的上限(毫秒):投射物不会永远飞下去。</summary>
        public const int DefaultLifeMs = 3000;

        private readonly AniTimeline _timeline;

        public AniFile Ani { get; }
        public string AniPath { get; }
        public HeroStrikeInfo Strike { get; }

        /// <summary>飞行定义;为 null 时投射物原地不动(客户端也会在没有 .ptl 时直接放出对象)。</summary>
        public ParticleDef Particle { get; }

        /// <summary>.obj 的 [object destroy condition] 里 [time limite] 的毫秒数;没有为 -1。</summary>
        public int ObjectLifeMs { get; }

        /// <summary>.obj 的 [object destroy condition] 含 [on end of animation]:动画播完就消失。</summary>
        public bool DestroyOnAnimationEnd { get; }

        public ProjectileTemplate(AniFile ani, string aniPath, HeroStrikeInfo strike, ParticleDef particle, int objectLifeMs = -1, bool destroyOnAnimationEnd = false)
        {
            Ani = ani ?? throw new ArgumentNullException(nameof(ani));
            AniPath = aniPath;
            Strike = strike;
            Particle = particle;
            ObjectLifeMs = objectLifeMs;
            DestroyOnAnimationEnd = destroyOnAnimationEnd;
            _timeline = new AniTimeline(ani);
        }

        public AniTimeline Timeline => _timeline;

        /// <summary>
        /// 寿命(毫秒):.ptl 的 [life time] 与 .obj 的 [time limite] 里写了的取较短的;都没写时,动画播完就消失的按动画时长,否则取 <see cref="DefaultLifeMs"/>(推断)。
        /// </summary>
        public int LifeMs
        {
            get
            {
                int life = int.MaxValue;
                if (Particle != null && Particle.LifeTimeMs > 0)
                {
                    life = Particle.LifeTimeMs;
                }
                if (ObjectLifeMs > 0 && ObjectLifeMs < life)
                {
                    life = ObjectLifeMs;
                }
                if (life == int.MaxValue)
                {
                    life = DestroyOnAnimationEnd && _timeline.TotalMilliseconds > 0 ? (int)_timeline.TotalMilliseconds : DefaultLifeMs;
                }
                return life;
            }
        }

        /// <summary>
        /// 按 <paramref name="record"/> 的第 <paramref name="slot"/> 个投射物配出模板:被动对象编号经 passiveobject.lst 找到 .obj,
        /// .ptl 路径相对 <paramref name="mobPath"/> 所在目录(没有再按 PVF 根目录下的完整路径找)。.obj 或它的基础动画读不到时返回 null(这一个投射物发射不出来)。
        /// </summary>
        public static ProjectileTemplate Load(DnfAssets assets, GameData data, string mobPath, ThrowAttackRecord record, int slot)
        {
            Dnf.Game.PassiveObjectDef obj = data.GetPassiveObject(record.PassiveObjectIndex);
            if (obj == null)
            {
                return null;
            }
            string aniPath = obj.BasicMotionPath;
            AniFile ani = aniPath == null ? null : assets.LoadAni(aniPath);
            if (ani == null || ani.Frames.Length == 0)
            {
                return null;
            }
            ParticleDef particle = data.GetParticleFor(mobPath, record.ParticleFile(slot));
            return new ProjectileTemplate(ani, aniPath, HeroStrikeInfo.Read(data, obj.AttackInfoPath), particle, obj.TimeLimitMs, obj.DestroyOnAnimationEnd);
        }
    }

    /// <summary>
    /// 一只怪物的全部投掷:状态机 <see cref="ThrowAttackRunner"/> 加每条记录每个投射物的模板。
    /// 模板为 null 的投射物发射不出来(状态机照常记账)。
    /// </summary>
    public sealed class MonsterThrows
    {
        private readonly ProjectileTemplate[][] _templates;

        public ThrowAttackRunner Runner { get; }

        public MonsterThrows(IReadOnlyList<ThrowAttackRecord> records, ProjectileTemplate[][] templates)
        {
            Runner = new ThrowAttackRunner(records);
            _templates = templates ?? new ProjectileTemplate[records.Count][];
        }

        public ProjectileTemplate TemplateFor(int record, int slot)
        {
            if ((uint)record >= (uint)_templates.Length || _templates[record] == null || (uint)slot >= (uint)_templates[record].Length)
            {
                return null;
            }
            return _templates[record][slot];
        }

        /// <summary>按 <paramref name="records"/> 读出所有模板;<paramref name="mobPath"/> 是怪物 .mob 的路径(.ptl 相对它)。没有记录时返回 null。</summary>
        public static MonsterThrows Load(DnfAssets assets, GameData data, string mobPath, IReadOnlyList<ThrowAttackRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return null;
            }
            var templates = new ProjectileTemplate[records.Count][];
            for (int i = 0; i < records.Count; i++)
            {
                templates[i] = new ProjectileTemplate[records[i].ShotCount];
                for (int k = 0; k < templates[i].Length; k++)
                {
                    templates[i][k] = ProjectileTemplate.Load(assets, data, mobPath, records[i], k);
                }
            }
            return new MonsterThrows(records, templates);
        }
    }

    /// <summary>
    /// 怪物扔出去的东西(证据 E-103):从发射点沿朝向直线飞(速度取 .ptl 的 x 轴直线运动,朝左时镜像),寿命到了或打中主角就消失;
    /// 飞行期间每一帧的攻击框(类型 15)与主角的受击框相交就算打中(攻击信息取 .obj 的 [attack info],伤害按发射者的等级与攻击力算)。
    /// 只支持 x、y、z 轴的直线运动(其余运动方式与重力、落地处理还没读通,不动)。
    /// </summary>
    public sealed class MonsterProjectile
    {
        private readonly ProjectileTemplate _template;
        private AnimatedEffect _effect;

        public MonsterActor Owner { get; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public bool FacingLeft { get; }
        public double AgeMs { get; private set; }

        /// <summary>已经消失(寿命到了、动画播完或打中了主角)。</summary>
        public bool Finished { get; private set; }

        public ProjectileTemplate Template => _template;

        public MonsterProjectile(MonsterActor owner, ProjectileTemplate template, double x, double y, double z, bool facingLeft)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _template = template ?? throw new ArgumentNullException(nameof(template));
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
        }

        /// <summary>当前帧(动画从发射那一刻起放;不循环的停在最后一帧)。</summary>
        public int CurrentFrame => _template.Timeline.FrameAt((long)AgeMs);

        /// <summary>推进 <paramref name="deltaMs"/> 毫秒:先按速度挪位置,再老化并判断消失。</summary>
        public void Update(double deltaMs)
        {
            if (Finished || deltaMs <= 0)
            {
                return;
            }
            double seconds = deltaMs / 1000.0;
            ParticleDef p = _template.Particle;
            if (p != null)
            {
                X += (FacingLeft ? -1.0 : 1.0) * p.X.StraightSpeed * seconds;
                Y += p.Y.StraightSpeed * seconds;
                Z += p.Z.StraightSpeed * seconds;
            }
            AgeMs += deltaMs;
            _effect?.Advance(deltaMs);
            _effect?.MoveTo(X, Y, Z);
            if (AgeMs >= _template.LifeMs || (_template.DestroyOnAnimationEnd && !_template.Timeline.Loops && AgeMs >= _template.Timeline.TotalMilliseconds))
            {
                Finished = true;
            }
        }

        /// <summary>打中了主角(或被别的原因收走):不再飞,不再画。</summary>
        public void Vanish()
        {
            Finished = true;
        }

        /// <summary>当前帧的攻击框(世界坐标,类型 15,跟着朝向镜像、跟着高度抬升);已经消失或这一帧没有攻击框时返回空表。</summary>
        public List<Aabb> AttackBoxes()
        {
            var result = new List<Aabb>();
            if (Finished)
            {
                return result;
            }
            int frame = CurrentFrame;
            AniFile ani = _template.Ani;
            if ((uint)frame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            int direction = FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(X), wy = (int)Math.Floor(Y), wz = (int)Math.Floor(Z);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Attack)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
            return result;
        }

        /// <summary>此刻的精灵;当前帧没有图或已经消失时返回 null。</summary>
        public SceneSprite Sprite(DnfAssets assets, TownProjection projection)
        {
            if (Finished)
            {
                return null;
            }
            if (_effect == null)
            {
                _effect = AnimatedEffect.CreateFromAni(_template.Ani, _template.AniPath, X, Y, Z, FacingLeft, 1, true);
                if (_effect == null)
                {
                    return null;
                }
                _effect.Advance(AgeMs);
            }
            return _effect.Sprite(assets, projection);
        }
    }
}

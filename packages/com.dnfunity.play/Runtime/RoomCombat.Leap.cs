using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    // 脚本前冲(状态 0x2d 的模式 1,规格与读码见 SkillLeapSpec,位置与高度的逐步计算见渲染包的 SkillLeapPlan 与 TownHero.StepLeap)在房间会话里的部分:
    // 主角自己按动画时间算位置与高度(水平带碰撞、高度走弧、落地计一次数),这里办落地块里与主角位置无关的事——
    // 声音、震屏、预载特效(SkillLeapLanding),落地之后撤特效的规则(SkillEffectTrim),以及每一段开始时的声音(SkillStageSpec.StartSounds)。
    public sealed partial class RoomCombatSession
    {
        private int _leapLandingsSeen = -1;
        private long _startSoundSwing = -1;
        private readonly List<LeapTrimJob> _leapTrims = new List<LeapTrimJob>();

        /// <summary>前冲落地办过落地块的次数(检查用)。</summary>
        public int LeapLandings { get; private set; }

        /// <summary>还没销掉的撤特效单数(检查用:每一单撤完就销,不会越攒越多)。</summary>
        internal int LeapTrimJobCount => _leapTrims.Count;

        /// <summary>撤特效的一单:盯着的那个特效播到 <see cref="CutMs"/>(它自己动画里第几帧的起始时刻)毫秒以后,把 <see cref="Remove"/> 里的特效一起撤掉。</summary>
        private sealed class LeapTrimJob
        {
            public AnimatedEffect Watch;
            public double CutMs;
            public readonly List<AnimatedEffect> Remove = new List<AnimatedEffect>();
        }

        /// <summary>每一步在主角推进之后办的事:这一段开始的声音、前冲落地块、落地后撤特效。</summary>
        private void UpdateLeapSkill(TownHero hero)
        {
            EmitStageStartSounds(hero);
            if (_leapLandingsSeen < 0)
            {
                _leapLandingsSeen = hero.LeapLandings;       // 第一次看到主角时只记下当前计数,不补放
            }
            else if (hero.LeapLandings != _leapLandingsSeen)
            {
                _leapLandingsSeen = hero.LeapLandings;
                LeapLandings++;
                DoLeapLanding(hero);
            }
            TrimLandingEffects();
        }

        /// <summary>
        /// 这一段一开始放配方指定的声音(<see cref="SkillStageSpec.StartSounds"/>,状态 0x2d 模式 0 的进入块 0x00cd4c71 起):
        /// 换了一击(<see cref="TownHero.SwingNumber"/> 变了)的第一步放一次;没在放技能时清掉记录。
        /// </summary>
        private void EmitStageStartSounds(TownHero hero)
        {
            if (hero.Motion != HeroMotion.Skill)
            {
                _startSoundSwing = -1;
                return;
            }
            if (_startSoundSwing == hero.SwingNumber)
            {
                return;
            }
            _startSoundSwing = hero.SwingNumber;
            SkillRecipe recipe = Skills?.SlotOfSkill(hero.SkillId)?.Recipe;
            if (recipe == null || hero.SkillStage >= recipe.Stages.Count)
            {
                return;
            }
            foreach (string tag in recipe.Stages[hero.SkillStage].StartSounds)
            {
                _sounds.Add(tag, SoundCueKind.Effect);
            }
        }

        /// <summary>
        /// 前冲落地那一步(<see cref="TownHero.LeapLandings"/> 变了)办落地块里的事(0x00cbd5be 起,顺序同客户端):依次放声音、震屏、放预载特效。
        /// 配方没有落地的事、预载表里没有下标、读不到特效动画的,对应的那一项不办(其余照办)。
        /// </summary>
        private void DoLeapLanding(TownHero hero)
        {
            HeroSkillSlot slot = Skills?.SlotOfSkill(hero.LeapLandingSkillId);
            SkillRecipe recipe = slot?.Recipe;
            if (recipe == null || hero.LeapLandingStage >= recipe.Stages.Count)
            {
                return;
            }
            SkillLeapLanding landing = recipe.Stages[hero.LeapLandingStage].Leap?.Landing;
            if (landing == null)
            {
                return;
            }
            foreach (string tag in landing.Sounds)
            {
                _sounds.Add(tag, SoundCueKind.Effect);
            }
            if (landing.ShakeStrength > 0 && landing.ShakeMs > 0)
            {
                _shake.Start(landing.ShakeStrength, landing.ShakeMs);
            }
            var made = new Dictionary<int, AnimatedEffect>();
            foreach (SkillLandingEffect fx in landing.Effects)
            {
                if (fx.Index >= slot.StageEffects.Count)
                {
                    continue;
                }
                double x = hero.X + (hero.FacingLeft ? -fx.Forward : fx.Forward);
                AnimatedEffect effect = AnimatedEffect.Create(_assets, slot.StageEffects[fx.Index], x, hero.Y, hero.Z - fx.Down, hero.FacingLeft);
                if (effect == null)
                {
                    continue;
                }
                _effects.Add(effect);
                if (fx.Follows)
                {
                    _followEffects.Add(effect);
                }
                made[fx.Index] = effect;
            }
            SkillEffectTrim trim = landing.Trim;
            if (trim == null || !made.TryGetValue(trim.WatchIndex, out AnimatedEffect watch))
            {
                return;
            }
            AniFile ani = _assets.LoadAni(watch.Path);
            var timeline = new AniTimeline(ani);
            if (trim.Frame >= timeline.FrameCount)
            {
                return;                // 盯着的特效没有这一帧:客户端里它的帧号永远到不了,不撤
            }
            var job = new LeapTrimJob { Watch = watch, CutMs = timeline.StartMs(trim.Frame) };
            foreach (int index in trim.RemoveIndices)
            {
                if (made.TryGetValue(index, out AnimatedEffect victim))
                {
                    job.Remove.Add(victim);
                }
            }
            _leapTrims.Add(job);
        }

        /// <summary>
        /// 落地之后每一步(0x00cbda30 起):盯着的特效播到该撤的帧了就把一组特效一起撤掉,这一单随即销掉。
        /// 该撤的帧一定在盯着的特效的帧数以内(建单时已排除),所以盯着的特效即使在一步里跨过它直接播完(卡顿),也是"播过了",照撤;撤完的单子不留。
        /// </summary>
        private void TrimLandingEffects()
        {
            for (int i = _leapTrims.Count - 1; i >= 0; i--)
            {
                LeapTrimJob job = _leapTrims[i];
                if (job.Watch.AgeMs < job.CutMs)
                {
                    continue;
                }
                foreach (AnimatedEffect victim in job.Remove)
                {
                    _effects.Remove(victim);
                    _followEffects.Remove(victim);
                }
                _leapTrims.RemoveAt(i);
            }
        }
    }
}

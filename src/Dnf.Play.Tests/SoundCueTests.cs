using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 战斗会话发出的声音提示(证据 E-089、E-090):进房间的地图声音与怪物出场声、挥剑与语音、命中、怪物受击与死亡、
/// 主角受击与死亡、没魔法与冷却中的语音、跳跃与技能音效、冷却完成的界面音。只检查"在什么时刻发了哪个标签";
/// 声音标签全是合成的(只有鬼剑士的固定语音用真实拼写,那是客户端里写死的)。
/// </summary>
public class SoundCueTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static readonly WeaponSound Weapon = new WeaponSound("SW_A", "SW_B", "HIT_A", "HIT_B");

    private static HeroStrikeInfo[] Plain(string hitWav = null)
    {
        return new[]
        {
            new HeroStrikeInfo(0, 0, false, hitWav: hitWav),
            new HeroStrikeInfo(0, 0, false, hitWav: hitWav),
            new HeroStrikeInfo(0, 0, false, hitWav: hitWav),
        };
    }

    private static HeroVitals Vitals(int hpMax = 100, int mpMax = 50, double regenPerMinute = 0.0)
    {
        return new HeroVitals(1, hpMax, mpMax, 100, 0, 0, regenPerMinute);
    }

    private sealed class SoundArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }

        /// <param name="monsterX">不给就没有怪物。</param>
        /// <param name="keepEntryCues">为假时把造房间时发出的进房间提示丢掉(多数测试只关心后面发生的事)。</param>
        public SoundArena(string[] mapSounds = null, int? monsterX = null, MonsterSounds monsterSounds = default, int monsterHp = 100000,
            bool striker = false, HeroStrikeInfo[] strikes = null, HeroVitals vitals = null, bool withSkills = false, bool frozen = false,
            bool keepEntryCues = false)
        {
            Assets = DnfAssets.Open(_fx.Root);
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]));
            }
            var map = new RoomMap("map/test/arena.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null, sounds: mapSounds);
            var content = new RoomContent(map, null, null, null, null);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            var area = frozen ? new List<WorldRect> { new WorldRect(HeroX - 5, HeroY - 5, 10, 10) } : new List<WorldRect>();
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, area, HeroX, HeroY, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            var actors = new List<MonsterActor>();
            if (monsterX.HasValue)
            {
                MonsterModel model = MonsterModel.Read(Assets, striker ? AvatarFixture.BlobStrikerMobPath : AvatarFixture.BlobMobPath);
                var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX.Value, HeroY, 0, 1, 0, "[fixed]", "[normal]");
                Monster = new MonsterActor(0, spawn, model, 1, monsterHp, 222.5, 1500, 0, 0);
                Monster.Sounds = monsterSounds;
                if (striker)
                {
                    Monster.PhysicalAttack = 40;
                    Monster.Strike = HeroStrikeInfo.Read(new GameData(new PvfScriptSource(Assets.Pvf)), AvatarFixture.BlobStrikerAtk);
                }
                actors.Add(Monster);
            }
            HeroSkills skills = null;
            if (withSkills)
            {
                var data = new GameData(new PvfScriptSource(Assets.Pvf));
                skills = HeroSkills.Create(data, data.GetCharacter(0), 0);
            }
            Session = new RoomCombatSession(Assets, room, actors, strikes ?? Plain(), 1, default, default, skills, 0, -1f, null, vitals, null, Weapon);
            if (!keepEntryCues)
            {
                Drain();
            }
        }

        public List<SoundCue> Drain()
        {
            var list = new List<SoundCue>();
            Session.DrainSounds(list);
            return list;
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒,把期间发出的提示都收集起来;<paramref name="attackAt"/> 里的时刻按攻击键。</summary>
        public List<SoundCue> Run(int ms, SkillKeys keys = SkillKeys.None, params int[] attackAt)
        {
            var all = new List<SoundCue>();
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, 0, 0, Array.IndexOf(attackAt, t) >= 0, false, t == 0 ? keys : SkillKeys.None);
                Session.DrainSounds(all);
            }
            return all;
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    private static string[] Keys(IEnumerable<SoundCue> cues)
    {
        return cues.Select(c => c.Kind + ":" + c.Key).ToArray();
    }

    // ------------------------------------------------------------------ 队列

    [Fact]
    public void TheQueueIgnoresEmptyKeysDrainsInOrderAndCountsEverything()
    {
        var q = new SoundCueQueue();
        q.Add(null, SoundCueKind.Effect);
        q.Add("", SoundCueKind.Effect);
        q.Add("A", SoundCueKind.Effect);
        q.Add("B", SoundCueKind.Voice);
        Assert.Equal(2, q.PendingCount);
        Assert.Equal(2, q.TotalCount);

        var into = new List<SoundCue> { new SoundCue("OLD", SoundCueKind.Ui) };
        q.Drain(into);
        Assert.Equal(new[] { "Ui:OLD", "Effect:A", "Voice:B" }, Keys(into));          // 追加在后面,不清对方的
        Assert.Equal(0, q.PendingCount);
        Assert.Equal(2, q.TotalCount);
        Assert.Throws<ArgumentNullException>(() => q.Drain(null));
    }

    [Fact]
    public void TheQueueKeepsOnlyTheLatestWhenNobodyDrainsIt()
    {
        var q = new SoundCueQueue();
        for (int i = 0; i < SoundCueQueue.MaxPending + 40; i++)
        {
            q.Add("K" + i, SoundCueKind.Effect);
        }
        Assert.Equal(SoundCueQueue.MaxPending, q.PendingCount);
        Assert.Equal(SoundCueQueue.MaxPending + 40, q.TotalCount);
        Assert.Equal("K40", q.Pending[0].Key);                                           // 最早的 40 条被挤掉
        Assert.Equal("K" + (SoundCueQueue.MaxPending + 39), q.Pending[SoundCueQueue.MaxPending - 1].Key);
    }

    [Fact]
    public void MonsterSoundsTreatBlankTagsAsMissing()
    {
        var s = new MonsterSounds("A", "", "  ", null, "E");
        Assert.Equal(("A", null, null, null, "E"), (s.Appear, s.Damage, s.Die, s.Attack, s.Ambient));
        Assert.False(s.IsEmpty);
        Assert.True(default(MonsterSounds).IsEmpty);
    }

    [Fact]
    public void TheStrikeInfoReadsTheAttackInfosHitWavAndKeepsItThroughSkillDamage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroStrikeInfo info = HeroStrikeInfo.Read(data, AvatarFixture.SoundAtk);
        Assert.Equal("TEST_HIT_WAV", info.HitWav);
        Assert.Equal("TEST_HIT_WAV", info.WithSkillDamage(10, 20).HitWav);
        Assert.Null(HeroStrikeInfo.Read(data, AvatarFixture.HardAttackAtk).HitWav);      // 没写 [hit wav]
        Assert.Null(HeroStrikeInfo.Read(data, null).HitWav);
        Assert.Null(new HeroStrikeInfo(0, 0, false, hitWav: "").HitWav);                 // 空串当没写
    }

    // ------------------------------------------------------------------ 进房间

    [Fact]
    public void EnteringTheRoomEmitsTheMapSoundsThenTheMonstersAppearSounds()
    {
        using var arena = new SoundArena(new[] { "M_TEST", "AMB_TEST" }, 340, new MonsterSounds("APPEAR_X", "D", "E", null), keepEntryCues: true);
        Assert.Equal(new[] { "RoomEnter:map/test/arena.map", "RoomSound:M_TEST", "RoomSound:AMB_TEST", "Voice:APPEAR_X" }, Keys(arena.Drain()));
        Assert.Empty(arena.Drain());                                                     // 取走之后队列是空的
        Assert.Empty(arena.Run(100));                                                    // 什么也没发生就不再发声音
    }

    [Fact]
    public void ARoomWithoutSoundsOrMonstersOnlyMarksTheEntryAndCanReplayItsMapSounds()
    {
        using var arena = new SoundArena(keepEntryCues: true);
        Assert.Equal(new[] { "RoomEnter:map/test/arena.map" }, Keys(arena.Drain()));

        using var withSounds = new SoundArena(new[] { "M_TEST" }, 340, new MonsterSounds("APPEAR_X", null, null, null));
        withSounds.Session.ReplayRoomSounds();                                           // 走回来:只有地图声音,没有怪物出场声
        Assert.Equal(new[] { "RoomEnter:map/test/arena.map", "RoomSound:M_TEST" }, Keys(withSounds.Drain()));
    }

    // ------------------------------------------------------------------ 挥剑、语音、命中

    [Fact]
    public void ASwingEmitsTheAttackVoiceAndTheWeaponSwingSoundOnceAtItsStart()
    {
        using var arena = new SoundArena();
        List<SoundCue> cues = arena.Run(500, SkillKeys.None, 0);
        Assert.Equal(new[] { "Voice:" + SwordmanVoice.Attack, "Effect:SW_A" }, Keys(cues));
    }

    [Fact]
    public void TheFinalComboStageUsesTheSecondSwingSound()
    {
        using var arena = new SoundArena();
        var presses = new List<int>();
        for (int t = 0; t < 3000; t += 50)
        {
            presses.Add(t);
        }
        List<SoundCue> cues = arena.Run(3000, SkillKeys.None, presses.ToArray());
        string[] swings = cues.Where(c => c.Key == "SW_A" || c.Key == "SW_B").Select(c => c.Key).ToArray();
        Assert.True(swings.Length >= 4, "连按攻击键三千毫秒应该至少挥了四次");
        Assert.Equal(new[] { "SW_A", "SW_A", "SW_B", "SW_A" }, swings.Take(4).ToArray());   // 三段连招:前两段 A、最后一段 B,再从头
        // 每次挥击都有一次攻击语音
        Assert.Equal(swings.Length, cues.Count(c => c.Key == SwordmanVoice.Attack));
    }

    [Fact]
    public void AHitEmitsTheWeaponHitSoundAndTheMonstersDamageVoice()
    {
        using var arena = new SoundArena(null, 340, new MonsterSounds(null, "R_MON_DMG", "R_MON_DIE", null));
        List<SoundCue> cues = arena.Run(500, SkillKeys.None, 0);
        Assert.Equal(new[] { "Voice:" + SwordmanVoice.Attack, "Effect:SW_A", "Effect:HIT_A", "Voice:R_MON_DMG" }, Keys(cues));
        // 语音按主人区分:主角是 0,怪物是它在房间里的序号加 1(同一个主人的语音不叠放)
        Assert.Equal(new[] { 0, 0, 0, arena.Monster.Index + 1 }, cues.Select(c => c.Owner).ToArray());
    }

    [Fact]
    public void TheAttackInfosHitWavReplacesTheWeaponHitSound()
    {
        using var arena = new SoundArena(null, 340, new MonsterSounds(null, "R_MON_DMG", null, null), strikes: Plain("ATK_HIT"));
        string[] keys = Keys(arena.Run(500, SkillKeys.None, 0));
        Assert.Contains("Effect:ATK_HIT", keys);
        Assert.DoesNotContain("Effect:HIT_A", keys);
        Assert.Contains("Voice:R_MON_DMG", keys);
    }

    [Fact]
    public void AKillEmitsTheDieVoiceInsteadOfTheDamageVoice()
    {
        using var arena = new SoundArena(null, 340, new MonsterSounds(null, "R_MON_DMG", "R_MON_DIE", null), monsterHp: 1);
        string[] keys = Keys(arena.Run(500, SkillKeys.None, 0));
        Assert.Contains("Voice:R_MON_DIE", keys);
        Assert.DoesNotContain("Voice:R_MON_DMG", keys);
    }

    [Fact]
    public void AMonsterWithoutSoundTagsStillGetsTheHitSound()
    {
        using var arena = new SoundArena(null, 340);
        string[] keys = Keys(arena.Run(500, SkillKeys.None, 0));
        Assert.Equal(new[] { "Voice:" + SwordmanVoice.Attack, "Effect:SW_A", "Effect:HIT_A" }, keys);
    }

    // ------------------------------------------------------------------ 跳跃与技能

    [Fact]
    public void AJumpEmitsTheTakeoffSoundWithTheJumpVoiceAndTheLandingSoundOnce()
    {
        using var arena = new SoundArena();
        var all = new List<SoundCue>();
        arena.Session.Update(10, 0, 0, false, true);
        arena.Session.DrainSounds(all);
        // 起跳那一步:起跳声加跳跃语音
        Assert.Equal(new[] { "Effect:" + SwordmanVoice.JumpStart, "Voice:" + SwordmanVoice.Jump }, Keys(all));
        for (int i = 0; i < 400 && !(arena.Session.Hero.Motion == HeroMotion.Rest || arena.Session.Hero.Motion == HeroMotion.Move); i++)
        {
            arena.Session.Update(10, 0, 0, false, false);
            arena.Session.DrainSounds(all);
        }
        Assert.Equal(1, all.Count(c => c.Key == SwordmanVoice.JumpEnd));
        Assert.Equal(1, all.Count(c => c.Key == SwordmanVoice.JumpStart));
        Assert.Equal(SoundCueKind.Effect, all.First(c => c.Key == SwordmanVoice.JumpEnd).Kind);
    }

    [Fact]
    public void AJumpAttackEmitsTheJumpAttackVoice()
    {
        using var arena = new SoundArena();
        arena.Session.Update(10, 0, 0, false, true);
        arena.Drain();
        // 起跳之后再按攻击键:空中攻击
        var all = new List<SoundCue>();
        for (int i = 0; i < 80 && arena.Session.Hero.Motion != HeroMotion.JumpAttack; i++)
        {
            arena.Session.Update(10, 0, 0, i > 20, false);
            arena.Session.DrainSounds(all);
        }
        Assert.Equal(HeroMotion.JumpAttack, arena.Session.Hero.Motion);
        Assert.Contains("Voice:" + SwordmanVoice.JumpAttack, Keys(all));
        Assert.Contains("Effect:SW_A", Keys(all));
    }

    [Fact]
    public void TheUpperSlashSkillEmitsItsSoundAtTheCastAndNothingWhenItDoesNotStart()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals());
        string[] keys = Keys(arena.Run(30, SkillKeys.S));
        Assert.Contains("Effect:" + SwordmanVoice.UpperSlash, keys);
        Assert.Equal(1, arena.Session.SkillCasts);
    }

    [Fact]
    public void TheBackStepSkillEmitsTheBackstepVoiceAndNotTheJumpVoice()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals());
        string[] keys = Keys(arena.Run(30, SkillKeys.D));
        Assert.Contains("Voice:" + SwordmanVoice.Backstep, keys);
        Assert.DoesNotContain("Voice:" + SwordmanVoice.Jump, keys);
        Assert.Contains("Effect:" + SwordmanVoice.JumpStart, keys);
    }

    [Fact]
    public void WithoutEnoughMpTheNoManaVoicePlaysAndNothingElse()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals(mpMax: 1));
        string[] keys = Keys(arena.Run(30, SkillKeys.A));
        Assert.Equal(new[] { "Voice:" + SwordmanVoice.NoMana }, keys);
        Assert.Equal(0, arena.Session.SkillCasts);
    }

    [Fact]
    public void PressingASkillKeyDuringItsCooldownPlaysTheCoolTimeVoice()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals(mpMax: 500));
        arena.Run(10, SkillKeys.A);
        Assert.Equal(1, arena.Session.SkillCasts);
        arena.Drain();
        string[] keys = Keys(arena.Run(10, SkillKeys.A));
        Assert.Equal(new[] { "Voice:" + SwordmanVoice.CoolTime }, keys);
    }

    [Fact]
    public void AnUnboundOrImplementedButBusySkillKeyPlaysNoVoice()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals(mpMax: 500));
        arena.Run(10, SkillKeys.A);                                                      // 鬼斩在放
        arena.Drain();
        string[] keys = Keys(arena.Run(10, SkillKeys.S));                                // 上挑:忙,起不来 —— 不算没魔法也不算冷却
        Assert.Empty(keys);
    }

    [Fact]
    public void WhenACooldownEndsTheUiSoundPlaysExactlyOnce()
    {
        using var arena = new SoundArena(withSkills: true, vitals: Vitals(mpMax: 500));
        HeroSkillSlot hard = arena.Session.Skills.SlotOf(SkillKeys.A);
        arena.Run(10, SkillKeys.A);
        arena.Drain();
        Assert.False(hard.Ready);
        int ms = (int)hard.CooldownLeftMs + 100;
        List<SoundCue> cues = arena.Run(ms);
        Assert.True(hard.Ready);
        Assert.Equal(new[] { "Ui:" + RoomCombatSession.CooldownEndSound }, Keys(cues.Where(c => c.Kind == SoundCueKind.Ui)));
        Assert.Empty(arena.Run(200));                                                    // 之后不再重复
    }

    // ------------------------------------------------------------------ 怪物出招与主角受击

    [Fact]
    public void AMonsterSwingEmitsItsAttackSoundOnceAndAHitEmitsTheHitWavAndTheHeroHurtVoice()
    {
        using var arena = new SoundArena(null, 340, new MonsterSounds(null, null, null, "MON_ATK"), striker: true, vitals: Vitals(), frozen: true);
        arena.Session.AttackVoicePercent = 100;              // 出招语音默认只有 15% 的概率(E-104);这里要每次都中才好数
        arena.Drain();
        List<SoundCue> cues = arena.Run(800);
        string[] keys = Keys(cues);
        Assert.True(arena.Session.HeroHits >= 1);
        Assert.True(arena.Monster.SwingNumber >= 1);
        Assert.Contains("Voice:" + SwordmanVoice.Hurt, keys);
        // 每次出招一次攻击声:出招次数 = 怪物的 SwingNumber
        Assert.Equal((int)arena.Monster.SwingNumber, keys.Count(k => k == "Voice:MON_ATK"));
        // 每次打中主角一次受击语音
        Assert.Equal(arena.Session.HeroHits, keys.Count(k => k == "Voice:" + SwordmanVoice.Hurt));
    }

    [Fact]
    public void TheMonsterAttackVoiceIsRolledPerSwingAndNeverPlaysAtZeroPercent()
    {
        Assert.Equal(15, MonsterAttackRules.AttackVoicePercent);
        using var arena = new SoundArena(null, 340, new MonsterSounds(null, null, null, "MON_ATK"), striker: true, vitals: Vitals(), frozen: true);
        Assert.Equal(15, arena.Session.AttackVoicePercent);                  // 缺省就是客户端的 15%
        arena.Session.AttackVoicePercent = 0;
        string[] keys = Keys(arena.Run(800));
        Assert.True(arena.Monster.SwingNumber >= 1);                         // 出招了,但一次语音也没放
        Assert.DoesNotContain("Voice:MON_ATK", keys);
    }

    [Fact]
    public void TheMonstersAttackInfoHitWavPlaysWhenItHitsTheHero()
    {
        using var arena = new SoundArena(null, 340, default, striker: true, vitals: Vitals(), frozen: true);
        arena.Monster.Strike = new HeroStrikeInfo(25, 0, false, hitWav: "MON_HIT");
        string[] keys = Keys(arena.Run(800));
        Assert.True(arena.Session.HeroHits >= 1);
        Assert.Contains("Effect:MON_HIT", keys);
    }

    [Fact]
    public void ADeadlyHitEmitsTheDieVoiceNotTheHurtVoice()
    {
        using var arena = new SoundArena(null, 340, default, striker: true, vitals: Vitals(hpMax: 1), frozen: true);
        string[] keys = Keys(arena.Run(800));
        Assert.True(arena.Session.HeroDead);
        Assert.Contains("Voice:" + SwordmanVoice.Die, keys);
        Assert.DoesNotContain("Voice:" + SwordmanVoice.Hurt, keys);
    }

    [Fact]
    public void WithoutHeroVitalsNothingHurtsAndNoHeroVoicePlays()
    {
        using var arena = new SoundArena(null, 340, default, striker: true, vitals: null, frozen: true);
        string[] keys = Keys(arena.Run(800));
        Assert.DoesNotContain("Voice:" + SwordmanVoice.Hurt, keys);
        Assert.DoesNotContain("Voice:" + SwordmanVoice.Die, keys);
    }
}

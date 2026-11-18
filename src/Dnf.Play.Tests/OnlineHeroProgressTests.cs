using System.Text;
using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网选中角色的内存进度(<see cref="HeroProgress.CreateOnline"/>)的逐项测试:成长类型与觉醒怎么从服务端字节落到进度上、
/// 经验从哪里起算、到当前等级为止的技能点与觉醒点有没有一次给齐、职业不支持时怎么退回。
/// 游戏数据全是合成的:一个只有职业标签的鬼剑士角色脚本、每级门槛 = (级 − 1) × 100 的经验表、每级 3 点的技能点表。
/// </summary>
public sealed class OnlineHeroProgressTests
{
    /// <summary>角色列表里的条目相对列表所在目录(character/)。</summary>
    private const string SwordmanChrInList = "swordman/test.chr";

    private const string SwordmanChr = "character/swordman/test.chr";

    /// <summary>经验表:2 级的门槛 100、3 级 200……(<c>LevelExp(n) = (n − 1) × 100</c>,到 100 级);技能点表:1 到 60 级每级 3 点。</summary>
    private static GameData Data(bool withCharacter = true)
    {
        var thresholds = new int[99];
        for (int i = 0; i < thresholds.Length; i++)
        {
            thresholds[i] = (i + 1) * 100;
        }
        var fx = new Fixture();
        if (withCharacter)
        {
            fx.List(GameData.CharacterListPath, (EquipRules.SwordmanJob, SwordmanChrInList))
                .Tokens(SwordmanChr, w => w.Tag("[job]").Str("[swordman]"));
        }
        fx.Tokens(ExpTable.Path, w => w.Ints(thresholds))
            .Tokens(SpTable.Path, w =>
            {
                w.Tag("[sp table]");
                for (int level = 1; level <= 60; level++)
                {
                    w.Int(level).Int(3);
                }
            });
        return new GameData(fx.Build());
    }

    private static OnlineHeroProfile Profile(int job = 0, int growTypeByte = 0x00, int level = 52)
    {
        var entry = new OnlineCharacterEntry(9301, Encoding.UTF8.GetBytes("测试角色1"), 0, (byte)job, (byte)growTypeByte, (byte)level,
            0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return OnlineHeroProfile.FromEntry(entry, 0);
    }

    // ───────────────────────── 成长类型与觉醒 ─────────────────────────

    // 字节的低四位是成长类型,第 4 到 6 位是觉醒;鬼剑士的觉醒最多取 2
    [Theory]
    [InlineData(0x00, 0, 0)]
    [InlineData(0x01, 1, 0)]
    [InlineData(0x02, 2, 0)]
    [InlineData(0x03, 3, 0)]
    [InlineData(0x10, 0, 1)]
    [InlineData(0x12, 2, 1)]
    [InlineData(0x22, 2, 2)]
    [InlineData(0x32, 2, 2)]
    [InlineData(0x42, 2, 2)]
    [InlineData(0x72, 2, 2)]
    [InlineData(0xA3, 3, 2)]
    [InlineData(0xF3, 3, 2)]
    public void A_swordman_takes_the_growtype_and_the_capped_awakening_from_the_server_byte(int growTypeByte, int growtype, int awakening)
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, growTypeByte: growTypeByte));

        Assert.NotNull(progress);
        Assert.False(progress.IsJobFallback);
        Assert.Equal(growtype, progress.Growtype);
        Assert.Equal(awakening, progress.Awakening);
    }

    [Fact]
    public void A_swordman_with_no_growtype_and_no_awakening_keeps_both_at_zero()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, growTypeByte: 0x00));

        Assert.Equal(0, progress.Growtype);
        Assert.Equal(0, progress.Awakening);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Any_other_job_is_an_explicit_swordman_fallback_with_no_growtype_and_no_awakening(int job)
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: job, growTypeByte: 0x73));

        Assert.NotNull(progress);
        Assert.True(progress.IsJobFallback);
        Assert.Equal(0, progress.Growtype);
        Assert.Equal(0, progress.Awakening);
        Assert.Equal(0, progress.Job);
        Assert.Equal(52, progress.Level);
    }

    [Fact]
    public void The_job_that_is_not_swordman_still_gets_its_name_and_level()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 2, growTypeByte: 0x24, level: 48));

        Assert.Equal("测试角色1", progress.HeroName);
        Assert.Equal(48, progress.Level);
    }

    // ───────────────────────── 经验与点数 ─────────────────────────

    // 经验从本级的门槛起算:52 级的门槛是 51 × 100 = 5100,本级要攒 5200 - 5100 = 100
    [Fact]
    public void The_experience_starts_exactly_at_the_threshold_of_the_level()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, level: 52));

        Assert.Equal(5100, progress.Exp);
        Assert.Equal(0, progress.ExpInLevel);
        Assert.Equal(100, progress.LevelSpan);
    }

    [Fact]
    public void A_level_one_hero_starts_with_no_experience()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, level: 1));

        Assert.Equal(1, progress.Level);
        Assert.Equal(0, progress.Exp);
        Assert.Equal(0, progress.ExpInLevel);
    }

    // 技能点:1 到 52 级每级 3 点 = 156(一次给齐,因为这个角色还一点都没花);觉醒点:50 级起每级一点,52 − 49 = 3
    [Fact]
    public void The_skill_points_and_awakening_points_up_to_the_level_are_granted_at_once()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, level: 52));

        Assert.Equal(156, progress.SkillPoints);
        Assert.Equal(3, progress.AwakeningPoints);
    }

    [Theory]
    [InlineData(1, 3, 0)]
    [InlineData(20, 60, 0)]
    [InlineData(49, 147, 0)]
    [InlineData(50, 150, 1)]
    [InlineData(60, 180, 11)]
    public void The_granted_points_follow_the_level(int level, int skillPoints, int awakeningPoints)
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 0, level: level));

        Assert.Equal(skillPoints, progress.SkillPoints);
        Assert.Equal(awakeningPoints, progress.AwakeningPoints);
    }

    // 不支持的职业退回鬼剑士数据,点数照样按等级给
    [Fact]
    public void The_fallback_job_gets_the_same_total_points_for_its_level()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile(job: 3, growTypeByte: 0x21, level: 50));

        Assert.True(progress.IsJobFallback);
        Assert.Equal(150, progress.SkillPoints);
        Assert.Equal(1, progress.AwakeningPoints);
    }

    // ───────────────────────── 缺参数 ─────────────────────────

    [Fact]
    public void Missing_game_data_or_profile_is_rejected_by_name()
    {
        ArgumentNullException noData = Assert.Throws<ArgumentNullException>(() => HeroProgress.CreateOnline(null, null, Profile()));
        Assert.Equal("data", noData.ParamName);
        ArgumentNullException noProfile = Assert.Throws<ArgumentNullException>(() => HeroProgress.CreateOnline(Data(), null, null));
        Assert.Equal("profile", noProfile.ParamName);
    }

    [Fact]
    public void Game_data_without_the_swordman_character_gives_no_progress()
    {
        Assert.Null(HeroProgress.CreateOnline(Data(withCharacter: false), null, Profile()));
    }

    [Fact]
    public void The_host_entry_point_rejects_missing_assets_or_profile_by_name()
    {
        ArgumentNullException noAssets = Assert.Throws<ArgumentNullException>(() => HeroProgress.CreateForOnlineHero(null, Profile()));
        Assert.Equal("assets", noAssets.ParamName);

        using var fx = new HostedRoomFixture();
        ArgumentNullException noProfile = Assert.Throws<ArgumentNullException>(() => HeroProgress.CreateForOnlineHero(fx.Assets, null));
        Assert.Equal("profile", noProfile.ParamName);
    }

    [Fact]
    public void The_hero_name_falls_back_to_the_local_default_for_an_empty_name()
    {
        HeroProgress progress = HeroProgress.CreateOnline(Data(), null, Profile());
        Assert.Equal("测试角色1", progress.HeroName);

        progress.HeroName = null;
        Assert.Equal(CharacterRoster.LocalName, progress.HeroName);
        progress.HeroName = "测试角色2";
        Assert.Equal("测试角色2", progress.HeroName);
        progress.HeroName = string.Empty;
        Assert.Equal(CharacterRoster.LocalName, progress.HeroName);
    }
}

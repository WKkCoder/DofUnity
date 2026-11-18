using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网进城后的主角建立、不落盘隔离、不支持职业显式退回、快照交叉核对与战斗/城镇名牌注入测试(纯内存合成夹具)。
/// 注意:这个类里的测试都不许改全进程共用的静态状态(如 <c>OverlayArt.SharedFonts</c>),因为它与别的测试类并行执行;
/// 需要改的(战斗名牌注入那一条)在 <see cref="OnlineHeroTestsSharedFonts"/>,放在禁止并行的 <see cref="GlobalStateCollection"/> 里。
/// </summary>
public sealed class OnlineHeroTests
{
    private static OnlineCharacterEntry Entry(
        ushort uid = 9101,
        string name = "测试剑魂",
        byte job = 0,
        byte growTypeByte = 0x12,
        byte level = 55)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? string.Empty);
        return new OnlineCharacterEntry(uid, nameBytes, 0, job, growTypeByte, level, 0, 0, Array.Empty<OnlineCharacterEquip>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void Profile_decodes_name_splits_grow_and_awakening_bits_and_keeps_unsynced_flags_explicit()
    {
        OnlineCharacterEntry entry = Entry(uid: 9102, name: "测试狂战", job: 0, growTypeByte: 0xA3, level: 60);
        OnlineTownSnapshotCheck snap = OnlineHeroProfile.CompareSnapshot(entry, selectedSlot: 2, hasSnapshot: true,
            snapSlot: 2, snapLevel: 60, snapJob: 0, snapGrowType: 3);

        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(
            entry,
            slot: 2,
            serverUniqueId: 4096,
            serverVillage: 5,
            snapshotCheck: snap,
            dungeonPermissionCount: 7);

        Assert.Equal("测试狂战", profile.Name);
        Assert.Equal(0, profile.Job);
        Assert.Equal(3, profile.GrowType);
        Assert.Equal(2, profile.Awakening); // bits 4..6 of 0xA3 = 2
        Assert.Equal(60, profile.Level);
        Assert.Equal((ushort)4096, profile.ServerUniqueId);
        Assert.Equal(5, profile.ServerVillage);
        Assert.False(profile.JobFallback);
        Assert.Null(profile.JobFallbackMessage(true));
        Assert.False(profile.SnapshotCheck.HasMismatch);
        Assert.False(profile.InventorySynced);
        Assert.False(profile.EquipsSynced);
        Assert.False(profile.GoldSynced);
        Assert.False(profile.SkillsSynced);
        Assert.Equal(7, profile.DungeonPermissionCount);
    }

    [Fact]
    public void Invalid_utf8_name_falls_back_to_default_name_instead_of_blank()
    {
        var invalid = new OnlineCharacterEntry(9103, new byte[] { 0xFF, 0xFE, 0xFD }, 0, 0, 0, 12, 0, 0, Array.Empty<OnlineCharacterEquip>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(invalid, 0);

        Assert.False(string.IsNullOrEmpty(profile.Name));
        Assert.Equal(PlaceholderTitles.Instance.DefaultName, profile.Name);
    }

    [Fact]
    public void Unsupported_job_marks_explicit_fallback_uses_swordman_def_and_never_touches_save_file()
    {
        using var fx = new HostedRoomFixture();
        string saveDir = Path.Combine(Path.GetTempPath(), "dnf-online-hero-test-" + Guid.NewGuid().ToString("N"));
        string savePath = Path.Combine(saveDir, "hero.sav");
        OnlineCharacterEntry gunner = Entry(uid: 9104, name: "测试神枪手", job: 2, growTypeByte: 0x24, level: 48);
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(gunner, 1);

        Assert.True(profile.JobFallback);
        Assert.Contains("鬼剑士", profile.JobFallbackMessage(hasFonts: true));
        Assert.Contains("FALLBACK", profile.JobFallbackMessage(hasFonts: false));

        HeroProgress progress = HeroProgress.CreateForOnlineHero(fx.Assets, profile);

        Assert.NotNull(progress);
        Assert.True(progress.IsJobFallback);
        Assert.Equal("测试神枪手", progress.HeroName);
        Assert.Equal(48, progress.Level);
        Assert.Equal(EquipRules.SwordmanJob, progress.Job);
        Assert.Equal(HeroProgress.DefaultGrowtype, progress.Growtype);
        Assert.Equal(0, progress.Awakening);
        Assert.False(File.Exists(savePath));
    }

    [Fact]
    public void Swordman_builds_in_memory_progress_with_growtype_awakening_and_name()
    {
        using var fx = new HostedRoomFixture();
        OnlineCharacterEntry swordman = Entry(uid: 9105, name: "测试鬼剑", job: 0, growTypeByte: 0x12, level: 52);
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(swordman, 0);

        HeroProgress progress = HeroProgress.CreateForOnlineHero(fx.Assets, profile);

        Assert.NotNull(progress);
        Assert.False(progress.IsJobFallback);
        Assert.Equal("测试鬼剑", progress.HeroName);
        Assert.Equal(52, progress.Level);
        Assert.Equal(2, progress.Growtype);
        Assert.Equal(1, progress.Awakening);
        Assert.Equal(EquipRules.SwordmanJob, progress.Job);
    }

    [Fact]
    public void Snapshot_check_detects_mismatches_without_exposing_raw_numbers_in_diagnostics()
    {
        OnlineCharacterEntry entry = Entry(uid: 9106, name: "比对角色", job: 0, growTypeByte: 0x12, level: 45);
        OnlineTownSnapshotCheck mismatch = OnlineHeroProfile.CompareSnapshot(entry, selectedSlot: 1, hasSnapshot: true,
            snapSlot: 1, snapLevel: 44, snapJob: 2, snapGrowType: 2);

        Assert.True(mismatch.HasSnapshot);
        Assert.True(mismatch.HasMismatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, mismatch.SlotMatch);
        Assert.Equal(OnlineSnapshotMatch.Mismatch, mismatch.LevelMatch);
        Assert.Equal(OnlineSnapshotMatch.Mismatch, mismatch.JobMatch);
        Assert.Equal(OnlineSnapshotMatch.Matched, mismatch.GrowTypeMatch);

        string diag = mismatch.FormatDiagnostic();
        Assert.Contains("level=mismatch", diag);
        Assert.Contains("job=mismatch", diag);
        Assert.DoesNotContain("45", diag);
        Assert.DoesNotContain("44", diag);
        Assert.DoesNotContain("9106", diag);
    }
}

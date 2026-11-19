using System.Text;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇类型层(角色档案、快照核对、起点与区域映射规则、坐标格式化、脱敏日志行)的逐项测试。
/// 日志行一律整行对字面量(手写,不调产品代码算期望);每个开关、每个分支、每个字段顺序都单独钉住。
/// 数据全是合成的:UID ≥ 9000、角色名"测试角色N"。
/// </summary>
public sealed class OnlineTownTypesTests
{
    private static OnlineCharacterEntry Entry(
        ushort uid = 9101, byte[] name = null, byte job = 0, byte growTypeByte = 0x32, byte level = 55,
        IEnumerable<OnlineCharacterEquip> equips = null)
    {
        return new OnlineCharacterEntry(uid, name ?? Encoding.UTF8.GetBytes("测试角色1"), 0, job, growTypeByte, level, 0, 0, equips,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static OnlineHeroProfile Profile(
        int job = 0, bool synced = true, int? perms = null, OnlineTownSnapshotCheck snap = default, int level = 55,
        string name = "测试角色1", string className = "测试职业", int equips = 2)
    {
        return new OnlineHeroProfile(3, 9301, 9302, job, 0, 0, 0, level, name, className, equips, 77, snap, perms, synced);
    }

    private static OnlineTownSnapshotCheck Snap(int kind)
    {
        switch (kind)
        {
            case 1:
                return new OnlineTownSnapshotCheck(true, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched,
                    OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched);
            case 2:
                return new OnlineTownSnapshotCheck(true, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.Matched,
                    OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Mismatch);
            default:
                return default;
        }
    }

    // 四项比对结论压成四个字母:N = 没带、M = 一致、X = 不一致(顺序:槽位、等级、职业、一转)
    private static string Code(OnlineTownSnapshotCheck check)
    {
        static char Letter(OnlineSnapshotMatch m) => m == OnlineSnapshotMatch.Matched ? 'M' : m == OnlineSnapshotMatch.Mismatch ? 'X' : 'N';
        return new string(new[] { Letter(check.SlotMatch), Letter(check.LevelMatch), Letter(check.JobMatch), Letter(check.GrowTypeMatch) });
    }

    private sealed class StubTitles : ICharacterTitles
    {
        private readonly string _className;

        public StubTitles(string defaultName, string className)
        {
            DefaultName = defaultName;
            _className = className;
        }

        public string DefaultName { get; }

        public List<(int Job, int GrowType, int Awakening)> Asked { get; } = new();

        public string ClassName(int job, int growType, int awakening)
        {
            Asked.Add((job, growType, awakening));
            return _className;
        }
    }

    // ───────────────────────── 进城日志行 ─────────────────────────

    [Theory]
    // 联网同步会话、鬼剑士、有快照且全部一致、有权限、城镇没退回、强切开
    [InlineData(0, true, 3, 1, false, true,
        "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=present snap=present slot=match level=match job=match grow=match")]
    // 未同步会话、非鬼剑士、没快照、没权限、城镇退回、强切关
    [InlineData(2, false, -1, 0, true, false,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=true townfallback=true areasync=off perms=none snap=none slot=na level=na job=na grow=na")]
    // 同步会话、非鬼剑士、权限个数为 0(仍然算"有")、快照有不一致与没带的项、城镇没退回、强切关
    [InlineData(2, true, 0, 2, false, false,
        "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=true townfallback=false areasync=off perms=present snap=present slot=mismatch level=match job=na grow=mismatch")]
    // 未同步会话、鬼剑士、快照一致、没权限、城镇退回、强切开
    [InlineData(0, false, -1, 1, true, true,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=true areasync=on perms=none snap=present slot=match level=match job=match grow=match")]
    public void Enter_line_is_the_exact_literal_for_every_switch_combination(
        int job, bool synced, int perms, int snapKind, bool townFallback, bool areaSync, string expected)
    {
        OnlineHeroProfile profile = Profile(job: job, synced: synced, perms: perms >= 0 ? perms : (int?)null, snap: Snap(snapKind));

        Assert.Equal(expected, OnlineTownLog.Enter(profile, townFallback, areaSync));
    }

    [Theory]
    [InlineData(false, false,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=off perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(true, false,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=true areasync=off perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(false, true,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(true, true,
        "DNF_ONLINE stage=town event=enter mode=offline unsynced=inventory,equips,gold jobfallback=false townfallback=true areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    public void Enter_line_without_a_profile_does_not_throw_and_reads_as_offline_with_nothing_provided(
        bool townFallback, bool areaSync, string expected)
    {
        Assert.Equal(expected, OnlineTownLog.Enter(null, townFallback, areaSync));
    }

    [Fact]
    public void Enter_line_carries_no_name_uid_coordinate_or_area_number()
    {
        OnlineHeroProfile profile = Profile(job: 2, perms: 5, snap: Snap(2), name: "测试角色1");

        string line = OnlineTownLog.Enter(profile, true, true);

        Assert.DoesNotContain("测试角色1", line);
        Assert.DoesNotContain("9301", line);
        Assert.DoesNotContain("9302", line);
        Assert.False(line.Any(char.IsDigit), "进城日志行里不该出现任何数字(名字、UID、编号、坐标)");
    }

    // ───────────────────────── 换区日志行 ─────────────────────────

    [Theory]
    [InlineData(null, null, null, "DNF_ONLINE stage=town event=areachange outcome=unknown")]
    [InlineData("", null, null, "DNF_ONLINE stage=town event=areachange outcome=unknown")]
    [InlineData("confirmed", null, null, "DNF_ONLINE stage=town event=areachange outcome=confirmed")]
    [InlineData("rolledback", 17, null, "DNF_ONLINE stage=town event=areachange outcome=rolledback code=17")]
    [InlineData("rolledback", 0, null, "DNF_ONLINE stage=town event=areachange outcome=rolledback")]
    [InlineData("forbidden", 68, 7, "DNF_ONLINE stage=town event=areachange outcome=forbidden code=68 minutes=7")]
    [InlineData("forbidden", 68, 1, "DNF_ONLINE stage=town event=areachange outcome=forbidden code=68 minutes=1")]
    [InlineData("forbidden", 68, 0, "DNF_ONLINE stage=town event=areachange outcome=forbidden code=68")]
    [InlineData("forbidden", 68, -2, "DNF_ONLINE stage=town event=areachange outcome=forbidden code=68")]
    [InlineData("forbidden", null, 12, "DNF_ONLINE stage=town event=areachange outcome=forbidden minutes=12")]
    [InlineData("forbidden", 0, 5, "DNF_ONLINE stage=town event=areachange outcome=forbidden minutes=5")]
    [InlineData(null, 3, 2, "DNF_ONLINE stage=town event=areachange outcome=unknown code=3 minutes=2")]
    public void Area_change_line_is_the_exact_literal(string outcome, int? code, int? minutes, string expected)
    {
        Assert.Equal(expected, OnlineTownLog.AreaChange(outcome, code, minutes));
    }

    [Fact]
    public void Area_change_line_defaults_leave_out_code_and_minutes()
    {
        Assert.Equal("DNF_ONLINE stage=town event=areachange outcome=timeout", OnlineTownLog.AreaChange("timeout"));
        Assert.Equal("DNF_ONLINE stage=town event=areachange outcome=rejected code=9", OnlineTownLog.AreaChange("rejected", 9));
    }

    // ───────────────────────── 断线与退出日志行 ─────────────────────────

    [Theory]
    [InlineData(null, null, "DNF_ONLINE stage=town event=disconnect reason=connection_lost")]
    [InlineData("", null, "DNF_ONLINE stage=town event=disconnect reason=connection_lost")]
    [InlineData("kicked", null, "DNF_ONLINE stage=town event=disconnect reason=kicked")]
    [InlineData("kicked", 9, "DNF_ONLINE stage=town event=disconnect reason=kicked code=9")]
    [InlineData(null, 77, "DNF_ONLINE stage=town event=disconnect reason=connection_lost code=77")]
    [InlineData("", 5, "DNF_ONLINE stage=town event=disconnect reason=connection_lost code=5")]
    [InlineData("kicked", 0, "DNF_ONLINE stage=town event=disconnect reason=kicked code=0")]
    public void Disconnect_line_is_the_exact_literal(string reason, int? code, string expected)
    {
        Assert.Equal(expected, OnlineTownLog.Disconnect(reason, code));
    }

    [Fact]
    public void Disconnect_line_default_code_is_left_out()
    {
        Assert.Equal("DNF_ONLINE stage=town event=disconnect reason=kicked", OnlineTownLog.Disconnect("kicked"));
    }

    [Theory]
    [InlineData("select", "ok", "DNF_ONLINE stage=town event=exit target=select outcome=ok")]
    [InlineData(null, "ok", "DNF_ONLINE stage=town event=exit target=unknown outcome=ok")]
    [InlineData("select", "", "DNF_ONLINE stage=town event=exit target=select outcome=unknown")]
    [InlineData("", null, "DNF_ONLINE stage=town event=exit target=unknown outcome=unknown")]
    public void Exit_line_is_the_exact_literal(string target, string outcome, string expected)
    {
        Assert.Equal(expected, OnlineTownLog.Exit(target, outcome));
    }

    // ───────────────────────── 角色档案 ─────────────────────────

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(60, 60)]
    public void Profile_constructor_keeps_the_level_at_least_one(int level, int expected)
    {
        Assert.Equal(expected, Profile(level: level).Level);
    }

    [Fact]
    public void Profile_constructor_falls_back_for_empty_name_and_class_name_and_keeps_given_ones()
    {
        Assert.Equal("测试角色1", Profile(name: "测试角色1", className: "测试职业").Name);
        Assert.Equal("测试职业", Profile(name: "测试角色1", className: "测试职业").ClassName);
        Assert.Equal(CharacterRoster.LocalName, Profile(name: null).Name);
        Assert.Equal(CharacterRoster.LocalName, Profile(name: string.Empty).Name);
        Assert.Equal(CharacterRoster.LocalJob, Profile(className: null).ClassName);
        Assert.Equal(CharacterRoster.LocalJob, Profile(className: string.Empty).ClassName);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    public void Profile_constructor_never_reports_a_negative_appearance_equip_count(int given, int expected)
    {
        Assert.Equal(expected, Profile(equips: given).AppearanceEquipCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(10, true)]
    public void Job_fallback_is_set_for_every_job_but_swordman(int job, bool expected)
    {
        Assert.Equal(expected, Profile(job: job).JobFallback);
    }

    [Fact]
    public void Job_fallback_message_is_the_exact_text_in_both_font_modes_and_null_for_swordman()
    {
        OnlineHeroProfile gunner = Profile(job: 2);
        Assert.Equal("当前仅支持鬼剑士外观与技能，暂按鬼剑士显示", gunner.JobFallbackMessage(true));
        Assert.Equal("ONLY SWORDMAN LOOK/SKILLS SUPPORTED; FALLBACK ACTIVE", gunner.JobFallbackMessage(false));

        OnlineHeroProfile swordman = Profile(job: 0);
        Assert.Null(swordman.JobFallbackMessage(true));
        Assert.Null(swordman.JobFallbackMessage(false));
    }

    [Fact]
    public void Profile_declares_every_synced_capability_as_false()
    {
        OnlineHeroProfile profile = Profile();

        Assert.False(profile.InventorySynced);
        Assert.False(profile.EquipsSynced);
        Assert.False(profile.GoldSynced);
        Assert.False(profile.SkillsSynced);
    }

    [Fact]
    public void From_entry_without_an_entry_throws()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineHeroProfile.FromEntry(null, 0));
    }

    [Fact]
    public void From_entry_asks_the_titles_with_job_and_split_grow_and_awakening_and_uses_their_answer()
    {
        var titles = new StubTitles("测试默认名", "测试职业称号");

        // growTypeByte 0xA3:低 4 位 = 一转 3,第 4~6 位 = 觉醒 2
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(job: 1, growTypeByte: 0xA3), 0, titles);

        Assert.Equal("测试职业称号", profile.ClassName);
        Assert.Equal((1, 3, 2), Assert.Single(titles.Asked));
        Assert.Equal(3, profile.GrowType);
        Assert.Equal(2, profile.Awakening);
        Assert.Equal((byte)0xA3, profile.GrowTypeByte);
        Assert.Equal(1, profile.Job);
    }

    [Theory]
    [InlineData(0, 0x00, "SWORDMAN")]
    [InlineData(0, 0x12, "SWORDMAN G2 A1")]
    [InlineData(1, 0xA3, "FIGHTER G3 A2")]
    [InlineData(5, 0x01, "AT GUNNER G1")]
    [InlineData(99, 0x10, "JOB99 A1")]
    public void From_entry_uses_the_placeholder_class_name_when_the_titles_have_none(int job, int growTypeByte, string expected)
    {
        var titles = new StubTitles("测试默认名", null);

        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(job: (byte)job, growTypeByte: (byte)growTypeByte), 0, titles);

        Assert.Equal(expected, profile.ClassName);
        Assert.Single(titles.Asked);
    }

    [Fact]
    public void From_entry_without_titles_uses_the_placeholder_titles()
    {
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(job: 0, growTypeByte: 0x00, name: new byte[] { 0xFF, 0xFE }), 0);

        Assert.Equal("SWORDMAN", profile.ClassName);
        Assert.Equal("NONAME", profile.Name);
    }

    [Fact]
    public void From_entry_name_comes_from_the_bytes_and_falls_back_to_the_default_name_then_the_local_name()
    {
        var titles = new StubTitles("测试默认名", null);
        Assert.Equal("测试角色1", OnlineHeroProfile.FromEntry(Entry(), 0, titles).Name);
        Assert.Equal("测试默认名", OnlineHeroProfile.FromEntry(Entry(name: new byte[] { 0xFF, 0xFE, 0xFD }), 0, titles).Name);
        Assert.Equal("测试默认名", OnlineHeroProfile.FromEntry(Entry(name: Array.Empty<byte>()), 0, titles).Name);

        var emptyDefault = new StubTitles(string.Empty, null);
        Assert.Equal(CharacterRoster.LocalName, OnlineHeroProfile.FromEntry(Entry(name: new byte[] { 0xFF, 0xFE, 0xFD }), 0, emptyDefault).Name);
    }

    [Fact]
    public void From_entry_carries_uid_slot_level_and_pass_through_arguments()
    {
        var equips = new[] { new OnlineCharacterEquip(1, 2, 3, 4, 5, 6), new OnlineCharacterEquip(2, 3, 4, 5, 6, 7) };
        OnlineTownSnapshotCheck check = Snap(2);

        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(uid: 9101, level: 48, equips: equips), 4, null, null, 11, check, 6);

        Assert.Equal(4, profile.Slot);
        Assert.Equal(9101u, profile.CharacterUid);
        Assert.Equal((ushort)9101, profile.ServerUniqueId);
        Assert.Equal(48, profile.Level);
        Assert.Equal(2, profile.AppearanceEquipCount);
        Assert.Equal(11, profile.ServerVillage);
        Assert.Equal(6, profile.DungeonPermissionCount);
        Assert.Equal("XMNX", Code(profile.SnapshotCheck));
        Assert.True(profile.SnapshotCheck.HasSnapshot);
    }

    [Fact]
    public void From_entry_defaults_leave_village_and_permissions_unset_and_session_synced()
    {
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(), 0);

        Assert.Null(profile.ServerVillage);
        Assert.Null(profile.DungeonPermissionCount);
        Assert.True(profile.IsSyncedSession);
        Assert.False(profile.SnapshotCheck.HasSnapshot);
        Assert.Equal("NNNN", Code(profile.SnapshotCheck));
        Assert.Equal(0, profile.AppearanceEquipCount);
    }

    [Fact]
    public void From_entry_takes_an_explicit_server_unique_id_and_session_flag()
    {
        OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(Entry(uid: 9101), 0, null, 4096, null, default, null, false);

        Assert.Equal((ushort)4096, profile.ServerUniqueId);
        Assert.False(profile.IsSyncedSession);
        Assert.Equal(9101u, profile.CharacterUid);
    }

    [Fact]
    public void From_entry_with_a_zero_level_entry_reads_as_level_one()
    {
        Assert.Equal(1, OnlineHeroProfile.FromEntry(Entry(level: 0), 0).Level);
    }

    // ───────────────────────── 快照核对 ─────────────────────────

    [Theory]
    //     槽位 等级 职业 一转   期望(槽位、等级、职业、一转)
    [InlineData(2, 55, 1, 2, "MMMM")]
    [InlineData(3, 55, 1, 2, "XMMM")]
    [InlineData(2, 54, 1, 2, "MXMM")]
    [InlineData(2, 55, 0, 2, "MMXM")]
    [InlineData(2, 55, 1, 3, "MMMX")]
    [InlineData(2, 55, 1, 50, "MMMX")]   // 快照里给整个字节 0x32 不算一致:只比较低 4 位的一转
    [InlineData(null, null, null, null, "NNNN")]
    [InlineData(null, 55, null, 2, "NMNM")]
    [InlineData(2, null, 1, null, "MNMN")]
    [InlineData(9, 99, 9, 9, "XXXX")]
    public void Snapshot_comparison_judges_every_field_on_its_own(int? slot, int? level, int? job, int? grow, string expected)
    {
        // 选中条目:职业 1、一转字节 0x32(一转 2、觉醒 3)、等级 55;选中的槽位是 2
        OnlineCharacterEntry entry = Entry(job: 1, growTypeByte: 0x32, level: 55);

        OnlineTownSnapshotCheck check = OnlineHeroProfile.CompareSnapshot(entry, 2, true, slot, level, job, grow);

        Assert.True(check.HasSnapshot);
        Assert.Equal(expected, Code(check));
        Assert.Equal(expected.Contains('X'), check.HasMismatch);
    }

    [Fact]
    public void Snapshot_comparison_without_a_snapshot_provides_nothing_even_when_values_are_given()
    {
        OnlineCharacterEntry entry = Entry(job: 1, growTypeByte: 0x32, level: 55);

        OnlineTownSnapshotCheck check = OnlineHeroProfile.CompareSnapshot(entry, 2, false, 9, 99, 9, 9);

        Assert.False(check.HasSnapshot);
        Assert.Equal("NNNN", Code(check));
        Assert.False(check.HasMismatch);
        Assert.Equal("snap=none slot=na level=na job=na grow=na", check.FormatDiagnostic());
    }

    [Fact]
    public void Snapshot_comparison_without_an_entry_throws_even_without_a_snapshot()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineHeroProfile.CompareSnapshot(null, 0, false, null, null, null, null));
        Assert.Throws<ArgumentNullException>(() => OnlineHeroProfile.CompareSnapshot(null, 0, true, 1, 1, 1, 1));
    }

    [Theory]
    [InlineData(OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, true)]
    [InlineData(OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, true)]
    [InlineData(OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.Matched, true)]
    [InlineData(OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch, true)]
    [InlineData(OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Mismatch, true)]
    [InlineData(OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, true)]
    [InlineData(OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, false)]
    [InlineData(OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, false)]
    [InlineData(OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.NotProvided, false)]
    public void Has_mismatch_is_true_when_any_single_field_mismatches(
        OnlineSnapshotMatch slot, OnlineSnapshotMatch level, OnlineSnapshotMatch job, OnlineSnapshotMatch grow, bool expected)
    {
        Assert.Equal(expected, new OnlineTownSnapshotCheck(true, slot, level, job, grow).HasMismatch);
    }

    [Theory]
    [InlineData(true, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Matched,
        "snap=present slot=match level=mismatch job=na grow=match")]
    [InlineData(false, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided,
        "snap=none slot=na level=na job=na grow=na")]
    [InlineData(true, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch,
        "snap=present slot=mismatch level=na job=match grow=mismatch")]
    [InlineData(true, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Mismatch, OnlineSnapshotMatch.NotProvided,
        "snap=present slot=na level=match job=mismatch grow=na")]
    [InlineData(false, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched, OnlineSnapshotMatch.Matched,
        "snap=none slot=match level=match job=match grow=match")]
    public void Snapshot_diagnostic_is_the_exact_literal(
        bool has, OnlineSnapshotMatch slot, OnlineSnapshotMatch level, OnlineSnapshotMatch job, OnlineSnapshotMatch grow, string expected)
    {
        Assert.Equal(expected, new OnlineTownSnapshotCheck(has, slot, level, job, grow).FormatDiagnostic());
    }

    // ───────────────────────── 起点城镇 ─────────────────────────

    private static KitArea[] OneArea() => new[] { new KitArea(0) };

    [Theory]
    [InlineData(2, 2, false)]   // 服务端给的城镇在图册里(既不是第一座也不是默认城镇):就用它
    [InlineData(3, 3, false)]   // 图册里排第一的城镇
    [InlineData(1, 1, false)]   // 默认城镇 1 本身
    [InlineData(9, 1, true)]    // 图册里没有:退回默认城镇 1(它存在,且不是枚举里的第一座)
    [InlineData(0, 1, true)]    // 0 也是"没有":退回默认城镇 1
    [InlineData(-4, 1, true)]
    [InlineData(null, 1, true)] // 服务端没给:同样退回 1
    public void Start_town_prefers_the_server_town_then_falls_back_to_town_one(int? server, int expected, bool fallback)
    {
        TownAtlas atlas = TownKit.Atlas((3, OneArea()), (2, OneArea()), (1, OneArea()));

        int town = OnlineTownAreaRules.ResolveStartTown(atlas, server, out bool usedFallback);

        Assert.Equal(expected, town);
        Assert.Equal(fallback, usedFallback);
    }

    [Theory]
    [InlineData(null, 5, true)]   // 没有城镇 1:退回图册里枚举的第一座
    [InlineData(9, 5, true)]
    [InlineData(7, 7, false)]
    [InlineData(5, 5, false)]
    public void Start_town_without_town_one_falls_back_to_the_first_town_in_the_atlas(int? server, int expected, bool fallback)
    {
        TownAtlas atlas = TownKit.Atlas((5, OneArea()), (7, OneArea()));

        int town = OnlineTownAreaRules.ResolveStartTown(atlas, server, out bool usedFallback);

        Assert.Equal(expected, town);
        Assert.Equal(fallback, usedFallback);
    }

    [Fact]
    public void Start_town_in_an_empty_atlas_throws_and_a_missing_atlas_throws_argument_null()
    {
        TownAtlas empty = TownKit.Atlas();

        Assert.Throws<InvalidOperationException>(() => OnlineTownAreaRules.ResolveStartTown(empty, null, out _));
        Assert.Throws<InvalidOperationException>(() => OnlineTownAreaRules.ResolveStartTown(empty, 4, out _));
        Assert.Throws<ArgumentNullException>(() => OnlineTownAreaRules.ResolveStartTown(null, 1, out _));
    }

    // ───────────────────────── 局部图号 → 服务端编号 ─────────────────────────

    private static TownAtlas MappingAtlas() => TownKit.Atlas(
        (0, OneArea()),
        (1, new[] { new KitArea(0), new KitArea(255), new KitArea(256) }),
        (255, OneArea()),
        (256, OneArea()));

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, 255, 1, 255)]
    [InlineData(255, 0, 255, 0)]
    public void Map_to_server_area_accepts_zero_and_the_largest_byte_numbers(int town, int area, int village, int serverArea)
    {
        bool ok = OnlineTownAreaRules.TryMapToServerArea(MappingAtlas(), new TownPlace(town, area), out byte v, out byte a);

        Assert.True(ok);
        Assert.Equal((byte)village, v);
        Assert.Equal((byte)serverArea, a);
    }

    [Theory]
    [InlineData(256, 0)]    // 图册里登记了,但村号超过一个字节
    [InlineData(1, 256)]    // 图册里登记了,但区号超过一个字节
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(7, 9)]      // 图册里没有这座城镇
    [InlineData(1, 77)]     // 城镇在,区域不在
    public void Map_to_server_area_rejects_out_of_range_and_unknown_places_and_zeroes_the_outputs(int town, int area)
    {
        bool ok = OnlineTownAreaRules.TryMapToServerArea(MappingAtlas(), new TownPlace(town, area), out byte v, out byte a);

        Assert.False(ok);
        Assert.Equal((byte)0, v);
        Assert.Equal((byte)0, a);
    }

    [Fact]
    public void Map_to_server_area_without_an_atlas_fails_with_zero_outputs()
    {
        bool ok = OnlineTownAreaRules.TryMapToServerArea(null, new TownPlace(1, 0), out byte v, out byte a);

        Assert.False(ok);
        Assert.Equal((byte)0, v);
        Assert.Equal((byte)0, a);
    }

    // ───────────────────────── 切区完成与地下城入口判定 ─────────────────────────

    private static TownAtlas KindAtlas() => TownKit.Atlas(
        (1, new[]
        {
            new KitArea(0, "[gate]", new[] { 233, 177 }),
            new KitArea(1, "[normal]"),
            new KitArea(2, "[dungeon gate]", new[] { 3 }),
        }));

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(0, false, true, true)]    // 关口区只看 0/24
    [InlineData(0, true, false, false)]
    [InlineData(0, true, true, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, false, true, false)]   // 普通区要两条都到
    [InlineData(1, true, false, false)]
    [InlineData(1, true, true, true)]
    public void Area_complete_depends_on_the_kind_of_the_destination(int area, bool userArea, bool areaUsers, bool expected)
    {
        TownAreaInfo info = KindAtlas().GetArea(1, area);

        Assert.Equal(expected, OnlineTownAreaRules.IsAreaComplete(info, userArea, areaUsers));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Area_complete_without_area_info_is_judged_like_a_normal_area(bool userArea, bool areaUsers, bool expected)
    {
        Assert.Equal(expected, OnlineTownAreaRules.IsAreaComplete(null, userArea, areaUsers));
    }

    [Theory]
    [InlineData(1, 1, true)]    // 等级不高于 1:任何区域都算入口
    [InlineData(1, 0, true)]
    [InlineData(2, 1, false)]   // 等级 2 的普通区不算
    [InlineData(2, 0, false)]
    [InlineData(2, 2, true)]    // 地下城入口区域永远算
    [InlineData(50, 2, true)]
    [InlineData(50, 1, false)]
    public void Dungeon_entrance_area_is_low_level_or_a_dungeon_gate(int level, int area, bool expected)
    {
        TownAreaInfo info = KindAtlas().GetArea(1, area);

        Assert.Equal(expected, OnlineTownAreaRules.IsDungeonEntranceArea(info, level));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, true)]
    [InlineData(-3, true)]
    [InlineData(2, false)]
    [InlineData(60, false)]
    public void Dungeon_entrance_area_without_area_info_depends_on_the_level_only(int level, bool expected)
    {
        Assert.Equal(expected, OnlineTownAreaRules.IsDungeonEntranceArea(null, level));
    }

    // ───────────────────────── 坐标格式化 ─────────────────────────

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-1.0, 0)]
    [InlineData(-100.4, 0)]
    [InlineData(double.MinValue, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(double.Epsilon, 0)]
    [InlineData(0.4, 0)]
    [InlineData(0.6, 1)]
    [InlineData(1.0, 1)]
    [InlineData(100.4, 100)]
    [InlineData(100.6, 101)]
    [InlineData(233.0, 233)]
    [InlineData(410.6, 411)]
    [InlineData(65534.4, 65534)]
    [InlineData(65534.6, 65535)]
    [InlineData(65535.0, 65535)]
    [InlineData(65535.4, 65535)]
    [InlineData(65536.0, 65535)]
    [InlineData(70000.0, 65535)]
    [InlineData(1e12, 65535)]
    [InlineData(double.MaxValue, 65535)]
    public void Coordinates_are_rounded_to_the_nearest_and_clamped_into_sixteen_unsigned_bits(double value, int expected)
    {
        Assert.Equal((ushort)expected, OnlineTownAreaRules.FormatCoordinate(value));
    }

    [Fact]
    public void Negative_zero_is_formatted_as_zero()
    {
        double negativeZero = -1.0 * 0.0;

        Assert.True(double.IsNegative(negativeZero));
        Assert.Equal((ushort)0, OnlineTownAreaRules.FormatCoordinate(negativeZero));
    }
}

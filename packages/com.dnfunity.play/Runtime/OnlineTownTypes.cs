using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选中角色字段与进城快照之间的比对单项结果。</summary>
    public enum OnlineSnapshotMatch
    {
        /// <summary>进城快照没带这一项。</summary>
        NotProvided = 0,

        /// <summary>角色列表与进城快照一致。</summary>
        Matched = 1,

        /// <summary>角色列表与进城快照不一致。</summary>
        Mismatch = 2,
    }

    /// <summary>
    /// 选中角色与进城快照(<c>TownEntrySnapshot</c>)的逐项核对结果。
    /// 只暴露比对结论枚举,不存、不打印任何账号、UID、等级数字或坐标。
    /// </summary>
    public readonly struct OnlineTownSnapshotCheck
    {
        public OnlineTownSnapshotCheck(bool hasSnapshot, OnlineSnapshotMatch slotMatch, OnlineSnapshotMatch levelMatch,
            OnlineSnapshotMatch jobMatch, OnlineSnapshotMatch growTypeMatch)
        {
            HasSnapshot = hasSnapshot;
            SlotMatch = slotMatch;
            LevelMatch = levelMatch;
            JobMatch = jobMatch;
            GrowTypeMatch = growTypeMatch;
        }

        public bool HasSnapshot { get; }
        public OnlineSnapshotMatch SlotMatch { get; }
        public OnlineSnapshotMatch LevelMatch { get; }
        public OnlineSnapshotMatch JobMatch { get; }
        public OnlineSnapshotMatch GrowTypeMatch { get; }

        public bool HasMismatch =>
            SlotMatch == OnlineSnapshotMatch.Mismatch
            || LevelMatch == OnlineSnapshotMatch.Mismatch
            || JobMatch == OnlineSnapshotMatch.Mismatch
            || GrowTypeMatch == OnlineSnapshotMatch.Mismatch;

        /// <summary>格式化为不含具体数值的诊断片段。</summary>
        public string FormatDiagnostic()
        {
            return "snap=" + (HasSnapshot ? "present" : "none")
                + " slot=" + Token(SlotMatch)
                + " level=" + Token(LevelMatch)
                + " job=" + Token(JobMatch)
                + " grow=" + Token(GrowTypeMatch);
        }

        private static string Token(OnlineSnapshotMatch match)
        {
            switch (match)
            {
                case OnlineSnapshotMatch.Matched: return "match";
                case OnlineSnapshotMatch.Mismatch: return "mismatch";
                default: return "na";
            }
        }
    }

    /// <summary>
    /// 联网选角进城时从服务端角色条目拆出来的只读主角档案(纯内存,不触碰离线存档)。
    /// 包含显式的退回标记(<see cref="JobFallback"/>)与未同步能力声明,不虚构服务端数据。
    /// </summary>
    public sealed class OnlineHeroProfile
    {
        public const int SwordmanJobId = EquipRules.SwordmanJob;

        public OnlineHeroProfile(
            int slot,
            uint characterUid,
            ushort serverUniqueId,
            int job,
            byte growTypeByte,
            int growType,
            int awakening,
            int level,
            string name,
            string className,
            int appearanceEquipCount,
            int? serverVillage,
            OnlineTownSnapshotCheck snapshotCheck,
            int? dungeonPermissionCount,
            bool isSyncedSession = true)
        {
            Slot = slot;
            CharacterUid = characterUid;
            ServerUniqueId = serverUniqueId;
            Job = job;
            GrowTypeByte = growTypeByte;
            GrowType = growType;
            Awakening = awakening;
            Level = level < 1 ? 1 : level;
            Name = string.IsNullOrEmpty(name) ? CharacterRoster.LocalName : name;
            ClassName = string.IsNullOrEmpty(className) ? CharacterRoster.LocalJob : className;
            AppearanceEquipCount = appearanceEquipCount < 0 ? 0 : appearanceEquipCount;
            ServerVillage = serverVillage;
            SnapshotCheck = snapshotCheck;
            DungeonPermissionCount = dungeonPermissionCount;
            IsSyncedSession = isSyncedSession;
            JobFallback = job != SwordmanJobId;
        }

        public int Slot { get; }
        public uint CharacterUid { get; }
        public ushort ServerUniqueId { get; }
        public int Job { get; }
        public byte GrowTypeByte { get; }
        public int GrowType { get; }
        public int Awakening { get; }
        public int Level { get; }
        public string Name { get; }
        public string ClassName { get; }
        public int AppearanceEquipCount { get; }
        public int? ServerVillage { get; }
        public OnlineTownSnapshotCheck SnapshotCheck { get; }
        public int? DungeonPermissionCount { get; }
        public bool IsSyncedSession { get; }

        /// <summary>服务端职业不是鬼剑士时为 true:外显与技能退回鬼剑士并在界面与日志中明确声明。</summary>
        public bool JobFallback { get; }

        /// <summary>物品/装备/金币同步状态(默认 false,同步落地后按实际写入置位);技能本轮恒为 false。</summary>
        public bool InventorySynced { get; private set; }
        public bool EquipsSynced { get; private set; }
        public bool GoldSynced { get; private set; }
        public bool SkillsSynced => false;

        /// <summary>更新金币、背包、装备栏同步标志(不影响技能标志)。</summary>
        public void ApplyItemSyncState(bool goldSynced, bool inventorySynced, bool equipsSynced)
        {
            GoldSynced = goldSynced;
            InventorySynced = inventorySynced;
            EquipsSynced = equipsSynced;
        }

        /// <summary>当职业退回鬼剑士时返回明确提示文案;否则返回 null。</summary>
        public string JobFallbackMessage(bool hasFonts)
        {
            if (!JobFallback)
            {
                return null;
            }
            return hasFonts ? "当前仅支持鬼剑士外观与技能，暂按鬼剑士显示" : "ONLY SWORDMAN LOOK/SKILLS SUPPORTED; FALLBACK ACTIVE";
        }

        /// <summary>从选中的角色条目、进城加载信息与进城快照核对构建联网角色档案。</summary>
        public static OnlineHeroProfile FromEntry(
            OnlineCharacterEntry entry,
            int slot,
            ICharacterTitles titles = null,
            ushort? serverUniqueId = null,
            int? serverVillage = null,
            OnlineTownSnapshotCheck snapshotCheck = default,
            int? dungeonPermissionCount = null,
            bool isSyncedSession = true)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            ICharacterTitles source = titles ?? PlaceholderTitles.Instance;
            string defaultName = string.IsNullOrEmpty(source.DefaultName) ? CharacterRoster.LocalName : source.DefaultName;
            string name = CharacterNames.Decode(entry.NameBytes, defaultName, out _);
            if (string.IsNullOrEmpty(name))
            {
                name = defaultName;
            }
            int growType = entry.GrowTypeByte & 0x0F;
            int awakening = (entry.GrowTypeByte >> 4) & 0x07;
            string className = source.ClassName(entry.Job, growType, awakening)
                ?? OnlineCharacterRoster.PlaceholderClassName(entry.Job, growType, awakening);
            return new OnlineHeroProfile(
                slot,
                entry.Uid,
                serverUniqueId ?? unchecked((ushort)entry.Uid),
                entry.Job,
                entry.GrowTypeByte,
                growType,
                awakening,
                entry.Level < 1 ? 1 : entry.Level,
                name,
                className,
                entry.Equips != null ? entry.Equips.Count : 0,
                serverVillage,
                snapshotCheck,
                dungeonPermissionCount,
                isSyncedSession);
        }

        /// <summary>按原始选中条目字段核对进城快照中的基础槽位/等级/职业/一转信息。</summary>
        public static OnlineTownSnapshotCheck CompareSnapshot(
            OnlineCharacterEntry entry,
            int selectedSlot,
            bool hasSnapshot,
            int? snapSlot,
            int? snapLevel,
            int? snapJob,
            int? snapGrowType)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if (!hasSnapshot)
            {
                return new OnlineTownSnapshotCheck(false, OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided,
                    OnlineSnapshotMatch.NotProvided, OnlineSnapshotMatch.NotProvided);
            }
            int growType = entry.GrowTypeByte & 0x0F;
            return new OnlineTownSnapshotCheck(
                true,
                CompareInt(selectedSlot, snapSlot),
                CompareInt(entry.Level, snapLevel),
                CompareInt(entry.Job, snapJob),
                CompareInt(growType, snapGrowType));
        }

        private static OnlineSnapshotMatch CompareInt(int expected, int? actual)
        {
            if (!actual.HasValue)
            {
                return OnlineSnapshotMatch.NotProvided;
            }
            return expected == actual.Value ? OnlineSnapshotMatch.Matched : OnlineSnapshotMatch.Mismatch;
        }
    }

    /// <summary>
    /// 联网城镇的起点落点、局部图号到服务端 <c>village/area</c> 映射及切区完成判定规则(静态 RE 证据见 NOTES)。
    /// </summary>
    public static class OnlineTownAreaRules
    {
        public const int DefaultFallbackTownId = 1;

        /// <summary>按服务端 <c>SelectCharacterLoadInfo.Village</c> 选起始城镇;图册里找不到时退回默认城镇 1(或图册第一座城镇)。</summary>
        public static int ResolveStartTown(TownAtlas atlas, int? serverVillage, out bool usedFallback)
        {
            if (atlas == null)
            {
                throw new ArgumentNullException(nameof(atlas));
            }
            if (serverVillage.HasValue && atlas.GetTown(serverVillage.Value) != null)
            {
                usedFallback = false;
                return serverVillage.Value;
            }
            usedFallback = true;
            if (atlas.GetTown(DefaultFallbackTownId) != null)
            {
                return DefaultFallbackTownId;
            }
            foreach (int townId in atlas.TownIds)
            {
                return townId;
            }
            throw new InvalidOperationException("城镇图册里没有任何城镇");
        }

        /// <summary>校验本地 <see cref="TownPlace"/> 是否能映射到线上的 byte 编号并在图册中有登记。</summary>
        public static bool TryMapToServerArea(TownAtlas atlas, TownPlace place, out byte village, out byte area)
        {
            if (atlas != null
                && place.Town >= 0 && place.Town <= byte.MaxValue
                && place.Area >= 0 && place.Area <= byte.MaxValue
                && atlas.GetArea(place.Town, place.Area) != null)
            {
                village = (byte)place.Town;
                area = (byte)place.Area;
                return true;
            }
            village = 0;
            area = 0;
            return false;
        }

        /// <summary>
        /// 服务端 <c>Area::insert_user</c> 的分支特征:
        /// <see cref="TownAreaKind.Gate"/> 只收 <c>0/24 AREA_USERS</c>(跳过 <c>0/23</c>),非 Gate 区域需同时收到 <c>0/23</c> 与 <c>0/24</c>。
        /// </summary>
        public static bool IsAreaComplete(TownAreaInfo areaInfo, bool receivedUserArea, bool receivedAreaUsers)
        {
            if (areaInfo != null && areaInfo.Kind == TownAreaKind.Gate)
            {
                return receivedAreaUsers;
            }
            return receivedUserArea && receivedAreaUsers;
        }

        /// <summary>服务端 <c>GameWorld::isDungeonEntranceArea</c>(等级不高于 1 或区域种类为 <see cref="TownAreaKind.DungeonGate"/>)。</summary>
        public static bool IsDungeonEntranceArea(TownAreaInfo areaInfo, int level)
        {
            return level <= 1 || (areaInfo != null && areaInfo.Kind == TownAreaKind.DungeonGate);
        }

        /// <summary>把世界浮点坐标夹成协议里的 16 位无符号像素坐标(1/38 里的目标落点 x、y):非数、无穷、不大于 0 的取 0,四舍五入,超过 65535 取 65535。</summary>
        public static ushort FormatCoordinate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                return 0;
            }
            double rounded = Math.Round(value);
            return rounded >= ushort.MaxValue ? ushort.MaxValue : (ushort)rounded;
        }
    }

    /// <summary>
    /// 联网城镇阶段的脱敏日志行格式:严格不写角色名、UID、坐标、城镇与区域编号、IP、端口或凭据。
    /// </summary>
    public static class OnlineTownLog
    {
        public static string Enter(OnlineHeroProfile profile, bool townFallback, bool areaSyncEnabled)
        {
            bool synced = profile != null && profile.IsSyncedSession;
            bool jobFallback = profile != null && profile.JobFallback;
            OnlineTownSnapshotCheck snap = profile != null ? profile.SnapshotCheck : default;
            return "DNF_ONLINE stage=town event=enter"
                + " mode=" + (synced ? "online" : "offline")
                + " unsynced=" + UnsyncedToken(profile)
                + " jobfallback=" + BoolToken(jobFallback)
                + " townfallback=" + BoolToken(townFallback)
                + " areasync=" + (areaSyncEnabled ? "on" : "off")
                + " perms=" + (profile != null && profile.DungeonPermissionCount.HasValue ? "present" : "none")
                + " " + snap.FormatDiagnostic();
        }

        private static string UnsyncedToken(OnlineHeroProfile profile)
        {
            bool invSynced = profile != null && profile.InventorySynced;
            bool eqSynced = profile != null && profile.EquipsSynced;
            bool goldSynced = profile != null && profile.GoldSynced;
            bool skillsSynced = profile != null && profile.SkillsSynced;
            if (!invSynced && !eqSynced && !goldSynced)
            {
                return "inventory,equips,gold";
            }
            var parts = new List<string>(4);
            if (!invSynced) parts.Add("inventory");
            if (!eqSynced) parts.Add("equips");
            if (!goldSynced) parts.Add("gold");
            if (!skillsSynced) parts.Add("skills");
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        public static string AreaChange(string outcome, int? errorCode = null, int? minutes = null)
        {
            string line = "DNF_ONLINE stage=town event=areachange outcome=" + (string.IsNullOrEmpty(outcome) ? "unknown" : outcome);
            if (errorCode.HasValue && errorCode.Value != 0)
            {
                line += " code=" + errorCode.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (minutes.HasValue && minutes.Value > 0)
            {
                line += " minutes=" + minutes.Value.ToString(CultureInfo.InvariantCulture);
            }
            return line;
        }

        public static string Disconnect(string reason, int? code = null)
        {
            string line = "DNF_ONLINE stage=town event=disconnect reason=" + (string.IsNullOrEmpty(reason) ? "connection_lost" : reason);
            if (code.HasValue)
            {
                line += " code=" + code.Value.ToString(CultureInfo.InvariantCulture);
            }
            return line;
        }

        public static string Exit(string target, string outcome)
        {
            return "DNF_ONLINE stage=town event=exit target=" + (string.IsNullOrEmpty(target) ? "unknown" : target)
                + " outcome=" + (string.IsNullOrEmpty(outcome) ? "unknown" : outcome);
        }

        private static string BoolToken(bool value) => value ? "true" : "false";
    }
}

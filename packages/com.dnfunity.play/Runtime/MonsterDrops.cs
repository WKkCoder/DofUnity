using System;
using System.Runtime.CompilerServices;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 从用户自己的 PVF 里读来的怪物金币掉落数据(证据 E-116):<c>Etc/ItemDropInfo_Monseter.etc</c>(掉落概率行与各档倍率)
    /// 与 <c>Etc/ItemDropInfo_Common.etc</c>(按怪物等级的基础金额)。两个都读到才算有数据;缺一个或读错时 <see cref="Table"/>、<see cref="Gold"/> 为 null,
    /// 杀怪不掉金币(旧行为),原因写在 <see cref="Error"/>。同一个 <see cref="DnfAssets"/> 只读一次(一座副本每个房间各建一个战斗会话)。
    /// </summary>
    public sealed class MonsterDrops
    {
        /// <summary>掉落概率表的脚本路径(原版数据文件名就是这样拼的)。</summary>
        public const string MonsterPath = "Etc/ItemDropInfo_Monseter.etc";

        /// <summary>基础金额表的脚本路径。</summary>
        public const string CommonPath = "Etc/ItemDropInfo_Common.etc";

        private static readonly ConditionalWeakTable<DnfAssets, MonsterDrops> Cache = new ConditionalWeakTable<DnfAssets, MonsterDrops>();

        public MonsterDrops(MonsterDropTable table, GoldGenTable gold, string error)
        {
            Table = table;
            Gold = gold;
            Error = error;
        }

        public MonsterDropTable Table { get; }

        public GoldGenTable Gold { get; }

        /// <summary>读不到数据的原因;读到了是 null。</summary>
        public string Error { get; }

        /// <summary>两张表都读到了。</summary>
        public bool Available => Table != null && Gold != null;

        /// <summary>读两张表(按 <see cref="DnfAssets"/> 缓存);<paramref name="assets"/> 为 null 时抛 <see cref="ArgumentNullException"/>。</summary>
        public static MonsterDrops Load(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            return Cache.GetValue(assets, ReadFrom);
        }

        private static MonsterDrops ReadFrom(DnfAssets assets)
        {
            ScriptCursor monster = Open(assets, MonsterPath, out string error);
            if (monster == null)
            {
                return new MonsterDrops(null, null, error);
            }
            MonsterDropTable table = MonsterDropTable.Load(monster, out error);
            if (table == null)
            {
                return new MonsterDrops(null, null, MonsterPath + ": " + error);
            }
            ScriptCursor common = Open(assets, CommonPath, out error);
            if (common == null)
            {
                return new MonsterDrops(null, null, error);
            }
            GoldGenTable gold = GoldGenTable.Load(common, out error);
            if (gold == null)
            {
                return new MonsterDrops(null, null, CommonPath + ": " + error);
            }
            return new MonsterDrops(table, gold, null);
        }

        private static ScriptCursor Open(DnfAssets assets, string path, out string error)
        {
            error = null;
            if (assets.Pvf == null || !assets.Pvf.TryGetEntry(PvfPath.Normalize(path), out PvfEntry entry))
            {
                error = path + ": 没有这个脚本";
                return null;
            }
            byte[] raw = assets.Pvf.Read(entry);
            if (!TokenScript.IsTokenScript(raw))
            {
                error = path + ": 不是二进制脚本";
                return null;
            }
            return new ScriptCursor(TokenScript.Parse(raw), assets.Strings);
        }
    }
}

using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:服务端 importAccountCargoScript 0x088c7e80(读账号金库脚本)、AccountCargoScript::GetCreateInfo 0x088c8056 / GetCurrUpgradeInfo 0x088c80ba / GetNextUpgradeInfo 0x088c8124
// (档位存在 std::map<容量, 四个整数> 里:建立用第一项、当前档按容量精确查、下一档是紧跟着的那一项)。
// 建立 CAccountCargo::CreateAccountCargo 0x0828ad72 与升级 UpgradeAccountCargo 0x0828b0b2 取档位的第 2、3、4 个整数当金币上限、材料件数、价钱。
namespace Dnf.Game
{
    /// <summary>账号金库的一档:这一档的容量、金币上限、升到这一档要的材料件数与金币。四个数都是脚本里的原版数据。</summary>
    public sealed class AccountCargoTier
    {
        public AccountCargoTier(int capacity, int goldLimit, int materialCount, int priceGold)
        {
            Capacity = capacity;
            GoldLimit = goldLimit;
            MaterialCount = materialCount;
            PriceGold = priceGold;
        }

        /// <summary>这一档开放的格数(同时是这一档在表里的键)。</summary>
        public int Capacity { get; }

        /// <summary>这一档金库里最多能放多少金币。</summary>
        public int GoldLimit { get; }

        /// <summary>升到这一档要交几件材料(不大于 0 表示不要;服务端只判断是不是 0,负数没有意义)。</summary>
        public int MaterialCount { get; }

        /// <summary>升到这一档要交多少金币(不大于 0 表示不要)。</summary>
        public int PriceGold { get; }
    }

    /// <summary>
    /// 账号金库脚本(<c>Etc/AccountCargo.etc</c>):建立要求的等级与材料物品、按容量排好的档位表。
    /// 标签:<c>[required level]</c>、<c>[required item]</c>、<c>[upgrade info]</c>(后面一串整数,每四个一组:容量、金币上限、材料件数、价钱)、<c>[account cargo end]</c>。
    /// 装载照服务端:整数一直读到读不出整数为止,多读的那个标记已被消耗(所以档位块后面要有一个收尾标记被它吃掉,紧跟着的 <c>[account cargo end]</c> 才读得到;
    /// 收尾标记缺了,<c>[account cargo end]</c> 被当成多读的标记吃掉,装载失败);同一个容量写了两组时留先写的那组(std::map::insert 不覆盖);
    /// 没读到 <c>[account cargo end]</c> 就算装载失败。表里最小容量的那一档是"建立"用的,其余是逐档升级。
    /// </summary>
    public sealed class AccountCargoScript
    {
        public const string Path = "Etc/AccountCargo.etc";

        private readonly List<AccountCargoTier> _tiers;

        /// <summary>用给定的档位构造(按容量升序排;同容量留先给的);测试用。</summary>
        public AccountCargoScript(int requiredLevel, int requiredItem, IEnumerable<AccountCargoTier> tiers)
        {
            RequiredLevel = requiredLevel;
            RequiredItem = requiredItem;
            var seen = new HashSet<int>();
            _tiers = new List<AccountCargoTier>();
            if (tiers != null)
            {
                foreach (AccountCargoTier tier in tiers)
                {
                    if (tier != null && seen.Add(tier.Capacity))
                    {
                        _tiers.Add(tier);
                    }
                }
            }
            _tiers.Sort((a, b) => a.Capacity.CompareTo(b.Capacity));
        }

        /// <summary>建立账号金库要的角色等级(<c>[required level]</c>,没写是 0)。</summary>
        public int RequiredLevel { get; }

        /// <summary>建立与升级要交的材料物品编号(<c>[required item]</c>,没写是 0)。</summary>
        public int RequiredItem { get; }

        /// <summary>全部档位,按容量从小到大。</summary>
        public IReadOnlyList<AccountCargoTier> Tiers => _tiers;

        /// <summary>建立账号金库用的那一档(容量最小的);没有档位返回 null。</summary>
        public AccountCargoTier Create => _tiers.Count > 0 ? _tiers[0] : null;

        /// <summary>容量正好是 <paramref name="capacity"/> 的那一档;没有这个容量返回 null(服务端 GetCurrUpgradeInfo 按键精确查)。</summary>
        public AccountCargoTier Current(int capacity)
        {
            int index = IndexOf(capacity);
            return index < 0 ? null : _tiers[index];
        }

        /// <summary>
        /// 现有容量 <paramref name="capacity"/> 的下一档(容量紧接着更大的那一档);现有容量不在表里、或已经是最后一档时返回 null(服务端 GetNextUpgradeInfo)。
        /// </summary>
        public AccountCargoTier Next(int capacity)
        {
            int index = IndexOf(capacity);
            return index < 0 || index + 1 >= _tiers.Count ? null : _tiers[index + 1];
        }

        private int IndexOf(int capacity)
        {
            for (int i = 0; i < _tiers.Count; i++)
            {
                if (_tiers[i].Capacity == capacity)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>读一份脚本的原始字节;不是标记流脚本或装载失败(没有 <c>[account cargo end]</c>)返回 null。</summary>
        public static AccountCargoScript Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings);
        }

        /// <summary>读一份已经拆成标记的脚本(逐个标记照服务端的取法);装载失败返回 null。</summary>
        public static AccountCargoScript Parse(ScriptToken[] tokens, PvfStringTable strings)
        {
            var cursor = new ScriptCursor(tokens, strings);
            int requiredLevel = 0;
            int requiredItem = 0;
            var tiers = new List<AccountCargoTier>();
            while (cursor.ScanType(out string name))
            {
                if (string.Equals(name, "[required level]", StringComparison.Ordinal))
                {
                    requiredLevel = cursor.ScanInt(out bool _);
                }
                else if (string.Equals(name, "[required item]", StringComparison.Ordinal))
                {
                    requiredItem = cursor.ScanInt(out bool _);
                }
                else if (string.Equals(name, "[upgrade info]", StringComparison.Ordinal))
                {
                    while (true)
                    {
                        int capacity = cursor.ScanInt(out bool ok);
                        if (!ok)
                        {
                            break;
                        }
                        int goldLimit = cursor.ScanInt(out bool _);
                        int materialCount = cursor.ScanInt(out bool _);
                        int priceGold = cursor.ScanInt(out bool _);
                        tiers.Add(new AccountCargoTier(capacity, goldLimit, materialCount, priceGold));
                    }
                }
                else if (string.Equals(name, "[account cargo end]", StringComparison.Ordinal))
                {
                    return new AccountCargoScript(requiredLevel, requiredItem, tiers);
                }
            }
            return null;
        }

        /// <summary>从数据里读账号金库脚本;读不到或装载失败返回 null。</summary>
        public static AccountCargoScript Read(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            PvfStringTable strings = data.Strings;
            if (strings == null || !data.TryReadBytes(Path, out byte[] bytes))
            {
                return null;
            }
            try
            {
                return Parse(bytes, strings);
            }
            catch (DnfFormatException)
            {
                return null;
            }
        }
    }
}

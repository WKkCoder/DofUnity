using System;
using System.IO;
using Dnf.Game;
using Dnf.Render;

// 证据:E-111(基础经验表来自服务端数据库表 monster_reward_ref,不在客户端数据里)。
namespace Dnf.Play
{
    /// <summary>
    /// 找怪物基础经验表(<see cref="MobRewardTable"/>)的来源。这张表在服务端的数据库里,<b>不在用户自备的客户端数据里</b>,
    /// 所以按下面的顺序取:
    /// <list type="number">
    /// <item>环境变量 <see cref="EnvVariable"/> 指的文件;</item>
    /// <item>数据目录里的 <see cref="FileName"/>(用户自己从自己的服务端导出的 <c>等级,经验</c> 两列文本);</item>
    /// <item>都没有:占位表(<see cref="MobRewardTable.Placeholder"/>,不是原版数据,<see cref="MobRewardTable.IsPlaceholder"/> 为真)。</item>
    /// </list>
    /// 读文件失败(权限、格式)按没有处理;读到但一个有效行也没有也按没有处理(免得经验一直是 0)。
    /// </summary>
    public static class MobRewards
    {
        public const string FileName = "monster_reward_ref.csv";
        public const string EnvVariable = "DNF_MOB_REWARD_CSV";

        /// <summary>按上面的顺序取;<paramref name="table"/> 用来造占位表。</summary>
        public static MobRewardTable Load(DnfAssets assets, ExpTable table)
        {
            string explicitPath = null;
            try
            {
                explicitPath = Environment.GetEnvironmentVariable(EnvVariable);
            }
            catch (Exception)
            {
                // 受限环境读不了环境变量:当作没设
            }
            MobRewardTable fromFile = TryRead(explicitPath);
            if (fromFile == null && assets != null && !string.IsNullOrEmpty(assets.DataDirectory))
            {
                fromFile = TryRead(Path.Combine(assets.DataDirectory, FileName));
            }
            return fromFile ?? MobRewardTable.Placeholder(table);
        }

        /// <summary>读一个导出文件;路径为空、文件不存在、读不了或没有有效行时返回 null。</summary>
        public static MobRewardTable TryRead(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                MobRewardTable table = MobRewardTable.ParseCsv(File.ReadAllText(path));
                return table.Count > 0 ? table : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

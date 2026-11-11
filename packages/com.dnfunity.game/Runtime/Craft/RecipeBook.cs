using System.Collections.Generic;

// 本项目自己的设计:配方簿 = 学会了哪些配方(配方物品的编号集合)。服务端对应物是副职业配方的"已学位图"(CCharacExpertJob::set_recipe 0x0832b264、
// get_recipe 0x0832b306:30 字节、按脚本给的下标置位),那是副职业线的状态;这里的配方簿是制作线自己的,不读也不写副职业的任何东西。
namespace Dnf.Game
{
    /// <summary>
    /// 学会的配方(配方物品编号)集合。<see cref="Learn"/> 对应服务端 set_recipe:新学会返回 true,已经会了(或编号不合法)返回 false。
    /// 存档里每个编号一行(见 Dnf.Play 的 CraftSave)。
    /// </summary>
    public sealed class RecipeBook
    {
        private readonly SortedSet<int> _learned = new SortedSet<int>();

        /// <summary>学会了几个。</summary>
        public int Count => _learned.Count;

        /// <summary>学会的配方编号(升序)。</summary>
        public IEnumerable<int> Codes => _learned;

        public bool IsLearned(int code)
        {
            return _learned.Contains(code);
        }

        /// <summary>记下学会了一个配方;编号不大于 0 或已经会了返回 false。</summary>
        public bool Learn(int code)
        {
            return code > 0 && _learned.Add(code);
        }

        /// <summary>忘掉一个配方(测试与宿主的管理操作用;服务端读码里没有"遗忘"的规则);本来就不会返回 false。</summary>
        public bool Forget(int code)
        {
            return _learned.Remove(code);
        }

        public void Clear()
        {
            _learned.Clear();
        }
    }
}

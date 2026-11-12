using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网副本房间的出生点计划(r15-dungeonhost):外面(联网起始地图)给的是"这间房有几只怪、各自的服务端编号",本地房间要的是"每只怪摆在哪、是什么怪物编号"。
    /// 起始地图的怪记录里,除了服务端的怪物号,其余字段的含义没有读懂(未核对),所以这里<b>不</b>从记录里推怪物编号与位置:
    /// 拿房间地图自己 [monster] 里的出生点当模板,按顺序循环取用,凑够要的只数。计划的第 i 项就是第 i 条记录的本地怪,
    /// 交给 <see cref="RoomCombatSession.Create"/> 的 <c>spawnPlan</c> 参数后,怪物的 <see cref="MonsterActor.Index"/> 恒等于 i。
    /// 纯函数,不读任何数据,不读随机数;结果与模板的顺序一一对应(同样的输入永远得到同样的计划)。
    /// </summary>
    public static class RoomSpawnPlan
    {
        /// <summary>
        /// 按 <paramref name="templates"/> 的顺序循环凑出 <paramref name="count"/> 个出生点:第 i 项是 <c>templates[i % templates.Count]</c>。
        /// 模板为 null 或为空时返回空计划(没有可摆的怪,宿主据此不进联网房间);<paramref name="count"/> 为 0 同样返回空计划。
        /// 模板比要的只数多时只取前 <paramref name="count"/> 个——房间里的怪物只能比对应表少或相等,不能多出没有服务端编号的怪。
        /// 模板比要的只数少时循环取用,多出来的怪与前面的怪摆在同一个出生点上(位置是否合适没有核对,宿主日志里有只数)。
        /// </summary>
        /// <param name="count">要摆几只(联网对应表里的怪物数)。</param>
        /// <param name="templates">房间地图自己的 [monster] 出生点(<see cref="RoomMap.Monsters"/>)。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 为负数。</exception>
        public static IReadOnlyList<RoomMonsterSpawn> Fill(int count, IReadOnlyList<RoomMonsterSpawn> templates)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "怪物只数不能为负数");
            }
            if (templates == null || templates.Count == 0)
            {
                return new List<RoomMonsterSpawn>();
            }
            var plan = new List<RoomMonsterSpawn>(count);
            for (int i = 0; i < count; i++)
            {
                plan.Add(templates[i % templates.Count]);
            }
            return plan;
        }
    }
}

using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-119(NPC 脚本 npc/*.npc 的 [role] 段写的是"角色名 参数"的成对数据;服务端 loadItemShopFiles 0x08512aa0 与 NPC 导入把 "[item shop]" 的参数当商店编号用)。
namespace Dnf.Game
{
    /// <summary>NPC 脚本 <c>[role]</c> 段里的一项:角色名(如 "[item shop]")与后面的整数参数(没写为 0)。</summary>
    public readonly struct NpcRole
    {
        public NpcRole(string name, int argument)
        {
            Name = name;
            Argument = argument;
        }

        public string Name { get; }

        public int Argument { get; }
    }

    /// <summary>读 NPC 脚本(<c>npc/*.npc</c>)的 <c>[role]</c>:这个 NPC 在城镇里能干什么(开商店、传送……)。目前只有商店用到。</summary>
    public static class NpcRoles
    {
        /// <summary>开 NPC 商店的角色名;参数是 <c>itemshop.lst</c> 的商店编号。</summary>
        public const string ItemShop = "[item shop]";

        /// <summary>
        /// 脚本里所有的角色项(按出现顺序)。<c>[role]</c> 的值是"字符串 整数"成对的,字符串后面没有整数时参数为 0;
        /// 脚本为 null 或没有 <c>[role]</c> 返回空表。
        /// </summary>
        public static List<NpcRole> FromScript(ScriptLine[] lines)
        {
            var roles = new List<NpcRole>();
            if (lines == null)
            {
                return roles;
            }
            foreach (ScriptLine line in lines)
            {
                if (!line.TagIs("[role]"))
                {
                    continue;
                }
                string name = null;
                foreach (ScriptValue v in line.Values)
                {
                    if (v.Kind == ScriptValueKind.String)
                    {
                        if (name != null)
                        {
                            roles.Add(new NpcRole(name, 0));
                        }
                        name = v.Text;
                    }
                    else if (v.IsNumber && name != null)
                    {
                        roles.Add(new NpcRole(name, v.Int));
                        name = null;
                    }
                }
                if (name != null)
                {
                    roles.Add(new NpcRole(name, 0));
                }
            }
            return roles;
        }

        /// <summary>脚本里 <c>[item shop]</c> 角色的商店编号(第一个);没有这个角色返回 −1。</summary>
        public static int ItemShopIndex(ScriptLine[] lines)
        {
            foreach (NpcRole role in FromScript(lines))
            {
                if (string.Equals(role.Name, ItemShop, StringComparison.OrdinalIgnoreCase))
                {
                    return role.Argument;
                }
            }
            return -1;
        }
    }
}

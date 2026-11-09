using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 起始地图通知(类 0、包号 <see cref="PacketId"/>)的明文载荷解析器:把客户端静态读码定案的读序落成只读记录 <see cref="StartMapNotice"/>。
    /// 29 的字段决定起始房间(格子坐标、种子、地图类型与编号、怪记录、物品记录);真实客户端进副本靠服务端依次推 27、28、29。
    /// <para>
    /// 范围:纯明文解析。这批通知在探针的明文流里被封在 561 信封内,信封内层读通之后,把内层帧的载荷直接喂给 <see cref="TryParse"/>;
    /// 解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="TownEntryParser"/> 同风格):短包、读越界一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一个字段(playerIndex)即停,容忍尾部多余字节(只在 <see cref="StartMapNotice.TrailingByteCount"/> 报告个数,不核对);
    /// 字段取值不做范围检查(语义未定,没有可核对的范围)。
    /// </para>
    /// <para>
    /// 读序(小端;clire_NOTES §1.4、§2.6 的【观】,客户端处理器 0x00749c50..0x0074a8f6;读取点之间每一个越过读取的前向跳转都核对过):
    /// 头部 <c>u8 格X, u8 格Y, u8 flagA, i32 种子, u8 全局1, u8 全局2, u8 mapType</c>;
    /// 仅当 mapType 为 1 或 2(0x00749f97..0x00749fa0)
    /// <c>u16 mapId, u8 N, N × 怪记录(16 字节), u8 M, M × 物品记录(16 字节), u8 全局3</c>;
    /// 其后恒读 <c>u8 K, K × {i32,i32,i32,i32}, u8 playerIndex</c>。
    /// 可能越过读取的跳转只有四类:mapType 门、N 为 0、M 为 0、K 为 0。
    /// </para>
    /// <para>
    /// 【全局 3 的归属】全局 3 在"仅 mapType 为 1 或 2"的块里,不在恒读尾部:mapType 门是 <c>0x00749fa0 JNE 0x0074a439</c>,
    /// 目标在读全局 3 的 <c>0x0074a418</c> 之后、读 K 的 <c>0x0074a442</c> 之前,所以 mapType 为其它值时全局 3 不读
    /// (clire_NOTES §2.6 表第 7 行"仅 1 或 2 才有 8-13 项",第 13 项就是全局 3)。
    /// §1.4 的读序表把"(无论 mapType)"标在 K 前面,容易读成全局 3 也恒读;以 §2.6 与反汇编为准。
    /// mapType 为其它值时 <see cref="StartMapNotice.Global3"/> 为 null。起始房的 mapType 实际应是 1 或 2,两种读法在那里结果相同。
    /// </para>
    /// <para>
    /// 【未决点:怪记录步长】本解析器以客户端读序为准,每条怪记录读 16 字节:u16、i32、u16、i32、u8、u8、u8、u8。
    /// 这 8 次读取是直接读到的【观】(0x0074a09c..0x0074a0e3)。服务端侧对怪记录的描述是"short、int、short、int 外加 5 个字节"共 17 字节
    /// (服务端静态读码得来,本任务没有复核),比客户端读序多一个字节。
    /// "客户端没有第 9 次读取"是推断(较强)【推】:8 次读取的实参清栈量合计 0x30,有第 5 个字节读就该是 0x38;
    /// 桩链里能直接看到 <c>83 C4 30</c>(<c>add esp,0x30</c>);但 0x0074a0e8 处的指令被混淆成了跳转桩,没有做完整还原
    /// (clire_NOTES §2.6"怪记录 16 字节 vs 17 字节"段与 §4 未知 3;该笔记被 .gitignore 忽略,不入库)。
    /// 若服务端真按 17 字节写,N 不小于 1 的真实帧会从第一条怪记录之后整体错位;客户端不检查"读完",这个解析器也一样。
    /// 这里不改步长、不做猜测性容错;真实帧校验(用各个个数字段与 <see cref="StartMapNotice.TrailingByteCount"/> 对账)留待信封解开后另行进行。
    /// </para>
    /// </summary>
    public static class StartMapParser
    {
        /// <summary>起始地图通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 29;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>读完全部字段为 true;载荷为 null、过短或某个个数声明超出载荷为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out StartMapNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte cellX)
                || !reader.TryGetByte(out byte cellY)
                || !reader.TryGetByte(out byte flagA)
                || !reader.TryGetInt(out int seed)
                || !reader.TryGetByte(out byte global1)
                || !reader.TryGetByte(out byte global2)
                || !reader.TryGetByte(out byte mapType))
            {
                return false;
            }

            ushort? mapId = null;
            byte? global3 = null;
            StartMapMonsterRecord[] monsters = new StartMapMonsterRecord[0];
            StartMapItemRecord[] items = new StartMapItemRecord[0];
            if (mapType == 1 || mapType == 2)
            {
                if (!reader.TryGetUShort(out ushort mapIdValue)
                    || !TryReadMonsters(reader, out monsters)
                    || !TryReadItems(reader, out items)
                    || !reader.TryGetByte(out byte global3Value))
                {
                    return false;
                }
                mapId = mapIdValue;
                global3 = global3Value;
            }

            if (!TryReadQuads(reader, out StartMapQuadRecord[] quads) || !reader.TryGetByte(out byte playerIndex))
            {
                return false;
            }

            notice = new StartMapNotice(cellX, cellY, flagA, seed, global1, global2, mapType,
                mapId, monsters, items, global3, quads, playerIndex, reader.Remaining);
            return true;
        }

        // u8 个数 N + N 条怪记录,每条 16 字节:u16 A、i32 B、u16 C、i32 D、u8 E、u8 F、u8 G、u8 H(步长的未决点见类说明)。
        private static bool TryReadMonsters(PacketReader reader, out StartMapMonsterRecord[] monsters)
        {
            monsters = null;
            if (!reader.TryGetByte(out byte monsterCount))
            {
                return false;
            }

            var list = new StartMapMonsterRecord[monsterCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort a)
                    || !reader.TryGetInt(out int b)
                    || !reader.TryGetUShort(out ushort c)
                    || !reader.TryGetInt(out int d)
                    || !reader.TryGetByte(out byte e)
                    || !reader.TryGetByte(out byte f)
                    || !reader.TryGetByte(out byte g)
                    || !reader.TryGetByte(out byte h))
                {
                    return false;
                }
                list[i] = new StartMapMonsterRecord(a, b, c, d, e, f, g, h);
            }

            monsters = list;
            return true;
        }

        // u8 个数 M + M 条物品记录,每条 16 字节:u8 A、u16 B、i32 C、i32 D、u16 E、u8 F、u16 G。
        private static bool TryReadItems(PacketReader reader, out StartMapItemRecord[] items)
        {
            items = null;
            if (!reader.TryGetByte(out byte itemCount))
            {
                return false;
            }

            var list = new StartMapItemRecord[itemCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetByte(out byte a)
                    || !reader.TryGetUShort(out ushort b)
                    || !reader.TryGetInt(out int c)
                    || !reader.TryGetInt(out int d)
                    || !reader.TryGetUShort(out ushort e)
                    || !reader.TryGetByte(out byte f)
                    || !reader.TryGetUShort(out ushort g))
                {
                    return false;
                }
                list[i] = new StartMapItemRecord(a, b, c, d, e, f, g);
            }

            items = list;
            return true;
        }

        // u8 个数 K + K 组 {i32 A, i32 B, i32 C, i32 D}(无论 mapType 都读)。
        private static bool TryReadQuads(PacketReader reader, out StartMapQuadRecord[] quads)
        {
            quads = null;
            if (!reader.TryGetByte(out byte quadCount))
            {
                return false;
            }

            var list = new StartMapQuadRecord[quadCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetInt(out int a)
                    || !reader.TryGetInt(out int b)
                    || !reader.TryGetInt(out int c)
                    || !reader.TryGetInt(out int d))
                {
                    return false;
                }
                list[i] = new StartMapQuadRecord(a, b, c, d);
            }

            quads = list;
            return true;
        }
    }
}

using System;
using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 客户端舞台的瓦片格(证据 E-074,对应 DNF.exe 的 0x007D77D0 与 0x007DAAA0):一格 16 × 12 像素,列数 = 瓦片数 × 14,行数取第一块瓦片通行表的行数,
    /// 每格一个通行值,出界按 2(挡路)。各瓦片的通行表按瓦片顺序横向拼起来;出口的门体盖在上面(<see cref="Stamp"/>:逐格取较大的值)。
    /// <see cref="IsFree"/> 是主角能不能站在 (x, y) 的判断:中心点先要在地图里,再把占位盒([width] 的宽与深,左右各取一半,上下各取一半,边界像素算在内)
    /// 覆盖到的所有格子都过一遍,有一格不是 0 就站不下。客户端对 1、3、4、5、6、7 号通行值还要看对象自己的属性(层、飞行等),
    /// 对鬼剑士这些值在洛兰的房间里极少出现(门体与瓦片里只有 0 和 2),这里一律当作挡路(与 <see cref="TileFile"/> 一致)。
    /// </summary>
    public sealed class RoomGrid
    {
        public const int CellWidth = TileFile.CellWidth;
        public const int CellHeight = TileFile.CellHeight;

        /// <summary>表外与挡路格的通行值。</summary>
        public const byte Blocked = 2;

        private readonly byte[] _cells;

        public int Columns { get; }
        public int Rows { get; }

        /// <summary>地图的宽(像素)。</summary>
        public int WidthPixels => Columns * CellWidth;

        /// <summary>地图的高(像素)。</summary>
        public int HeightPixels => Rows * CellHeight;

        public RoomGrid(RoomMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            IReadOnlyList<TileFile> tiles = map.Tiles;
            Columns = tiles.Count * TileFile.Columns;
            Rows = tiles.Count == 0 ? 0 : tiles[0].Rows;
            _cells = new byte[Columns * Rows];
            for (int t = 0; t < tiles.Count; t++)
            {
                TileFile tile = tiles[t];
                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < TileFile.Columns; c++)
                    {
                        _cells[r * Columns + t * TileFile.Columns + c] = r < tile.Rows ? tile.Pass[r * TileFile.Columns + c] : Blocked;
                    }
                }
            }
        }

        /// <summary>某一格的通行值;出界为 <see cref="Blocked"/>。</summary>
        public byte TypeAt(int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            {
                return Blocked;
            }
            return _cells[row * Columns + column];
        }

        /// <summary>
        /// 把出口的门体盖到格子上(客户端 0x00A43770):门体表里每个值与格子里原有的值取较大者;左出口的门体第 0 列、右出口第 13 列的值再沿用到地图边缘。
        /// </summary>
        public void Stamp(GateBody body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }
            for (int r = 0; r < body.Tile.Rows; r++)
            {
                int row = body.OriginRow + r;
                if (row < 0 || row >= Rows)
                {
                    continue;
                }
                for (int column = 0; column < Columns; column++)
                {
                    byte v = body.ValueAt(column, row);
                    if (v != 0)
                    {
                        Raise(column, row, v);
                    }
                }
            }
        }

        private void Raise(int column, int row, byte value)
        {
            int i = row * Columns + column;
            if (_cells[i] < value)
            {
                _cells[i] = value;
            }
        }

        /// <summary>
        /// 占位盒 <paramref name="bodyWidth"/> × <paramref name="bodyDepth"/> 的主角中心在 (x, y) 时能不能站:中心要在地图里,盒子覆盖的格子([x − 宽/2, x + 宽/2] × [y − 深/2, y + 深/2],
        /// 列 = 像素 / 16,行 = 像素 / 12,夹在地图格子范围内)都得是 0;宽与深都为 0 的对象只看中心点在不在地图里。
        /// </summary>
        public bool IsFree(int x, int y, int bodyWidth, int bodyDepth)
        {
            if (x < 0 || x >= WidthPixels || y < 0 || y >= HeightPixels)
            {
                return false;
            }
            if (bodyWidth == 0 && bodyDepth == 0)
            {
                return true;
            }
            int c0 = Math.Max((x - (bodyWidth >> 1)) / CellWidth, 0);
            int c1 = Math.Min((x + (bodyWidth >> 1)) / CellWidth, Columns - 1);
            int r0 = Math.Max((y - (bodyDepth >> 1)) / CellHeight, 0);
            int r1 = Math.Min((y + (bodyDepth >> 1)) / CellHeight, Rows - 1);
            for (int c = c0; c <= c1; c++)
            {
                for (int r = r0; r <= r1; r++)
                {
                    if (_cells[r * Columns + c] != 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}

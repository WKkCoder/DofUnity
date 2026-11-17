using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 验收后补的九宫格拼装测试(变异存活项):九块图块的宽高各不相同时,每一块的落点只取决于它自己的宽高
/// (现有的拼装测试里九块图块一样大,右边缘取了别的块的宽也看不出来)。期望值是手算的字面量。
/// </summary>
public sealed class PickerArtNineSliceTests
{
    private static SpriteFrame Solid(int width, int height, byte r)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 3] = 255;
        }
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    private static (int R, int A) Px(SpriteFrame frame, int x, int y)
    {
        int at = (y * frame.Width + x) * 4;
        return (frame.Rgba[at], frame.Rgba[at + 3]);
    }

    /// <summary>红色分量等于 <paramref name="r"/> 且完全不透明的像素:外框(最小 x、最小 y、最大 x、最大 y)与个数。</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY, int Count) Extent(SpriteFrame frame, int r)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                (int pr, int pa) = Px(frame, x, y);
                if (pr != r || pa != 255)
                {
                    continue;
                }
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                count++;
            }
        }
        return (minX, minY, maxX, maxY, count);
    }

    [Fact]
    public void Every_tile_of_the_board_lands_where_its_own_width_and_height_put_it_when_all_nine_tiles_differ()
    {
        // 九块图块宽高各不相同(红色分量 = 10 × (序号 + 1),序号 0..8 = 左上、上、右上、左、中、右、左下、下、右下):
        //   宽 9、5、11、7、8、10、3、4、2   高 8、3、5、4、6、2、10、7、9
        // 每一块都比它所在的那一格小(上边与角的高、左右边与角的宽、边与中心的宽高),所以块与块之间不重叠、有缝,每一块的落点只取决于它自己的宽高。
        int[] widths = { 9, 5, 11, 7, 8, 10, 3, 4, 2 };
        int[] heights = { 8, 3, 5, 4, 6, 2, 10, 7, 9 };
        var sprites = new ShellFakeSprites();
        for (int i = 0; i < 9; i++)
        {
            sprites.Frames[(PickerArt.WindowImage, i)] = Solid(widths[i], heights[i], (byte)(10 * (i + 1)));
        }
        var art = new PickerArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;

        art.AddBoard(list, ref order, "board");

        SpriteFrame board = Assert.Single(list).Frame;
        Assert.Equal((420, 186), (board.Width, board.Height));            // 9 + 50×8 + 11 与 8 + 28×6 + 10

        // 每种颜色的外框与个数(没有重叠,所以个数 = 块数 × 宽 × 高)
        Assert.Equal((0, 0, 8, 7, 72), Extent(board, 10));                // 左上角 9×8,贴左上
        Assert.Equal((9, 0, 405, 2, 750), Extent(board, 20));             // 上边 5×3 共 50 块,步长 8(中心的宽):9..13、17..21、…、401..405
        Assert.Equal((409, 0, 419, 4, 55), Extent(board, 30));            // 右上角 11×5,贴右上:x 从 420 - 11 = 409 起
        Assert.Equal((0, 8, 6, 173, 784), Extent(board, 40));             // 左边 7×4 共 28 块,步长 6(中心的高):y 8..11、14..17、…、170..173
        Assert.Equal((9, 8, 408, 175, 67200), Extent(board, 50));         // 中心 8×6 铺满 400×168
        Assert.Equal((410, 8, 419, 171, 560), Extent(board, 60));         // 右边 10×2 共 28 块,贴右:x 从 420 - 10 = 410 起(取自它自己的宽),y 8..9、14..15、…、170..171
        Assert.Equal((0, 176, 2, 185, 30), Extent(board, 70));            // 左下角 3×10,贴左下:y 从 186 - 10 = 176 起
        Assert.Equal((9, 179, 404, 185, 1400), Extent(board, 80));        // 下边 4×7 共 50 块,贴底:y 从 186 - 7 = 179 起(取自它自己的高),x 9..12、17..20、…、401..404
        Assert.Equal((418, 177, 419, 185, 18), Extent(board, 90));        // 右下角 2×9,贴右下:x 从 418、y 从 177 起

        // 几个关键的缝与边:右边的左缘是 410 而不是 409(右上角的左缘),中心与右边之间空一列;左边与中心之间空两列
        Assert.Equal((60, 255), Px(board, 410, 8));
        Assert.Equal(0, Px(board, 409, 8).A);
        Assert.Equal((40, 255), Px(board, 6, 8));
        Assert.Equal(0, Px(board, 7, 8).A);
        Assert.Equal(0, Px(board, 8, 8).A);
        Assert.Equal((50, 255), Px(board, 9, 8));
        Assert.Equal(0, Px(board, 9, 7).A);                               // 上边下面、中心上面:上边只有 3 高,角有 8 高,中间空着
        Assert.Equal((80, 255), Px(board, 9, 179));
        Assert.Equal(0, Px(board, 9, 178).A);

        // 其余全透明:一共 420 × 186 = 78120 个像素,画了 70869 个,没画的 7251 个
        int transparent = 0;
        for (int y = 0; y < board.Height; y++)
        {
            for (int x = 0; x < board.Width; x++)
            {
                if (Px(board, x, y).A == 0)
                {
                    transparent++;
                }
            }
        }
        Assert.Equal(7251, transparent);
    }
}

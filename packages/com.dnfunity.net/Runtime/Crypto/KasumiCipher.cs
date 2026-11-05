using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 6:3GPP KASUMI(TS 35.202),块长 8 字节,切片 16 字节全部参与(原版 kasumi_setup 0x080b1a27;本机模拟器逐字节翻转实测)。
    /// 字节序:16 字节密钥按大端切成 8 个 16 位字,64 位块按大端切成两个 32 位半块,输出同样按大端(与 3GPP 公开测试向量一致);
    /// 8 轮,奇数轮 FL 后接 FO、偶数轮 FO 后接 FL(与标准一致,没有变体)。
    /// S7、S9 是公开算法常量(3GPP TS 35.202 的表,与公共领域参考实现一致),并且与原版二进制里的两张表逐项相同(本机脚本整表比对,非 REA)。
    /// 对象在 SetKey 之后只读,同一个对象可以被多个线程同时加解密(SetKey 本身不能与加解密并发)。
    /// </summary>
    public sealed class KasumiCipher : BlockCipher
    {
        // 公开算法常量:密钥修饰常量 C1..C8(TS 35.202)。
        private static readonly int[] KeyMask = { 0x0123, 0x4567, 0x89AB, 0xCDEF, 0xFEDC, 0xBA98, 0x7654, 0x3210 };

        // 公开算法常量:S7 盒(128 项)。
        private static readonly ushort[] S7 =
        {
             54,  50,  62,  56,  22,  34,  94,  96,  38,   6,  63,  93,   2,  18, 123,  33,
             55, 113,  39, 114,  21,  67,  65,  12,  47,  73,  46,  27,  25, 111, 124,  81,
             53,   9, 121,  79,  52,  60,  58,  48, 101, 127,  40, 120, 104,  70,  71,  43,
             20, 122,  72,  61,  23, 109,  13, 100,  77,   1,  16,   7,  82,  10, 105,  98,
            117, 116,  76,  11,  89, 106,   0, 125, 118,  99,  86,  69,  30,  57, 126,  87,
            112,  51,  17,   5,  95,  14,  90,  84,  91,   8,  35, 103,  32,  97,  28,  66,
            102,  31,  26,  45,  75,   4,  85,  92,  37,  74,  80,  49,  68,  29, 115,  44,
             64, 107, 108,  24, 110,  83,  36,  78,  42,  19,  15,  41,  88, 119,  59,   3,
        };

        // 公开算法常量:S9 盒(512 项)。
        private static readonly ushort[] S9 =
        {
            167, 239, 161, 379, 391, 334,   9, 338,  38, 226,  48, 358, 452, 385,  90, 397,
            183, 253, 147, 331, 415, 340,  51, 362, 306, 500, 262,  82, 216, 159, 356, 177,
            175, 241, 489,  37, 206,  17,   0, 333,  44, 254, 378,  58, 143, 220,  81, 400,
             95,   3, 315, 245,  54, 235, 218, 405, 472, 264, 172, 494, 371, 290, 399,  76,
            165, 197, 395, 121, 257, 480, 423, 212, 240,  28, 462, 176, 406, 507, 288, 223,
            501, 407, 249, 265,  89, 186, 221, 428, 164,  74, 440, 196, 458, 421, 350, 163,
            232, 158, 134, 354,  13, 250, 491, 142, 191,  69, 193, 425, 152, 227, 366, 135,
            344, 300, 276, 242, 437, 320, 113, 278,  11, 243,  87, 317,  36,  93, 496,  27,
            487, 446, 482,  41,  68, 156, 457, 131, 326, 403, 339,  20,  39, 115, 442, 124,
            475, 384, 508,  53, 112, 170, 479, 151, 126, 169,  73, 268, 279, 321, 168, 364,
            363, 292,  46, 499, 393, 327, 324,  24, 456, 267, 157, 460, 488, 426, 309, 229,
            439, 506, 208, 271, 349, 401, 434, 236,  16, 209, 359,  52,  56, 120, 199, 277,
            465, 416, 252, 287, 246,   6,  83, 305, 420, 345, 153, 502,  65,  61, 244, 282,
            173, 222, 418,  67, 386, 368, 261, 101, 476, 291, 195, 430,  49,  79, 166, 330,
            280, 383, 373, 128, 382, 408, 155, 495, 367, 388, 274, 107, 459, 417,  62, 454,
            132, 225, 203, 316, 234,  14, 301,  91, 503, 286, 424, 211, 347, 307, 140, 374,
             35, 103, 125, 427,  19, 214, 453, 146, 498, 314, 444, 230, 256, 329, 198, 285,
             50, 116,  78, 410,  10, 205, 510, 171, 231,  45, 139, 467,  29,  86, 505,  32,
             72,  26, 342, 150, 313, 490, 431, 238, 411, 325, 149, 473,  40, 119, 174, 355,
            185, 233, 389,  71, 448, 273, 372,  55, 110, 178, 322,  12, 469, 392, 369, 190,
              1, 109, 375, 137, 181,  88,  75, 308, 260, 484,  98, 272, 370, 275, 412, 111,
            336, 318,   4, 504, 492, 259, 304,  77, 337, 435,  21, 357, 303, 332, 483,  18,
             47,  85,  25, 497, 474, 289, 100, 269, 296, 478, 270, 106,  31, 104, 433,  84,
            414, 486, 394,  96,  99, 154, 511, 148, 413, 361, 409, 255, 162, 215, 302, 201,
            266, 351, 343, 144, 441, 365, 108, 298, 251,  34, 182, 509, 138, 210, 335, 133,
            311, 352, 328, 141, 396, 346, 123, 319, 450, 281, 429, 228, 443, 481,  92, 404,
            485, 422, 248, 297,  23, 213, 130, 466,  22, 217, 283,  70, 294, 360, 419, 127,
            312, 377,   7, 468, 194,   2, 117, 295, 463, 258, 224, 447, 247, 187,  80, 398,
            284, 353, 105, 390, 299, 471, 470, 184,  57, 200, 348,  63, 204, 188,  33, 451,
             97,  30, 310, 219,  94, 160, 129, 493,  64, 179, 263, 102, 189, 207, 114, 402,
            438, 477, 387, 122, 192,  42, 381,   5, 145, 118, 180, 449, 293, 323, 136, 380,
             43,  66,  60, 455, 341, 445, 202, 432,   8, 237,  15, 376, 436, 464,  59, 461,
        };

        private readonly int[] _kl1 = new int[8];
        private readonly int[] _kl2 = new int[8];
        private readonly int[] _ko1 = new int[8];
        private readonly int[] _ko2 = new int[8];
        private readonly int[] _ko3 = new int[8];
        private readonly int[] _ki1 = new int[8];
        private readonly int[] _ki2 = new int[8];
        private readonly int[] _ki3 = new int[8];
        private bool _keyed;

        public override int Slot => 6;

        public override int BlockSize => 8;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            int[] k = new int[8];
            int[] kp = new int[8];
            for (int n = 0; n < 8; n++)
            {
                k[n] = (key[offset + 2 * n] << 8) | key[offset + 2 * n + 1];
                kp[n] = k[n] ^ KeyMask[n];
            }
            for (int n = 0; n < 8; n++)
            {
                _kl1[n] = Rol16(k[n], 1);
                _kl2[n] = kp[(n + 2) & 7];
                _ko1[n] = Rol16(k[(n + 1) & 7], 5);
                _ko2[n] = Rol16(k[(n + 5) & 7], 8);
                _ko3[n] = Rol16(k[(n + 6) & 7], 13);
                _ki1[n] = kp[(n + 4) & 7];
                _ki2[n] = kp[(n + 3) & 7];
                _ki3[n] = kp[(n + 7) & 7];
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint left = SlotKit0To6.LoadBe32(input, inOffset);
            uint right = SlotKit0To6.LoadBe32(input, inOffset + 4);
            for (int n = 0; n < 8;)
            {
                right ^= Fo(Fl(left, n), n);
                n++;
                left ^= Fl(Fo(right, n), n);
                n++;
            }
            SlotKit0To6.StoreBe32(output, outOffset, left);
            SlotKit0To6.StoreBe32(output, outOffset + 4, right);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint left = SlotKit0To6.LoadBe32(input, inOffset);
            uint right = SlotKit0To6.LoadBe32(input, inOffset + 4);
            for (int n = 7; n >= 0;)
            {
                left ^= Fl(Fo(right, n), n);
                n--;
                right ^= Fo(Fl(left, n), n);
                n--;
            }
            SlotKit0To6.StoreBe32(output, outOffset, left);
            SlotKit0To6.StoreBe32(output, outOffset + 4, right);
        }

        private static int Rol16(int x, int n)
        {
            return ((x << n) | (x >> (16 - n))) & 0xFFFF;
        }

        /// <summary>FI 函数:16 位输入拆成 9 位与 7 位两半,过 S9、S7 各两次。</summary>
        private static int Fi(int input, int subKey)
        {
            int nine = (input >> 7) & 0x1FF;
            int seven = input & 0x7F;
            nine = S9[nine] ^ seven;
            seven = S7[seven] ^ (nine & 0x7F);
            seven ^= subKey >> 9;
            nine ^= subKey & 0x1FF;
            nine = S9[nine] ^ seven;
            seven = S7[seven] ^ (nine & 0x7F);
            return (seven << 9) + nine;
        }

        /// <summary>FO 函数:对两个 16 位半块做三次"异或轮密钥、过 FI、异或另一半"。</summary>
        private uint Fo(uint input, int round)
        {
            int left = (int)(input >> 16);
            int right = (int)(input & 0xFFFF);
            left ^= _ko1[round];
            left = Fi(left, _ki1[round]);
            left ^= right;
            right ^= _ko2[round];
            right = Fi(right, _ki2[round]);
            right ^= left;
            left ^= _ko3[round];
            left = Fi(left, _ki3[round]);
            left ^= right;
            return ((uint)right << 16) + (uint)left;
        }

        /// <summary>FL 函数:左半块与 KL1 相与后循环左移 1 位异或进右半块,再用 KL2 相或的结果对左半块做同样的事。</summary>
        private uint Fl(uint input, int round)
        {
            int l = (int)(input >> 16);
            int r = (int)(input & 0xFFFF);
            r ^= Rol16(l & _kl1[round], 1);
            l ^= Rol16(r | _kl2[round], 1);
            return ((uint)l << 16) + (uint)r;
        }
    }
}

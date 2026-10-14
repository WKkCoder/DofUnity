"""DNF 数据层参考实现(Python 标准库,只读)。

用途:作为 P1 的 C# 数据层库的对照标准(oracle)。
约束:只读取用户本机的 Script.pvf 与 ImagePacks2,不写回,不把素材导出到任何项目目录。

已在真实数据上验证的格式事实:
  PVF  : [u32 guidLen][guid][u32 version][u32 dirTreeLen][u32 dirTreeCrc][u32 nFiles]
         目录树与文件内容都按 32 位小端字解密: out = ROR32(word ^ crc ^ 0x81A79011, 6)
         目录项: u32 fileNo, u32 pathLen, path, u32 length, u32 crc, u32 relOffset
         同一个 crc 也是校验值(E-011,全库成立,见 pvf_crc_probe.py):
           文件:   crc == zlib.crc32(解密后按 4 字节对齐的整段, 种子 = fileNo)
           目录树: dirTreeCrc == zlib.crc32(解密后的整段, 种子 = nFiles);整段含末尾 1 个零填充字节
         文件数据起点 = guidLen + 20 + dirTreeLen + relOffset,读取长度按 4 字节向上对齐
  NPK  : "NeoplePack_Bill\\0" + u32 count + count * 264 字节目录项
         目录项: u32 offset, u32 size, 256 字节路径(与固定密钥逐字节异或)
  IMG  : v2 头 32 字节 = 魔数16 + i64 indexSize + i32 version + i32 frameCount
         索引项: 链接帧 8 字节(i32 0x11, i32 target),其他帧 36 字节(i32 type + 8 * i32)
         帧数据从 32 + indexSize 开始,按索引顺序紧挨着排列,compress==6 时为 zlib
"""
import array
import os
import struct
import sys
import zlib
from collections import namedtuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dnfpaths import find_dnf_dir, require_dnf_dir  # noqa: E402

DNF_DIR = find_dnf_dir()  # 找不到时为 None;真正要读数据时才报错
PVF_PATH = os.path.join(DNF_DIR, "Script.pvf") if DNF_DIR else None
NPK_DIR = os.path.join(DNF_DIR, "ImagePacks2") if DNF_DIR else None

# --------------------------------------------------------------------------- PVF

_PVF_XOR = 0x81A79011


def ror6_decrypt(buf, crc):
    """逐 32 位小端字解密。用大整数一次处理整段,比 Python 循环快两个数量级。"""
    n = len(buf) // 4
    if n == 0:
        return bytes(buf)
    tail = bytes(buf[n * 4:])
    x = int.from_bytes(buf[: n * 4], "little")
    k = (crc ^ _PVF_XOR) & 0xFFFFFFFF
    x ^= int.from_bytes(struct.pack("<I", k) * n, "little")
    lo = int.from_bytes(b"\xff\xff\xff\x03" * n, "little")  # 每个字的低 26 位
    hi = int.from_bytes(b"\x00\x00\x00\xfc" * n, "little")  # 每个字的高 6 位
    y = ((x >> 6) & lo) | ((x << 26) & hi)
    return y.to_bytes(n * 4, "little") + tail


def ror6_decrypt_slow(buf, crc):
    """逐字循环版本,只用来交叉检查 ror6_decrypt。"""
    out = bytearray()
    k = (crc ^ _PVF_XOR) & 0xFFFFFFFF
    for i in range(0, len(buf) - len(buf) % 4, 4):
        w = struct.unpack_from("<I", buf, i)[0] ^ k
        w = ((w >> 6) | (w << 26)) & 0xFFFFFFFF
        out += struct.pack("<I", w)
    return bytes(out) + bytes(buf[len(buf) - len(buf) % 4:])


PvfEntry = namedtuple("PvfEntry", "path length crc offset")


class Pvf:
    def __init__(self, path=None):
        path = path or PVF_PATH or os.path.join(require_dnf_dir(), "Script.pvf")
        self.path = path
        self._f = open(path, "rb")
        f = self._f
        guid_len = struct.unpack("<I", f.read(4))[0]
        self.guid = f.read(guid_len)
        self.version, tree_len, tree_crc, n_files = struct.unpack("<IIII", f.read(16))
        self.header_len = guid_len + 20
        self.tree_len = tree_len
        self.tree_crc = tree_crc
        self.n_files = n_files
        f.seek(self.header_len)
        tree = ror6_decrypt(f.read(tree_len), tree_crc)
        self.data_base = self.header_len + tree_len
        ents = []
        pos = 0
        for _ in range(n_files):
            _fno, plen = struct.unpack_from("<II", tree, pos)
            pos += 8
            p = tree[pos: pos + plen].decode("latin1")
            pos += plen
            length, crc, off = struct.unpack_from("<III", tree, pos)
            pos += 12
            ents.append(PvfEntry(p, length, crc, off))
        self.entries = ents
        self.tree_leftover = len(tree) - pos
        self._by_path = None

    def read(self, e):
        f = self._f
        f.seek(self.data_base + e.offset)
        raw = f.read((e.length + 3) & ~3)
        return ror6_decrypt(raw, e.crc)[: e.length]

    def get(self, path):
        if self._by_path is None:
            self._by_path = {e.path.lower(): e for e in self.entries}
        e = self._by_path.get(path.lower())
        return None if e is None else self.read(e)

    def close(self):
        self._f.close()


# --------------------------------------------------------------------------- NPK

NPK_MAGIC = b"NeoplePack_Bill\0"
_NPK_KEY = (b"puchikon@neople dungeon and fighter " + b"DNF" * 100)[:255] + b"\0"
_NPK_KEY_INT = int.from_bytes(_NPK_KEY, "big")

NpkEntry = namedtuple("NpkEntry", "name offset size")


def read_npk_toc(path):
    with open(path, "rb") as f:
        head = f.read(20)
        if head[:16] != NPK_MAGIC:
            raise ValueError("not an NPK: " + path)
        count = struct.unpack_from("<I", head, 16)[0]
        toc = f.read(count * 264)
    out = []
    for i in range(count):
        off, size = struct.unpack_from("<II", toc, i * 264)
        raw = toc[i * 264 + 8: i * 264 + 264]
        name = (int.from_bytes(raw, "big") ^ _NPK_KEY_INT).to_bytes(256, "big")
        out.append(NpkEntry(name.split(b"\0", 1)[0].decode("latin1"), off, size))
    return out


# --------------------------------------------------------------------------- IMG

T_ARGB1555 = 0x0E
T_ARGB4444 = 0x0F
T_ARGB8888 = 0x10
T_LINK = 0x11
BPP = {T_ARGB1555: 2, T_ARGB4444: 2, T_ARGB8888: 4}

Frame = namedtuple("Frame", "type comp w h size x y fw fh link")


class ImgError(Exception):
    pass


def parse_img_v2(buf):
    """返回 (frames, data_offsets, expected_total_len)。任何不一致都抛 ImgError。"""
    if buf[:15] != b"Neople Img File":
        raise ImgError("magic")
    index_size, version, count = struct.unpack_from("<qii", buf, 16)
    if version != 2:
        raise ImgError("version %d" % version)
    pos = 32
    end = 32 + index_size
    frames = []
    for _ in range(count):
        if pos + 8 > len(buf):
            raise ImgError("index overrun")
        t = struct.unpack_from("<i", buf, pos)[0]
        if t == T_LINK:
            tgt = struct.unpack_from("<i", buf, pos + 4)[0]
            frames.append(Frame(T_LINK, 0, 0, 0, 0, 0, 0, 0, 0, tgt))
            pos += 8
        else:
            if pos + 36 > len(buf):
                raise ImgError("index overrun")
            comp, w, h, size, x, y, fw, fh = struct.unpack_from("<8i", buf, pos + 4)
            frames.append(Frame(t, comp, w, h, size, x, y, fw, fh, -1))
            pos += 36
    if pos != end:
        raise ImgError("index size mismatch: parsed %d, header %d" % (pos, end))
    offs = []
    d = end
    for fr in frames:
        if fr.type == T_LINK:
            offs.append(-1)
        else:
            offs.append(d)
            d += stored_len(fr)
    return frames, offs, d


def stored_len(fr):
    """帧数据在文件里实际占的字节数。

    compress==6(zlib): 索引里的 size 就是压缩后长度。
    compress==5(未压缩): 实际长度是 w*h*bpp。16 位格式(1555/4444)的 size 字段按 32 位算,
    会比实际大一倍,不能直接用来推进偏移(用 size 推进会让后面所有帧错位)。
    """
    if fr.comp == 6:
        return fr.size
    return fr.w * fr.h * BPP.get(fr.type, 4)


def resolve_link(frames, i, max_hops=16):
    hops = 0
    while frames[i].type == T_LINK:
        i = frames[i].link
        hops += 1
        if hops > max_hops or not (0 <= i < len(frames)):
            raise ImgError("bad link chain")
    return i


def frame_raw(buf, frames, offs, i):
    """返回 (真实帧, 解压后的原始像素字节)。链接帧会跟到目标帧。"""
    j = resolve_link(frames, i)
    fr = frames[j]
    data = buf[offs[j]: offs[j] + stored_len(fr)]
    if fr.comp == 6:
        data = zlib.decompress(data)
    elif fr.comp != 5:
        raise ImgError("compress %d" % fr.comp)
    return fr, data


_TAB4444 = None
_TAB1555 = None


def _tab4444():
    global _TAB4444
    if _TAB4444 is None:
        t = []
        for p in range(65536):
            a, r, g, b = (p >> 12) & 15, (p >> 8) & 15, (p >> 4) & 15, p & 15
            t.append(bytes((r * 17, g * 17, b * 17, a * 17)))
        _TAB4444 = t
    return _TAB4444


def _tab1555():
    global _TAB1555
    if _TAB1555 is None:
        t = []
        for p in range(65536):
            a, r, g, b = (p >> 15) & 1, (p >> 10) & 31, (p >> 5) & 31, p & 31
            t.append(bytes(((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2), 255 if a else 0)))
        _TAB1555 = t
    return _TAB1555


def to_rgba(fr, data):
    """原始像素 -> RGBA8888 字节串(行优先,无填充)。"""
    if fr.type == T_ARGB8888:
        out = bytearray(data)
        out[0::4] = data[2::4]  # 内存序 BGRA -> RGBA
        out[2::4] = data[0::4]
        return bytes(out)
    arr = array.array("H")
    arr.frombytes(data)
    tab = _tab4444() if fr.type == T_ARGB4444 else _tab1555()
    return b"".join([tab[p] for p in arr])

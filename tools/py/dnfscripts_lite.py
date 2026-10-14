"""最小的标记流读取:取一个脚本里某个标签后面的所有字符串(用于研究脚本;只读测量,非 REA)。"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import script_dump as S

_strings = {}


def _get_strings(pvf):
    s = _strings.get(id(pvf))
    if s is None:
        s = S.Strings(pvf)
        _strings[id(pvf)] = s
    return s


def lines(pvf, path):
    """返回 [(标签, [值文本])];不是标记流脚本时返回 []。"""
    raw = pvf.get(path.lower())
    if raw is None or raw[:2] != b"\xb0\xd0":
        return []
    return S.decode_text(raw, _get_strings(pvf))


def chr_list(pvf, path, tag):
    """取 tag 这一行里所有带引号的字符串(.chr 的 [etc motion]、[etc attack info] 等)。"""
    for t, vals in lines(pvf, path):
        if t == tag:
            return [m for v in vals for m in re.findall(r'^"(.*)"$', v)]
    return []

#!/usr/bin/env python3
"""CPU 模拟器外壳(Unicorn):把 DNF.exe 的磁盘映像摆进模拟内存,调用其中的函数,跟踪读写、调用与跳转。
不运行游戏本体,不连网络,不改任何文件;模拟器只模拟 CPU 指令,系统调用与导入的接口都被截下来(见 ImportStubs)。

依据:用户在 2026-10-15 答复"给你最高权限 你想逆向就逆向"(E-059),允许在模拟器里运行客户端与服务端的机器码。
用途:(1)给明文里的纯函数当对拍的"标准答案";(2)跟踪受保护区域里被混淆的块,看它读写哪里、调用谁、返回什么。
输出只放 out/ 与 re/(不入库,提交守卫会拦)。

依赖:re/venv 里的 unicorn 与 capstone(`re/venv/bin/python tools/py/emu.py --selftest`)。

常用写法:
    from emu import ClientEmu
    e = ClientEmu()
    e.scratch_write(0x30000000, b"...")           # 摆数据
    r = e.call(0x00872c60, args=[x], ecx=obj)     # 调用(thiscall:ecx 是 this;返回 eax)
    print(hex(r.eax), r.reads[:10])
"""
import os
import collections
import struct
import sys
from collections import namedtuple

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from unicorn import (  # noqa: E402
    Uc, UcError, UC_ARCH_X86, UC_MODE_32, UC_PROT_ALL,
    UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_HOOK_MEM_READ_UNMAPPED,
    UC_HOOK_MEM_WRITE_UNMAPPED, UC_HOOK_MEM_FETCH_UNMAPPED, UC_HOOK_INTR, UC_HOOK_INSN_INVALID,
)
from unicorn import x86_const as X  # noqa: E402

PAGE = 0x1000
RET_MAGIC = 0x0F000000          # 返回哨兵:函数 ret 到这里就停
IMPORT_STUB_BASE = 0x0E000000   # 导入函数的假地址区
IMPORT_STUB_SIZE = 0x00100000
STACK_LO = 0x20000000
STACK_SIZE = 0x00200000
SCRATCH_LO = 0x30000000         # 摆假对象的暂存区
SCRATCH_SIZE = 0x04000000
TEB_BASE = 0x10000000
GDT_BASE = 0x10010000

PROTECTED_LO = 0x01DD9000       # 受保护区域(节 #3 到 #8,E-044)
PROTECTED_HI = 0x02C98000
PLAIN_LO = 0x00401000           # 明文主体代码(节 #0)
PLAIN_HI = 0x0155A000

Result = namedtuple("Result", "eax edx st0 eip esp stop insns reads writes jumps calls faults")


def align_down(v):
    return v & ~(PAGE - 1)


def align_up(v):
    return (v + PAGE - 1) & ~(PAGE - 1)


class ImportStubs:
    """导入表里的接口:每个槽改写成一个假地址,执行到那里就由这里应付(记一笔,返回 0 或常见值)。"""

    # 名字 -> (参数个数, 返回值)。参数个数用于 stdcall 的栈清理;没登记的记一笔并按 0 个参数处理
    KNOWN = {
        "GetTickCount": (0, 1000), "timeGetTime": (0, 1000), "GetCurrentThreadId": (0, 1), "GetCurrentProcessId": (0, 1),
        "GetLastError": (0, 0), "SetLastError": (1, 0), "QueryPerformanceCounter": (1, 1), "QueryPerformanceFrequency": (1, 1),
        "EnterCriticalSection": (1, 0), "LeaveCriticalSection": (1, 0), "TlsGetValue": (1, 0), "TlsSetValue": (2, 1),
        "IsDebuggerPresent": (0, 0), "GetModuleHandleA": (1, 0), "GetModuleHandleW": (1, 0), "LoadLibraryW": (1, 0), "LoadLibraryA": (1, 0), "GetProcAddress": (2, 0),
        "GetProcessHeap": (0, 0x50000000), "HeapAlloc": (3, 0), "HeapFree": (3, 1), "Sleep": (1, 0),
        "SwitchToThread": (0, 0), "GetSystemTimeAsFileTime": (1, 0),
        "EncodePointer": (1, None), "DecodePointer": (1, None), "GetModuleFileNameW": (3, 0), "GetModuleFileNameA": (3, 0),
        "HeapReAlloc": (4, 0), "HeapSize": (3, 0), "HeapCreate": (3, 0x50000000), "HeapDestroy": (1, 1),
        "VirtualAlloc": (4, 0), "VirtualFree": (3, 1), "InitializeCriticalSection": (1, 0),
        "InitializeCriticalSectionAndSpinCount": (2, 1), "DeleteCriticalSection": (1, 0), "TlsAlloc": (0, 1),
        "lstrlenA": (1, 0), "lstrlenW": (1, 0), "GetCurrentProcess": (0, 0xFFFFFFFF), "GetCurrentThread": (0, 0xFFFFFFFE),
        "InterlockedIncrement": (1, None), "InterlockedDecrement": (1, None), "InterlockedExchange": (2, None),
        "InterlockedCompareExchange": (3, None), "GetSystemInfo": (1, 0), "GetVersion": (0, 0x0A00),
        "GetStartupInfoW": (1, 0), "GetStartupInfoA": (1, 0), "GetCommandLineA": (0, 0), "GetCommandLineW": (0, 0),
        # C 运行库启动要用到的(stdcall 参数个数按 Win32 文档)
        "FlsAlloc": (1, 0), "FlsFree": (1, 1), "FlsGetValue": (1, 0), "FlsSetValue": (2, 1), "TlsFree": (1, 1),
        "GetEnvironmentStringsW": (0, 0), "GetEnvironmentStrings": (0, 0), "FreeEnvironmentStringsW": (1, 1),
        "FreeEnvironmentStringsA": (1, 1), "GetStdHandle": (1, 0x10), "SetHandleCount": (1, 0x20), "GetFileType": (1, 2),
        "GetACP": (0, 936), "GetOEMCP": (0, 936), "GetCPInfo": (2, 0), "IsProcessorFeaturePresent": (1, 1),
        "SetUnhandledExceptionFilter": (1, 0), "UnhandledExceptionFilter": (1, 0), "TerminateProcess": (2, 1),
        "ExitProcess": (1, 0), "RtlUnwind": (4, 0), "HeapSetInformation": (4, 1), "HeapValidate": (3, 1),
        "WideCharToMultiByte": (8, 0), "MultiByteToWideChar": (6, 0), "GetStringTypeW": (5, 1), "GetStringTypeA": (5, 1),
        "LCMapStringW": (6, 0), "LCMapStringA": (6, 0), "GetLocaleInfoA": (4, 0), "GetLocaleInfoW": (4, 0),
        "GetUserDefaultLCID": (0, 0x804), "IsValidCodePage": (1, 1), "IsValidLocale": (2, 1),
        "GetEnvironmentVariableA": (3, 0), "GetEnvironmentVariableW": (3, 0), "SetEnvironmentVariableA": (2, 1),
        "FlushFileBuffers": (1, 1), "WriteFile": (5, 1), "SetFilePointer": (4, 0), "CloseHandle": (1, 1),
        "GetCurrentDirectoryA": (2, 0), "GetCurrentDirectoryW": (2, 0), "OutputDebugStringA": (1, 0), "OutputDebugStringW": (1, 0),
        "InterlockedExchangeAdd": (2, None), "GetTimeZoneInformation": (1, 0xFFFFFFFF), "GetSystemTime": (1, 0),
        "GetLocalTime": (1, 0), "GetThreadLocale": (0, 0x804), "RaiseException": (4, 0), "SetLastErrorEx": (2, 0),
        "GetCurrentProcessId": (0, 1), "GetVersionExA": (1, 0), "GetVersionExW": (1, 0), "FreeLibrary": (1, 1),
        "GetDriveTypeA": (1, 3), "SetErrorMode": (1, 0), "VirtualProtect": (4, 1), "VirtualQuery": (3, 0),
        "FindResourceW": (3, 0), "FindResourceA": (3, 0), "GetDesktopWindow": (0, 0x10010), "MessageBoxW": (4, 1),
        "MessageBoxA": (4, 1), "RegisterClipboardFormatW": (1, 0xC001), "InternetOpenW": (5, 0), "FindWindowW": (2, 0),
        "timeBeginPeriod": (1, 0),
    }

    # 导入名 -> 替身方法名(C++ 名字里有不能做方法名的字符)
    ALIAS = {"??2@YAPAXI@Z": "malloc", "??3@YAXPAX@Z": "free", "_strcmpi": "_stricmp", "_snprintf": "snprintf0"}

    def __init__(self):
        self.slots = {}     # 假地址 -> "DLL!名字"
        self.calls = []

    def register(self, index, label):
        addr = IMPORT_STUB_BASE + index * 8
        self.slots[addr] = label
        return addr


class ClientEmu:
    def __init__(self, exe=None, trace_limit=200000):
        from decode_strings import Pe
        from vtable_scan import exe_path
        self.path = exe or exe_path()
        self.pe = Pe(self.path)
        self.base = self.pe.image_base
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        self.mapped = []            # (lo, hi)
        self.imports = ImportStubs()
        self.iat_slots = {}         # 标签 -> [导入地址表里的槽地址]
        self._load_image()
        self._map(IMPORT_STUB_BASE, IMPORT_STUB_SIZE)
        self._map(RET_MAGIC, PAGE)
        self.uc.mem_write(RET_MAGIC, b"\xcc")
        self._map(STACK_LO, STACK_SIZE)
        self._map(SCRATCH_LO, SCRATCH_SIZE)
        self._setup_fs()
        self._enable_simd()
        self._link_imports()
        self.initial_maps = len(self.mapped)        # 之后自动映射的页排在后面(存快照时用)
        self.trace_limit = trace_limit
        self._log = None
        self.auto_pages = 0
        self._next_scratch = SCRATCH_LO + 0x10000
        self.alloc_sizes = {}
        self.qpc = 1000000
        self.tls = {}
        self.tls_next = 0
        self.py_hooks = {}          # 地址 -> 函数(emu):执行到该地址时先调它(可以改寄存器与 EIP)
        self._install_native_stubs()

    # 用真机器码写的替身(需要真的执行 x87 指令的运行库函数)
    NATIVE_STUBS = {
        # msvcr71.dll!_ftol:切换成截断舍入,fistp 取整,EDX:EAX 返回(与运行库同样的做法)
        "msvcr71.dll!_ftol": bytes.fromhex("55" "8bec" "83c4f4" "d97dfe" "668b45fe" "80cc0c" "668945fc" "d96dfc"
                                           "df7df4" "d96dfe" "8b45f4" "8b55f8" "c9" "c3"),
    }

    def _install_native_stubs(self):
        for label, code in self.NATIVE_STUBS.items():
            slots = self.iat_slots.get(label)
            if not slots:
                continue
            addr = self.scratch_alloc(len(code))
            self.write(addr, code)
            for slot in slots:
                self.write32(slot, addr)

    # ------------------------------------------------------------ 装载
    def _map(self, lo, size):
        lo = align_down(lo)
        size = align_up(size)
        self.uc.mem_map(lo, size, UC_PROT_ALL)
        self.mapped.append((lo, lo + size))

    def is_mapped(self, addr):
        for lo, hi in self.mapped:
            if lo <= addr < hi:
                return True
        return False

    def _load_image(self):
        f = open(self.path, "rb")
        data = f.read()
        f.close()
        self.file = data
        end = 0
        for (_name, va, vsize, roff, rsize) in self.pe.sections:
            end = max(end, va + max(vsize, rsize))
        self._map(self.base, align_up(end))
        self.uc.mem_write(self.base, data[:0x400])        # 头部
        for (_name, va, vsize, roff, rsize) in self.pe.sections:
            n = min(rsize, vsize) if vsize else rsize
            if n > 0:
                self.uc.mem_write(self.base + va, data[roff:roff + n])

    def _setup_fs(self):
        """FS 段指向假的线程环境块:fs:[0] 异常链(0xFFFFFFFF)、fs:[0x18] 自身、fs:[0x30] 假进程环境块。"""
        self._map(TEB_BASE, 0x20000)
        teb = TEB_BASE
        peb = TEB_BASE + 0x8000
        self.uc.mem_write(teb + 0x00, struct.pack("<I", 0xFFFFFFFF))
        self.uc.mem_write(teb + 0x04, struct.pack("<I", STACK_LO + STACK_SIZE))
        self.uc.mem_write(teb + 0x08, struct.pack("<I", STACK_LO))
        self.uc.mem_write(teb + 0x18, struct.pack("<I", teb))
        self.uc.mem_write(teb + 0x2C, struct.pack("<I", TEB_BASE + 0xA000))   # 线程本地存储指针数组
        self.uc.mem_write(teb + 0x30, struct.pack("<I", peb))
        self.uc.mem_write(peb + 0x02, b"\x00")                                  # BeingDebugged = 0
        # GDT:0 空、1 代码、2 数据、3 FS
        def desc(base, limit, access, flags):
            return struct.pack("<HHBBBB", limit & 0xFFFF, base & 0xFFFF, (base >> 16) & 0xFF, access,
                               ((flags & 0xF) << 4) | ((limit >> 16) & 0xF), (base >> 24) & 0xFF)
        gdt = b"\x00" * 8 + desc(0, 0xFFFFF, 0x9B, 0xC) + desc(0, 0xFFFFF, 0x93, 0xC) + desc(teb, 0xFFF, 0xF3, 0x4)
        self.uc.mem_write(GDT_BASE, gdt)
        self.uc.reg_write(X.UC_X86_REG_GDTR, (0, GDT_BASE, len(gdt) - 1, 0))
        self.uc.reg_write(X.UC_X86_REG_CS, 0x08)
        self.uc.reg_write(X.UC_X86_REG_SS, 0x10)
        self.uc.reg_write(X.UC_X86_REG_DS, 0x10)
        self.uc.reg_write(X.UC_X86_REG_ES, 0x10)
        self.uc.reg_write(X.UC_X86_REG_FS, 0x18 | 3)

    def _enable_simd(self):
        """打开 SSE 所需的控制位(CR0.EM 清零、CR0.MP 置位、CR4.OSFXSR 与 OSXMMEXCPT 置位)。"""
        try:
            cr0 = self.uc.reg_read(X.UC_X86_REG_CR0)
            cr0 = (cr0 & ~0x4) | 0x2
            self.uc.reg_write(X.UC_X86_REG_CR0, cr0)
            cr4 = self.uc.reg_read(X.UC_X86_REG_CR4)
            self.uc.reg_write(X.UC_X86_REG_CR4, cr4 | (1 << 9) | (1 << 10))
        except UcError:
            pass

    def _link_imports(self):
        """读导入目录,把每个导入槽写成假地址(登记在 self.imports)。"""
        d = self.file
        pe_off = struct.unpack_from("<I", d, 0x3C)[0]
        opt = pe_off + 24
        imp_rva, imp_size = struct.unpack_from("<II", d, opt + 96 + 8)
        if imp_rva == 0:
            return
        index = 0
        # 导入目录在映像里的位置:用模拟内存读(目录本身在 .idata 的文件内容里)
        pos = self.base + imp_rva
        while True:
            desc = self.uc.mem_read(pos, 20)
            oft, _ts, _fc, name_rva, ft = struct.unpack("<IIIII", desc)
            if oft == 0 and name_rva == 0 and ft == 0:
                break
            dll = self._cstr(self.base + name_rva)
            thunk = self.base + (oft or ft)
            slot = self.base + ft
            while True:
                (t,) = struct.unpack("<I", self.uc.mem_read(thunk, 4))
                if t == 0:
                    break
                if t & 0x80000000:
                    label = "%s!#%d" % (dll, t & 0xFFFF)
                else:
                    label = "%s!%s" % (dll, self._cstr(self.base + t + 2))
                addr = self.imports.register(index, label)
                self.iat_slots.setdefault(label, []).append(slot)
                self.uc.mem_write(slot, struct.pack("<I", addr))
                index += 1
                thunk += 4
                slot += 4
            pos += 20

    def _cstr(self, va, limit=256):
        out = bytearray()
        for i in range(limit):
            b = bytes(self.uc.mem_read(va + i, 1))
            if b == b"\x00":
                break
            out += b
        return out.decode("latin-1")

    # ------------------------------------------------------------ 内存读写
    def read(self, addr, n):
        return bytes(self.uc.mem_read(addr, n))

    def read32(self, addr):
        return struct.unpack("<I", self.read(addr, 4))[0]

    def write(self, addr, data):
        data = bytes(data)
        self.uc.mem_write(addr, data)
        if not (STACK_LO <= addr < STACK_LO + STACK_SIZE):
            try:
                # 往可能被当作代码执行的内存里写之后,清掉已翻译的块(否则会执行旧代码)
                self.uc.ctl_remove_cache(addr, addr + max(len(data), 1))
            except (AttributeError, UcError):
                pass

    def write32(self, addr, value):
        self.write(addr, struct.pack("<I", value & 0xFFFFFFFF))

    def scratch_alloc(self, size, fill=b"\x00"):
        """在暂存区取一块(16 字节对齐),返回地址。"""
        addr = (self._next_scratch + 15) & ~15
        self._next_scratch = addr + size
        if self._next_scratch > SCRATCH_LO + SCRATCH_SIZE:
            raise MemoryError("暂存区用完")
        if fill != b"\x00":
            self.write(addr, (fill * (size // len(fill) + 1))[:size])
        return addr

    # ------------------------------------------------------------ 受保护整数(客户端的"防内存修改"整数,E-060)
    PINT_TABLE_PTR = 0x01B08A10     # 全局指针:指向密钥表(读取函数 0x011A0DE0 用)

    def pint_install(self):
        """摆一张假的密钥表:T[0x24 + 高16位*4] 指向桶,桶[0x2114 + 低16位*4] 是密钥字。
        密钥字 = (校验 << 16) | 密钥;校验 = ((值高16 + 值低16) ^ 密钥) & 0xFFFF。"""
        self._pint_table = self.scratch_alloc(0x100)
        self._pint_bucket = self.scratch_alloc(0x2114 + 4 * 0x10000)
        self.write32(self._pint_table + 0x24, self._pint_bucket)
        self.write32(self.PINT_TABLE_PTR, self._pint_table)
        self._pint_next = 1
        self._pint_key = 0x1234

    def pint_set(self, addr, value):
        """在 addr 处摆一组受保护整数(12 字节:+0 句柄,+4 未用,+8 密文),读取函数能读回 value。"""
        if not hasattr(self, "_pint_table"):
            self.pint_install()
        value &= 0xFFFFFFFF
        handle = self._pint_next
        self._pint_next += 1
        self._pint_key = (self._pint_key * 75 + 74) & 0xFFFF or 1
        key = self._pint_key
        check = ((value >> 16) + (value & 0xFFFF) ^ key) & 0xFFFF
        self.write32(addr, handle)                       # 高 16 位为 0:桶 0
        self.write32(addr + 4, 0)
        self.write32(addr + 8, value ^ ((key << 16) | key))
        self.write32(self._pint_bucket + 0x2114 + handle * 4, (check << 16) | key)

    def pint_get(self, addr):
        """在 Python 里按同样的规则读回受保护整数(不运行模拟器)。"""
        handle = self.read32(addr)
        hi, lo = handle >> 16, handle & 0xFFFF
        bucket = self.read32(self.read32(self.PINT_TABLE_PTR) + 0x24 + hi * 4)
        word = self.read32(bucket + 0x2114 + lo * 4)
        key = word & 0xFFFF
        return self.read32(addr + 8) ^ ((key << 16) | key)

    def reg(self, name):
        return self.uc.reg_read(getattr(X, "UC_X86_REG_" + name.upper()))

    def set_reg(self, name, value):
        self.uc.reg_write(getattr(X, "UC_X86_REG_" + name.upper()), value & 0xFFFFFFFF)

    # ------------------------------------------------------------ 调用
    def call(self, addr, args=(), ecx=None, regs=None, until=None, max_insns=2_000_000,
             watch=(), trace=False, return_address=None, track_jumps=None, fast=False, tail=0):
        """从 addr 开始执行,直到 ret 到哨兵(或到 until)。args 按 cdecl/stdcall 顺序(从左到右)压栈,
        thiscall 把 this 放 ecx。watch 是 [(lo, hi), ...]:这些范围内的读写会记下来。
        trace 为真时记每条指令的地址;缺省(fast=False)每条指令都挂 Python 钩子并记明文与受保护区之间的跳转:慢,
        但受保护区的代码会改写自己(自修改),只有逐条指令的钩子才能保证 Unicorn 把改写后的字节翻译对。
        只跑明文代码、不关心细节时可以 fast=True:只挂窄钩子(导入接口区、py_hooks 的地址),用 Unicorn 自带的指令数上限,快得多(此时 insns 为 -1)。
        返回 (Result, log):Result 有 eax、edx、st0、eip、esp、停止原因、指令数、读写表、跨区跳转表、导入调用表、缺页表。"""
        uc = self.uc
        if track_jumps is None:
            track_jumps = not fast
        esp = STACK_LO + STACK_SIZE - 0x1000
        for a in reversed(list(args)):
            esp -= 4
            self.write32(esp, a)
        ret = return_address if return_address is not None else RET_MAGIC
        esp -= 4
        self.write32(esp, ret)
        zero = ("eax", "ebx", "edx", "esi", "edi", "ebp")
        for n in zero:
            self.set_reg(n, 0)
        self.set_reg("ecx", ecx or 0)
        uc.reg_write(X.UC_X86_REG_EFLAGS, 0x202)
        for k, v in (regs or {}).items():
            self.set_reg(k, v)
        self.set_reg("esp", esp)
        log = {"reads": [], "writes": [], "jumps": [], "calls": [], "faults": [], "insns": 0, "trace": []}
        ring = collections.deque(maxlen=tail) if tail else None
        log["tail"] = ring
        self._log = log
        hooks = []
        stop_at = until if until is not None else ret
        state = {"prev": None, "stop": "end"}
        full = bool(trace or track_jumps) and not fast

        def on_narrow(uc_, address, size, user):
            if address in self.imports.slots:
                if not self._import_call(address, log):
                    state["stop"] = "未登记接口 " + log["unknown_api"]
                    uc_.emu_stop()
                return
            hook = self.py_hooks.get(address)
            if hook is not None:
                hook(self)

        def on_code(uc_, address, size, user):
            log["insns"] += 1
            if address in self.imports.slots:
                if not self._import_call(address, log):
                    state["stop"] = "未登记接口 " + log["unknown_api"]
                    uc_.emu_stop()
                return
            hook = self.py_hooks.get(address)
            if hook is not None:
                hook(self)
                if self.reg("eip") != address:
                    state["prev"] = None
                    return
            prev = state["prev"]
            if prev is not None:
                prev_prot = PROTECTED_LO <= prev < PROTECTED_HI
                cur_prot = PROTECTED_LO <= address < PROTECTED_HI
                if prev_prot != cur_prot and len(log["jumps"]) < 2000:
                    log["jumps"].append((prev, address, "进入受保护区" if cur_prot else "回到明文"))
            state["prev"] = address
            if ring is not None:
                ring.append(address)
            if trace and len(log["trace"]) < self.trace_limit:
                log["trace"].append(address)
            if log["insns"] >= max_insns:
                state["stop"] = "max_insns"
                uc_.emu_stop()

        def on_mem(uc_, access, address, size, value, user):
            for lo, hi in watch:
                if lo <= address < hi:
                    if access == 17:       # UC_MEM_WRITE
                        log["writes"].append((address, size, value & ((1 << (8 * size)) - 1), self.reg("eip")))
                    else:
                        try:
                            v = int.from_bytes(bytes(uc_.mem_read(address, size)), "little")
                        except UcError:
                            v = None
                        log["reads"].append((address, size, v, self.reg("eip")))
                    break

        def on_unmapped(uc_, access, address, size, value, user):
            page = align_down(address)
            if address >= 0xC0000000 or address < 0x1000:
                log["faults"].append((address, size, "拒绝(非法地址)", self.reg("eip")))
                return False
            try:
                uc_.mem_map(page, PAGE, UC_PROT_ALL)
                self.mapped.append((page, page + PAGE))
                self.auto_pages += 1
                log["faults"].append((address, size, "自动映射(全零)", self.reg("eip")))
                return True
            except UcError:
                return False

        def on_intr(uc_, intno, user):
            log["faults"].append((intno, 0, "中断", self.reg("eip")))
            state["stop"] = "int %d" % intno
            uc_.emu_stop()

        def on_invalid(uc_, user):
            state["stop"] = "invalid insn"
            return False

        if full:
            hooks.append(uc.hook_add(UC_HOOK_CODE, on_code))
        else:
            hooks.append(uc.hook_add(UC_HOOK_CODE, on_narrow, begin=IMPORT_STUB_BASE, end=IMPORT_STUB_BASE + IMPORT_STUB_SIZE))
            for a in list(self.py_hooks):
                hooks.append(uc.hook_add(UC_HOOK_CODE, on_narrow, begin=a, end=a))
        if watch:
            hooks.append(uc.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, on_mem))
        hooks.append(uc.hook_add(UC_HOOK_MEM_READ_UNMAPPED | UC_HOOK_MEM_WRITE_UNMAPPED | UC_HOOK_MEM_FETCH_UNMAPPED, on_unmapped))
        hooks.append(uc.hook_add(UC_HOOK_INTR, on_intr))
        hooks.append(uc.hook_add(UC_HOOK_INSN_INVALID, on_invalid))
        err = None
        try:
            uc.emu_start(addr, stop_at, 0, 0 if full else max_insns)
            if not full and self.reg("eip") != stop_at and state["stop"] == "end":
                state["stop"] = "max_insns"
        except UcError as e:
            err = str(e)
            state["stop"] = "错误: " + err
        finally:
            for h in hooks:
                uc.hook_del(h)
        eip = self.reg("eip")
        try:
            st0 = self._read_st0()
        except Exception:
            st0 = None
        n = log["insns"] if full else -1
        return Result(self.reg("eax"), self.reg("edx"), st0, eip, self.reg("esp"), state["stop"], n,
                      log["reads"], log["writes"], log["jumps"], log["calls"], log["faults"]), log

    def _read_st0(self):
        v = self.uc.reg_read(X.UC_X86_REG_ST0)
        return v

    def _import_call(self, address, log):
        """处理一次导入函数调用。返回 False 表示这个接口没登记(调用者会停下模拟)。"""
        import win32_argc
        label = self.imports.slots[address]
        dll, short = label.split("!", 1)
        ret = 0
        if short in ImportStubs.KNOWN:
            argc, ret = ImportStubs.KNOWN[short]
        elif short in win32_argc.ARGC:
            argc = win32_argc.ARGC[short]
        elif short in win32_argc.CDECL or short in win32_argc.THISCALL_NOARG or dll.lower().startswith("msvcr"):
            argc = 0                      # cdecl 或无参成员函数:调用方清栈
        elif hasattr(self, "_api_" + ImportStubs.ALIAS.get(short, short)):
            argc = 0
        else:
            log["unknown_api"] = label
            return False
        if dll.lower().startswith("msvcr"):
            argc = 0                      # C 运行库的函数是 cdecl:调用方自己清栈
        esp = self.reg("esp")
        ret_addr = self.read32(esp)
        args = [self.read32(esp + 4 + 4 * i) for i in range(6)]
        log["calls"].append((label, ret_addr, args[:4]))
        impl = getattr(self, "_api_" + ImportStubs.ALIAS.get(short, short), None)
        if impl is not None:
            eax = impl(args)
        elif ret is None:
            eax = args[0]
        else:
            eax = ret
        self.set_reg("eax", eax if eax is not None else 0)
        self.set_reg("esp", esp + 4 + 4 * argc)
        self.uc.reg_write(X.UC_X86_REG_EIP, ret_addr)
        return True

    # ---- 少量系统接口的替身(够把客户端的构造函数跑起来;没登记的返回 0)
    def _api_HeapAlloc(self, a):
        size = max(a[2], 1)
        p = self.scratch_alloc(size)
        self.alloc_sizes[p] = size
        return p

    def _api_HeapReAlloc(self, a):
        size = max(a[3], 1)
        p = self.scratch_alloc(size)
        old = a[2]
        n = min(size, self.alloc_sizes.get(old, 0))
        if n:
            self.write(p, self.read(old, n))
        self.alloc_sizes[p] = size
        return p

    def _api_HeapSize(self, a):
        return self.alloc_sizes.get(a[2], 0)

    def _api_VirtualAlloc(self, a):
        p = self.scratch_alloc(align_up(max(a[1], PAGE)))
        self.alloc_sizes[p] = a[1]
        return p

    def _api_GetModuleFileNameW(self, a):
        text = "C:\\DNF\\DNF.exe\0".encode("utf-16-le")
        self.write(a[1], text)
        return len(text) // 2 - 1

    def _api_GetModuleFileNameA(self, a):
        text = b"C:\\DNF\\DNF.exe\0"
        self.write(a[1], text)
        return len(text) - 1

    def _api_GetModuleHandleW(self, a):
        if a[0] == 0:
            return self.base
        name = self.read(a[0], 64).decode("utf-16-le", "ignore").split("\0")[0].lower()
        return self.base if name.endswith("dnf.exe") else 0x7C000000

    def _api_GetModuleHandleA(self, a):
        if a[0] == 0:
            return self.base
        name = self._cstr(a[0], 64).lower()
        return self.base if name.endswith("dnf.exe") else 0x7C000000

    def _api_LoadLibraryW(self, a):
        return 0x7C000000

    def _api_LoadLibraryA(self, a):
        return 0x7C000000

    def _api_GetProcAddress(self, a):
        if a[0] != 0x7C000000:
            return 0
        if a[1] < 0x10000:
            name = "#%d" % a[1]
        else:
            name = self._cstr(a[1], 80)
        for addr, label in self.imports.slots.items():
            if label.split("!", 1)[1] == name:
                return addr
        addr = IMPORT_STUB_BASE + 0x80000 + 8 * len(self.imports.slots)
        self.imports.slots[addr] = "dyn!" + name
        return addr

    def _api_malloc(self, a):
        p = self.scratch_alloc(max(a[0], 1))
        self.alloc_sizes[p] = a[0]
        return p

    def _api_calloc(self, a):
        n = max(a[0] * a[1], 1)
        p = self.scratch_alloc(n)
        self.alloc_sizes[p] = n
        return p

    def _api_free(self, a):
        return 0

    def _api_realloc(self, a):
        return self._api_HeapReAlloc([0, 0, a[0], a[1]])

    def _api_memcpy(self, a):
        self.write(a[0], self.read(a[1], a[2]))
        return a[0]

    _api_memmove = _api_memcpy

    def _api_memset(self, a):
        self.write(a[0], bytes([a[1] & 0xFF]) * a[2])
        return a[0]

    def _api_strlen(self, a):
        return len(self._cstr(a[0], 1 << 16))

    def _api_lstrlenA(self, a):
        return len(self._cstr(a[0], 4096))

    def _api_lstrlenW(self, a):
        n = 0
        while self.read(a[0] + 2 * n, 2) != b"\0\0" and n < 4096:
            n += 1
        return n

    def _api_QueryPerformanceCounter(self, a):
        self.qpc += 12345
        self.write(a[0], struct.pack("<Q", self.qpc))
        return 1

    def _api_QueryPerformanceFrequency(self, a):
        self.write(a[0], struct.pack("<Q", 10_000_000))
        return 1

    def _api_InterlockedIncrement(self, a):
        v = (self.read32(a[0]) + 1) & 0xFFFFFFFF
        self.write32(a[0], v)
        return v

    def _api_InterlockedDecrement(self, a):
        v = (self.read32(a[0]) - 1) & 0xFFFFFFFF
        self.write32(a[0], v)
        return v

    def _api_InterlockedExchange(self, a):
        old = self.read32(a[0])
        self.write32(a[0], a[1])
        return old

    def _api_InterlockedCompareExchange(self, a):
        old = self.read32(a[0])
        if old == a[2]:
            self.write32(a[0], a[1])
        return old

    def _api_TlsAlloc(self, a):
        self.tls_next += 1
        return self.tls_next

    def _api_TlsSetValue(self, a):
        self.tls[a[0]] = a[1]
        return 1

    def _api_TlsGetValue(self, a):
        return self.tls.get(a[0], 0)

    # ---- C 运行库字符串函数(cdecl;只实现游戏逻辑里可能碰到的)
    def _wstr(self, va, limit=4096):
        out = bytearray()
        for i in range(limit):
            b = self.read(va + 2 * i, 2)
            if b == b"\0\0":
                break
            out += b
        return out.decode("utf-16-le", "ignore")

    def _api_strcmp(self, a):
        x, y = self._cstr(a[0], 1 << 16), self._cstr(a[1], 1 << 16)
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api_strncmp(self, a):
        x, y = self._cstr(a[0], a[2])[:a[2]], self._cstr(a[1], a[2])[:a[2]]
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api__stricmp(self, a):
        x, y = self._cstr(a[0], 1 << 16).lower(), self._cstr(a[1], 1 << 16).lower()
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api__strnicmp(self, a):
        x, y = self._cstr(a[0], a[2])[:a[2]].lower(), self._cstr(a[1], a[2])[:a[2]].lower()
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api__memicmp(self, a):
        x, y = self.read(a[0], a[2]).lower(), self.read(a[1], a[2]).lower()
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api__wcsicmp(self, a):
        x, y = self._wstr(a[0]).lower(), self._wstr(a[1]).lower()
        return 0 if x == y else (1 if x > y else 0xFFFFFFFF)

    def _api_wcslen(self, a):
        return len(self._wstr(a[0]))

    def _api_wcsncpy(self, a):
        text = self._wstr(a[1], a[2])[:a[2]]
        data = text.encode("utf-16-le")
        data += b"\0\0" * (a[2] - len(text))
        self.write(a[0], data[:2 * a[2]])
        return a[0]

    def _api_strncpy(self, a):
        text = self._cstr(a[1], a[2])[:a[2]].encode("latin-1")
        data = text + b"\0" * (a[2] - len(text))
        self.write(a[0], data[:a[2]])
        return a[0]

    def _api_strncat(self, a):
        cur = self._cstr(a[0], 1 << 16)
        add = self._cstr(a[1], a[2])[:a[2]]
        self.write(a[0] + len(cur), add.encode("latin-1") + b"\0")
        return a[0]

    def _api_strstr(self, a):
        hay, needle = self._cstr(a[0], 1 << 16), self._cstr(a[1], 1 << 16)
        i = hay.find(needle)
        return 0 if i < 0 else a[0] + i

    def _api_strrchr(self, a):
        hay = self._cstr(a[0], 1 << 16)
        i = hay.rfind(chr(a[1] & 0xFF))
        return 0 if i < 0 else a[0] + i

    def _api_atoi(self, a):
        text = self._cstr(a[0], 64).strip()
        num = ""
        for i, ch in enumerate(text):
            if ch.isdigit() or (i == 0 and ch in "+-"):
                num += ch
            else:
                break
        try:
            return int(num) & 0xFFFFFFFF
        except ValueError:
            return 0

    def _api__strlwr(self, a):
        self.write(a[0], self._cstr(a[0], 1 << 16).lower().encode("latin-1") + b"\0")
        return a[0]

    def _api__onexit(self, a):
        return a[0]

    def _api___dllonexit(self, a):
        return a[0]

    def _api__initterm(self, a):
        return 0

    def _api_snprintf0(self, a):
        return 0

    def _api_FlsAlloc(self, a):
        self.tls_next += 1
        return self.tls_next

    def _api_FlsSetValue(self, a):
        self.tls[a[0]] = a[1]
        return 1

    def _api_FlsGetValue(self, a):
        return self.tls.get(a[0], 0)

    def _api_TlsFree(self, a):
        return 1

    def _api_GetEnvironmentStringsW(self, a):
        p = self.scratch_alloc(16)
        return p

    _api_GetEnvironmentStrings = _api_GetEnvironmentStringsW

    def _api_GetCommandLineA(self, a):
        p = self.scratch_alloc(32)
        self.write(p, b"DNF.exe\0")
        return p

    def _api_GetCommandLineW(self, a):
        p = self.scratch_alloc(64)
        self.write(p, "DNF.exe\0".encode("utf-16-le"))
        return p

    def _api_GetStartupInfoW(self, a):
        self.write(a[0], b"\0" * 68)
        self.write32(a[0], 68)
        return 0

    _api_GetStartupInfoA = _api_GetStartupInfoW

    def _api_GetSystemInfo(self, a):
        self.write(a[0], struct.pack("<9I", 0, 4096, 0x10000, 0x7FFEFFFF, 15, 4, 586, 0x10000, 0))
        return 0


def disasm(emu, addr, count=20):
    """用 capstone 反汇编模拟内存里的字节(只给读代码用)。"""
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    code = emu.read(addr, count * 15)
    out = []
    for ins in md.disasm(code, addr):
        out.append("%08x  %-8s %s" % (ins.address, ins.mnemonic, ins.op_str))
        if len(out) >= count:
            break
    return out


def selftest():
    e = ClientEmu()
    # 1. 自制一小段机器码,检查调用与浮点:mov eax,[esp+4]; add eax,[esp+8]; ret
    addr = e.scratch_alloc(64)
    e.write(addr, bytes.fromhex("8b442404034424 08c3".replace(" ", "")))
    r, _ = e.call(addr, args=[40, 2])
    assert r.eax == 42 and r.stop == "end", (r.eax, r.stop)
    # 2. SSE:movss xmm0,[esp+4]; addss xmm0,xmm0; movss [esp+4],xmm0; mov eax,[esp+4]; ret
    code = bytes.fromhex("f30f10442404" "f30f58c0" "f30f11442404" "8b442404" "c3")
    e.write(addr, code)
    r, _ = e.call(addr, args=[struct.unpack("<I", struct.pack("<f", 1.5))[0]])
    got = struct.unpack("<f", struct.pack("<I", r.eax))[0]
    assert got == 3.0, got
    # 3. FS:mov eax,fs:[0x18]; ret
    e.write(addr, bytes.fromhex("64a118000000c3"))
    r, _ = e.call(addr)
    assert r.eax == TEB_BASE, hex(r.eax)
    # 4. _ftol 替身:fld dword [esp+4]; call [IAT 槽]; ret   — 截断取整(正负都检查)
    slot = e.iat_slots["msvcr71.dll!_ftol"][0]
    e.write(addr, bytes.fromhex("d9442404" "ff15") + struct.pack("<I", slot) + b"\xc3")
    for value, want in ((3.99, 3), (-3.99, -3), (100000.5, 100000)):
        r, _ = e.call(addr, args=[struct.unpack("<I", struct.pack("<f", value))[0]])
        got = r.eax - (1 << 32) if r.eax >= (1 << 31) else r.eax
        assert got == want and r.stop == "end", (value, got, r.stop)
    print("emu selftest ok;映像已装载(%d 个导入槽)" % len(e.imports.slots))


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        selftest()
    else:
        print(__doc__)

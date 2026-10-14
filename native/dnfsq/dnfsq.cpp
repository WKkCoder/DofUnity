// dnfsq:Squirrel 虚拟机的最小宿主封装(C 接口),供 C# 通过 P/Invoke 调用。
//
// 为什么需要它:
//   1. Squirrel 的打印回调是 C 的可变参数函数(printf 风格),C# 无法直接实现;这里统一格式化成文本再回调。
//   2. 编译错误回调要把"文件:行:列:描述"留下来,方便调用方取回。
//   3. 打开虚拟机时统一注册数学库与字符串库,并设置运行时错误处理(打印调用栈)。
//
// 不包含任何原版内容。许可:本文件随仓库许可;Squirrel 本体是 MIT 许可(见 ../squirrel/COPYRIGHT)。
#include <squirrel.h>
#include <sqstdaux.h>
#include <sqstdmath.h>
#include <sqstdstring.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define DNFSQ_API extern "C" __declspec(dllexport)
#else
#define DNFSQ_API extern "C" __attribute__((visibility("default")))
#endif

// level:0 = print 输出,1 = 错误(编译错误、运行时错误与调用栈);vm 是产生输出的虚拟机,方便 C# 找到对应对象。
typedef void (*dnfsq_log_fn)(HSQUIRRELVM vm, int level, const char *text);

struct DnfSqContext {
    dnfsq_log_fn log;
    char compile_error[1024];
};

static DnfSqContext *ctx_of(HSQUIRRELVM v) {
    return (DnfSqContext *)sq_getforeignptr(v);
}

static void emit(HSQUIRRELVM v, int level, const SQChar *fmt, va_list ap) {
    char buf[4096];
    vsnprintf(buf, sizeof(buf), fmt, ap);
    DnfSqContext *c = ctx_of(v);
    if (c && c->log) {
        c->log(v, level, buf);
    }
}

static void print_cb(HSQUIRRELVM v, const SQChar *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    emit(v, 0, fmt, ap);
    va_end(ap);
}

static void error_cb(HSQUIRRELVM v, const SQChar *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    emit(v, 1, fmt, ap);
    va_end(ap);
}

static void compile_error_cb(HSQUIRRELVM v, const SQChar *desc, const SQChar *source, SQInteger line, SQInteger column) {
    DnfSqContext *c = ctx_of(v);
    if (!c) {
        return;
    }
    snprintf(c->compile_error, sizeof(c->compile_error), "%s:%lld:%lld: %s", source ? source : "?",
             (long long)line, (long long)column, desc ? desc : "?");
    if (c->log) {
        c->log(v, 1, c->compile_error);
    }
}

DNFSQ_API HSQUIRRELVM dnfsq_open(int stack_size, dnfsq_log_fn log) {
    HSQUIRRELVM v = sq_open(stack_size);
    if (!v) {
        return NULL;
    }
    DnfSqContext *c = (DnfSqContext *)calloc(1, sizeof(DnfSqContext));
    c->log = log;
    sq_setforeignptr(v, c);
    sq_setprintfunc(v, print_cb, error_cb);
    sq_pushroottable(v);
    sqstd_register_mathlib(v);
    sqstd_register_stringlib(v);
    sq_pop(v, 1);
    sqstd_seterrorhandlers(v);
    sq_setcompilererrorhandler(v, compile_error_cb);
    sq_enabledebuginfo(v, SQTrue);
    return v;
}

DNFSQ_API void dnfsq_close(HSQUIRRELVM v) {
    if (!v) {
        return;
    }
    DnfSqContext *c = ctx_of(v);
    sq_close(v);
    free(c);
}

// 编译并在根表上下文里运行一段脚本(定义函数与常量);成功返回 0,编译失败 -1,运行失败 -2。
// 无论成败,栈都恢复到调用前的高度。
DNFSQ_API int dnfsq_run_buffer(HSQUIRRELVM v, const char *buf, int len, const char *name) {
    DnfSqContext *c = ctx_of(v);
    if (c) {
        c->compile_error[0] = 0;
    }
    SQInteger top = sq_gettop(v);
    if (SQ_FAILED(sq_compilebuffer(v, buf, (SQInteger)len, name, SQTrue))) {
        sq_settop(v, top);
        return -1;
    }
    sq_pushroottable(v);
    if (SQ_FAILED(sq_call(v, 1, SQFalse, SQTrue))) {
        sq_settop(v, top);
        return -2;
    }
    sq_settop(v, top);
    return 0;
}

// 只编译(不运行);用于批量检查语法。成功返回 0,失败 -1,栈恢复。
DNFSQ_API int dnfsq_compile_only(HSQUIRRELVM v, const char *buf, int len, const char *name) {
    DnfSqContext *c = ctx_of(v);
    if (c) {
        c->compile_error[0] = 0;
    }
    SQInteger top = sq_gettop(v);
    int rc = SQ_FAILED(sq_compilebuffer(v, buf, (SQInteger)len, name, SQTrue)) ? -1 : 0;
    sq_settop(v, top);
    return rc;
}

DNFSQ_API const char *dnfsq_compile_error(HSQUIRRELVM v) {
    DnfSqContext *c = ctx_of(v);
    return c ? c->compile_error : "";
}

DNFSQ_API int dnfsq_version(void) {
    return SQUIRREL_VERSION_NUMBER;
}

DNFSQ_API int dnfsq_integer_size(void) {
    return (int)sizeof(SQInteger);
}

DNFSQ_API int dnfsq_float_size(void) {
    return (int)sizeof(SQFloat);
}

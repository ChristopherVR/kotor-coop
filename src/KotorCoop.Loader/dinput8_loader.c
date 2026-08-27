/*
 * KotorCoop.Loader — a tiny generic dinput8.dll chain loader.
 *
 * The co-op plugin installs as dinput8.dll because that is the one DLL swkotor.exe loads
 * from its own directory before the system one. Another mod may want that exact same slot
 * (ReShade, an ASI loader, a widescreen fix...). Only one file can be called dinput8.dll,
 * so this wrapper takes the name instead and loads EVERYONE listed in dinput8.chain:
 *
 *     # dinput8.chain — one DLL per line, relative to the game directory
 *     KotorCoop.Plugin.dll
 *     dinput8.prev.dll        # the other mod's dinput8.dll, renamed
 *
 * Rules, deliberately few:
 *   - Every entry is LoadLibrary'd once, in order, by absolute path. A DLL that does its
 *     work on load (ASI-style) needs nothing more.
 *   - The six dinput8 exports are forwarded to the FIRST entry that exports
 *     DirectInput8Create. That entry is a proxy, and a proxy forwards onward itself: the
 *     co-op plugin reads this same file and forwards to the entry after itself, then to the
 *     system dinput8.dll (see src/KotorCoop.Plugin/Exports.cs). Put a proxy that does NOT
 *     chain (one that always loads the system DLL) LAST.
 *   - If nothing in the chain exports DirectInput8Create, or the file is missing entirely,
 *     the system dinput8.dll is used, so a stray copy of this loader is harmless.
 *
 * No CRT dependency and nothing done inside DllMain (the loader lock forbids LoadLibrary
 * there); the chain is resolved lazily on the first export call, exactly like the plugin.
 * Diagnostics go to dinput8-chain.log beside the game.
 *
 * Built by build.ps1 with cl.exe (see the .def next to this file for the export names).
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Exported data symbol, so install.ps1 can tell this loader apart from a stranger's
 * dinput8.dll by searching the file for the string (exports are never stripped). */
__declspec(dllexport) const char KotorCoopLoaderMarker[] = "KotorCoop.Loader chain dinput8";

typedef HRESULT (WINAPI *DirectInput8Create_t)(HINSTANCE, DWORD, REFIID, LPVOID *, LPVOID);
typedef HRESULT (WINAPI *DllCanUnloadNow_t)(void);
typedef HRESULT (WINAPI *DllGetClassObject_t)(REFCLSID, REFIID, LPVOID *);
typedef HRESULT (WINAPI *DllRegisterServer_t)(void);
typedef HRESULT (WINAPI *DllUnregisterServer_t)(void);
typedef LPVOID  (WINAPI *GetdfDIJoystick_t)(void);

static HMODULE g_self;
static HMODULE g_target;
static LONG    g_resolved;
static CRITICAL_SECTION g_gate;
static BOOL    g_gateReady;

static DirectInput8Create_t   p_DirectInput8Create;
static DllCanUnloadNow_t      p_DllCanUnloadNow;
static DllGetClassObject_t    p_DllGetClassObject;
static DllRegisterServer_t    p_DllRegisterServer;
static DllUnregisterServer_t  p_DllUnregisterServer;
static GetdfDIJoystick_t      p_GetdfDIJoystick;

static WCHAR g_gameDir[MAX_PATH];
static WCHAR g_logPath[MAX_PATH];

/* --- tiny string helpers: no CRT ---------------------------------------------------------- */

static size_t wlen(const WCHAR *s) { size_t n = 0; while (s[n]) n++; return n; }

static void wcopy(WCHAR *dst, size_t cap, const WCHAR *src)
{
    size_t i = 0;
    for (; i + 1 < cap && src[i]; i++) dst[i] = src[i];
    dst[i] = 0;
}

static void wcat(WCHAR *dst, size_t cap, const WCHAR *src)
{
    size_t n = wlen(dst);
    size_t i = 0;
    for (; n + i + 1 < cap && src[i]; i++) dst[n + i] = src[i];
    dst[n + i] = 0;
}

static BOOL wieq(const WCHAR *a, const WCHAR *b)
{
    return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL;
}

static BOOL is_abs(const WCHAR *p)
{
    return (p[0] && p[1] == L':') || (p[0] == L'\\' && p[1] == L'\\');
}

/* --- logging ------------------------------------------------------------------------------ */

static void log_line(const char *text, const WCHAR *path)
{
    HANDLE h;
    DWORD written;
    char buf[1024];
    size_t n = 0, i;

    if (!g_logPath[0]) return;
    h = CreateFileW(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                    OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;

    for (i = 0; text[i] && n + 4 < sizeof buf; i++) buf[n++] = text[i];
    if (path && path[0])
    {
        buf[n++] = ' ';
        n += (size_t)WideCharToMultiByte(CP_UTF8, 0, path, -1, buf + n, (int)(sizeof buf - n - 3), NULL, NULL);
        if (n && buf[n - 1] == 0) n--; /* drop the terminator WideCharToMultiByte wrote */
    }
    buf[n++] = '\r'; buf[n++] = '\n';
    WriteFile(h, buf, (DWORD)n, &written, NULL);
    CloseHandle(h);
}

/* --- chain resolution --------------------------------------------------------------------- */

static void init_paths(void)
{
    WCHAR exe[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
    size_t i;
    if (n == 0 || n >= MAX_PATH) { g_gameDir[0] = 0; return; }
    for (i = n; i > 0 && exe[i - 1] != L'\\'; i--) ;
    exe[i] = 0;
    wcopy(g_gameDir, MAX_PATH, exe);
    wcopy(g_logPath, MAX_PATH, g_gameDir);
    wcat(g_logPath, MAX_PATH, L"dinput8-chain.log");
}

static HMODULE load_system_dinput8(void)
{
    WCHAR path[MAX_PATH];
    UINT n = GetSystemDirectoryW(path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 12) return NULL;
    wcat(path, MAX_PATH, L"\\dinput8.dll");
    log_line("loading system", path);
    return LoadLibraryW(path);
}

/* Reads dinput8.chain (ANSI/UTF-8, any line ending) into a NUL-terminated buffer, or NULL. */
static char *read_chain_file(void)
{
    WCHAR path[MAX_PATH];
    HANDLE h;
    DWORD size, got = 0;
    char *buf;

    wcopy(path, MAX_PATH, g_gameDir);
    wcat(path, MAX_PATH, L"dinput8.chain");
    h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { log_line("no dinput8.chain beside the game; system dinput8 only", NULL); return NULL; }

    size = GetFileSize(h, NULL);
    if (size == INVALID_FILE_SIZE || size > 64 * 1024) { CloseHandle(h); return NULL; }
    buf = (char *)HeapAlloc(GetProcessHeap(), 0, size + 1);
    if (!buf) { CloseHandle(h); return NULL; }
    if (!ReadFile(h, buf, size, &got, NULL)) got = 0;
    CloseHandle(h);
    buf[got] = 0;
    log_line("reading", path);
    return buf;
}

/* Loads one chain entry; returns the module and whether it is a DirectInput8 proxy. */
static HMODULE load_entry(const char *line, size_t len, BOOL *isProxy)
{
    WCHAR rel[MAX_PATH];
    WCHAR full[MAX_PATH];
    WCHAR selfPath[MAX_PATH];
    HMODULE m;
    int n;

    *isProxy = FALSE;
    n = MultiByteToWideChar(CP_UTF8, 0, line, (int)len, rel, MAX_PATH - 1);
    if (n <= 0) return NULL;
    rel[n] = 0;

    if (is_abs(rel)) wcopy(full, MAX_PATH, rel);
    else { wcopy(full, MAX_PATH, g_gameDir); wcat(full, MAX_PATH, rel); }

    /* Never load ourselves by another name, and never chain back to the game-dir dinput8.dll. */
    if (GetModuleFileNameW(g_self, selfPath, MAX_PATH) && wieq(full, selfPath))
    {
        log_line("skipping entry (this loader)", full);
        return NULL;
    }

    m = LoadLibraryW(full);
    if (!m) { log_line("FAILED to load entry", full); return NULL; }
    if (m == g_self) { log_line("skipping entry (resolved to this loader)", full); return NULL; }

    *isProxy = GetProcAddress(m, "DirectInput8Create") != NULL;
    log_line(*isProxy ? "loaded entry (dinput8 proxy)" : "loaded entry", full);
    return m;
}

static void resolve_chain(void)
{
    char *chain;
    HMODULE target = NULL;

    init_paths();
    log_line("=== KotorCoop.Loader: resolving dinput8 chain ===", NULL);

    chain = read_chain_file();
    if (chain)
    {
        char *p = chain;
        while (*p)
        {
            char *start, *end;
            BOOL isProxy;
            HMODULE m;

            /* one line */
            start = p;
            while (*p && *p != '\n' && *p != '\r') p++;
            end = p;
            while (*p == '\n' || *p == '\r') p++;

            /* trim */
            while (start < end && (*start == ' ' || *start == '\t')) start++;
            while (end > start && (end[-1] == ' ' || end[-1] == '\t')) end--;
            if (start == end || *start == '#' || *start == ';') continue;
            if ((size_t)(end - start) >= MAX_PATH) continue;

            m = load_entry(start, (size_t)(end - start), &isProxy);
            if (m && isProxy && !target) target = m;
        }
        HeapFree(GetProcessHeap(), 0, chain);
    }

    if (!target) target = load_system_dinput8();
    g_target = target;
    if (!target) { log_line("FATAL: nothing to forward to", NULL); return; }

    p_DirectInput8Create  = (DirectInput8Create_t)  GetProcAddress(target, "DirectInput8Create");
    p_DllCanUnloadNow     = (DllCanUnloadNow_t)     GetProcAddress(target, "DllCanUnloadNow");
    p_DllGetClassObject   = (DllGetClassObject_t)   GetProcAddress(target, "DllGetClassObject");
    p_DllRegisterServer   = (DllRegisterServer_t)   GetProcAddress(target, "DllRegisterServer");
    p_DllUnregisterServer = (DllUnregisterServer_t) GetProcAddress(target, "DllUnregisterServer");
    p_GetdfDIJoystick     = (GetdfDIJoystick_t)     GetProcAddress(target, "GetdfDIJoystick");

    /* A proxy that implements only DirectInput8Create still gets the rest from the system. */
    if (!p_DllCanUnloadNow || !p_DllGetClassObject || !p_GetdfDIJoystick)
    {
        HMODULE sys = load_system_dinput8();
        if (sys)
        {
            if (!p_DllCanUnloadNow)     p_DllCanUnloadNow     = (DllCanUnloadNow_t)     GetProcAddress(sys, "DllCanUnloadNow");
            if (!p_DllGetClassObject)   p_DllGetClassObject   = (DllGetClassObject_t)   GetProcAddress(sys, "DllGetClassObject");
            if (!p_DllRegisterServer)   p_DllRegisterServer   = (DllRegisterServer_t)   GetProcAddress(sys, "DllRegisterServer");
            if (!p_DllUnregisterServer) p_DllUnregisterServer = (DllUnregisterServer_t) GetProcAddress(sys, "DllUnregisterServer");
            if (!p_GetdfDIJoystick)     p_GetdfDIJoystick     = (GetdfDIJoystick_t)     GetProcAddress(sys, "GetdfDIJoystick");
        }
    }
    log_line("chain resolved", NULL);
}

static void ensure_resolved(void)
{
    if (g_resolved) return;
    if (g_gateReady) EnterCriticalSection(&g_gate);
    if (!g_resolved)
    {
        resolve_chain();
        InterlockedExchange(&g_resolved, 1);
    }
    if (g_gateReady) LeaveCriticalSection(&g_gate);
}

/* --- exports (names come from dinput8.def) ------------------------------------------------- */

HRESULT WINAPI Proxy_DirectInput8Create(HINSTANCE inst, DWORD version, REFIID riid, LPVOID *out, LPVOID outer)
{
    ensure_resolved();
    return p_DirectInput8Create ? p_DirectInput8Create(inst, version, riid, out, outer) : E_FAIL;
}

HRESULT WINAPI Proxy_DllCanUnloadNow(void)
{
    ensure_resolved();
    return p_DllCanUnloadNow ? p_DllCanUnloadNow() : E_FAIL;
}

HRESULT WINAPI Proxy_DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID *out)
{
    ensure_resolved();
    return p_DllGetClassObject ? p_DllGetClassObject(clsid, riid, out) : E_FAIL;
}

HRESULT WINAPI Proxy_DllRegisterServer(void)
{
    ensure_resolved();
    return p_DllRegisterServer ? p_DllRegisterServer() : E_FAIL;
}

HRESULT WINAPI Proxy_DllUnregisterServer(void)
{
    ensure_resolved();
    return p_DllUnregisterServer ? p_DllUnregisterServer() : E_FAIL;
}

LPVOID WINAPI Proxy_GetdfDIJoystick(void)
{
    ensure_resolved();
    return p_GetdfDIJoystick ? p_GetdfDIJoystick() : NULL;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        InitializeCriticalSection(&g_gate);
        g_gateReady = TRUE;
    }
    return TRUE;
}

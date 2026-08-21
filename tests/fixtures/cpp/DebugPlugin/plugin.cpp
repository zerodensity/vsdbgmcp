// A module the host loads long after it starts, the way a real plugin arrives.
//
// It exists for the two things a single-module fixture cannot show: a breakpoint that
// sits unbound until its module turns up, and a type that only this module's symbols
// describe, which an expression in the host's frame cannot name without being told
// where to look.

#include <cstring>
#include <cstdio>

// Deliberately not declared in any header the host sees. Reaching a field of this from
// a frame in DebugTarget.exe is what typeModule is for.
struct PluginState {
    char name[16];
    int refCount;
    unsigned int guard;
};

static void Copy(char (&into)[16], const char* from) {
    const size_t length = from ? std::strlen(from) : 0;
    const size_t kept = length < sizeof(into) - 1 ? length : sizeof(into) - 1;
    std::memcpy(into, from, kept);
    into[kept] = '\0';
}

static PluginState g_state;

extern "C" __declspec(dllexport) void* CreateState(const char* name) {
    std::memset(&g_state, 0, sizeof(g_state));
    Copy(g_state.name, name);
    g_state.refCount = 1;
    g_state.guard = 0xABCDEF01;
    return &g_state;
}

// A good breakpoint site inside a late-loaded module: it cannot bind until the host
// has loaded this DLL.
extern "C" __declspec(dllexport) int Touch(void* state) {
    PluginState* s = static_cast<PluginState*>(state);
    s->refCount += 1;
    return s->refCount;
}

// Returns a pointer to memory it has already freed. The debug CRT fills a freed block
// with 0xdd, so reading through this is the difference between "some pointer is bad"
// and "this object was already deleted".
extern "C" __declspec(dllexport) void* MakeDangling() {
    PluginState* p = new PluginState();
    Copy(p->name, "released");
    p->refCount = 7;
    delete p;
    return p;
}

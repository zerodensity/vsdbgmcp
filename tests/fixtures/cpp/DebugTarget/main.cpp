// A small native program with the failure modes this server is meant to help with:
// a breakpoint worth hitting, a container worth visualising, worker threads worth
// telling apart, a value that gets overwritten, and a crash worth triaging.
//
//   DebugTarget            run through every stage, then exit
//   DebugTarget crash      end with an access violation
//   DebugTarget wait       block on input, for exercising console_send

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>

namespace {

struct Mesh {
    std::string name;
    std::vector<float> vertices;
    int refCount = 1;
};

// Deliberately adjacent, so writing past the end of the buffer lands on the guard.
struct Buffer {
    char data[16];
    unsigned int guard;
};

std::atomic<int> g_state{0};
std::atomic<bool> g_stop{false};

int Upload(Mesh& mesh, int scale) {
    // A good place for a breakpoint: locals, a container, and an argument.
    int total = 0;
    for (size_t i = 0; i < mesh.vertices.size(); ++i) {
        total += static_cast<int>(mesh.vertices[i] * scale);
    }
    mesh.refCount += 1;
    return total;
}

void Worker(int id) {
    // Each worker parks on a different value, so evaluating one expression across
    // all threads gives visibly different answers.
    while (!g_stop.load()) {
        g_state.store(id * 100 + 7);
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
}

void Corrupt(Buffer& buffer) {
    // Writes one past data[], landing on guard. A data breakpoint on &buffer.guard
    // catches this the moment it happens.
    std::memset(buffer.data, 'A', sizeof(buffer.data) + 1);
}

void Crash() {
    volatile int* nowhere = reinterpret_cast<int*>(0x10);
    *nowhere = 42;
}

}  // namespace

int main(int argc, char** argv) {
    const std::string mode = argc > 1 ? argv[1] : "";

    std::printf("DebugTarget starting, pid %lu\n", GetCurrentProcessId());
    std::fflush(stdout);

    OutputDebugStringA("DebugTarget: OutputDebugString reaches the Debug pane\n");

    Mesh mesh;
    mesh.name = "terrain";
    mesh.vertices = {1.5f, 2.5f, 3.5f, 4.5f};

    const int total = Upload(mesh, 10);
    std::printf("upload total %d, refCount %d\n", total, mesh.refCount);
    std::fflush(stdout);

    std::vector<std::thread> workers;
    for (int i = 1; i <= 4; ++i) workers.emplace_back(Worker, i);

    Buffer buffer;
    std::memset(&buffer, 0, sizeof(buffer));
    buffer.guard = 0xDEADBEEF;
    Corrupt(buffer);
    std::printf("guard is now 0x%08X\n", buffer.guard);
    std::fflush(stdout);

    if (mode == "wait") {
        std::printf("type something and press enter: ");
        std::fflush(stdout);
        char line[128] = {0};
        if (std::fgets(line, sizeof(line), stdin)) {
            std::printf("you typed: %s", line);
            std::fflush(stdout);
        }
    }

    if (mode == "crash") {
        std::printf("crashing now\n");
        std::fflush(stdout);
        Crash();
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    g_stop.store(true);
    for (auto& worker : workers) worker.join();

    std::printf("done\n");
    std::fflush(stdout);
    return 0;
}

#include "engine.hpp"
#include "util.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using namespace wisp;

static int g_failures = 0;

#define CHECK(condition)                                                        \
    do {                                                                        \
        if (!(condition)) {                                                     \
            std::cerr << "FAIL " << __FILE__ << ":" << __LINE__ << "  "         \
                      << #condition << "\n";                                    \
            ++g_failures;                                                       \
        }                                                                       \
    } while (false)

static fs::path make_temp_root() {
    const fs::path root = fs::temp_directory_path() /
        path_from_utf8("wisp-test-" + random_hex(6));
    fs::create_directories(root);
    return root;
}

static void write_pattern(const fs::path& path, size_t size, uint8_t seed) {
    fs::create_directories(path.parent_path());
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    std::vector<uint8_t> block(64 * 1024);
    for (size_t i = 0; i < block.size(); ++i) {
        block[i] = static_cast<uint8_t>((i * 31u + seed) & 0xffu);
    }
    size_t written = 0;
    uint8_t counter = seed;
    while (written < size) {
        std::fill(block.begin(), block.end(), static_cast<uint8_t>(counter++));
        const size_t count = std::min(block.size(), size - written);
        stream.write(reinterpret_cast<const char*>(block.data()),
                     static_cast<std::streamsize>(count));
        written += count;
    }
}

static void test_path_validation() {
    std::string normalized;
    std::string error;

    CHECK(valid_relative_path("photos/cat.jpg", normalized, error));
    CHECK(normalized == "photos/cat.jpg");
    CHECK(!valid_relative_path("../escape", normalized, error));
    CHECK(!valid_relative_path("/etc/passwd", normalized, error));
    CHECK(!valid_relative_path("C:\\Windows\\file", normalized, error));
    CHECK(!valid_relative_path("folder/../../escape", normalized, error));
    CHECK(!valid_relative_path("CON.txt", normalized, error));
    CHECK(!valid_relative_path("folder/trailing. ", normalized, error));
    CHECK(!valid_relative_path("folder/name:stream", normalized, error));
    CHECK(!valid_relative_path("folder//name", normalized, error));
    CHECK(valid_relative_path("emoji-\xF0\x9F\x8C\x8A.txt", normalized, error));
}

static void test_engine_transfer(const fs::path& root) {
    const fs::path source_root = root / "source";
    const fs::path save_root = root / "received";
    const fs::path sender_save = root / "sender-downloads";
    fs::create_directories(source_root);
    write_pattern(source_root / "photo.bin", 12u * 1024u * 1024u + 12345u, 11);
    write_pattern(source_root / "small.txt", 42, 3);
    write_pattern(source_root / "empty.dat", 0, 0);
    write_pattern(source_root / "album" / "one.jpg", 2u * 1024u * 1024u, 7);
    write_pattern(source_root / "album" / "two.jpg", 3u * 1024u * 1024u + 1, 9);
    for (int i = 0; i < 40; ++i) {
        write_pattern(source_root / "many" / ("file-" + std::to_string(i) + ".bin"),
                      1024u + static_cast<size_t>(i) * 37u,
                      static_cast<uint8_t>(i));
    }

    std::mutex event_mutex;
    std::condition_variable event_cv;
    std::atomic<bool> sender_done{false};
    std::atomic<bool> sender_ok{false};
    std::atomic<bool> receiver_done{false};
    std::atomic<bool> receiver_ok{false};
    uint64_t completed_bytes = 0;
    uint64_t started = now_ms();

    Engine receiver(
        EngineConfig{
            "Test Receiver",
            path_to_utf8(save_root),
            path_to_utf8(root / "id-receiver"),
            0,
            0,
            true,
            4,
            0,
        },
        [&](int type, const std::string& json) {
            std::lock_guard<std::mutex> lock(event_mutex);
            if (type == 6) {
                receiver_done = true;
                receiver_ok = true;
                event_cv.notify_all();
            } else if (type == 7) {
                std::cerr << "receiver error: " << json << "\n";
                receiver_done = true;
                receiver_ok = false;
                event_cv.notify_all();
            }
        },
        [](int, const std::string&) {});
    Engine sender(
        EngineConfig{
            "Test Sender",
            path_to_utf8(sender_save),
            path_to_utf8(root / "id-sender"),
            0,
            0,
            false,
            4,
            0,
        },
        [&](int type, const std::string& json) {
            std::lock_guard<std::mutex> lock(event_mutex);
            if (type == 6) {
                sender_done = true;
                sender_ok = true;
                for (const auto& entry : fs::recursive_directory_iterator(source_root)) {
                    if (entry.is_regular_file()) completed_bytes += entry.file_size();
                }
                event_cv.notify_all();
            } else if (type == 7) {
                std::cerr << "sender error: " << json << "\n";
                sender_done = true;
                sender_ok = false;
                event_cv.notify_all();
            }
        },
        [](int, const std::string&) {});

    std::string error;
    CHECK(receiver.start(error));
    CHECK(sender.start(error));

    PeerInfo peer;
    peer.id = receiver.own_id();
    peer.name = "Test Receiver";
    peer.platform = "windows";
    peer.host = "127.0.0.1";
    peer.port = receiver.listen_port();
    peer.fingerprint = receiver.public_key_fingerprint();
    CHECK(sender.add_peer(peer, error));

    const std::string transfer_id = sender.send_paths(
        peer.id, {path_to_utf8(source_root)}, error);
    if (transfer_id.empty()) {
        std::cerr << "send_paths failed: " << error << "\n";
        ++g_failures;
        sender.stop();
        receiver.stop();
        return;
    }

    {
        std::unique_lock<std::mutex> lock(event_mutex);
        event_cv.wait_for(lock, std::chrono::seconds(120), [&] {
            return sender_done && receiver_done;
        });
    }
    CHECK(sender_done.load());
    CHECK(sender_ok.load());
    CHECK(receiver_done.load());
    CHECK(receiver_ok.load());

    const fs::path received = save_root / "source";
    CHECK(fs::exists(received / "photo.bin"));
    CHECK(fs::exists(received / "small.txt"));
    CHECK(fs::exists(received / "empty.dat"));
    CHECK(fs::exists(received / "album" / "one.jpg"));
    CHECK(fs::exists(received / "album" / "two.jpg"));
    CHECK(fs::file_size(received / "photo.bin") == 12u * 1024u * 1024u + 12345u);
    CHECK(fs::file_size(received / "empty.dat") == 0);

    std::array<uint8_t, 32> source_hash{};
    std::array<uint8_t, 32> received_hash{};
    CHECK(sha256_file(source_root / "photo.bin", source_hash));
    CHECK(sha256_file(received / "photo.bin", received_hash));
    CHECK(source_hash == received_hash);
    source_hash.fill(0);
    received_hash.fill(0);
    CHECK(sha256_file(source_root / "album" / "two.jpg", source_hash));
    CHECK(sha256_file(received / "album" / "two.jpg", received_hash));
    CHECK(source_hash == received_hash);

    const uint64_t elapsed = now_ms() - started;
    if (elapsed > 0) {
        const double mib = static_cast<double>(completed_bytes) /
                           (1024.0 * 1024.0);
        std::cout << "transferred " << mib << " MiB in " << elapsed
                  << " ms (" << (mib * 1000.0 / static_cast<double>(elapsed))
                  << " MiB/s)\n";
    }

    sender.stop();
    receiver.stop();
}

int main() {
    const fs::path root = make_temp_root();
    std::cout << "test directory: " << path_to_utf8(root) << "\n";
    test_path_validation();
    test_engine_transfer(root);
    if (g_failures == 0) {
        std::cout << "all native tests passed\n";
        std::error_code ec;
        fs::remove_all(root, ec);
        return 0;
    }
    std::cerr << g_failures << " native test(s) failed\n";
    return 1;
}

#include "wisp/c_api.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <csignal>
#include <cstring>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

std::atomic<bool> g_stop{false};

void on_signal(int) { g_stop.store(true); }

std::string json_string_field(const std::string& json, const std::string& key) {
    const std::string needle = "\"" + key + "\":\"";
    const size_t start = json.find(needle);
    if (start == std::string::npos) return {};
    const size_t value_start = start + needle.size();
    const size_t end = json.find('"', value_start);
    if (end == std::string::npos) return {};
    return json.substr(value_start, end - value_start);
}

uint64_t json_uint_field(const std::string& json, const std::string& key) {
    const std::string needle = "\"" + key + "\":";
    const size_t start = json.find(needle);
    if (start == std::string::npos) return 0;
    size_t offset = start + needle.size();
    while (offset < json.size() && (json[offset] == ' ' || json[offset] == '"')) ++offset;
    uint64_t value = 0;
    while (offset < json.size() && json[offset] >= '0' && json[offset] <= '9') {
        value = value * 10 + static_cast<uint64_t>(json[offset] - '0');
        ++offset;
    }
    return value;
}

std::string json_field_placeholder(const std::string& json);

struct CliState {
    wisp_engine* engine = nullptr;
    std::string save_dir;
    bool auto_accept = false;
    bool sending = false;
    std::string expected_transfer;
    std::mutex mutex;
    std::condition_variable cv;
    bool finished = false;
    bool success = false;
};

void event_callback(void* user, int type, const char* json_c) {
    auto* state = static_cast<CliState*>(user);
    const std::string json = json_c ? json_c : "";
    switch (type) {
    case WISP_EVT_PEER_FOUND: {
        if (json.find("\"online\":false") != std::string::npos) {
            std::cout << "[peer lost] " << json << "\n";
        } else {
            std::cout << "[nearby] " << json_field_placeholder(json) << "\n";
        }
        break;
    }
    case WISP_EVT_PAIRING:
        std::cout << "[pair] " << json_string_field(json, "peer_name")
                  << " code " << json_string_field(json, "code") << "\n";
        break;
    case WISP_EVT_OFFER: {
        const std::string offer_id = json_string_field(json, "offer_id");
        const std::string peer = json_string_field(json, "peer_name");
        const uint64_t bytes = json_uint_field(json, "total_bytes");
        const uint64_t files = json_uint_field(json, "file_count");
        std::cout << "[offer] " << peer << " wants to send " << files
                  << " file(s), " << bytes << " bytes\n";
        if (state->auto_accept) {
            std::cout << "[offer] auto-accepting\n";
            wisp_engine_respond(state->engine, offer_id.c_str(), 1,
                                state->save_dir.c_str());
        } else {
            std::cout << "Accept? [y/N] " << std::flush;
            std::string line;
            std::getline(std::cin, line);
            const bool accept = !line.empty() && (line[0] == 'y' || line[0] == 'Y');
            wisp_engine_respond(state->engine, offer_id.c_str(), accept ? 1 : 0,
                                state->save_dir.c_str());
        }
        break;
    }
    case WISP_EVT_PROGRESS: {
        const std::string state_name = json_string_field(json, "state");
        if (state_name == "waiting") {
            std::cout << "[send] waiting for the receiver to accept\n";
        } else if (state_name == "sending") {
            // Keep the console usable; progress is intentionally not printed per tick.
        } else if (state_name == "receiving") {
            // Same for receive progress.
        }
        break;
    }
    case WISP_EVT_COMPLETE: {
        std::lock_guard<std::mutex> lock(state->mutex);
        std::cout << "[complete] " << json << "\n";
        state->finished = true;
        state->success = true;
        state->cv.notify_all();
        break;
    }
    case WISP_EVT_ERROR:
    case WISP_EVT_CANCELLED: {
        std::cout << "[error] " << json << "\n";
        std::lock_guard<std::mutex> lock(state->mutex);
        if (state->sending) {
            state->finished = true;
            state->success = false;
            state->cv.notify_all();
        }
        break;
    }
    default:
        break;
    }
}

// Declared out-of-line only because the peer event line uses a compact display.
std::string json_field_placeholder(const std::string& json) {
    const std::string name = json_string_field(json, "name");
    const std::string id = json_string_field(json, "id");
    const uint64_t port = json_uint_field(json, "port");
    return name + "  id=" + id + "  port=" + std::to_string(port);
}

void log_callback(void*, int level, const char* message) {
    if (level >= WISP_LOG_WARN) {
        std::cerr << "[wisp] " << (message ? message : "") << "\n";
    }
}

void print_usage() {
    std::cout
        << "Wisp CLI\n"
        << "  wisp_cli --name NAME --save DIR [--identity DIR] [--auto-accept]\n"
        << "           [--port N] [--discovery-port N] [--no-discovery]\n"
        << "           [--send FILE_OR_DIR]... [--peer-wait SECONDS]\n"
        << "           [--peer-id ID --peer-name NAME --peer-host HOST --peer-port N\n"
        << "            --peer-fp FINGERPRINT]\n\n"
        << "Run without --send to receive. Ctrl+C stops the app.\n";
}

bool wait_for_peer(CliState& state, int seconds, std::string& id, std::string& name) {
    const uint64_t deadline = static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count()) +
        static_cast<uint64_t>(seconds) * 1000;
    while (!g_stop.load()) {
        char buffer[4096];
        const int length = wisp_engine_get_peers(state.engine, buffer, sizeof(buffer));
        if (length > 0) {
            const std::string json(buffer);
            id = json_string_field(json, "id");
            name = json_string_field(json, "name");
            if (!id.empty()) return true;
        }
        const uint64_t now = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count());
        if (now >= deadline) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
    }
    return false;
}

} // namespace

int main(int argc, char** argv) {
    std::cout << std::unitbuf;
    std::cerr << std::unitbuf;
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);

    std::string name = "Wisp CLI";
    std::string save_dir;
    std::string identity_dir;
    std::string manual_id;
    std::string manual_name;
    std::string manual_host;
    std::string manual_fp;
    int manual_port = 0;
    int listen_port = 0;
    int discovery_port = 47778;
    int peer_wait = 15;
    bool auto_accept = false;
    bool sending = false;
    std::vector<std::string> send_paths;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        auto next = [&](std::string& target) -> bool {
            if (i + 1 >= argc) return false;
            target = argv[++i];
            return true;
        };
        if (arg == "--name") {
            if (!next(name)) return 2;
        } else if (arg == "--save") {
            if (!next(save_dir)) return 2;
        } else if (arg == "--identity") {
            if (!next(identity_dir)) return 2;
        } else if (arg == "--auto-accept") {
            auto_accept = true;
        } else if (arg == "--port") {
            std::string value;
            if (!next(value)) return 2;
            listen_port = std::stoi(value);
        } else if (arg == "--discovery-port") {
            std::string value;
            if (!next(value)) return 2;
            discovery_port = std::stoi(value);
        } else if (arg == "--no-discovery") {
            discovery_port = 0;
        } else if (arg == "--peer-wait") {
            std::string value;
            if (!next(value)) return 2;
            peer_wait = std::stoi(value);
        } else if (arg == "--send") {
            std::string value;
            if (!next(value)) return 2;
            send_paths.push_back(value);
            sending = true;
        } else if (arg == "--peer-id") {
            if (!next(manual_id)) return 2;
        } else if (arg == "--peer-name") {
            if (!next(manual_name)) return 2;
        } else if (arg == "--peer-host") {
            if (!next(manual_host)) return 2;
        } else if (arg == "--peer-port") {
            std::string value;
            if (!next(value)) return 2;
            manual_port = std::stoi(value);
        } else if (arg == "--peer-fp") {
            if (!next(manual_fp)) return 2;
        } else if (arg == "--help" || arg == "-h") {
            print_usage();
            return 0;
        } else {
            std::cerr << "Unknown argument: " << arg << "\n";
            print_usage();
            return 2;
        }
    }

    if (save_dir.empty()) {
        std::cerr << "--save is required\n";
        return 2;
    }

    CliState state;
    state.save_dir = save_dir;
    state.auto_accept = auto_accept;
    state.sending = sending;

    wisp_engine_config config;
    wisp_engine_config_init(&config);
    config.name = name.c_str();
    config.save_dir = save_dir.c_str();
    config.identity_dir = identity_dir.empty() ? nullptr : identity_dir.c_str();
    config.listen_port = listen_port;
    config.discovery_port = discovery_port;
    config.auto_accept = auto_accept ? 1 : 0;
    config.data_channels = 4;

    state.engine = wisp_engine_create(&config, event_callback, log_callback, &state);
    if (!state.engine) {
        std::cerr << "Could not create engine\n";
        return 1;
    }
    if (wisp_engine_start(state.engine) != 0) {
        std::cerr << "Could not start engine\n";
        wisp_engine_destroy(state.engine);
        return 1;
    }

    {
        char self[1024];
        wisp_engine_get_self(state.engine, self, sizeof(self));
        std::cout << "[self] " << self << "\n";
        std::cout << "[self] identity files are under " << identity_dir << "\n";
    }

    if (!manual_host.empty()) {
        if (manual_port <= 0) {
            std::cerr << "--peer-port is required with --peer-host\n";
            wisp_engine_destroy(state.engine);
            return 2;
        }
        if (wisp_engine_add_peer(state.engine, manual_id.c_str(),
                                 manual_name.empty() ? "Manual peer" : manual_name.c_str(),
                                 manual_host.c_str(), manual_port,
                                 manual_fp.c_str()) != 0) {
            std::cerr << "Could not add manual peer\n";
            wisp_engine_destroy(state.engine);
            return 1;
        }
        std::cout << "[manual] peer added at " << manual_host << ":" << manual_port << "\n";
    }

    if (sending) {
        std::string peer_id = manual_id;
        std::string peer_name = manual_name;
        if (peer_id.empty()) {
            if (!wait_for_peer(state, peer_wait, peer_id, peer_name)) {
                std::cerr << "No nearby peer was discovered\n";
                wisp_engine_destroy(state.engine);
                return 1;
            }
        }
        std::cout << "[send] sending to " << peer_name << " (" << peer_id << ")\n";
        std::vector<const char*> pointers;
        pointers.reserve(send_paths.size());
        for (const std::string& path : send_paths) pointers.push_back(path.c_str());
        char transfer_id[64]{};
        const int result = wisp_engine_send_paths(state.engine, peer_id.c_str(),
                                                  pointers.data(), pointers.size(),
                                                  transfer_id, sizeof(transfer_id));
        if (result != 0) {
            std::cerr << "Could not start transfer: " << result << "\n";
            wisp_engine_destroy(state.engine);
            return 1;
        }
        state.expected_transfer = transfer_id;
        std::cout << "[send] transfer " << transfer_id << " started\n";
        {
            std::unique_lock<std::mutex> lock(state.mutex);
            state.cv.wait_for(lock, std::chrono::hours(2), [&] { return state.finished; });
        }
        const bool success = state.success;
        wisp_engine_stop(state.engine);
        wisp_engine_destroy(state.engine);
        return success ? 0 : 1;
    }

    std::cout << "[receive] waiting for files; press Ctrl+C to quit\n";
    while (!g_stop.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    wisp_engine_stop(state.engine);
    wisp_engine_destroy(state.engine);
    return 0;
}

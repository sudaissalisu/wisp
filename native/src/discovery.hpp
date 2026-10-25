#pragma once

#include <atomic>
#include <cstdint>
#include <map>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace wisp {

struct PeerInfo {
    std::string id;
    std::string name;
    std::string platform;
    std::string host;
    std::string fingerprint;
    int port = 0;
    uint64_t last_seen_ms = 0;
};

struct DiscoveryInfo {
    std::string id;
    std::string name;
    std::string platform;
    std::string fingerprint;
    int listen_port = 0;
};

class Discovery {
public:
    using PeerCallback = std::function<void(const PeerInfo&, bool online)>;
    using LogCallback = std::function<void(int, const std::string&)>;

    Discovery() = default;
    ~Discovery();
    Discovery(const Discovery&) = delete;
    Discovery& operator=(const Discovery&) = delete;

    bool start(int port, DiscoveryInfo info, PeerCallback peer_callback,
               LogCallback log_callback, std::string& error);
    void update_info(DiscoveryInfo info);
    void stop();

private:
    void run();
    void send_beacon();
    void send_beacon_to(uint32_t address, uint16_t port);
    void sweep_subnets();
    void handle_packet(const char* data, size_t size, const std::string& host,
                       uint32_t source_address, uint16_t source_port);

    intptr_t socket_ = -1;
    int port_ = 0;
    std::atomic<bool> running_{false};
    std::thread thread_;
    mutable std::mutex info_mutex_;
    DiscoveryInfo info_;
    std::map<std::string, PeerInfo> peers_;
    std::mutex peers_mutex_;
    PeerCallback peer_callback_;
    LogCallback log_callback_;
};

} // namespace wisp

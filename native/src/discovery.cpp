#include "discovery.hpp"
#include "net.hpp"
#include "util.hpp"

#include <algorithm>
#include <cstring>
#include <map>
#include <set>
#include <vector>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <winsock2.h>
#  include <ws2tcpip.h>
#  include <iphlpapi.h>
#  include <ipifcons.h>
using socket_t = SOCKET;
#  define WISP_INVALID_SOCKET INVALID_SOCKET
#else
#  include <arpa/inet.h>
#  include <ifaddrs.h>
#  include <net/if.h>
#  include <netinet/in.h>
#  include <sys/select.h>
#  include <sys/socket.h>
#  include <unistd.h>
using socket_t = int;
#  define WISP_INVALID_SOCKET (-1)
#endif

namespace wisp {

static constexpr const char* kMulticastGroup = "239.255.42.99";

struct Ipv4Interface {
    uint32_t address = 0;
    uint32_t network = 0;
    uint8_t prefix = 0;
};

static std::vector<Ipv4Interface> local_ipv4_interfaces() {
    std::set<uint64_t> seen;
    std::vector<Ipv4Interface> result;
#if defined(_WIN32)
    ULONG size = 16 * 1024;
    std::vector<uint8_t> buffer(size);
    auto* addresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
    DWORD status = GetAdaptersAddresses(AF_INET,
        GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
        nullptr, addresses, &size);
    if (status == ERROR_BUFFER_OVERFLOW) {
        buffer.resize(size);
        addresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        status = GetAdaptersAddresses(AF_INET,
            GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
            nullptr, addresses, &size);
    }
    if (status != NO_ERROR) return result;
    for (auto* adapter = addresses; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp) continue;
        if (adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        for (auto* unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
            if (!unicast->Address.lpSockaddr ||
                unicast->Address.lpSockaddr->sa_family != AF_INET) {
                continue;
            }
            const auto* ipv4 = reinterpret_cast<const sockaddr_in*>(unicast->Address.lpSockaddr);
            const uint32_t address = ntohl(ipv4->sin_addr.s_addr);
            const uint8_t prefix = unicast->OnLinkPrefixLength;
            if (address == 0 || prefix == 0 || prefix >= 32) continue;
            const uint32_t mask = 0xffffffffu << (32 - prefix);
            const uint32_t network = address & mask;
            const uint64_t key = (static_cast<uint64_t>(network) << 8) | prefix;
            if (!seen.insert(key).second) continue;
            result.push_back({address, network, prefix});
        }
    }
#else
    ifaddrs* interfaces = nullptr;
    if (getifaddrs(&interfaces) != 0) return result;
    for (auto* entry = interfaces; entry; entry = entry->ifa_next) {
        if (!entry->ifa_addr || entry->ifa_addr->sa_family != AF_INET) continue;
        if (!(entry->ifa_flags & IFF_UP) || (entry->ifa_flags & IFF_LOOPBACK)) continue;
        if (!entry->ifa_netmask) continue;
        const auto* address = reinterpret_cast<const sockaddr_in*>(entry->ifa_addr);
        const auto* netmask = reinterpret_cast<const sockaddr_in*>(entry->ifa_netmask);
        const uint32_t host_address = ntohl(address->sin_addr.s_addr);
        const uint32_t mask = ntohl(netmask->sin_addr.s_addr);
        if (host_address == 0 || mask == 0) continue;
        const uint8_t prefix = static_cast<uint8_t>(__builtin_popcount(mask));
        if (prefix >= 32) continue;
        const uint32_t network = host_address & mask;
        const uint64_t key = (static_cast<uint64_t>(network) << 8) | prefix;
        if (!seen.insert(key).second) continue;
        result.push_back({host_address, network, prefix});
    }
    freeifaddrs(interfaces);
#endif
    return result;
}

static void close_udp(socket_t fd) {
#if defined(_WIN32)
    closesocket(fd);
#else
    ::close(fd);
#endif
}

static std::string beacon_packet(const DiscoveryInfo& info) {
    if (info.id.empty() || info.listen_port <= 0 || info.fingerprint.empty()) return {};
    std::string packet = "WISP1|1|" + info.id + "|" + percent_encode(info.name) + "|" +
                         percent_encode(info.platform) + "|" +
                         std::to_string(info.listen_port) + "|" + info.fingerprint;
    return packet.size() <= 1200 ? packet : std::string{};
}

Discovery::~Discovery() { stop(); }

bool Discovery::start(int port, DiscoveryInfo info, PeerCallback peer_callback,
                      LogCallback log_callback, std::string& error) {
    stop();
    if (port <= 0 || port > 65535 || info.listen_port <= 0) {
        error = "discovery is disabled";
        return false;
    }
    net_init();
    socket_t fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (fd == WISP_INVALID_SOCKET) {
        error = "cannot create discovery socket: " + net_last_error();
        return false;
    }
    int reuse = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR,
               reinterpret_cast<const char*>(&reuse), sizeof(reuse));
    int broadcast = 1;
    setsockopt(fd, SOL_SOCKET, SO_BROADCAST,
               reinterpret_cast<const char*>(&broadcast), sizeof(broadcast));
#if defined(_WIN32)
    DWORD timeout = 500;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO,
               reinterpret_cast<const char*>(&timeout), sizeof(timeout));
#else
    timeval timeout{0, 500000};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
#endif
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons(static_cast<uint16_t>(port));
    if (bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
        close_udp(fd);
        error = "cannot listen for nearby devices: " + net_last_error();
        return false;
    }

    ip_mreq membership{};
    if (inet_pton(AF_INET, kMulticastGroup, &membership.imr_multiaddr) == 1) {
        membership.imr_interface.s_addr = htonl(INADDR_ANY);
        setsockopt(fd, IPPROTO_IP, IP_ADD_MEMBERSHIP,
                   reinterpret_cast<const char*>(&membership), sizeof(membership));
        int ttl = 1;
        unsigned char loop = 1;
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL,
                   reinterpret_cast<const char*>(&ttl), sizeof(ttl));
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_LOOP,
                   reinterpret_cast<const char*>(&loop), sizeof(loop));
    }

    {
        std::lock_guard<std::mutex> lock(info_mutex_);
        info_ = std::move(info);
    }
    socket_ = static_cast<intptr_t>(fd);
    port_ = port;
    peer_callback_ = std::move(peer_callback);
    log_callback_ = std::move(log_callback);
    running_.store(true);
    thread_ = std::thread(&Discovery::run, this);
    return true;
}

void Discovery::update_info(DiscoveryInfo info) {
    std::lock_guard<std::mutex> lock(info_mutex_);
    info_ = std::move(info);
}

void Discovery::stop() {
    running_.store(false);
    if (thread_.joinable()) thread_.join();
    if (socket_ != -1) {
        close_udp(static_cast<socket_t>(socket_));
        socket_ = -1;
    }
}

void Discovery::send_beacon() {
    DiscoveryInfo info;
    {
        std::lock_guard<std::mutex> lock(info_mutex_);
        info = info_;
    }
    const std::string packet = beacon_packet(info);
    if (packet.empty()) return;

    sockaddr_in target{};
    target.sin_family = AF_INET;
    target.sin_port = htons(static_cast<uint16_t>(port_));
    target.sin_addr.s_addr = htonl(INADDR_BROADCAST);
    sendto(static_cast<socket_t>(socket_), packet.data(),
           static_cast<int>(packet.size()), 0,
           reinterpret_cast<sockaddr*>(&target), sizeof(target));

    for (const Ipv4Interface& interface : local_ipv4_interfaces()) {
        const uint32_t mask = 0xffffffffu << (32 - interface.prefix);
        const uint32_t directed = interface.network | ~mask;
        if (directed == interface.address) continue;
        target.sin_addr.s_addr = htonl(directed);
        sendto(static_cast<socket_t>(socket_), packet.data(),
               static_cast<int>(packet.size()), 0,
               reinterpret_cast<sockaddr*>(&target), sizeof(target));
    }

    if (inet_pton(AF_INET, kMulticastGroup, &target.sin_addr) == 1) {
        sendto(static_cast<socket_t>(socket_), packet.data(),
               static_cast<int>(packet.size()), 0,
               reinterpret_cast<sockaddr*>(&target), sizeof(target));
    }

    target.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sendto(static_cast<socket_t>(socket_), packet.data(),
           static_cast<int>(packet.size()), 0,
           reinterpret_cast<sockaddr*>(&target), sizeof(target));
}

void Discovery::send_beacon_to(uint32_t address, uint16_t port) {
    DiscoveryInfo info;
    {
        std::lock_guard<std::mutex> lock(info_mutex_);
        info = info_;
    }
    const std::string packet = beacon_packet(info);
    if (packet.empty()) return;
    sockaddr_in target{};
    target.sin_family = AF_INET;
    target.sin_port = htons(port);
    target.sin_addr.s_addr = htonl(address);
    sendto(static_cast<socket_t>(socket_), packet.data(),
           static_cast<int>(packet.size()), 0,
           reinterpret_cast<sockaddr*>(&target), sizeof(target));
}

void Discovery::sweep_subnets() {
    static const char kProbe[] = "WISP-PROBE|1";
    for (const Ipv4Interface& interface : local_ipv4_interfaces()) {
        if (interface.prefix < 22 || interface.prefix > 30) continue;
        const uint32_t mask = 0xffffffffu << (32 - interface.prefix);
        const uint32_t network = interface.network;
        const uint32_t directed = network | ~mask;
        if (directed <= network + 2) continue;
        sockaddr_in target{};
        target.sin_family = AF_INET;
        target.sin_port = htons(static_cast<uint16_t>(port_));
        for (uint32_t host = network + 1; host < directed; ++host) {
            if (host == interface.address) continue;
            target.sin_addr.s_addr = htonl(host);
            sendto(static_cast<socket_t>(socket_), kProbe,
                   static_cast<int>(sizeof(kProbe) - 1), 0,
                   reinterpret_cast<sockaddr*>(&target), sizeof(target));
        }
    }
}

void Discovery::handle_packet(const char* data, size_t size, const std::string& host,
                              uint32_t source_address, uint16_t source_port) {
    if (size == 0 || size > 1200) return;
    std::string packet(data, size);
    if (packet.rfind("WISP-PROBE|1", 0) == 0) {
        send_beacon_to(source_address, source_port);
        return;
    }
    const std::vector<std::string> fields = split(packet, '|');
    if (fields.size() != 7 || fields[0] != "WISP1" || fields[1] != "1") return;

    PeerInfo peer;
    peer.id = fields[2];
    std::string name;
    std::string platform;
    if (!percent_decode(fields[3], name) || !percent_decode(fields[4], platform)) return;
    peer.name = name.empty() ? "Nearby device" : name;
    peer.platform = platform;
    peer.host = host;
    peer.fingerprint = fields[6];
    try {
        peer.port = std::stoi(fields[5]);
    } catch (...) {
        return;
    }
    if (peer.id.size() != 32 || peer.fingerprint.size() != 64 ||
        peer.port <= 0 || peer.port > 65535 || peer.host.empty()) {
        return;
    }
    for (char c : peer.id) {
        if (!std::isxdigit(static_cast<unsigned char>(c))) return;
    }
    for (char c : peer.fingerprint) {
        if (!std::isxdigit(static_cast<unsigned char>(c))) return;
    }

    std::string self_id;
    {
        std::lock_guard<std::mutex> lock(info_mutex_);
        self_id = info_.id;
    }
    if (peer.id == self_id) return;

    peer.last_seen_ms = now_ms();
    bool changed = false;
    {
        std::lock_guard<std::mutex> lock(peers_mutex_);
        const auto found = peers_.find(peer.id);
        changed = found == peers_.end() || found->second.host != peer.host ||
                  found->second.port != peer.port || found->second.name != peer.name ||
                  found->second.fingerprint != peer.fingerprint;
        peers_[peer.id] = peer;
    }
    if (changed && peer_callback_) peer_callback_(peer, true);
}

void Discovery::run() {
    char buffer[2048];
    uint64_t next_beacon = 0;
    uint64_t next_sweep = 0;

    while (running_.load()) {
        const uint64_t now = now_ms();
        if (now >= next_beacon) {
            send_beacon();
            next_beacon = now + 1500;
        }
        if (now >= next_sweep) {
            sweep_subnets();
            next_sweep = now + 7000;
        }

        sockaddr_in source{};
        socklen_t length = sizeof(source);
        const int received = recvfrom(static_cast<socket_t>(socket_), buffer,
                                      sizeof(buffer) - 1, 0,
                                      reinterpret_cast<sockaddr*>(&source), &length);
        if (received > 0) {
            char host[INET_ADDRSTRLEN]{};
            inet_ntop(AF_INET, &source.sin_addr, host, sizeof(host));
            handle_packet(buffer, static_cast<size_t>(received), host,
                          ntohl(source.sin_addr.s_addr), ntohs(source.sin_port));
        }

        std::vector<PeerInfo> lost;
        {
            // Use a fresh timestamp: beacons handled during the blocking
            // recvfrom above are newer than the 'now' captured at loop start,
            // and subtracting the older value underflowed, expiring every peer
            // immediately (the on/off flicker).
            const uint64_t expiry_now = now_ms();
            std::lock_guard<std::mutex> lock(peers_mutex_);
            for (auto it = peers_.begin(); it != peers_.end();) {
                const bool stale = expiry_now > it->second.last_seen_ms &&
                                   expiry_now - it->second.last_seen_ms > 25000;
                if (stale) {
                    lost.push_back(it->second);
                    it = peers_.erase(it);
                } else {
                    ++it;
                }
            }
        }
        for (const PeerInfo& peer : lost) {
            if (peer_callback_) peer_callback_(peer, false);
        }
    }
}

} // namespace wisp

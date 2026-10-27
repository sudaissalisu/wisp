#pragma once

#include "discovery.hpp"
#include "identity.hpp"
#include "net.hpp"
#include "protocol.hpp"
#include "util.hpp"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace wisp {

struct EngineConfig {
    std::string name = "Wisp Device";
    std::string save_dir;
    std::string identity_dir;
    int listen_port = 0;
    int discovery_port = 47778;
    bool auto_accept = false;
    int data_channels = 4;
    int conflict_policy = 0;
};

class Engine {
public:
    using EventCallback = std::function<void(int, const std::string&)>;
    using LogCallback = std::function<void(int, const std::string&)>;

    Engine(EngineConfig config, EventCallback event_callback, LogCallback log_callback);
    ~Engine();
    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    bool start(std::string& error);
    void stop();

    std::string send_paths(const std::string& peer_id,
                           const std::vector<std::string>& paths,
                           std::string& error);
    bool respond(const std::string& offer_id, bool accept,
                 const std::string& save_dir, std::string& error);
    bool cancel(const std::string& transfer_id, std::string& error);
    bool add_peer(const PeerInfo& peer, std::string& error);
    // QR temporary-link support. The sender presents the token in Hello; the
    // receiver binds it to the first peer fingerprint and permits only that peer
    // to reuse it. Empty clears either value.
    void set_link_token(const std::string& token);
    void set_expected_link_token(const std::string& token);
    std::string link_token() const;
    std::string expected_link_token() const;
    bool consume_expected_link_token(const std::string& presented,
                                     const std::string& peer_fingerprint);
    // Source address for outgoing transfer sockets. Used by the Android
    // LocalOnlyHotspot host, whose AP is not a ConnectivityManager network.
    void set_bind_address(const std::string& address);
    std::string bind_address() const;
    std::string peers_json() const;
    std::string peer() const;
    std::string public_key_fingerprint() const;
    std::string own_id() const;
    int listen_port() const { return listener_.port(); }
    bool running() const { return running_.load(); }

private:
    struct IncomingFile {
        ManifestFile manifest;
        std::filesystem::path final_path;
        std::filesystem::path part_path;
        std::vector<uint8_t> chunk_received;
        std::vector<std::array<uint8_t, 32>> chunk_hashes;
        uint32_t chunks_completed = 0;
        std::mutex mutex;
    };

    struct IncomingSession {
        uint64_t transfer_id = 0;
        std::string transfer_id_hex;
        std::string peer_id;
        std::string peer_name;
        std::string peer_fingerprint;
        std::string pairing_code;
        std::string destination_root;
        std::vector<ManifestFile> manifest;
        std::vector<std::shared_ptr<IncomingFile>> files;
        std::vector<uint64_t> chunk_starts;
        uint64_t total_chunks = 0;
        uint64_t total_bytes = 0;
        std::atomic<uint64_t> bytes_done{0};
        std::atomic<uint32_t> files_done{0};
        std::atomic<int> active_data_channels{0};
        std::atomic<bool> cancel{false};
        std::atomic<bool> failed{false};
        std::mutex error_mutex;
        std::string error;
        uint64_t started_ms = 0;
        std::atomic<uint64_t> last_progress_ms{0};
        std::atomic<uint64_t> last_progress_log_ms{0};
        std::atomic<bool> data_progress_started{false};
        std::mutex state_mutex;
        std::condition_variable state_cv;
    };

    struct PendingOffer {
        std::string offer_id;
        std::shared_ptr<IncomingSession> session;
        std::mutex mutex;
        std::condition_variable cv;
        bool answered = false;
        bool accepted = false;
        std::string save_dir;
    };

    struct OutgoingSession {
        uint64_t transfer_id = 0;
        std::string transfer_id_hex;
        PeerInfo peer;
        std::vector<ManifestFile> files;
        std::vector<uint64_t> chunk_starts;
        uint64_t total_chunks = 0;
        uint64_t total_bytes = 0;
        std::atomic<uint64_t> bytes_done{0};
        std::atomic<uint32_t> next_chunk{0};
        std::atomic<uint32_t> files_done{0};
        std::atomic<bool> cancel{false};
        std::atomic<bool> failed{false};
        std::mutex error_mutex;
        std::string error;
        std::vector<uint32_t> file_chunks_sent;
        std::mutex file_counter_mutex;
        uint64_t started_ms = 0;
        std::atomic<uint64_t> last_progress_ms{0};
        std::atomic<uint64_t> last_progress_log_ms{0};
        std::atomic<bool> data_progress_started{false};
        std::mutex state_mutex;
        std::condition_variable state_cv;
    };

    bool prepare_incoming(const std::shared_ptr<IncomingSession>& session, std::string& error);
    bool finalize_incoming(const std::shared_ptr<IncomingSession>& session, std::string& error);
    void incoming_progress(const std::shared_ptr<IncomingSession>& session, bool force);
    void outgoing_progress(const std::shared_ptr<OutgoingSession>& session, bool force);
    void run_sender(const std::shared_ptr<OutgoingSession>& session);
    void run_receiver_control(TlsConnection& tls, const std::string& peer_fingerprint,
                              const std::string& pairing, Frame hello_frame);
    void run_data_channel(TlsConnection& tls, Frame attach_frame);
    void handle_connection(Socket socket);
    void accept_loop();
    void discovery_loop_event(const PeerInfo& peer, bool online);
    void spawn_worker(std::function<void()> work);
    void emit(int type, const std::string& json);
    void log(int level, const std::string& message);
    void remove_outgoing(const std::string& transfer_id);
    std::string own_peer_json_unlocked() const;

    EngineConfig config_;
    EventCallback event_callback_;
    LogCallback log_callback_;
    Identity identity_;
    TcpListener listener_;
    Discovery discovery_;
    std::thread accept_thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> stopping_{false};
    std::atomic<int> active_workers_{0};
    std::mutex worker_mutex_;
    std::condition_variable worker_cv_;

    mutable std::mutex peers_mutex_;
    std::map<std::string, PeerInfo> peers_;

    mutable std::mutex link_token_mutex_;
    std::string link_token_;
    std::string expected_link_token_;
    // Set when the QR token is first accepted. Later connections must present
    // the same token from the same certificate fingerprint.
    std::string bound_link_token_;
    std::string bound_link_fingerprint_;

    mutable std::mutex bind_mutex_;
    std::string bind_address_;

    std::mutex outgoing_mutex_;
    std::map<std::string, std::shared_ptr<OutgoingSession>> outgoing_;

    std::mutex offers_mutex_;
    std::map<std::string, std::shared_ptr<PendingOffer>> offers_;

    std::mutex incoming_mutex_;
    std::map<std::string, std::shared_ptr<IncomingSession>> incoming_tokens_;
    std::map<std::string, std::shared_ptr<IncomingSession>> incoming_transfers_;
};

} // namespace wisp

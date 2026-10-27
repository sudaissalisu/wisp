#include "engine.hpp"
#include "file_io.hpp"
#include "wisp/c_api.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <iomanip>
#include <set>
#include <sstream>
#include <thread>

namespace fs = std::filesystem;

namespace wisp {

static std::string platform_name() {
#if defined(_WIN32)
    return "windows";
#elif defined(__ANDROID__)
    return "android";
#elif defined(__APPLE__)
    return "macos";
#else
    return "linux";
#endif
}

static std::string transfer_id_hex(uint64_t value) {
    std::ostringstream stream;
    stream << std::hex << std::setfill('0') << std::setw(16) << value;
    return stream.str();
}

static std::string peer_event_json(const PeerInfo& peer, bool online) {
    std::ostringstream json;
    json << "{\"id\":\"" << json_escape(peer.id)
         << "\",\"name\":\"" << json_escape(peer.name)
         << "\",\"platform\":\"" << json_escape(peer.platform)
         << "\",\"host\":\"" << json_escape(peer.host)
         << "\",\"port\":" << peer.port
         << ",\"fingerprint\":\"" << json_escape(peer.fingerprint)
         << "\",\"online\":" << (online ? "true" : "false") << "}";
    return json.str();
}

static std::string state_progress_json(const std::string& transfer_id,
                                       const std::string& direction,
                                       const PeerInfo& peer,
                                       const std::string& state,
                                       uint64_t bytes_done,
                                       uint64_t bytes_total,
                                       uint32_t files_done,
                                       uint32_t files_total,
                                       double speed_bps) {
    std::ostringstream json;
    json << "{\"transfer_id\":\"" << transfer_id
         << "\",\"direction\":\"" << direction
         << "\",\"peer_id\":\"" << json_escape(peer.id)
         << "\",\"peer_name\":\"" << json_escape(peer.name)
         << "\",\"state\":\"" << state
         << "\",\"bytes_done\":" << bytes_done
         << ",\"bytes_total\":" << bytes_total
         << ",\"files_done\":" << files_done
         << ",\"files_total\":" << files_total
         << ",\"speed_bps\":" << static_cast<uint64_t>(speed_bps) << "}";
    return json.str();
}

static uint64_t monotonic_ms() {
    using namespace std::chrono;
    return static_cast<uint64_t>(
        duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
}

static double current_speed(uint64_t bytes, uint64_t started_ms) {
    const uint64_t elapsed = now_ms() > started_ms ? now_ms() - started_ms : 0;
    if (elapsed == 0) return 0;
    return static_cast<double>(bytes) * 1000.0 / static_cast<double>(elapsed);
}

static bool secure_token_equals(const std::string& expected, const std::string& presented) {
    if (expected.empty() || expected.size() != presented.size()) return false;
    unsigned char diff = 0;
    for (size_t i = 0; i < expected.size(); ++i) {
        diff |= static_cast<unsigned char>(expected[i] ^ presented[i]);
    }
    return diff == 0;
}

Engine::Engine(EngineConfig config, EventCallback event_callback, LogCallback log_callback)
    : config_(std::move(config)),
      event_callback_(std::move(event_callback)),
      log_callback_(std::move(log_callback)) {
    if (config_.name.empty()) config_.name = "Wisp Device";
    if (config_.name.size() > 64) config_.name.resize(64);
    config_.data_channels = std::clamp(config_.data_channels, 1, 8);
    if (config_.save_dir.empty()) {
        config_.save_dir = path_to_utf8(fs::current_path() / "Wisp Downloads");
    }
    if (config_.identity_dir.empty()) {
        config_.identity_dir = path_to_utf8(path_from_utf8(config_.save_dir) / ".wisp-identity");
    }
}

Engine::~Engine() { stop(); }

void Engine::emit(int type, const std::string& json) {
    if (!event_callback_) return;
    try {
        event_callback_(type, json);
    } catch (...) {
    }
}

void Engine::log(int level, const std::string& message) {
    if (log_callback_) {
        try {
            log_callback_(level, message);
        } catch (...) {
        }
    }
}

bool Engine::start(std::string& error) {
    if (running_.load()) return true;
    stopping_.store(false);
    std::error_code ec;
    fs::create_directories(path_from_utf8(config_.save_dir), ec);
    if (ec) {
        error = "cannot create save directory: " + config_.save_dir;
        return false;
    }
    if (!identity_.load_or_create(config_.identity_dir, config_.name, error)) return false;
    if (!listener_.open(config_.listen_port, error)) return false;

    running_.store(true);
    accept_thread_ = std::thread(&Engine::accept_loop, this);

    DiscoveryInfo info;
    info.id = identity_.peer_id();
    info.name = config_.name;
    info.platform = platform_name();
    info.fingerprint = identity_.fingerprint();
    info.listen_port = listener_.port();

    if (config_.discovery_port > 0) {
        std::string discovery_error;
        const bool started = discovery_.start(
            config_.discovery_port, info,
            [this](const PeerInfo& peer, bool online) {
                discovery_loop_event(peer, online);
            },
            [this](int level, const std::string& message) { log(level, message); },
            discovery_error);
        if (!started) {
            log(WISP_LOG_WARN, "LAN discovery unavailable: " + discovery_error);
        }
    }

    log(WISP_LOG_INFO, "Wisp listening on port " + std::to_string(listener_.port()));
    emit(WISP_EVT_PROGRESS, "{\"state\":\"ready\",\"port\":" +
                                std::to_string(listener_.port()) + "}");
    return true;
}

void Engine::stop() {
    if (!running_.exchange(false)) {
        stopping_.store(true);
        listener_.close();
        discovery_.stop();
        return;
    }
    stopping_.store(true);
    listener_.close();
    discovery_.stop();
    {
        std::lock_guard<std::mutex> lock(outgoing_mutex_);
        for (auto& entry : outgoing_) entry.second->cancel.store(true);
    }
    {
        std::lock_guard<std::mutex> lock(offers_mutex_);
        for (auto& entry : offers_) {
            {
                std::lock_guard<std::mutex> offer_lock(entry.second->mutex);
                entry.second->answered = true;
                entry.second->accepted = false;
            }
            entry.second->cv.notify_all();
        }
    }
    {
        std::lock_guard<std::mutex> lock(incoming_mutex_);
        for (auto& entry : incoming_transfers_) {
            entry.second->cancel.store(true);
            entry.second->state_cv.notify_all();
        }
    }
    if (accept_thread_.joinable()) accept_thread_.join();

    // Workers use finite socket timeouts (10-30s). Wait for them before the
    // callbacks and engine state are destroyed.
    std::unique_lock<std::mutex> lock(worker_mutex_);
    worker_cv_.wait(lock, [this] { return active_workers_.load() == 0; });
    lock.unlock();
    log(WISP_LOG_INFO, "Engine stopped");
}

void Engine::spawn_worker(std::function<void()> work) {
    {
        std::lock_guard<std::mutex> lock(worker_mutex_);
        if (stopping_.load()) return;
        active_workers_.fetch_add(1);
    }
    try {
        std::thread([this, work = std::move(work)]() {
            try {
                work();
            } catch (const std::exception& exception) {
                log(WISP_LOG_ERROR, std::string("worker exception: ") + exception.what());
            } catch (...) {
                log(WISP_LOG_ERROR, "worker exception");
            }
            {
                std::lock_guard<std::mutex> lock(worker_mutex_);
                active_workers_.fetch_sub(1);
            }
            worker_cv_.notify_all();
        }).detach();
    } catch (...) {
        std::lock_guard<std::mutex> lock(worker_mutex_);
        active_workers_.fetch_sub(1);
    }
}

void Engine::accept_loop() {
    while (running_.load()) {
        Socket socket;
        std::string error;
        if (listener_.accept(socket, 500, error)) {
            auto accepted = std::make_shared<Socket>(std::move(socket));
            spawn_worker([this, accepted]() {
                handle_connection(std::move(*accepted));
            });
            continue;
        }
        if (!running_.load()) break;
        if (!error.empty()) {
            log(WISP_LOG_WARN, "accept failed: " + error);
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    }
}

void Engine::handle_connection(Socket socket) {
    TlsConnection tls;
    std::string error;
    if (!tls.accept(std::move(socket), identity_.server_context(), error)) {
        log(WISP_LOG_WARN, "TLS handshake failed: " + error);
        return;
    }
    const std::string fingerprint = peer_certificate_fingerprint(tls.ssl());
    if (fingerprint.size() != 64) {
        log(WISP_LOG_WARN, "connection without a client certificate");
        return;
    }
    const std::string pairing = pairing_code(tls.ssl());
    Frame first;
    if (!recv_frame(tls, first, error)) {
        log(WISP_LOG_WARN, "connection did not start with a frame: " + error);
        return;
    }
    if (first.type == FrameType::Hello) {
        run_receiver_control(tls, fingerprint, pairing, std::move(first));
    } else if (first.type == FrameType::Attach) {
        run_data_channel(tls, std::move(first));
    } else {
        log(WISP_LOG_WARN, "unexpected first frame");
    }
}

void Engine::discovery_loop_event(const PeerInfo& peer, bool online) {
    {
        std::lock_guard<std::mutex> lock(peers_mutex_);
        if (online) {
            peers_[peer.id] = peer;
        } else {
            peers_.erase(peer.id);
        }
    }
    emit(WISP_EVT_PEER_FOUND, peer_event_json(peer, online));
}

std::string Engine::own_peer_json_unlocked() const {
    PeerInfo peer;
    peer.id = identity_.peer_id();
    peer.name = config_.name;
    peer.platform = platform_name();
    peer.host = "127.0.0.1";
    peer.port = listener_.port();
    peer.fingerprint = identity_.fingerprint();
    return peer_event_json(peer, true);
}

std::string Engine::peer() const {
    std::lock_guard<std::mutex> lock(peers_mutex_);
    return own_peer_json_unlocked();
}

std::string Engine::peers_json() const {
    std::lock_guard<std::mutex> lock(peers_mutex_);
    std::ostringstream json;
    json << "[";
    bool first = true;
    for (const auto& entry : peers_) {
        if (!first) json << ",";
        first = false;
        json << peer_event_json(entry.second, true);
    }
    json << "]";
    return json.str();
}

std::string Engine::public_key_fingerprint() const { return identity_.fingerprint(); }
std::string Engine::own_id() const { return identity_.peer_id(); }

bool Engine::add_peer(const PeerInfo& peer, std::string& error) {
    PeerInfo value = peer;
    if (value.id.empty() && value.fingerprint.size() == 64) {
        value.id = peer_id_from_fingerprint(value.fingerprint);
    }
    if (value.id.size() != 32 || value.port <= 0 || value.port > 65535 || value.host.empty()) {
        error = "invalid peer address";
        return false;
    }
    if (value.name.empty()) value.name = value.host;
    if (value.platform.empty()) value.platform = "unknown";
    value.last_seen_ms = now_ms();
    {
        std::lock_guard<std::mutex> lock(peers_mutex_);
        peers_[value.id] = value;
    }
    emit(WISP_EVT_PEER_FOUND, peer_event_json(value, true));
    return true;
}

void Engine::set_link_token(const std::string& token) {
    std::lock_guard<std::mutex> lock(link_token_mutex_);
    link_token_ = token;
}

void Engine::set_expected_link_token(const std::string& token) {
    std::lock_guard<std::mutex> lock(link_token_mutex_);
    expected_link_token_ = token;
    bound_link_token_.clear();
    bound_link_fingerprint_.clear();
}

std::string Engine::link_token() const {
    std::lock_guard<std::mutex> lock(link_token_mutex_);
    return link_token_;
}

std::string Engine::expected_link_token() const {
    std::lock_guard<std::mutex> lock(link_token_mutex_);
    return expected_link_token_;
}

bool Engine::consume_expected_link_token(const std::string& presented,
                                         const std::string& peer_fingerprint) {
    std::lock_guard<std::mutex> lock(link_token_mutex_);
    if (!expected_link_token_.empty()) {
        if (!secure_token_equals(expected_link_token_, presented)) return false;
        // One QR token, one peer identity. Keep the token so the same guest can
        // start additional transfers over the still-active temporary link.
        expected_link_token_.clear();
        bound_link_token_ = presented;
        bound_link_fingerprint_ = peer_fingerprint;
        return true;
    }
    if (!bound_link_token_.empty()) {
        return secure_token_equals(bound_link_token_, presented) &&
               !bound_link_fingerprint_.empty() &&
               bound_link_fingerprint_ == peer_fingerprint;
    }
    return true;
}

void Engine::set_bind_address(const std::string& address) {
    std::lock_guard<std::mutex> lock(bind_mutex_);
    bind_address_ = address;
}

std::string Engine::bind_address() const {
    std::lock_guard<std::mutex> lock(bind_mutex_);
    return bind_address_;
}

std::string Engine::send_paths(const std::string& peer_id,
                               const std::vector<std::string>& paths,
                               std::string& error) {
    if (!running_.load()) {
        error = "engine is not running";
        return {};
    }
    PeerInfo peer;
    {
        std::lock_guard<std::mutex> lock(peers_mutex_);
        const auto it = peers_.find(peer_id);
        if (it == peers_.end()) {
            error = "peer is no longer nearby";
            return {};
        }
        peer = it->second;
    }
    std::vector<ManifestFile> files;
    if (!collect_manifest(paths, files, error)) return {};

    auto session = std::make_shared<OutgoingSession>();
    session->transfer_id = random_u64();
    if (session->transfer_id == 0) session->transfer_id = 1;
    session->transfer_id_hex = transfer_id_hex(session->transfer_id);
    session->peer = peer;
    session->files = std::move(files);
    session->chunk_starts.push_back(0);
    for (const ManifestFile& file : session->files) {
        session->total_bytes += file.size;
        session->total_chunks += file.chunk_count();
        session->chunk_starts.push_back(session->total_chunks);
        session->file_chunks_sent.push_back(0);
    }
    session->started_ms = now_ms();
    {
        std::lock_guard<std::mutex> lock(outgoing_mutex_);
        outgoing_[session->transfer_id_hex] = session;
    }
    spawn_worker([this, session] { run_sender(session); });
    return session->transfer_id_hex;
}

bool Engine::respond(const std::string& offer_id, bool accept,
                     const std::string& save_dir, std::string& error) {
    std::shared_ptr<PendingOffer> pending;
    {
        std::lock_guard<std::mutex> lock(offers_mutex_);
        const auto it = offers_.find(offer_id);
        if (it == offers_.end()) {
            error = "offer is no longer available";
            return false;
        }
        pending = it->second;
    }
    {
        std::lock_guard<std::mutex> lock(pending->mutex);
        if (pending->answered) {
            error = "offer already answered";
            return false;
        }
        pending->answered = true;
        pending->accepted = accept;
        pending->save_dir = save_dir;
    }
    pending->cv.notify_all();
    return true;
}

bool Engine::cancel(const std::string& transfer_id, std::string& error) {
    {
        std::lock_guard<std::mutex> lock(outgoing_mutex_);
        const auto it = outgoing_.find(transfer_id);
        if (it != outgoing_.end()) {
            it->second->cancel.store(true);
            return true;
        }
    }
    {
        std::lock_guard<std::mutex> lock(incoming_mutex_);
        const auto it = incoming_transfers_.find(transfer_id);
        if (it != incoming_transfers_.end()) {
            it->second->cancel.store(true);
            it->second->state_cv.notify_all();
            return true;
        }
    }
    error = "unknown transfer";
    return false;
}

void Engine::incoming_progress(const std::shared_ptr<IncomingSession>& session, bool force) {
    const uint64_t now = monotonic_ms();
    const uint64_t bytes_done = session->bytes_done.load();
    // Always publish the first non-zero byte count, even on a transfer that
    // completes inside the 250 ms throttle window. Otherwise the UI can sit at
    // 0% until the completion event.
    const bool first_data = bytes_done > 0 &&
        !session->data_progress_started.exchange(true);
    if (!force && !first_data) {
        uint64_t last = session->last_progress_ms.load(std::memory_order_relaxed);
        if (now < last || now - last < 250) return;
        if (!session->last_progress_ms.compare_exchange_strong(last, now)) return;
    } else {
        session->last_progress_ms.store(now, std::memory_order_relaxed);
    }

    PeerInfo peer;
    peer.id = session->peer_id;
    peer.name = session->peer_name;
    peer.fingerprint = session->peer_fingerprint;
    const double speed = current_speed(bytes_done, session->started_ms);
    emit(WISP_EVT_PROGRESS,
         state_progress_json(session->transfer_id_hex, "receive", peer, "receiving",
                             bytes_done, session->total_bytes,
                             session->files_done.load(),
                             static_cast<uint32_t>(session->files.size()), speed));

    // Instrumentation for the Android progress meter. Intermediate byte counts
    // must reach the UI; log them at the start, every five seconds, and at end.
    uint64_t last_log = session->last_progress_log_ms.load(std::memory_order_relaxed);
    if (force || last_log == 0 || now >= last_log + 5000) {
        session->last_progress_log_ms.store(now, std::memory_order_relaxed);
        log(WISP_LOG_DEBUG, "receive progress " + std::to_string(bytes_done) + "/" +
                                std::to_string(session->total_bytes) + " bytes");
    }
}

void Engine::outgoing_progress(const std::shared_ptr<OutgoingSession>& session, bool force) {
    const uint64_t now = monotonic_ms();
    const uint64_t bytes_done = session->bytes_done.load();
    const bool first_data = bytes_done > 0 &&
        !session->data_progress_started.exchange(true);
    if (!force && !first_data) {
        uint64_t last = session->last_progress_ms.load(std::memory_order_relaxed);
        if (now < last || now - last < 250) return;
        if (!session->last_progress_ms.compare_exchange_strong(last, now)) return;
    } else {
        session->last_progress_ms.store(now, std::memory_order_relaxed);
    }

    const double speed = current_speed(bytes_done, session->started_ms);
    emit(WISP_EVT_PROGRESS,
         state_progress_json(session->transfer_id_hex, "send", session->peer, "sending",
                             bytes_done, session->total_bytes,
                             session->files_done.load(),
                             static_cast<uint32_t>(session->files.size()), speed));

    uint64_t last_log = session->last_progress_log_ms.load(std::memory_order_relaxed);
    if (force || last_log == 0 || now >= last_log + 5000) {
        session->last_progress_log_ms.store(now, std::memory_order_relaxed);
        log(WISP_LOG_DEBUG, "send progress " + std::to_string(bytes_done) + "/" +
                                std::to_string(session->total_bytes) + " bytes");
    }
}


} // namespace wisp

#include "engine.hpp"
#include "engine_internal.hpp"
#include "file_io.hpp"

#include <algorithm>
#include <chrono>
#include <cstring>

namespace fs = std::filesystem;

namespace wisp {

static bool path_is_within(const fs::path& child, const fs::path& parent) {
    auto child_it = child.begin();
    for (auto parent_it = parent.begin(); parent_it != parent.end(); ++parent_it) {
        if (child_it == child.end() || *child_it != *parent_it) return false;
        ++child_it;
    }
    return true;
}

static fs::path choose_unique_target(const fs::path& desired) {
    if (!path_exists(desired)) return desired;
    const std::string stem = path_to_utf8(desired.stem());
    const std::string extension = path_to_utf8(desired.extension());
    for (int i = 1; i < 10000; ++i) {
        const fs::path candidate = desired.parent_path() /
            path_from_utf8(stem + " (" + std::to_string(i) + ")" + extension);
        if (!path_exists(candidate)) return candidate;
    }
    return desired;
}

bool Engine::prepare_incoming(const std::shared_ptr<IncomingSession>& session,
                              std::string& error) {
    fs::path root = path_from_utf8(session->destination_root);
    if (root.empty()) root = path_from_utf8(config_.save_dir);
    std::error_code ec;
    if (root.is_relative()) root = fs::absolute(root, ec);
    fs::create_directories(root, ec);
    if (ec) {
        error = "cannot create the destination folder";
        return false;
    }
    root = fs::weakly_canonical(root, ec);
    if (ec) {
        error = "cannot resolve the destination folder";
        return false;
    }
    session->destination_root = path_to_utf8(root);

    session->files.clear();
    session->files_done.store(0);
    for (const ManifestFile& manifest : session->manifest) {
        auto file = std::make_shared<IncomingFile>();
        file->manifest = manifest;
        fs::path target = root / path_from_utf8(manifest.relative_path);
        std::error_code parent_ec;
        fs::create_directories(target.parent_path(), parent_ec);
        if (parent_ec) {
            error = "cannot create a destination folder for " + manifest.relative_path;
            return false;
        }
        const fs::path canonical_parent = fs::weakly_canonical(target.parent_path(), parent_ec);
        if (parent_ec || !path_is_within(canonical_parent, root)) {
            error = "destination path escapes the save folder";
            return false;
        }
        if (fs::exists(target, parent_ec) && config_.conflict_policy != 1) {
            target = choose_unique_target(target);
        }
        file->final_path = target;
        const std::string part_name =
            path_to_utf8(target.filename()) + ".wisp-" +
            session->transfer_id_hex.substr(0, 8) + ".part";
        file->part_path = target.parent_path() / path_from_utf8(part_name);
        remove_file_quiet(file->part_path);
        if (!prepare_file(file->part_path, manifest.size, error)) return false;
        const uint64_t chunks = manifest.chunk_count();
        file->chunk_received.assign(static_cast<size_t>(chunks), 0);
        file->chunk_hashes.assign(static_cast<size_t>(chunks), {});
        if (chunks == 0) session->files_done.fetch_add(1);
        session->files.push_back(file);
    }
    return true;
}

bool Engine::finalize_incoming(const std::shared_ptr<IncomingSession>& session,
                               std::string& error) {
    for (const auto& file : session->files) {
        const uint64_t chunks = file->manifest.chunk_count();
        {
            std::lock_guard<std::mutex> lock(file->mutex);
            if (file->chunks_completed != chunks) {
                error = "missing data for " + file->manifest.relative_path;
                return false;
            }
        }
        // CloseHandle/rename provide atomic visibility. Forcing a physical disk
        // flush for every small file makes photo folders dramatically slower, so
        // durability is left to the OS writeback policy.
        if (!replace_file(file->part_path, file->final_path, error)) return false;
    }
    return true;
}

void Engine::run_receiver_control(TlsConnection& tls, const std::string& peer_fingerprint,
                                  const std::string& pairing, Frame hello_frame) {
    std::string error;
    std::string sender_name;
    std::string sender_platform;
    if (!decode_hello(hello_frame.payload, sender_name, sender_platform, error)) {
        log(WISP_LOG_WARN, "bad hello: " + error);
        return;
    }

    {
        Frame ack;
        ack.type = FrameType::HelloAck;
        ack.payload = encode_hello(config_.name, platform_name());
        if (!send_frame(tls, ack, error)) {
            log(WISP_LOG_WARN, "cannot send hello ack: " + error);
            return;
        }
    }
    tls.socket().set_timeouts(300000, 30000);

    Frame offer_frame;
    if (!recv_frame(tls, offer_frame, error)) {
        log(WISP_LOG_WARN, "cannot read offer: " + error);
        return;
    }
    if (offer_frame.type != FrameType::Offer || offer_frame.transfer_id == 0) {
        send_error_frame(tls, offer_frame.transfer_id, "expected an offer");
        return;
    }

    std::vector<ManifestFile> manifest;
    uint64_t total_bytes = 0;
    if (!decode_offer(offer_frame.payload, manifest, total_bytes, error)) {
        send_error_frame(tls, offer_frame.transfer_id, error);
        return;
    }

    auto session = std::make_shared<IncomingSession>();
    session->transfer_id = offer_frame.transfer_id;
    session->transfer_id_hex = transfer_id_hex(offer_frame.transfer_id);
    session->peer_id = peer_id_from_fingerprint(peer_fingerprint);
    session->peer_name = sender_name.empty() ? "Nearby device" : sender_name;
    session->peer_fingerprint = peer_fingerprint;
    session->pairing_code = pairing;
    session->manifest = std::move(manifest);
    session->total_bytes = total_bytes;
    session->started_ms = now_ms();

    auto pending = std::make_shared<PendingOffer>();
    pending->offer_id = session->transfer_id_hex;
    pending->session = session;
    {
        std::lock_guard<std::mutex> lock(offers_mutex_);
        offers_[pending->offer_id] = pending;
    }

    {
        std::ostringstream json;
        json << "{\"transfer_id\":\"" << session->transfer_id_hex
             << "\",\"peer_id\":\"" << json_escape(session->peer_id)
             << "\",\"peer_name\":\"" << json_escape(session->peer_name)
             << "\",\"code\":\"" << json_escape(pairing)
             << "\",\"direction\":\"receive\"}";
        emit(WISP_EVT_PAIRING, json.str());
    }

    {
        std::ostringstream json;
        json << "{\"offer_id\":\"" << session->transfer_id_hex
             << "\",\"transfer_id\":\"" << session->transfer_id_hex
             << "\",\"peer_id\":\"" << json_escape(session->peer_id)
             << "\",\"peer_name\":\"" << json_escape(session->peer_name)
             << "\",\"fingerprint\":\"" << json_escape(peer_fingerprint)
             << "\",\"pairing_code\":\"" << json_escape(pairing)
             << "\",\"file_count\":" << session->manifest.size()
             << ",\"total_bytes\":" << session->total_bytes
             << ",\"save_dir\":\"" << json_escape(config_.save_dir)
             << "\",\"files\":[";
        const size_t shown = std::min<size_t>(session->manifest.size(), 200);
        for (size_t i = 0; i < shown; ++i) {
            if (i != 0) json << ",";
            json << "{\"path\":\"" << json_escape(session->manifest[i].relative_path)
                 << "\",\"size\":" << session->manifest[i].size << "}";
        }
        json << "],\"files_truncated\":"
             << (session->manifest.size() > shown ? session->manifest.size() - shown : 0)
             << "}";
        emit(WISP_EVT_OFFER, json.str());
    }

    const uint64_t offer_started = now_ms();
    {
        std::unique_lock<std::mutex> lock(pending->mutex);
        if (config_.auto_accept) {
            pending->answered = true;
            pending->accepted = true;
        }
        while (!pending->answered) {
            if (stopping_.load() || !running_.load()) break;
            if (now_ms() - offer_started > 120000) break;
            pending->cv.wait_for(lock, std::chrono::milliseconds(200));
        }
    }
    {
        std::lock_guard<std::mutex> lock(offers_mutex_);
        offers_.erase(pending->offer_id);
    }

    if (!pending->answered || !pending->accepted) {
        Frame response;
        response.type = FrameType::OfferResponse;
        response.transfer_id = session->transfer_id;
        response.payload = encode_offer_response(false, "Declined by receiver", nullptr);
        std::string ignored;
        send_frame(tls, response, ignored);
        return;
    }

    if (!pending->save_dir.empty()) session->destination_root = pending->save_dir;
    if (session->destination_root.empty()) session->destination_root = config_.save_dir;
    session->started_ms = now_ms();
    if (!prepare_incoming(session, error)) {
        for (const auto& file : session->files) remove_file_quiet(file->part_path);
        Frame response;
        response.type = FrameType::OfferResponse;
        response.transfer_id = session->transfer_id;
        response.payload = encode_offer_response(false, error, nullptr);
        std::string ignored;
        send_frame(tls, response, ignored);
        PeerInfo peer;
        peer.id = session->peer_id;
        peer.name = session->peer_name;
        emit(WISP_EVT_ERROR,
             error_event_json(session->transfer_id_hex, "receive", peer, error));
        return;
    }

    std::array<uint8_t, 32> token{};
    if (!random_bytes(token.data(), token.size())) {
        Frame response;
        response.type = FrameType::OfferResponse;
        response.transfer_id = session->transfer_id;
        response.payload = encode_offer_response(false, "Cannot create session token", nullptr);
        std::string ignored;
        send_frame(tls, response, ignored);
        return;
    }
    const std::string token_hex = hex_encode(token.data(), token.size());
    {
        std::lock_guard<std::mutex> lock(incoming_mutex_);
        incoming_tokens_[token_hex] = session;
        incoming_transfers_[session->transfer_id_hex] = session;
    }
    auto cleanup = [&] {
        std::lock_guard<std::mutex> lock(incoming_mutex_);
        incoming_tokens_.erase(token_hex);
        incoming_transfers_.erase(session->transfer_id_hex);
    };

    {
        Frame response;
        response.type = FrameType::OfferResponse;
        response.transfer_id = session->transfer_id;
        response.payload = encode_offer_response(true, "", token.data());
        if (!send_frame(tls, response, error)) {
            cleanup();
            return;
        }
    }
    tls.socket().set_timeouts(20000, 30000);
    incoming_progress(session, true);

    PeerInfo peer;
    peer.id = session->peer_id;
    peer.name = session->peer_name;

    enum class Terminal { None, Finish, Cancel, Failure };
    Terminal terminal = Terminal::None;
    while (terminal == Terminal::None && running_.load() && !stopping_.load() &&
           !session->cancel.load()) {
        Frame frame;
        if (!recv_frame(tls, frame, error)) {
            terminal = Terminal::Failure;
            break;
        }
        switch (frame.type) {
        case FrameType::Ping:
            break;
        case FrameType::Finish:
            terminal = Terminal::Finish;
            break;
        case FrameType::Cancel:
            terminal = Terminal::Cancel;
            break;
        case FrameType::Error:
            terminal = Terminal::Failure;
            break;
        default:
            error = "unexpected control frame";
            terminal = Terminal::Failure;
            break;
        }
    }
    if (session->cancel.load() && terminal == Terminal::None) {
        terminal = Terminal::Cancel;
    }

    if (terminal == Terminal::Cancel) {
        session->cancel.store(true);
        cleanup();
        emit(WISP_EVT_CANCELLED,
             error_event_json(session->transfer_id_hex, "receive", peer, "Transfer cancelled"));
        return;
    }
    if (terminal != Terminal::Finish) {
        if (session->failed.load()) {
            std::lock_guard<std::mutex> lock(session->error_mutex);
            if (!session->error.empty()) error = session->error;
        }
        if (error.empty()) error = "connection closed before the transfer finished";
        session->failed.store(true);
        session->state_cv.notify_all();
        send_error_frame(tls, session->transfer_id, error);
        cleanup();
        emit(WISP_EVT_ERROR,
             error_event_json(session->transfer_id_hex, "receive", peer, error));
        return;
    }

    {
        std::unique_lock<std::mutex> lock(session->state_mutex);
        const bool drained = session->state_cv.wait_for(
            lock, std::chrono::seconds(120), [&] {
                return session->failed.load() ||
                       session->active_data_channels.load() == 0;
            });
        if (!drained) error = "timed out waiting for data channels";
    }
    if (session->failed.load()) {
        std::lock_guard<std::mutex> lock(session->error_mutex);
        if (!session->error.empty()) error = session->error;
    }
    if (error.empty() && session->bytes_done.load() != session->total_bytes) {
        error = "transfer ended with missing data";
    }
    if (error.empty()) {
        finalize_incoming(session, error);
    }
    if (!error.empty()) {
        send_error_frame(tls, session->transfer_id, error);
        cleanup();
        emit(WISP_EVT_ERROR,
             error_event_json(session->transfer_id_hex, "receive", peer, error));
        return;
    }

    {
        Frame done;
        done.type = FrameType::Done;
        done.transfer_id = session->transfer_id;
        done.payload = encode_done(true, "");
        std::string ignored;
        send_frame(tls, done, ignored);
    }
    cleanup();
    log(WISP_LOG_INFO, "received " + std::to_string(session->files.size()) +
                           " file(s) from " + session->peer_name);
    emit(WISP_EVT_COMPLETE,
         complete_event_json(session->transfer_id_hex, "receive", peer,
                             session->total_bytes, session->files.size(),
                             session->destination_root));
}

void Engine::run_data_channel(TlsConnection& tls, Frame attach_frame) {
    std::string error;
    Reader reader(attach_frame.payload.data(), attach_frame.payload.size());
    std::array<uint8_t, 32> token{};
    if (!reader.bytes(token.data(), token.size()) || reader.remaining() != 0) {
        Frame ack;
        ack.type = FrameType::AttachAck;
        ack.transfer_id = attach_frame.transfer_id;
        Writer writer;
        writer.u8(0);
        writer.string("malformed attach");
        ack.payload = writer.take();
        std::string ignored;
        send_frame(tls, ack, ignored);
        return;
    }

    std::shared_ptr<IncomingSession> session;
    {
        std::lock_guard<std::mutex> lock(incoming_mutex_);
        const auto it = incoming_tokens_.find(hex_encode(token.data(), token.size()));
        if (it != incoming_tokens_.end()) session = it->second;
    }
    if (!session) {
        Frame ack;
        ack.type = FrameType::AttachAck;
        ack.transfer_id = attach_frame.transfer_id;
        Writer writer;
        writer.u8(0);
        writer.string("unknown transfer token");
        ack.payload = writer.take();
        std::string ignored;
        send_frame(tls, ack, ignored);
        return;
    }

    {
        Frame ack;
        ack.type = FrameType::AttachAck;
        ack.transfer_id = attach_frame.transfer_id;
        Writer writer;
        writer.u8(1);
        writer.string("");
        ack.payload = writer.take();
        if (!send_frame(tls, ack, error)) return;
    }
    session->active_data_channels.fetch_add(1);
    tls.socket().set_timeouts(30000, 30000);

    while (running_.load() && !session->cancel.load() && !session->failed.load()) {
        Frame frame;
        if (!recv_frame(tls, frame, error)) {
            if (error != "peer closed the connection") {
                set_failure(session->failed, session->error_mutex, session->error,
                            error.empty() ? "data channel failed" : error);
                session->state_cv.notify_all();
            }
            break;
        }
        if (frame.type == FrameType::Ping) continue;
        if (frame.type != FrameType::Data) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "unexpected frame on data channel");
            session->state_cv.notify_all();
            break;
        }

        Reader data_reader(frame.payload.data(), frame.payload.size());
        uint32_t file_index = 0;
        uint64_t offset = 0;
        uint8_t expected_hash[32]{};
        size_t data_size = 0;
        if (!decode_data_header(data_reader, file_index, offset, expected_hash, data_size)) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "malformed data frame");
            session->state_cv.notify_all();
            break;
        }
        if (file_index >= session->files.size()) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "invalid file index");
            session->state_cv.notify_all();
            break;
        }
        const auto& file = session->files[file_index];
        if (file->manifest.size == 0 || offset % kChunkSize != 0 ||
            offset >= file->manifest.size) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "invalid data offset");
            session->state_cv.notify_all();
            break;
        }
        const uint64_t expected_size =
            std::min<uint64_t>(kChunkSize, file->manifest.size - offset);
        if (data_size != expected_size) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "invalid chunk length");
            session->state_cv.notify_all();
            break;
        }
        const auto computed = sha256_bytes(data_reader.current(), data_size);
        if (std::memcmp(computed.data(), expected_hash, 32) != 0) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "chunk integrity check failed");
            session->state_cv.notify_all();
            break;
        }

        const uint64_t chunk_index = offset / kChunkSize;
        bool duplicate = false;
        bool duplicate_mismatch = false;
        {
            std::lock_guard<std::mutex> lock(file->mutex);
            if (chunk_index < file->chunk_received.size() &&
                file->chunk_received[static_cast<size_t>(chunk_index)]) {
                duplicate = true;
                duplicate_mismatch =
                    std::memcmp(file->chunk_hashes[static_cast<size_t>(chunk_index)].data(),
                                expected_hash, 32) != 0;
            }
        }
        if (duplicate_mismatch) {
            set_failure(session->failed, session->error_mutex, session->error,
                        "duplicate chunk does not match");
            session->state_cv.notify_all();
            break;
        }
        if (duplicate) continue;

        if (!write_at(file->part_path, data_reader.current(), data_size, offset, error)) {
            set_failure(session->failed, session->error_mutex, session->error, error);
            session->state_cv.notify_all();
            break;
        }

        bool counted = false;
        {
            std::lock_guard<std::mutex> lock(file->mutex);
            if (chunk_index >= file->chunk_received.size()) {
                set_failure(session->failed, session->error_mutex, session->error,
                            "chunk index out of range");
                session->state_cv.notify_all();
                break;
            }
            if (!file->chunk_received[static_cast<size_t>(chunk_index)]) {
                file->chunk_received[static_cast<size_t>(chunk_index)] = 1;
                std::memcpy(file->chunk_hashes[static_cast<size_t>(chunk_index)].data(),
                            expected_hash, 32);
                ++file->chunks_completed;
                counted = true;
                if (file->chunks_completed == file->manifest.chunk_count()) {
                    session->files_done.fetch_add(1);
                }
            }
        }
        if (counted) session->bytes_done.fetch_add(data_size);
        incoming_progress(session, false);
    }

    session->active_data_channels.fetch_sub(1);
    session->state_cv.notify_all();
}

} // namespace wisp

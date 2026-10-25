#include "engine.hpp"
#include "engine_internal.hpp"
#include "file_io.hpp"

#include <algorithm>
#include <chrono>
#include <thread>

namespace wisp {

void Engine::remove_outgoing(const std::string& transfer_id) {
    std::lock_guard<std::mutex> lock(outgoing_mutex_);
    outgoing_.erase(transfer_id);
}

void Engine::run_sender(const std::shared_ptr<OutgoingSession>& session) {
    const std::string direction = "send";
    auto cleanup = [&] { remove_outgoing(session->transfer_id_hex); };
    auto fail = [&](const std::string& message) {
        set_failure(session->failed, session->error_mutex, session->error, message);
        emit(WISP_EVT_ERROR,
             error_event_json(session->transfer_id_hex, direction, session->peer, message));
    };

    try {
        emit(WISP_EVT_PROGRESS,
             progress_event_json(session->transfer_id_hex, direction, session->peer,
                                 "preparing", 0, session->total_bytes, 0,
                                 static_cast<uint32_t>(session->files.size()), 0));

        std::string error;
        Socket socket;
        if (!connect_tcp(session->peer.host, session->peer.port, 8000, socket, error)) {
            fail(error);
            cleanup();
            return;
        }
        TlsConnection control;
        if (!control.connect(std::move(socket), identity_.client_context(),
                             session->peer.host, error)) {
            fail(error);
            cleanup();
            return;
        }
        control.socket().set_timeouts(300000, 30000);

        const std::string actual_fingerprint = peer_certificate_fingerprint(control.ssl());
        if (!session->peer.fingerprint.empty() &&
            actual_fingerprint != session->peer.fingerprint) {
            fail("peer identity changed; refusing to send");
            cleanup();
            return;
        }
        const std::string pairing = pairing_code(control.ssl());

        {
            Frame hello;
            hello.type = FrameType::Hello;
            hello.payload = encode_hello(config_.name, platform_name());
            if (!send_frame(control, hello, error)) {
                fail(error);
                cleanup();
                return;
            }
        }
        {
            Frame ack;
            if (!recv_frame(control, ack, error) || ack.type != FrameType::HelloAck) {
                fail(error.empty() ? "peer did not acknowledge the connection" : error);
                cleanup();
                return;
            }
            std::string peer_name;
            std::string peer_platform;
            std::string decode_error;
            decode_hello(ack.payload, peer_name, peer_platform, decode_error);
            if (!peer_name.empty()) session->peer.name = peer_name;
        }
        {
            std::ostringstream json;
            json << "{\"transfer_id\":\"" << session->transfer_id_hex
                 << "\",\"peer_id\":\"" << json_escape(session->peer.id)
                 << "\",\"peer_name\":\"" << json_escape(session->peer.name)
                 << "\",\"code\":\"" << json_escape(pairing)
                 << "\",\"direction\":\"send\"}";
            emit(WISP_EVT_PAIRING, json.str());
        }

        {
            Frame offer;
            offer.type = FrameType::Offer;
            offer.transfer_id = session->transfer_id;
            offer.payload = encode_offer(session->files, session->total_bytes);
            if (!send_frame(control, offer, error)) {
                fail(error);
                cleanup();
                return;
            }
        }
        emit(WISP_EVT_PROGRESS,
             progress_event_json(session->transfer_id_hex, direction, session->peer,
                                 "waiting", 0, session->total_bytes, 0,
                                 static_cast<uint32_t>(session->files.size()), 0));

        Frame response;
        if (!recv_frame(control, response, error)) {
            fail(error.empty() ? "peer did not answer the offer" : error);
            cleanup();
            return;
        }
        if (response.type == FrameType::Error) {
            uint32_t code = 0;
            std::string message;
            decode_error(response.payload, code, message);
            fail(message.empty() ? "peer rejected the transfer" : message);
            cleanup();
            return;
        }
        if (response.type != FrameType::OfferResponse) {
            fail("unexpected response to offer");
            cleanup();
            return;
        }
        bool accepted = false;
        std::string response_message;
        std::array<uint8_t, 32> token{};
        if (!decode_offer_response(response.payload, accepted, response_message, token.data(),
                                   error)) {
            fail(error);
            cleanup();
            return;
        }
        if (!accepted) {
            fail(response_message.empty() ? "transfer declined" : response_message);
            cleanup();
            return;
        }

        std::atomic<bool> keepalive_stop{false};
        std::thread keepalive;
        if (session->total_chunks > 0) {
            keepalive = std::thread([&] {
                while (!keepalive_stop.load() && !session->failed.load() &&
                       !session->cancel.load()) {
                    for (int i = 0; i < 20 && !keepalive_stop.load(); ++i) {
                        std::this_thread::sleep_for(std::chrono::milliseconds(100));
                    }
                    if (keepalive_stop.load() || session->failed.load() ||
                        session->cancel.load()) {
                        break;
                    }
                    Frame ping;
                    ping.type = FrameType::Ping;
                    ping.transfer_id = session->transfer_id;
                    std::string ping_error;
                    if (!send_frame(control, ping, ping_error)) {
                        set_failure(session->failed, session->error_mutex, session->error,
                                    "control connection lost");
                        break;
                    }
                }
            });
        }

        const uint64_t channel_count = std::min<uint64_t>(
            static_cast<uint64_t>(config_.data_channels),
            std::max<uint64_t>(1, session->total_chunks));
        std::vector<std::thread> workers;
        for (uint64_t channel = 0; channel < channel_count && session->total_chunks > 0;
             ++channel) {
            workers.emplace_back([this, session, token] {
                try {
                    std::string error;
                    Socket socket;
                    if (!connect_tcp(session->peer.host, session->peer.port, 8000,
                                     socket, error)) {
                        set_failure(session->failed, session->error_mutex,
                                    session->error, error);
                        return;
                    }
                    TlsConnection data;
                    if (!data.connect(std::move(socket), identity_.client_context(),
                                      session->peer.host, error)) {
                        set_failure(session->failed, session->error_mutex,
                                    session->error, error);
                        return;
                    }
                    const std::string actual =
                        peer_certificate_fingerprint(data.ssl());
                    if (!session->peer.fingerprint.empty() &&
                        actual != session->peer.fingerprint) {
                        set_failure(session->failed, session->error_mutex,
                                    session->error,
                                    "peer identity changed on data channel");
                        return;
                    }
                    {
                        Frame attach;
                        attach.type = FrameType::Attach;
                        attach.transfer_id = session->transfer_id;
                        Writer writer;
                        writer.bytes(token.data(), token.size());
                        attach.payload = writer.take();
                        if (!send_frame(data, attach, error)) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error, error);
                            return;
                        }
                    }
                    {
                        Frame ack;
                        if (!recv_frame(data, ack, error) ||
                            ack.type != FrameType::AttachAck) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error,
                                        error.empty() ? "data channel was not attached"
                                                      : error);
                            return;
                        }
                        Reader reader(ack.payload.data(), ack.payload.size());
                        uint8_t attached = 0;
                        std::string attach_error;
                        if (!reader.u8(attached) || !reader.string(attach_error, 512) ||
                            attached == 0) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error,
                                        attach_error.empty() ? "data channel rejected"
                                                             : attach_error);
                            return;
                        }
                    }

                    std::vector<uint8_t> buffer(kChunkSize);
                    while (running_.load() && !session->cancel.load() &&
                           !session->failed.load()) {
                        const uint32_t chunk =
                            session->next_chunk.fetch_add(1, std::memory_order_relaxed);
                        if (chunk >= session->total_chunks) break;
                        const auto upper = std::upper_bound(
                            session->chunk_starts.begin(), session->chunk_starts.end(),
                            static_cast<uint64_t>(chunk));
                        const size_t file_index = static_cast<size_t>(
                            std::distance(session->chunk_starts.begin(), upper) - 1);
                        if (file_index >= session->files.size()) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error, "internal chunk mapping error");
                            break;
                        }
                        const ManifestFile& file = session->files[file_index];
                        const uint64_t chunk_in_file =
                            static_cast<uint64_t>(chunk) -
                            session->chunk_starts[file_index];
                        const uint64_t offset = chunk_in_file * kChunkSize;
                        const size_t length = static_cast<size_t>(
                            std::min<uint64_t>(kChunkSize, file.size - offset));
                        size_t read = 0;
                        if (!read_at(path_from_utf8(file.source_path), buffer.data(),
                                     length, offset, read, error) ||
                            read != length) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error,
                                        error.empty()
                                            ? "source file changed during transfer"
                                            : error);
                            break;
                        }
                        const auto digest = sha256_bytes(buffer.data(), length);
                        Frame data_frame;
                        data_frame.type = FrameType::Data;
                        data_frame.transfer_id = session->transfer_id;
                        data_frame.payload = encode_data(
                            static_cast<uint32_t>(file_index), offset, digest.data(),
                            buffer.data(), length);
                        if (!send_frame(data, data_frame, error)) {
                            set_failure(session->failed, session->error_mutex,
                                        session->error, error);
                            break;
                        }
                        session->bytes_done.fetch_add(length);
                        {
                            std::lock_guard<std::mutex> lock(
                                session->file_counter_mutex);
                            if (++session->file_chunks_sent[file_index] ==
                                file.chunk_count()) {
                                session->files_done.fetch_add(1);
                            }
                        }
                        outgoing_progress(session, false);
                    }
                } catch (const std::exception& exception) {
                    set_failure(session->failed, session->error_mutex, session->error,
                                std::string("data channel exception: ") +
                                    exception.what());
                } catch (...) {
                    set_failure(session->failed, session->error_mutex, session->error,
                                "data channel exception");
                }
            });
        }

        for (auto& worker : workers) {
            if (worker.joinable()) worker.join();
        }
        keepalive_stop.store(true);
        if (keepalive.joinable()) keepalive.join();

        if (session->cancel.load() || !running_.load()) {
            Frame cancel;
            cancel.type = FrameType::Cancel;
            cancel.transfer_id = session->transfer_id;
            cancel.payload = encode_message("cancelled by sender");
            send_frame(control, cancel, error);
            emit(WISP_EVT_CANCELLED,
                 error_event_json(session->transfer_id_hex, direction, session->peer,
                                  "Transfer cancelled"));
            cleanup();
            return;
        }
        if (session->failed.load()) {
            std::string message;
            {
                std::lock_guard<std::mutex> lock(session->error_mutex);
                message = session->error;
            }
            if (message.empty()) message = "transfer failed";
            send_error_frame(control, session->transfer_id, message);
            fail(message);
            cleanup();
            return;
        }

        {
            Frame finish;
            finish.type = FrameType::Finish;
            finish.transfer_id = session->transfer_id;
            if (!send_frame(control, finish, error)) {
                fail(error);
                cleanup();
                return;
            }
        }
        Frame done;
        if (!recv_frame(control, done, error) || done.type != FrameType::Done) {
            fail(error.empty() ? "receiver did not confirm the transfer" : error);
            cleanup();
            return;
        }
        bool success = false;
        std::string done_message;
        if (!decode_done(done.payload, success, done_message) || !success) {
            fail(done_message.empty() ? "receiver could not finalize the files"
                                      : done_message);
            cleanup();
            return;
        }

        outgoing_progress(session, true);
        log(WISP_LOG_INFO, "sent " + std::to_string(session->files.size()) +
                               " file(s) to " + session->peer.name);
        emit(WISP_EVT_COMPLETE,
             complete_event_json(session->transfer_id_hex, direction, session->peer,
                                 session->total_bytes, session->files.size(), ""));
        cleanup();
    } catch (const std::exception& exception) {
        fail(std::string("transfer exception: ") + exception.what());
        cleanup();
    } catch (...) {
        fail("transfer exception");
        cleanup();
    }
}

} // namespace wisp

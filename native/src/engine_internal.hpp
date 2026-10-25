#pragma once

#include "discovery.hpp"
#include "net.hpp"
#include "protocol.hpp"
#include "util.hpp"
#include "wisp/c_api.h"

#include <iomanip>
#include <sstream>

namespace wisp {

inline std::string platform_name() {
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

inline std::string transfer_id_hex(uint64_t value) {
    std::ostringstream stream;
    stream << std::hex << std::setfill('0') << std::setw(16) << value;
    return stream.str();
}

inline double transfer_speed(uint64_t bytes, uint64_t started_ms) {
    const uint64_t elapsed = now_ms() > started_ms ? now_ms() - started_ms : 0;
    if (elapsed == 0) return 0;
    return static_cast<double>(bytes) * 1000.0 / static_cast<double>(elapsed);
}

inline std::string progress_event_json(const std::string& transfer_id,
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

inline std::string error_event_json(const std::string& transfer_id,
                                    const std::string& direction,
                                    const PeerInfo& peer,
                                    const std::string& message) {
    std::ostringstream json;
    json << "{\"transfer_id\":\"" << transfer_id
         << "\",\"direction\":\"" << direction
         << "\",\"peer_id\":\"" << json_escape(peer.id)
         << "\",\"peer_name\":\"" << json_escape(peer.name)
         << "\",\"message\":\"" << json_escape(message)
         << "\",\"code\":1}";
    return json.str();
}

inline std::string complete_event_json(const std::string& transfer_id,
                                       const std::string& direction,
                                       const PeerInfo& peer,
                                       uint64_t bytes,
                                       size_t files,
                                       const std::string& saved_path) {
    std::ostringstream json;
    json << "{\"transfer_id\":\"" << transfer_id
         << "\",\"direction\":\"" << direction
         << "\",\"peer_id\":\"" << json_escape(peer.id)
         << "\",\"peer_name\":\"" << json_escape(peer.name)
         << "\",\"bytes_total\":" << bytes
         << ",\"files_total\":" << files;
    if (!saved_path.empty()) {
        json << ",\"saved_path\":\"" << json_escape(saved_path) << "\"";
    }
    json << "}";
    return json.str();
}

inline std::vector<uint8_t> encode_done(bool ok, const std::string& message) {
    Writer writer;
    writer.u8(ok ? 1 : 0);
    writer.string(message);
    return writer.take();
}

inline bool decode_done(const std::vector<uint8_t>& payload, bool& ok, std::string& message) {
    Reader reader(payload.data(), payload.size());
    uint8_t value = 0;
    if (!reader.u8(value) || !reader.string(message, 2048)) return false;
    ok = value != 0;
    return true;
}

inline std::vector<uint8_t> encode_message(const std::string& message) {
    Writer writer;
    writer.string(message);
    return writer.take();
}

inline std::string decode_message(const std::vector<uint8_t>& payload) {
    Reader reader(payload.data(), payload.size());
    std::string message;
    if (!reader.string(message, 2048)) return {};
    return message;
}

inline void send_error_frame(TlsConnection& tls, uint64_t transfer_id,
                             const std::string& message) {
    Frame frame;
    frame.type = FrameType::Error;
    frame.transfer_id = transfer_id;
    frame.payload = encode_error(1, message);
    std::string ignored;
    send_frame(tls, frame, ignored);
}

inline void set_failure(std::atomic<bool>& failed, std::mutex& mutex,
                        std::string& target, const std::string& message) {
    std::lock_guard<std::mutex> lock(mutex);
    if (target.empty()) target = message;
    failed.store(true);
}

} // namespace wisp

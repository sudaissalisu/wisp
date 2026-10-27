#include "protocol.hpp"

#include <cstring>
#include <set>

namespace wisp {

static constexpr uint32_t kFrameMagic = 0x57535031u; // WSP1
static constexpr uint16_t kProtocolVersion = 1;

bool send_frame(TlsConnection& connection, const Frame& frame, std::string& error) {
    if (frame.payload.size() > kMaxFramePayload) {
        error = "frame is too large";
        return false;
    }
    Writer header;
    header.u32(kFrameMagic);
    header.u16(static_cast<uint16_t>(frame.type));
    header.u16(frame.flags);
    header.u32(static_cast<uint32_t>(frame.payload.size()));
    header.u64(frame.transfer_id);
    if (!connection.send_all(header.data().data(), header.data().size(), error)) return false;
    if (!frame.payload.empty() &&
        !connection.send_all(frame.payload.data(), frame.payload.size(), error)) {
        return false;
    }
    return true;
}

bool recv_frame(TlsConnection& connection, Frame& frame, std::string& error) {
    uint8_t header[20];
    if (!connection.recv_all(header, sizeof(header), error)) return false;
    Reader reader(header, sizeof(header));
    uint32_t magic = 0;
    uint16_t type = 0;
    uint32_t length = 0;
    if (!reader.u32(magic) || !reader.u16(type) || !reader.u16(frame.flags) ||
        !reader.u32(length) || !reader.u64(frame.transfer_id)) {
        error = "malformed frame header";
        return false;
    }
    if (magic != kFrameMagic) {
        error = "not a Wisp connection";
        return false;
    }
    if (type < static_cast<uint16_t>(FrameType::Hello) ||
        type > static_cast<uint16_t>(FrameType::Pong)) {
        error = "unknown frame type";
        return false;
    }
    if (length > kMaxFramePayload) {
        error = "frame exceeds size limit";
        return false;
    }
    frame.type = static_cast<FrameType>(type);
    frame.payload.resize(length);
    if (length != 0 && !connection.recv_all(frame.payload.data(), length, error)) {
        return false;
    }
    return true;
}

std::vector<uint8_t> encode_hello(const std::string& name, const std::string& platform,
                                  const std::string& link_token) {
    Writer writer;
    writer.u16(kProtocolVersion);
    writer.string(name);
    writer.string(platform);
    if (!link_token.empty()) writer.string(link_token);
    return writer.take();
}

bool decode_hello(const std::vector<uint8_t>& payload,
                  std::string& name, std::string& platform, std::string& link_token,
                  std::string& error) {
    Reader reader(payload.data(), payload.size());
    uint16_t version = 0;
    link_token.clear();
    if (!reader.u16(version) || !reader.string(name, 512) || !reader.string(platform, 128)) {
        error = "malformed hello";
        return false;
    }
    if (version != kProtocolVersion) {
        error = "protocol version mismatch";
        return false;
    }
    if (reader.remaining() != 0 && !reader.string(link_token, 128)) {
        error = "malformed hello link token";
        return false;
    }
    if (reader.remaining() != 0) {
        error = "unexpected hello data";
        return false;
    }
    return true;
}

std::vector<uint8_t> encode_offer(const std::vector<ManifestFile>& files, uint64_t total_bytes) {
    Writer writer;
    writer.u32(static_cast<uint32_t>(files.size()));
    writer.u64(total_bytes);
    for (const ManifestFile& file : files) {
        writer.string(file.relative_path);
        writer.u64(file.size);
        writer.u64(file.mtime);
    }
    return writer.take();
}

bool decode_offer(const std::vector<uint8_t>& payload,
                  std::vector<ManifestFile>& files, uint64_t& total_bytes,
                  std::string& error) {
    files.clear();
    total_bytes = 0;
    Reader reader(payload.data(), payload.size());
    uint32_t count = 0;
    if (!reader.u32(count) || !reader.u64(total_bytes)) {
        error = "malformed offer";
        return false;
    }
    if (count == 0 || count > kMaxFiles) {
        error = "invalid file count";
        return false;
    }
    if (total_bytes > kMaxTransferBytes) {
        error = "transfer exceeds size limit";
        return false;
    }
    std::set<std::string> seen;
    uint64_t sum = 0;
    files.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        ManifestFile file;
        std::string raw_path;
        if (!reader.string(raw_path, kMaxPathBytes) ||
            !reader.u64(file.size) || !reader.u64(file.mtime)) {
            error = "malformed file entry";
            return false;
        }
        if (file.size > kMaxTransferBytes) {
            error = "file exceeds size limit";
            return false;
        }
        if (!valid_relative_path(raw_path, file.relative_path, error)) {
            return false;
        }
        if (!seen.insert(to_lower_ascii(file.relative_path)).second) {
            error = "duplicate file path in offer";
            return false;
        }
        sum += file.size;
        if (sum > kMaxTransferBytes) {
            error = "transfer exceeds size limit";
            return false;
        }
        files.push_back(std::move(file));
    }
    if (reader.remaining() != 0) {
        error = "unexpected offer data";
        return false;
    }
    if (sum != total_bytes) {
        error = "offer size mismatch";
        return false;
    }
    return true;
}

std::vector<uint8_t> encode_offer_response(bool accepted, const std::string& message,
                                           const uint8_t token[32]) {
    Writer writer;
    writer.u8(accepted ? 1 : 0);
    writer.string(message);
    if (accepted) writer.bytes(token, 32);
    return writer.take();
}

bool decode_offer_response(const std::vector<uint8_t>& payload,
                           bool& accepted, std::string& message, uint8_t token[32],
                           std::string& error) {
    Reader reader(payload.data(), payload.size());
    uint8_t value = 0;
    if (!reader.u8(value) || !reader.string(message, 2048)) {
        error = "malformed offer response";
        return false;
    }
    accepted = value != 0;
    std::memset(token, 0, 32);
    if (accepted && !reader.bytes(token, 32)) {
        error = "offer response is missing a session token";
        return false;
    }
    if (reader.remaining() != 0) {
        error = "unexpected offer response data";
        return false;
    }
    return true;
}

std::vector<uint8_t> encode_data(uint32_t file_index, uint64_t offset,
                                 const uint8_t hash[32], const void* data, size_t size) {
    Writer writer;
    writer.u32(file_index);
    writer.u64(offset);
    writer.bytes(hash, 32);
    writer.bytes(data, size);
    return writer.take();
}

bool decode_data_header(Reader& reader, uint32_t& file_index, uint64_t& offset,
                        uint8_t hash[32], size_t& data_available) {
    if (!reader.u32(file_index) || !reader.u64(offset) || !reader.bytes(hash, 32)) {
        return false;
    }
    data_available = reader.remaining();
    return true;
}

std::vector<uint8_t> encode_error(uint32_t code, const std::string& message) {
    Writer writer;
    writer.u32(code);
    writer.string(message);
    return writer.take();
}

bool decode_error(const std::vector<uint8_t>& payload, uint32_t& code, std::string& message) {
    Reader reader(payload.data(), payload.size());
    return reader.u32(code) && reader.string(message, 2048);
}

} // namespace wisp

#pragma once

#include "net.hpp"
#include "util.hpp"

#include <cstdint>
#include <string>
#include <vector>

namespace wisp {

enum class FrameType : uint16_t {
    Hello = 1,
    HelloAck = 2,
    Offer = 3,
    OfferResponse = 4,
    Attach = 5,
    AttachAck = 6,
    Data = 7,
    Finish = 8,
    Done = 9,
    Error = 10,
    Cancel = 11,
    Ping = 12,
    Pong = 13
};

struct Frame {
    FrameType type = FrameType::Hello;
    uint16_t flags = 0;
    uint64_t transfer_id = 0;
    std::vector<uint8_t> payload;
};

class Writer {
public:
    void u8(uint8_t value) { data_.push_back(value); }
    void u16(uint16_t value) {
        data_.push_back(static_cast<uint8_t>(value >> 8));
        data_.push_back(static_cast<uint8_t>(value));
    }
    void u32(uint32_t value) {
        data_.push_back(static_cast<uint8_t>(value >> 24));
        data_.push_back(static_cast<uint8_t>(value >> 16));
        data_.push_back(static_cast<uint8_t>(value >> 8));
        data_.push_back(static_cast<uint8_t>(value));
    }
    void u64(uint64_t value) {
        for (int shift = 56; shift >= 0; shift -= 8) {
            data_.push_back(static_cast<uint8_t>(value >> shift));
        }
    }
    void string(const std::string& value) {
        u32(static_cast<uint32_t>(value.size()));
        data_.insert(data_.end(), value.begin(), value.end());
    }
    void bytes(const void* data, size_t size) {
        const auto* start = static_cast<const uint8_t*>(data);
        data_.insert(data_.end(), start, start + size);
    }
    const std::vector<uint8_t>& data() const { return data_; }
    std::vector<uint8_t> take() { return std::move(data_); }

private:
    std::vector<uint8_t> data_;
};

class Reader {
public:
    Reader(const uint8_t* data, size_t size) : data_(data), size_(size) {}

    bool u8(uint8_t& value) {
        if (offset_ + 1 > size_) return false;
        value = data_[offset_++];
        return true;
    }
    bool u16(uint16_t& value) {
        if (offset_ + 2 > size_) return false;
        value = static_cast<uint16_t>((static_cast<uint16_t>(data_[offset_]) << 8) |
                                      data_[offset_ + 1]);
        offset_ += 2;
        return true;
    }
    bool u32(uint32_t& value) {
        if (offset_ + 4 > size_) return false;
        value = (static_cast<uint32_t>(data_[offset_]) << 24) |
                (static_cast<uint32_t>(data_[offset_ + 1]) << 16) |
                (static_cast<uint32_t>(data_[offset_ + 2]) << 8) |
                data_[offset_ + 3];
        offset_ += 4;
        return true;
    }
    bool u64(uint64_t& value) {
        if (offset_ + 8 > size_) return false;
        value = 0;
        for (int i = 0; i < 8; ++i) {
            value = (value << 8) | data_[offset_ + static_cast<size_t>(i)];
        }
        offset_ += 8;
        return true;
    }
    bool string(std::string& value, size_t max_length = 65535) {
        uint32_t length = 0;
        if (!u32(length) || length > max_length || offset_ + length > size_) return false;
        value.assign(reinterpret_cast<const char*>(data_ + offset_), length);
        offset_ += length;
        return true;
    }
    bool bytes(void* out, size_t count) {
        if (offset_ + count > size_) return false;
        std::memcpy(out, data_ + offset_, count);
        offset_ += count;
        return true;
    }
    bool skip(size_t count) {
        if (offset_ + count > size_) return false;
        offset_ += count;
        return true;
    }
    const uint8_t* current() const { return data_ + offset_; }
    size_t remaining() const { return size_ - offset_; }
    size_t offset() const { return offset_; }

private:
    const uint8_t* data_;
    size_t size_;
    size_t offset_ = 0;
};

bool send_frame(TlsConnection& connection, const Frame& frame, std::string& error);
bool recv_frame(TlsConnection& connection, Frame& frame, std::string& error);

std::vector<uint8_t> encode_hello(const std::string& name, const std::string& platform);
bool decode_hello(const std::vector<uint8_t>& payload,
                  std::string& name, std::string& platform, std::string& error);

std::vector<uint8_t> encode_offer(const std::vector<ManifestFile>& files, uint64_t total_bytes);
bool decode_offer(const std::vector<uint8_t>& payload,
                  std::vector<ManifestFile>& files, uint64_t& total_bytes,
                  std::string& error);

std::vector<uint8_t> encode_offer_response(bool accepted, const std::string& message,
                                           const uint8_t token[32]);
bool decode_offer_response(const std::vector<uint8_t>& payload,
                           bool& accepted, std::string& message, uint8_t token[32],
                           std::string& error);

std::vector<uint8_t> encode_data(uint32_t file_index, uint64_t offset,
                                 const uint8_t hash[32], const void* data, size_t size);
bool decode_data_header(Reader& reader, uint32_t& file_index, uint64_t& offset,
                        uint8_t hash[32], size_t& data_available);

std::vector<uint8_t> encode_error(uint32_t code, const std::string& message);
bool decode_error(const std::vector<uint8_t>& payload, uint32_t& code, std::string& message);

} // namespace wisp

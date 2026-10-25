#pragma once

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

namespace wisp {

constexpr uint32_t kMaxFramePayload = 8u * 1024u * 1024u;
constexpr uint32_t kChunkSize = 4u * 1024u * 1024u;
constexpr uint32_t kMaxFiles = 100000;
constexpr uint64_t kMaxTransferBytes = 2ull * 1024ull * 1024ull * 1024ull * 1024ull;
constexpr uint32_t kMaxPathBytes = 4096;
constexpr size_t kMaxManifestBytes = 24u * 1024u * 1024u;

struct ManifestFile {
    std::string source_path;
    std::string relative_path;
    uint64_t size = 0;
    uint64_t mtime = 0;

    uint64_t chunk_count() const {
        if (size == 0) return 0;
        return (size + kChunkSize - 1) / kChunkSize;
    }
};

uint64_t now_ms();
uint64_t random_u64();
std::string random_hex(size_t byte_count);
bool random_bytes(void* out, size_t count);

std::string hex_encode(const void* data, size_t count);
std::string to_lower_ascii(std::string value);
bool starts_with(const std::string& value, const std::string& prefix);
bool ends_with(const std::string& value, const std::string& suffix);
std::string trim_ascii(const std::string& value);
std::string json_escape(const std::string& value);
std::string percent_encode(const std::string& value);
bool percent_decode(const std::string& value, std::string& out);
std::vector<std::string> split(const std::string& value, char delimiter);

std::array<uint8_t, 32> sha256_bytes(const void* data, size_t count);
std::string sha256_hex(const void* data, size_t count);
bool sha256_file(const std::filesystem::path& path, std::array<uint8_t, 32>& out);

bool valid_relative_path(const std::string& raw, std::string& normalized, std::string& error);
bool collect_manifest(const std::vector<std::string>& inputs,
                      std::vector<ManifestFile>& out,
                      std::string& error);

std::string path_to_utf8(const std::filesystem::path& path);
std::filesystem::path path_from_utf8(const std::string& value);
std::string human_peer_id(const std::string& fingerprint);

std::string format_speed(double bytes_per_second);
std::string format_bytes(uint64_t bytes);


} // namespace wisp

#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

namespace wisp {

bool ensure_directory(const std::filesystem::path& path, std::string& error);
bool prepare_file(const std::filesystem::path& path, uint64_t size, std::string& error);
bool write_at(const std::filesystem::path& path, const void* data, size_t size,
              uint64_t offset, std::string& error);
bool read_at(const std::filesystem::path& path, void* data, size_t size,
             uint64_t offset, size_t& bytes_read, std::string& error);
bool flush_file(const std::filesystem::path& path, std::string& error);
bool replace_file(const std::filesystem::path& from, const std::filesystem::path& to,
                  std::string& error);
bool remove_file_quiet(const std::filesystem::path& path);
bool path_exists(const std::filesystem::path& path);

} // namespace wisp

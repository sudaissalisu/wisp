#include "util.hpp"

#include <openssl/evp.h>
#include <openssl/rand.h>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <iomanip>
#include <random>
#include <set>
#include <sstream>
#include <stdexcept>

namespace fs = std::filesystem;

namespace wisp {

uint64_t now_ms() {
    using namespace std::chrono;
    return static_cast<uint64_t>(
        duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count());
}

uint64_t random_u64() {
    uint64_t value = 0;
    if (!random_bytes(&value, sizeof(value))) {
        value = now_ms() ^ (static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&value)) << 21);
    }
    return value;
}

bool random_bytes(void* out, size_t count) {
    if (count == 0) return true;
    return RAND_bytes(static_cast<unsigned char*>(out), static_cast<int>(count)) == 1;
}

std::string random_hex(size_t byte_count) {
    std::vector<uint8_t> bytes(byte_count);
    if (!random_bytes(bytes.data(), bytes.size())) {
        for (size_t i = 0; i < bytes.size(); ++i) {
            bytes[i] = static_cast<uint8_t>(random_u64() & 0xff);
        }
    }
    return hex_encode(bytes.data(), bytes.size());
}

std::string hex_encode(const void* data, size_t count) {
    static const char* digits = "0123456789abcdef";
    const auto* bytes = static_cast<const uint8_t*>(data);
    std::string out;
    out.resize(count * 2);
    for (size_t i = 0; i < count; ++i) {
        out[i * 2] = digits[bytes[i] >> 4];
        out[i * 2 + 1] = digits[bytes[i] & 0x0f];
    }
    return out;
}

std::string to_lower_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

bool starts_with(const std::string& value, const std::string& prefix) {
    return value.size() >= prefix.size() &&
           value.compare(0, prefix.size(), prefix) == 0;
}

bool ends_with(const std::string& value, const std::string& suffix) {
    return value.size() >= suffix.size() &&
           value.compare(value.size() - suffix.size(), suffix.size(), suffix) == 0;
}

std::string trim_ascii(const std::string& value) {
    size_t begin = 0;
    size_t end = value.size();
    while (begin < end && std::isspace(static_cast<unsigned char>(value[begin]))) ++begin;
    while (end > begin && std::isspace(static_cast<unsigned char>(value[end - 1]))) --end;
    return value.substr(begin, end - begin);
}

std::string json_escape(const std::string& value) {
    std::string out;
    out.reserve(value.size() + 8);
    for (unsigned char c : value) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) {
                char buffer[8];
                std::snprintf(buffer, sizeof(buffer), "\\u%04x", c);
                out += buffer;
            } else {
                out.push_back(static_cast<char>(c));
            }
        }
    }
    return out;
}

std::vector<std::string> split(const std::string& value, char delimiter) {
    std::vector<std::string> out;
    size_t start = 0;
    while (true) {
        const size_t pos = value.find(delimiter, start);
        if (pos == std::string::npos) {
            out.push_back(value.substr(start));
            break;
        }
        out.push_back(value.substr(start, pos - start));
        start = pos + 1;
    }
    return out;
}

std::string percent_encode(const std::string& value) {
    static const char* digits = "0123456789ABCDEF";
    std::string out;
    out.reserve(value.size());
    for (unsigned char c : value) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') {
            out.push_back(static_cast<char>(c));
        } else {
            out.push_back('%');
            out.push_back(digits[c >> 4]);
            out.push_back(digits[c & 0x0f]);
        }
    }
    return out;
}

static int hex_value(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

bool percent_decode(const std::string& value, std::string& out) {
    out.clear();
    out.reserve(value.size());
    for (size_t i = 0; i < value.size(); ++i) {
        if (value[i] != '%') {
            out.push_back(value[i]);
            continue;
        }
        if (i + 2 >= value.size()) return false;
        const int hi = hex_value(value[i + 1]);
        const int lo = hex_value(value[i + 2]);
        if (hi < 0 || lo < 0) return false;
        out.push_back(static_cast<char>((hi << 4) | lo));
        i += 2;
    }
    return true;
}

static std::array<uint8_t, 32> digest_sha256(const void* data, size_t count) {
    std::array<uint8_t, 32> out{};
    unsigned int length = 0;
    EVP_Digest(data, count, out.data(), &length, EVP_sha256(), nullptr);
    return out;
}

std::array<uint8_t, 32> sha256_bytes(const void* data, size_t count) {
    return digest_sha256(data, count);
}

std::string sha256_hex(const void* data, size_t count) {
    const auto digest = digest_sha256(data, count);
    return hex_encode(digest.data(), digest.size());
}

bool sha256_file(const fs::path& path, std::array<uint8_t, 32>& out) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return false;
    EVP_MD_CTX* ctx = EVP_MD_CTX_new();
    if (!ctx) return false;
    bool ok = EVP_DigestInit_ex(ctx, EVP_sha256(), nullptr) == 1;
    std::vector<char> buffer(1024 * 1024);
    while (ok && stream) {
        stream.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const std::streamsize count = stream.gcount();
        if (count > 0) {
            ok = EVP_DigestUpdate(ctx, buffer.data(), static_cast<size_t>(count)) == 1;
        }
    }
    unsigned int length = 0;
    if (ok) ok = EVP_DigestFinal_ex(ctx, out.data(), &length) == 1 && length == 32;
    EVP_MD_CTX_free(ctx);
    return ok;
}

std::string path_to_utf8(const fs::path& path) {
#if defined(_WIN32)
    const auto u8 = path.u8string();
    return std::string(reinterpret_cast<const char*>(u8.data()), u8.size());
#else
    return path.string();
#endif
}

fs::path path_from_utf8(const std::string& value) {
#if defined(_WIN32)
    return fs::u8path(value);
#else
    return fs::path(value);
#endif
}

std::string human_peer_id(const std::string& fingerprint) {
    return fingerprint.substr(0, std::min<size_t>(16, fingerprint.size()));
}

static bool is_reserved_component(const std::string& component_lower) {
    std::string stem = component_lower;
    const size_t dot = stem.find('.');
    if (dot != std::string::npos) stem = stem.substr(0, dot);
    static const std::set<std::string> reserved = {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
    return reserved.count(stem) != 0;
}

bool valid_relative_path(const std::string& raw, std::string& normalized, std::string& error) {
    if (raw.empty()) {
        error = "empty relative path";
        return false;
    }
    if (raw.size() > kMaxPathBytes) {
        error = "path is too long";
        return false;
    }
    std::string path = raw;
    std::replace(path.begin(), path.end(), '\\', '/');
    if (path.front() == '/') {
        error = "absolute paths are not allowed";
        return false;
    }
    if (path.find('\0') != std::string::npos) {
        error = "path contains a NUL byte";
        return false;
    }
    if (path.size() >= 2 && std::isalpha(static_cast<unsigned char>(path[0])) && path[1] == ':') {
        error = "drive-letter paths are not allowed";
        return false;
    }

    std::vector<std::string> components;
    for (const std::string& component : split(path, '/')) {
        if (component.empty() || component == "." || component == "..") {
            error = "path contains an invalid component";
            return false;
        }
        if (component.size() > 255) {
            error = "path component is too long";
            return false;
        }
        if (component.back() == ' ' || component.back() == '.') {
            error = "path component ends with a space or dot";
            return false;
        }
        for (unsigned char c : component) {
            if (c < 0x20 || c == '<' || c == '>' || c == ':' || c == '"' ||
                c == '|' || c == '?' || c == '*') {
                error = "path contains a reserved character";
                return false;
            }
        }
        if (is_reserved_component(to_lower_ascii(component))) {
            error = "path contains a reserved Windows name";
            return false;
        }
        components.push_back(component);
    }

    normalized.clear();
    for (size_t i = 0; i < components.size(); ++i) {
        if (i != 0) normalized.push_back('/');
        normalized += components[i];
    }
    return true;
}

static std::string make_unique_relative(const std::string& candidate,
                                        std::set<std::string>& used) {
    std::string normalized = candidate;
    std::replace(normalized.begin(), normalized.end(), '\\', '/');
    std::string key = to_lower_ascii(normalized);
    if (used.insert(key).second) return normalized;

    fs::path base = path_from_utf8(normalized);
    const std::string stem = path_to_utf8(base.stem());
    const std::string extension = path_to_utf8(base.extension());
    const std::string parent = path_to_utf8(base.parent_path());
    for (int n = 2; n < 10000; ++n) {
        std::string name = stem + " (" + std::to_string(n) + ")" + extension;
        fs::path next = base.parent_path() / path_from_utf8(name);
        std::string next_string = path_to_utf8(next);
        std::string next_key = to_lower_ascii(next_string);
        if (used.insert(next_key).second) return next_string;
    }
    return normalized;
}

bool collect_manifest(const std::vector<std::string>& inputs,
                      std::vector<ManifestFile>& out,
                      std::string& error) {
    out.clear();
    std::set<std::string> used;
    uint64_t total = 0;

    for (const std::string& input : inputs) {
        if (input.empty()) {
            error = "empty input path";
            return false;
        }
        std::error_code ec;
        const fs::path root = path_from_utf8(input);
        const fs::file_status root_status = fs::symlink_status(root, ec);
        if (ec || root_status.type() == fs::file_type::not_found) {
            error = "input path does not exist: " + input;
            return false;
        }
        if (fs::is_symlink(root_status)) {
            error = "symlink inputs are not supported: " + input;
            return false;
        }

        if (fs::is_regular_file(root_status)) {
            const uint64_t size = fs::file_size(root, ec);
            if (ec) {
                error = "cannot read file size: " + input;
                return false;
            }
            std::string relative = path_to_utf8(root.filename());
            std::string normalized;
            if (!valid_relative_path(relative, normalized, error)) {
                error = "invalid file name: " + input + " (" + error + ")";
                return false;
            }
            if (out.size() >= kMaxFiles) {
                error = "too many files";
                return false;
            }
            total += size;
            if (total > kMaxTransferBytes) {
                error = "transfer is too large";
                return false;
            }
            ManifestFile file;
            file.source_path = path_to_utf8(root);
            file.relative_path = make_unique_relative(normalized, used);
            file.size = size;
            file.mtime = static_cast<uint64_t>(
                fs::last_write_time(root, ec).time_since_epoch().count());
            out.push_back(std::move(file));
            continue;
        }

        if (!fs::is_directory(root_status)) {
            error = "unsupported input type: " + input;
            return false;
        }

        const std::string root_name = path_to_utf8(root.filename());
        if (root_name.empty()) {
            error = "cannot use a filesystem root as an input";
            return false;
        }
        std::string root_norm;
        if (!valid_relative_path(root_name, root_norm, error)) {
            error = "invalid folder name: " + root_name + " (" + error + ")";
            return false;
        }

        fs::recursive_directory_iterator iterator(
            root, fs::directory_options::skip_permission_denied, ec);
        fs::recursive_directory_iterator end;
        if (ec) {
            error = "cannot read folder: " + input;
            return false;
        }
        while (iterator != end) {
            if (ec) {
                ec.clear();
                iterator.increment(ec);
                continue;
            }
            const fs::file_status status = iterator->symlink_status(ec);
            if (!ec && fs::is_symlink(status)) {
                iterator.disable_recursion_pending();
            } else if (!ec && fs::is_regular_file(status)) {
                const fs::path file_path = iterator->path();
                const fs::path relative_path = fs::path(root_name) / fs::relative(file_path, root, ec);
                if (ec) {
                    error = "cannot compute relative path for: " + path_to_utf8(file_path);
                    return false;
                }
                std::string relative = path_to_utf8(relative_path);
                std::string normalized;
                if (!valid_relative_path(relative, normalized, error)) {
                    error = "invalid path in folder: " + relative + " (" + error + ")";
                    return false;
                }
                const uint64_t size = fs::file_size(file_path, ec);
                if (ec) {
                    error = "cannot read file size: " + path_to_utf8(file_path);
                    return false;
                }
                if (out.size() >= kMaxFiles) {
                    error = "too many files";
                    return false;
                }
                total += size;
                if (total > kMaxTransferBytes) {
                    error = "transfer is too large";
                    return false;
                }
                ManifestFile file;
                file.source_path = path_to_utf8(file_path);
                file.relative_path = make_unique_relative(normalized, used);
                file.size = size;
                file.mtime = static_cast<uint64_t>(
                    fs::last_write_time(file_path, ec).time_since_epoch().count());
                out.push_back(std::move(file));
            }
            iterator.increment(ec);
        }
    }

    if (out.empty()) {
        error = "nothing to send";
        return false;
    }
    std::sort(out.begin(), out.end(), [](const ManifestFile& a, const ManifestFile& b) {
        return a.relative_path < b.relative_path;
    });
    return true;
}

std::string format_bytes(uint64_t bytes) {
    const char* units[] = {"B", "KB", "MB", "GB", "TB"};
    double value = static_cast<double>(bytes);
    int unit = 0;
    while (value >= 1000.0 && unit < 4) {
        value /= 1024.0;
        ++unit;
    }
    char buffer[64];
    if (unit == 0) {
        std::snprintf(buffer, sizeof(buffer), "%.0f %s", value, units[unit]);
    } else {
        std::snprintf(buffer, sizeof(buffer), "%.1f %s", value, units[unit]);
    }
    return buffer;
}

std::string format_speed(double bytes_per_second) {
    if (bytes_per_second < 0) bytes_per_second = 0;
    return format_bytes(static_cast<uint64_t>(bytes_per_second)) + "/s";
}

} // namespace wisp

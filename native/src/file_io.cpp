#include "file_io.hpp"
#include "util.hpp"

#include <cerrno>
#include <system_error>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <fcntl.h>
#  include <sys/stat.h>
#  include <unistd.h>
#endif

namespace fs = std::filesystem;

namespace wisp {

static std::string fs_error(const fs::path& path, const std::string& action) {
    return action + ": " + path_to_utf8(path);
}

bool ensure_directory(const fs::path& path, std::string& error) {
    std::error_code ec;
    if (path.empty()) return true;
    fs::create_directories(path, ec);
    if (ec) {
        error = fs_error(path, "cannot create directory");
        return false;
    }
    return true;
}

#if defined(_WIN32)

static HANDLE open_win(const fs::path& path, DWORD access, DWORD creation) {
    return CreateFileW(path.wstring().c_str(), access,
                       FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, creation,
                       FILE_ATTRIBUTE_NORMAL, nullptr);
}

bool prepare_file(const fs::path& path, uint64_t size, std::string& error) {
    HANDLE handle = open_win(path, GENERIC_WRITE, CREATE_ALWAYS);
    if (handle == INVALID_HANDLE_VALUE) {
        error = fs_error(path, "cannot create file");
        return false;
    }
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(size);
    if (!SetFilePointerEx(handle, position, nullptr, FILE_BEGIN) || !SetEndOfFile(handle)) {
        CloseHandle(handle);
        error = fs_error(path, "cannot allocate file");
        return false;
    }
    CloseHandle(handle);
    return true;
}

bool write_at(const fs::path& path, const void* data, size_t size,
              uint64_t offset, std::string& error) {
    HANDLE handle = open_win(path, GENERIC_WRITE, OPEN_EXISTING);
    if (handle == INVALID_HANDLE_VALUE) {
        error = fs_error(path, "cannot open file for writing");
        return false;
    }
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(handle, position, nullptr, FILE_BEGIN)) {
        CloseHandle(handle);
        error = fs_error(path, "cannot seek in file");
        return false;
    }
    const auto* bytes = static_cast<const uint8_t*>(data);
    size_t written = 0;
    while (written < size) {
        DWORD piece = static_cast<DWORD>(
            std::min<size_t>(size - written, 1024ull * 1024ull * 1024ull));
        DWORD actual = 0;
        if (!WriteFile(handle, bytes + written, piece, &actual, nullptr) || actual == 0) {
            CloseHandle(handle);
            error = fs_error(path, "file write failed");
            return false;
        }
        written += actual;
    }
    CloseHandle(handle);
    return true;
}


bool read_at(const fs::path& path, void* data, size_t size,
             uint64_t offset, size_t& bytes_read, std::string& error) {
    bytes_read = 0;
    HANDLE handle = open_win(path, GENERIC_READ, OPEN_EXISTING);
    if (handle == INVALID_HANDLE_VALUE) {
        error = fs_error(path, "cannot open file for reading");
        return false;
    }
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(handle, position, nullptr, FILE_BEGIN)) {
        CloseHandle(handle);
        error = fs_error(path, "cannot seek in file");
        return false;
    }
    auto* bytes = static_cast<uint8_t*>(data);
    size_t total = 0;
    while (total < size) {
        DWORD piece = static_cast<DWORD>(
            std::min<size_t>(size - total, 1024ull * 1024ull * 1024ull));
        DWORD actual = 0;
        if (!ReadFile(handle, bytes + total, piece, &actual, nullptr)) {
            CloseHandle(handle);
            error = fs_error(path, "file read failed");
            return false;
        }
        total += actual;
        if (actual < piece) break;
    }
    CloseHandle(handle);
    bytes_read = total;
    return true;
}


bool flush_file(const fs::path& path, std::string& error) {
    HANDLE handle = open_win(path, GENERIC_WRITE, OPEN_EXISTING);
    if (handle == INVALID_HANDLE_VALUE) {
        error = fs_error(path, "cannot open file for flushing");
        return false;
    }
    const bool ok = FlushFileBuffers(handle) != 0;
    CloseHandle(handle);
    if (!ok) error = fs_error(path, "cannot flush file");
    return ok;
}

bool replace_file(const fs::path& from, const fs::path& to, std::string& error) {
    if (!MoveFileExW(from.wstring().c_str(), to.wstring().c_str(),
                     MOVEFILE_REPLACE_EXISTING)) {
        error = fs_error(to, "cannot finalize file");
        return false;
    }
    return true;
}

#else

bool prepare_file(const fs::path& path, uint64_t size, std::string& error) {
    const int fd = ::open(path.c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) {
        error = fs_error(path, "cannot create file");
        return false;
    }
    const bool ok = ftruncate(fd, static_cast<off_t>(size)) == 0;
    close(fd);
    if (!ok) error = fs_error(path, "cannot allocate file");
    return ok;
}

bool write_at(const fs::path& path, const void* data, size_t size,
              uint64_t offset, std::string& error) {
    const int fd = ::open(path.c_str(), O_WRONLY);
    if (fd < 0) {
        error = fs_error(path, "cannot open file for writing");
        return false;
    }
    const auto* bytes = static_cast<const uint8_t*>(data);
    size_t written = 0;
    while (written < size) {
        const ssize_t result = pwrite(fd, bytes + written, size - written,
                                     static_cast<off_t>(offset + written));
        if (result <= 0) {
            close(fd);
            error = fs_error(path, "file write failed");
            return false;
        }
        written += static_cast<size_t>(result);
    }
    close(fd);
    return true;
}

bool read_at(const fs::path& path, void* data, size_t size,
             uint64_t offset, size_t& bytes_read, std::string& error) {
    bytes_read = 0;
    const int fd = ::open(path.c_str(), O_RDONLY);
    if (fd < 0) {
        error = fs_error(path, "cannot open file for reading");
        return false;
    }
    auto* bytes = static_cast<uint8_t*>(data);
    size_t total = 0;
    while (total < size) {
        const ssize_t result = pread(fd, bytes + total, size - total,
                                     static_cast<off_t>(offset + total));
        if (result < 0) {
            close(fd);
            error = fs_error(path, "file read failed");
            return false;
        }
        if (result == 0) break;
        total += static_cast<size_t>(result);
    }
    close(fd);
    bytes_read = total;
    return true;
}

bool flush_file(const fs::path& path, std::string& error) {
    const int fd = ::open(path.c_str(), O_WRONLY);
    if (fd < 0) {
        error = fs_error(path, "cannot open file for flushing");
        return false;
    }
    const bool ok = fsync(fd) == 0;
    close(fd);
    if (!ok) error = fs_error(path, "cannot flush file");
    return ok;
}

bool replace_file(const fs::path& from, const fs::path& to, std::string& error) {
    if (::rename(from.c_str(), to.c_str()) != 0) {
        error = fs_error(to, "cannot finalize file");
        return false;
    }
    return true;
}

#endif

bool remove_file_quiet(const fs::path& path) {
    std::error_code ec;
    fs::remove(path, ec);
    return !ec;
}

bool path_exists(const fs::path& path) {
    std::error_code ec;
    return fs::exists(path, ec);
}

} // namespace wisp

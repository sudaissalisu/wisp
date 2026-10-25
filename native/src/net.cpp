#include "net.hpp"
#include "util.hpp"

#include <openssl/err.h>
#include <openssl/x509.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cstring>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <winsock2.h>
#  include <ws2tcpip.h>
#  include <windows.h>
using socket_t = SOCKET;
#  define WISP_INVALID_SOCKET INVALID_SOCKET
#else
#  include <arpa/inet.h>
#  include <fcntl.h>
#  include <netdb.h>
#  include <netinet/in.h>
#  include <netinet/tcp.h>
#  include <signal.h>
#  include <sys/select.h>
#  include <sys/socket.h>
#  include <sys/types.h>
#  include <unistd.h>
using socket_t = int;
#  define WISP_INVALID_SOCKET (-1)
#endif

namespace wisp {

static std::string last_socket_error() {
#if defined(_WIN32)
    const int code = WSAGetLastError();
    char* message = nullptr;
    FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, static_cast<DWORD>(code), 0,
                   reinterpret_cast<char*>(&message), 0, nullptr);
    std::string result = message ? message : "socket error";
    if (message) LocalFree(message);
    while (!result.empty() && (result.back() == '\r' || result.back() == '\n')) {
        result.pop_back();
    }
    return result + " (" + std::to_string(code) + ")";
#else
    return std::string(std::strerror(errno)) + " (" + std::to_string(errno) + ")";
#endif
}

static int close_socket(socket_t fd) {
#if defined(_WIN32)
    return closesocket(fd);
#else
    return ::close(fd);
#endif
}

std::string openssl_error() {
    const unsigned long code = ERR_get_error();
    if (code == 0) return "TLS error";
    char buffer[256];
    ERR_error_string_n(code, buffer, sizeof(buffer));
    return buffer;
}

void net_init() {
    static std::atomic<bool> initialized{false};
    bool expected = false;
    if (!initialized.compare_exchange_strong(expected, true)) return;
#if defined(_WIN32)
    WSADATA data;
    WSAStartup(MAKEWORD(2, 2), &data);
#else
    signal(SIGPIPE, SIG_IGN);
#endif
}

std::string net_last_error() {
    return last_socket_error();
}

Socket::Socket(intptr_t fd) : fd_(fd) {}

Socket::~Socket() { close(); }

Socket::Socket(Socket&& other) noexcept : fd_(other.fd_) {
    other.fd_ = -1;
}

Socket& Socket::operator=(Socket&& other) noexcept {
    if (this != &other) {
        close();
        fd_ = other.fd_;
        other.fd_ = -1;
    }
    return *this;
}

bool Socket::valid() const { return fd_ != -1; }

void Socket::close() {
    if (fd_ != -1) {
        close_socket(static_cast<socket_t>(fd_));
        fd_ = -1;
    }
}

bool Socket::set_timeouts(int recv_ms, int send_ms) {
    if (!valid()) return false;
#if defined(_WIN32)
    DWORD recv_value = static_cast<DWORD>(recv_ms);
    DWORD send_value = static_cast<DWORD>(send_ms);
    return setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_RCVTIMEO,
                      reinterpret_cast<const char*>(&recv_value), sizeof(recv_value)) == 0 &&
           setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_SNDTIMEO,
                      reinterpret_cast<const char*>(&send_value), sizeof(send_value)) == 0;
#else
    timeval recv_value{recv_ms / 1000, (recv_ms % 1000) * 1000};
    timeval send_value{send_ms / 1000, (send_ms % 1000) * 1000};
    return setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_RCVTIMEO,
                      &recv_value, sizeof(recv_value)) == 0 &&
           setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_SNDTIMEO,
                      &send_value, sizeof(send_value)) == 0;
#endif
}

bool Socket::set_buffer_sizes(int bytes) {
    if (!valid()) return false;
    const int value = std::clamp(bytes, 256 * 1024, 16 * 1024 * 1024);
    const bool recv_ok = setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_RCVBUF,
                                    reinterpret_cast<const char*>(&value), sizeof(value)) == 0;
    const bool send_ok = setsockopt(static_cast<socket_t>(fd_), SOL_SOCKET, SO_SNDBUF,
                                    reinterpret_cast<const char*>(&value), sizeof(value)) == 0;
    return recv_ok || send_ok;
}

bool Socket::set_no_delay(bool enabled) {
    const int value = enabled ? 1 : 0;
    return setsockopt(static_cast<socket_t>(fd_), IPPROTO_TCP, TCP_NODELAY,
                      reinterpret_cast<const char*>(&value), sizeof(value)) == 0;
}

static bool wait_for_connect(socket_t fd, int timeout_ms) {
    fd_set writable;
    fd_set exceptfds;
    FD_ZERO(&writable);
    FD_ZERO(&exceptfds);
    FD_SET(fd, &writable);
    FD_SET(fd, &exceptfds);
    timeval timeout{timeout_ms / 1000, (timeout_ms % 1000) * 1000};
#if defined(_WIN32)
    const int result = select(0, nullptr, &writable, &exceptfds, &timeout);
#else
    const int result = select(fd + 1, nullptr, &writable, &exceptfds, &timeout);
#endif
    if (result <= 0) return false;
    if (FD_ISSET(fd, &exceptfds)) return false;
    int error = 0;
    socklen_t length = sizeof(error);
    if (getsockopt(fd, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) != 0) {
        return false;
    }
    return error == 0;
}

bool connect_tcp(const std::string& host, int port, int timeout_ms, Socket& out, std::string& error) {
    net_init();
    if (port <= 0 || port > 65535) {
        error = "invalid port";
        return false;
    }
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    addrinfo* results = nullptr;
    const std::string service = std::to_string(port);
    const int gai = getaddrinfo(host.c_str(), service.c_str(), &hints, &results);
    if (gai != 0) {
        error = "cannot resolve host: " + host;
        return false;
    }

    std::string last_error;
    bool connected = false;
    for (addrinfo* entry = results; entry != nullptr; entry = entry->ai_next) {
        socket_t fd = socket(entry->ai_family, entry->ai_socktype, entry->ai_protocol);
        if (fd == WISP_INVALID_SOCKET) {
            last_error = last_socket_error();
            continue;
        }
#if defined(_WIN32)
        u_long nonblocking = 1;
        ioctlsocket(fd, FIONBIO, &nonblocking);
#else
        const int flags = fcntl(fd, F_GETFL, 0);
        fcntl(fd, F_SETFL, flags | O_NONBLOCK);
#endif
        int rc = ::connect(fd, entry->ai_addr, static_cast<int>(entry->ai_addrlen));
        bool ok = rc == 0;
        if (!ok) {
#if defined(_WIN32)
            const bool in_progress = WSAGetLastError() == WSAEWOULDBLOCK;
#else
            const bool in_progress = errno == EINPROGRESS || errno == EWOULDBLOCK;
#endif
            if (in_progress) {
                ok = wait_for_connect(fd, timeout_ms);
            }
        }
        if (ok) {
#if defined(_WIN32)
            nonblocking = 0;
            ioctlsocket(fd, FIONBIO, &nonblocking);
#else
            fcntl(fd, F_SETFL, flags);
#endif
            out = Socket(static_cast<intptr_t>(fd));
            out.set_timeouts(30000, 30000);
            out.set_buffer_sizes(4 * 1024 * 1024);
            out.set_no_delay(true);
            connected = true;
            break;
        }
        last_error = last_socket_error();
        close_socket(fd);
    }
    freeaddrinfo(results);
    if (!connected) {
        error = "connection failed: " + (last_error.empty() ? host : last_error);
    }
    return connected;
}

TcpListener::~TcpListener() { close(); }

bool TcpListener::open(int port, std::string& error) {
    net_init();
    socket_t fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (fd == WISP_INVALID_SOCKET) {
        error = last_socket_error();
        return false;
    }
#if defined(_WIN32)
    BOOL exclusive = TRUE;
    setsockopt(fd, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
               reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
#else
    int reuse = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
#endif
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons(static_cast<uint16_t>(port));
    if (bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
        error = "cannot listen on port " + std::to_string(port) + ": " + last_socket_error();
        close_socket(fd);
        return false;
    }
    if (listen(fd, 64) != 0) {
        error = "listen failed: " + last_socket_error();
        close_socket(fd);
        return false;
    }
    sockaddr_in actual{};
    socklen_t length = sizeof(actual);
    if (getsockname(fd, reinterpret_cast<sockaddr*>(&actual), &length) == 0) {
        port_ = ntohs(actual.sin_port);
    } else {
        port_ = port;
    }
    fd_ = static_cast<intptr_t>(fd);
    return true;
}

bool TcpListener::accept(Socket& out, int timeout_ms, std::string& error) {
    if (fd_ == -1) {
        error = "listener is closed";
        return false;
    }
    fd_set readfds;
    FD_ZERO(&readfds);
    FD_SET(static_cast<socket_t>(fd_), &readfds);
    timeval timeout{timeout_ms / 1000, (timeout_ms % 1000) * 1000};
#if defined(_WIN32)
    const int selected = select(0, &readfds, nullptr, nullptr, &timeout);
#else
    const int selected = select(static_cast<int>(fd_) + 1, &readfds, nullptr, nullptr, &timeout);
#endif
    if (selected == 0) return false;
    if (selected < 0) {
        error = last_socket_error();
        return false;
    }
    sockaddr_in peer{};
    socklen_t length = sizeof(peer);
    socket_t accepted = ::accept(static_cast<socket_t>(fd_),
                                 reinterpret_cast<sockaddr*>(&peer), &length);
    if (accepted == WISP_INVALID_SOCKET) {
        error = last_socket_error();
        return false;
    }
    out = Socket(static_cast<intptr_t>(accepted));
    out.set_timeouts(30000, 30000);
    out.set_buffer_sizes(4 * 1024 * 1024);
    out.set_no_delay(true);
    return true;
}

void TcpListener::close() {
    if (fd_ != -1) {
        close_socket(static_cast<socket_t>(fd_));
        fd_ = -1;
    }
}

TlsConnection::~TlsConnection() { close(); }

static int alpn_select_cb(SSL*, const unsigned char** out, unsigned char* out_len,
                          const unsigned char* in, unsigned int in_len, void*) {
    static const char kProtocol[] = "wisp/1";
    const size_t protocol_length = sizeof(kProtocol) - 1;
    for (unsigned int i = 0; i + 1 < in_len;) {
        const unsigned int length = in[i];
        if (i + 1 + length > in_len) break;
        if (length == protocol_length &&
            std::memcmp(in + i + 1, kProtocol, protocol_length) == 0) {
            *out = in + i + 1;
            *out_len = static_cast<unsigned char>(length);
            return SSL_TLSEXT_ERR_OK;
        }
        i += 1 + length;
    }
    return SSL_TLSEXT_ERR_ALERT_FATAL;
}

bool TlsConnection::accept(Socket socket, SSL_CTX* server_ctx, std::string& error) {
    socket_ = std::move(socket);
    ssl_ = SSL_new(server_ctx);
    if (!ssl_) {
        error = openssl_error();
        return false;
    }
    SSL_set_fd(ssl_, static_cast<int>(socket_.native()));
    SSL_set_accept_state(ssl_);
    return complete_handshake(error);
}

bool TlsConnection::connect(Socket socket, SSL_CTX* client_ctx,
                            const std::string& host, std::string& error) {
    socket_ = std::move(socket);
    ssl_ = SSL_new(client_ctx);
    if (!ssl_) {
        error = openssl_error();
        return false;
    }
    SSL_set_fd(ssl_, static_cast<int>(socket_.native()));
    SSL_set_connect_state(ssl_);
    static const unsigned char kProtocols[] = {6, 'w', 'i', 's', 'p', '/', '1'};
    SSL_set_alpn_protos(ssl_, kProtocols, sizeof(kProtocols));
    if (!host.empty()) {
        SSL_set_tlsext_host_name(ssl_, host.c_str());
    }
    return complete_handshake(error);
}

bool TlsConnection::complete_handshake(std::string& error) {
    ERR_clear_error();
    for (;;) {
        const int result = SSL_do_handshake(ssl_);
        if (result == 1) return true;
        const int ssl_error = SSL_get_error(ssl_, result);
        if (ssl_error == SSL_ERROR_WANT_READ || ssl_error == SSL_ERROR_WANT_WRITE) continue;
        error = "TLS handshake failed: " + openssl_error();
        return false;
    }
}

void TlsConnection::close() {
    if (ssl_) {
        SSL_shutdown(ssl_);
        SSL_free(ssl_);
        ssl_ = nullptr;
    }
    socket_.close();
}

bool TlsConnection::send_all(const void* data, size_t size, std::string& error) {
    const auto* bytes = static_cast<const unsigned char*>(data);
    size_t sent = 0;
    while (sent < size) {
        size_t written = 0;
        ERR_clear_error();
        const int ok = SSL_write_ex(ssl_, bytes + sent, size - sent, &written);
        if (ok != 1) {
            const int ssl_error = SSL_get_error(ssl_, ok);
            if (ssl_error == SSL_ERROR_WANT_WRITE || ssl_error == SSL_ERROR_WANT_READ) continue;
            error = "TLS write failed: " + openssl_error();
            return false;
        }
        if (written == 0) {
            error = "TLS write returned zero";
            return false;
        }
        sent += written;
    }
    return true;
}

bool TlsConnection::recv_all(void* data, size_t size, std::string& error) {
    auto* bytes = static_cast<unsigned char*>(data);
    size_t received = 0;
    while (received < size) {
        size_t got = 0;
        ERR_clear_error();
        const int ok = SSL_read_ex(ssl_, bytes + received, size - received, &got);
        if (ok != 1) {
            const int ssl_error = SSL_get_error(ssl_, ok);
            if (ssl_error == SSL_ERROR_WANT_READ || ssl_error == SSL_ERROR_WANT_WRITE) continue;
            if (ssl_error == SSL_ERROR_ZERO_RETURN) {
                error = "peer closed the connection";
            } else if (ssl_error == SSL_ERROR_SYSCALL) {
                error = "connection lost";
            } else {
                error = "TLS read failed: " + openssl_error();
            }
            return false;
        }
        if (got == 0) {
            error = "peer closed the connection";
            return false;
        }
        received += got;
    }
    return true;
}

bool TlsConnection::read_some(void* data, size_t capacity, size_t& received, std::string& error) {
    received = 0;
    ERR_clear_error();
    const int ok = SSL_read_ex(ssl_, data, capacity, &received);
    if (ok != 1) {
        const int ssl_error = SSL_get_error(ssl_, ok);
        if (ssl_error == SSL_ERROR_WANT_READ || ssl_error == SSL_ERROR_WANT_WRITE) {
            error = "timeout";
            return false;
        }
        error = "TLS read failed";
        return false;
    }
    return true;
}

bool TlsConnection::export_secret(const char* label, unsigned char* out, size_t out_size) {
    const size_t label_length = std::strlen(label);
    return SSL_export_keying_material(ssl_, out, out_size, label, label_length,
                                      nullptr, 0, 0) == 1;
}

void set_server_alpn(SSL_CTX* ctx) {
    SSL_CTX_set_alpn_select_cb(ctx, alpn_select_cb, nullptr);
}

} // namespace wisp

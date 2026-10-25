#pragma once

#include <openssl/ssl.h>

#include <cstdint>
#include <string>

namespace wisp {

void net_init();
std::string net_last_error();
std::string openssl_error();
void set_server_alpn(SSL_CTX* ctx);

class Socket {
public:
    Socket() = default;
    explicit Socket(intptr_t fd);
    ~Socket();
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
    Socket(Socket&& other) noexcept;
    Socket& operator=(Socket&& other) noexcept;

    bool valid() const;
    void close();
    intptr_t native() const { return fd_; }
    bool set_timeouts(int recv_ms, int send_ms);
    bool set_buffer_sizes(int bytes);
    bool set_no_delay(bool enabled);

private:
    intptr_t fd_ = -1;
};

bool connect_tcp(const std::string& host, int port, int timeout_ms, Socket& out, std::string& error);

class TcpListener {
public:
    TcpListener() = default;
    ~TcpListener();
    TcpListener(const TcpListener&) = delete;
    TcpListener& operator=(const TcpListener&) = delete;

    bool open(int port, std::string& error);
    bool accept(Socket& out, int timeout_ms, std::string& error);
    int port() const { return port_; }
    void close();

private:
    intptr_t fd_ = -1;
    int port_ = 0;
};

class TlsConnection {
public:
    TlsConnection() = default;
    ~TlsConnection();
    TlsConnection(const TlsConnection&) = delete;
    TlsConnection& operator=(const TlsConnection&) = delete;

    bool accept(Socket socket, SSL_CTX* server_ctx, std::string& error);
    bool connect(Socket socket, SSL_CTX* client_ctx,
                 const std::string& host, std::string& error);
    void close();

    bool send_all(const void* data, size_t size, std::string& error);
    bool recv_all(void* data, size_t size, std::string& error);
    bool read_some(void* data, size_t capacity, size_t& received, std::string& error);
    bool export_secret(const char* label, unsigned char* out, size_t out_size);

    SSL* ssl() const { return ssl_; }
    Socket& socket() { return socket_; }

private:
    bool complete_handshake(std::string& error);

    Socket socket_;
    SSL* ssl_ = nullptr;
};

} // namespace wisp

#pragma once

#include <openssl/ssl.h>
#include <openssl/x509.h>

#include <string>

namespace wisp {

class Identity {
public:
    Identity() = default;
    ~Identity();
    Identity(const Identity&) = delete;
    Identity& operator=(const Identity&) = delete;

    bool load_or_create(const std::string& directory, const std::string& display_name,
                        std::string& error);
    void close();

    SSL_CTX* server_context() const { return server_ctx_; }
    SSL_CTX* client_context() const { return client_ctx_; }
    const std::string& fingerprint() const { return fingerprint_; }
    const std::string& peer_id() const { return peer_id_; }
    const std::string& certificate_path() const { return certificate_path_; }
    const std::string& private_key_path() const { return private_key_path_; }

private:
    bool generate(const std::string& display_name, std::string& error);
    bool load(std::string& error);
    bool create_contexts(std::string& error);

    std::string directory_;
    std::string certificate_path_;
    std::string private_key_path_;
    std::string fingerprint_;
    std::string peer_id_;
    X509* certificate_ = nullptr;
    EVP_PKEY* private_key_ = nullptr;
    SSL_CTX* server_ctx_ = nullptr;
    SSL_CTX* client_ctx_ = nullptr;
};

std::string certificate_fingerprint(X509* certificate);
std::string peer_certificate_fingerprint(SSL* ssl);
std::string pairing_code(SSL* ssl);
std::string peer_id_from_fingerprint(const std::string& fingerprint);

} // namespace wisp

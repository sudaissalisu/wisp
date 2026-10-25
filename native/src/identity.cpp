#include "identity.hpp"
#include "net.hpp"
#include "util.hpp"

#include <openssl/err.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/x509v3.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <fstream>
#include <iomanip>
#include <sstream>

#if !defined(_WIN32)
#  include <sys/stat.h>
#endif

namespace fs = std::filesystem;

namespace wisp {

static int verify_any_cb(int, X509_STORE_CTX*) {
    // Certificates are self-signed. Authentication is performed by pinning the
    // certificate fingerprint after the handshake, and by the out-of-band
    // pairing code. Returning 1 here only makes OpenSSL continue the handshake
    // so both peer certificates are available to the application.
    return 1;
}

static bool write_file(const fs::path& path, const std::string& data, std::string& error) {
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        error = "cannot write " + path_to_utf8(path);
        return false;
    }
    stream.write(data.data(), static_cast<std::streamsize>(data.size()));
    if (!stream) {
        error = "cannot write " + path_to_utf8(path);
        return false;
    }
#if !defined(_WIN32)
    chmod(path.c_str(), 0600);
#endif
    return true;
}

std::string certificate_fingerprint(X509* certificate) {
    if (!certificate) return {};
    unsigned char* der = nullptr;
    const int length = i2d_X509(certificate, &der);
    if (length <= 0 || !der) return {};
    const std::string result = sha256_hex(der, static_cast<size_t>(length));
    OPENSSL_free(der);
    return result;
}

std::string peer_certificate_fingerprint(SSL* ssl) {
    if (!ssl) return {};
    X509* certificate = SSL_get1_peer_certificate(ssl);
    if (!certificate) return {};
    const std::string result = certificate_fingerprint(certificate);
    X509_free(certificate);
    return result;
}

std::string peer_id_from_fingerprint(const std::string& fingerprint) {
    return fingerprint.substr(0, std::min<size_t>(32, fingerprint.size()));
}

std::string pairing_code(SSL* ssl) {
    unsigned char material[32]{};
    if (!ssl || SSL_export_keying_material(ssl, material, sizeof(material),
                                           "WISP-PAIR-1", 11, nullptr, 0, 0) != 1) {
        return "------";
    }
    uint32_t value = (static_cast<uint32_t>(material[0]) << 24) |
                     (static_cast<uint32_t>(material[1]) << 16) |
                     (static_cast<uint32_t>(material[2]) << 8) |
                     static_cast<uint32_t>(material[3]);
    char buffer[16];
    std::snprintf(buffer, sizeof(buffer), "%06u", value % 1000000u);
    return buffer;
}

Identity::~Identity() { close(); }

void Identity::close() {
    if (server_ctx_) {
        SSL_CTX_free(server_ctx_);
        server_ctx_ = nullptr;
    }
    if (client_ctx_) {
        SSL_CTX_free(client_ctx_);
        client_ctx_ = nullptr;
    }
    if (certificate_) {
        X509_free(certificate_);
        certificate_ = nullptr;
    }
    if (private_key_) {
        EVP_PKEY_free(private_key_);
        private_key_ = nullptr;
    }
}

bool Identity::load_or_create(const std::string& directory, const std::string& display_name,
                              std::string& error) {
    close();
    directory_ = directory;
    if (directory_.empty()) {
        error = "identity directory is empty";
        return false;
    }
    std::error_code ec;
    fs::create_directories(path_from_utf8(directory_), ec);
    if (ec) {
        error = "cannot create identity directory: " + directory_;
        return false;
    }
    certificate_path_ = (path_from_utf8(directory_) / "identity.crt").string();
    private_key_path_ = (path_from_utf8(directory_) / "identity.key").string();

    const bool has_cert = fs::exists(path_from_utf8(certificate_path_), ec);
    ec.clear();
    const bool has_key = fs::exists(path_from_utf8(private_key_path_), ec);
    if (!has_cert || !has_key) {
        if (!generate(display_name, error)) return false;
    }
    if (!load(error)) return false;
    return create_contexts(error);
}

bool Identity::generate(const std::string& display_name, std::string& error) {
    ERR_clear_error();
    EVP_PKEY* key = EVP_PKEY_Q_keygen(nullptr, nullptr, "ED25519");
    if (!key) {
        error = "cannot generate Ed25519 identity: " + openssl_error();
        return false;
    }
    X509* certificate = X509_new();
    if (!certificate) {
        EVP_PKEY_free(key);
        error = "cannot allocate certificate";
        return false;
    }

    X509_set_version(certificate, 2);
    ASN1_INTEGER_set(X509_get_serialNumber(certificate),
                     static_cast<long>(random_u64() & 0x7fffffff));
    X509_gmtime_adj(X509_get_notBefore(certificate), -60);
    X509_gmtime_adj(X509_get_notAfter(certificate), 10ll * 365ll * 24ll * 60ll * 60ll);
    X509_set_pubkey(certificate, key);

    X509_NAME* subject = X509_get_subject_name(certificate);
    std::string common_name = display_name.empty() ? "Wisp Device" : display_name;
    if (common_name.size() > 64) common_name.resize(64);
    X509_NAME_add_entry_by_txt(subject, "CN", MBSTRING_UTF8,
                               reinterpret_cast<const unsigned char*>(common_name.c_str()),
                               -1, -1, 0);
    X509_NAME_add_entry_by_txt(subject, "OU", MBSTRING_UTF8,
                               reinterpret_cast<const unsigned char*>("Wisp"), -1, -1, 0);
    X509_set_issuer_name(certificate, subject);

    X509V3_CTX extension_context{};
    X509V3_set_ctx(&extension_context, certificate, certificate, nullptr, nullptr, 0);
    auto add_extension = [&](int nid, const char* value) -> bool {
        X509_EXTENSION* extension = X509V3_EXT_conf_nid(
            nullptr, &extension_context, nid, const_cast<char*>(value));
        if (!extension) return false;
        const bool ok = X509_add_ext(certificate, extension, -1) == 1;
        X509_EXTENSION_free(extension);
        return ok;
    };
    add_extension(NID_basic_constraints, "critical,CA:FALSE");
    add_extension(NID_key_usage, "critical,digitalSignature");
    add_extension(NID_ext_key_usage, "serverAuth,clientAuth");

    if (X509_sign(certificate, key, nullptr) <= 0) {
        error = "cannot sign identity certificate: " + openssl_error();
        X509_free(certificate);
        EVP_PKEY_free(key);
        return false;
    }

    BIO* cert_bio = BIO_new(BIO_s_mem());
    BIO* key_bio = BIO_new(BIO_s_mem());
    if (!cert_bio || !key_bio ||
        PEM_write_bio_X509(cert_bio, certificate) != 1 ||
        PEM_write_bio_PrivateKey(key_bio, key, nullptr, nullptr, 0, nullptr, nullptr) != 1) {
        if (cert_bio) BIO_free(cert_bio);
        if (key_bio) BIO_free(key_bio);
        X509_free(certificate);
        EVP_PKEY_free(key);
        error = "cannot serialize identity";
        return false;
    }
    char* cert_data = nullptr;
    char* key_data = nullptr;
    const long cert_length = BIO_get_mem_data(cert_bio, &cert_data);
    const long key_length = BIO_get_mem_data(key_bio, &key_data);
    const std::string cert_pem(cert_data, static_cast<size_t>(cert_length));
    const std::string key_pem(key_data, static_cast<size_t>(key_length));
    BIO_free(cert_bio);
    BIO_free(key_bio);
    X509_free(certificate);
    EVP_PKEY_free(key);

    if (!write_file(path_from_utf8(certificate_path_), cert_pem, error) ||
        !write_file(path_from_utf8(private_key_path_), key_pem, error)) {
        return false;
    }
    return true;
}

bool Identity::load(std::string& error) {
    ERR_clear_error();
    std::ifstream cert_stream(path_from_utf8(certificate_path_), std::ios::binary);
    std::ifstream key_stream(path_from_utf8(private_key_path_), std::ios::binary);
    if (!cert_stream || !key_stream) {
        error = "cannot open identity files";
        return false;
    }
    std::string cert_pem((std::istreambuf_iterator<char>(cert_stream)),
                         std::istreambuf_iterator<char>());
    std::string key_pem((std::istreambuf_iterator<char>(key_stream)),
                        std::istreambuf_iterator<char>());
    BIO* cert_bio = BIO_new_mem_buf(cert_pem.data(), static_cast<int>(cert_pem.size()));
    BIO* key_bio = BIO_new_mem_buf(key_pem.data(), static_cast<int>(key_pem.size()));
    if (!cert_bio || !key_bio) {
        if (cert_bio) BIO_free(cert_bio);
        if (key_bio) BIO_free(key_bio);
        error = "cannot prepare identity files";
        return false;
    }
    X509* certificate = PEM_read_bio_X509(cert_bio, nullptr, nullptr, nullptr);
    EVP_PKEY* key = PEM_read_bio_PrivateKey(key_bio, nullptr, nullptr, nullptr);
    BIO_free(cert_bio);
    BIO_free(key_bio);
    if (!certificate || !key) {
        if (certificate) X509_free(certificate);
        if (key) EVP_PKEY_free(key);
        error = "cannot parse identity files";
        return false;
    }
    if (X509_check_private_key(certificate, key) != 1) {
        X509_free(certificate);
        EVP_PKEY_free(key);
        error = "identity key does not match certificate";
        return false;
    }
    certificate_ = certificate;
    private_key_ = key;
    fingerprint_ = certificate_fingerprint(certificate_);
    if (fingerprint_.size() != 64) {
        error = "cannot compute identity fingerprint";
        return false;
    }
    peer_id_ = peer_id_from_fingerprint(fingerprint_);
    return true;
}

bool Identity::create_contexts(std::string& error) {
    net_init();
    ERR_clear_error();
    server_ctx_ = SSL_CTX_new(TLS_server_method());
    client_ctx_ = SSL_CTX_new(TLS_client_method());
    if (!server_ctx_ || !client_ctx_) {
        error = "cannot create TLS contexts: " + openssl_error();
        return false;
    }
    for (SSL_CTX* ctx : {server_ctx_, client_ctx_}) {
        SSL_CTX_set_min_proto_version(ctx, TLS1_3_VERSION);
        SSL_CTX_set_options(ctx, SSL_OP_NO_COMPRESSION);
        SSL_CTX_set_read_ahead(ctx, 1);
        if (SSL_CTX_use_certificate(ctx, certificate_) != 1 ||
            SSL_CTX_use_PrivateKey(ctx, private_key_) != 1 ||
            SSL_CTX_check_private_key(ctx) != 1) {
            error = "cannot configure TLS identity: " + openssl_error();
            return false;
        }
    }
    SSL_CTX_set_verify(server_ctx_,
                       SSL_VERIFY_PEER | SSL_VERIFY_FAIL_IF_NO_PEER_CERT,
                       verify_any_cb);
    SSL_CTX_set_verify(client_ctx_, SSL_VERIFY_NONE, nullptr);
    set_server_alpn(server_ctx_);
    return true;
}

} // namespace wisp

#include "wisp/c_api.h"

#include "engine.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

struct wisp_engine {
    std::unique_ptr<wisp::Engine> impl;
    wisp_event_cb event_cb = nullptr;
    wisp_log_cb log_cb = nullptr;
    void* user = nullptr;
};

static void copy_string(char* out, size_t size, const std::string& value) {
    if (!out || size == 0) return;
    const size_t length = std::min(size - 1, value.size());
    std::memcpy(out, value.data(), length);
    out[length] = '\0';
}

void wisp_engine_config_init(wisp_engine_config* config) {
    if (!config) return;
    std::memset(config, 0, sizeof(*config));
    config->name = "Wisp Device";
    config->listen_port = 0;
    config->discovery_port = 47778;
    config->auto_accept = 0;
    config->data_channels = 4;
    config->conflict_policy = 0;
}

extern "C" {

wisp_engine* wisp_engine_create(const wisp_engine_config* config,
                                wisp_event_cb event_cb,
                                wisp_log_cb log_cb,
                                void* user) {
    if (!config) return nullptr;
    try {
        auto* wrapper = new wisp_engine();
        wrapper->event_cb = event_cb;
        wrapper->log_cb = log_cb;
        wrapper->user = user;

        wisp::EngineConfig engine_config;
        if (config->name) engine_config.name = config->name;
        if (config->save_dir) engine_config.save_dir = config->save_dir;
        if (config->identity_dir) engine_config.identity_dir = config->identity_dir;
        engine_config.listen_port = config->listen_port;
        engine_config.discovery_port = config->discovery_port;
        engine_config.auto_accept = config->auto_accept != 0;
        engine_config.data_channels = config->data_channels > 0 ? config->data_channels : 4;
        engine_config.conflict_policy = config->conflict_policy;

        wrapper->impl = std::make_unique<wisp::Engine>(
            std::move(engine_config),
            [wrapper](int type, const std::string& json) {
                if (wrapper->event_cb) wrapper->event_cb(wrapper->user, type, json.c_str());
            },
            [wrapper](int level, const std::string& message) {
                if (wrapper->log_cb) wrapper->log_cb(wrapper->user, level, message.c_str());
            });
        return wrapper;
    } catch (...) {
        return nullptr;
    }
}

int wisp_engine_start(wisp_engine* engine) {
    if (!engine || !engine->impl) return -1;
    try {
        std::string error;
        if (!engine->impl->start(error)) {
            if (engine->log_cb) {
                engine->log_cb(engine->user, WISP_LOG_ERROR, error.c_str());
            }
            return -2;
        }
        return 0;
    } catch (...) {
        return -3;
    }
}

void wisp_engine_stop(wisp_engine* engine) {
    if (engine && engine->impl) engine->impl->stop();
}

void wisp_engine_destroy(wisp_engine* engine) {
    if (!engine) return;
    if (engine->impl) engine->impl->stop();
    delete engine;
}

int wisp_engine_send_paths(wisp_engine* engine,
                           const char* peer_id,
                           const char* const* paths,
                           size_t path_count,
                           char* transfer_id_out,
                           size_t transfer_id_out_size) {
    if (!engine || !engine->impl || !peer_id || (!paths && path_count != 0) ||
        path_count == 0) {
        return -1;
    }
    try {
        std::vector<std::string> values;
        values.reserve(path_count);
        for (size_t i = 0; i < path_count; ++i) {
            if (!paths[i]) return -1;
            values.emplace_back(paths[i]);
        }
        std::string error;
        const std::string id = engine->impl->send_paths(peer_id, values, error);
        if (id.empty()) {
            if (engine->log_cb) {
                engine->log_cb(engine->user, WISP_LOG_ERROR, error.c_str());
            }
            return -2;
        }
        copy_string(transfer_id_out, transfer_id_out_size, id);
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_respond(wisp_engine* engine,
                        const char* offer_id,
                        int accept,
                        const char* save_dir) {
    if (!engine || !engine->impl || !offer_id) return -1;
    try {
        std::string error;
        if (!engine->impl->respond(offer_id, accept != 0,
                                   save_dir ? save_dir : "", error)) {
            if (engine->log_cb) {
                engine->log_cb(engine->user, WISP_LOG_WARN, error.c_str());
            }
            return -2;
        }
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_cancel(wisp_engine* engine, const char* transfer_id) {
    if (!engine || !engine->impl || !transfer_id) return -1;
    try {
        std::string error;
        if (!engine->impl->cancel(transfer_id, error)) {
            if (engine->log_cb) {
                engine->log_cb(engine->user, WISP_LOG_WARN, error.c_str());
            }
            return -2;
        }
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_add_peer(wisp_engine* engine,
                         const char* id,
                         const char* name,
                         const char* host,
                         int port,
                         const char* fingerprint) {
    if (!engine || !engine->impl || !host) return -1;
    try {
        wisp::PeerInfo peer;
        if (id) peer.id = id;
        if (name) peer.name = name;
        peer.host = host;
        peer.port = port;
        if (fingerprint) peer.fingerprint = fingerprint;
        std::string error;
        if (!engine->impl->add_peer(peer, error)) return -2;
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_set_link_token(wisp_engine* engine, const char* link_token) {
    if (!engine || !engine->impl) return -1;
    try {
        engine->impl->set_link_token(link_token ? link_token : "");
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_set_expected_link_token(wisp_engine* engine,
                                        const char* expected_token) {
    if (!engine || !engine->impl) return -1;
    try {
        engine->impl->set_expected_link_token(expected_token ? expected_token : "");
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_set_bind_address(wisp_engine* engine, const char* address) {
    if (!engine || !engine->impl) return -1;
    try {
        engine->impl->set_bind_address(address ? address : "");
        return 0;
    } catch (...) {
        return -3;
    }
}

int wisp_engine_get_peers(wisp_engine* engine, char* out, size_t out_size) {
    if (!engine || !engine->impl) return -1;
    try {
        const std::string json = engine->impl->peers_json();
        copy_string(out, out_size, json);
        return static_cast<int>(json.size());
    } catch (...) {
        return -3;
    }
}

int wisp_engine_get_self(wisp_engine* engine, char* out, size_t out_size) {
    if (!engine || !engine->impl) return -1;
    try {
        const std::string json = engine->impl->peer();
        copy_string(out, out_size, json);
        return static_cast<int>(json.size());
    } catch (...) {
        return -3;
    }
}

const char* wisp_version(void) { return "0.1.0"; }

} // extern "C"

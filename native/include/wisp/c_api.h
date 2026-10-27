#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(WISP_BUILDING)
#    define WISP_API __declspec(dllexport)
#  else
#    define WISP_API __declspec(dllimport)
#  endif
#else
#  define WISP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct wisp_engine wisp_engine;

typedef enum wisp_event_type {
    WISP_EVT_PEER_FOUND = 1,
    WISP_EVT_PEER_LOST = 2,
    WISP_EVT_OFFER = 3,
    WISP_EVT_PROGRESS = 4,
    WISP_EVT_PAIRING = 5,
    WISP_EVT_COMPLETE = 6,
    WISP_EVT_ERROR = 7,
    WISP_EVT_CANCELLED = 8
} wisp_event_type;

typedef enum wisp_log_level {
    WISP_LOG_DEBUG = 0,
    WISP_LOG_INFO = 1,
    WISP_LOG_WARN = 2,
    WISP_LOG_ERROR = 3
} wisp_log_level;

typedef void (*wisp_event_cb)(void* user, int type, const char* json);
typedef void (*wisp_log_cb)(void* user, int level, const char* message);

typedef struct wisp_engine_config {
    /* UTF-8 display name shown to nearby devices. */
    const char* name;
    /* Default directory where accepted files are written. */
    const char* save_dir;
    /* Directory used for the persistent identity. Empty = save_dir/.wisp-identity */
    const char* identity_dir;
    /* 0 chooses an ephemeral TCP port. */
    int listen_port;
    /* 0 disables LAN discovery. */
    int discovery_port;
    /* Used by the CLI and tests. Production UI should leave this 0 and answer offers. */
    int auto_accept;
    /* Number of parallel TLS data channels (1..8). */
    int data_channels;
    /* 0 = rename on conflict, 1 = overwrite, 2 = skip. */
    int conflict_policy;
} wisp_engine_config;

WISP_API void wisp_engine_config_init(wisp_engine_config* config);
WISP_API wisp_engine* wisp_engine_create(const wisp_engine_config* config,
                                         wisp_event_cb event_cb,
                                         wisp_log_cb log_cb,
                                         void* user);
WISP_API int wisp_engine_start(wisp_engine* engine);
WISP_API void wisp_engine_stop(wisp_engine* engine);
WISP_API void wisp_engine_destroy(wisp_engine* engine);

/* Starts an asynchronous outgoing transfer. transfer_id_out receives a 16-char hex id. */
WISP_API int wisp_engine_send_paths(wisp_engine* engine,
                                    const char* peer_id,
                                    const char* const* paths,
                                    size_t path_count,
                                    char* transfer_id_out,
                                    size_t transfer_id_out_size);

/* Answers a pending offer. save_dir may be NULL to use the engine default. */
WISP_API int wisp_engine_respond(wisp_engine* engine,
                                 const char* offer_id,
                                 int accept,
                                 const char* save_dir);

WISP_API int wisp_engine_cancel(wisp_engine* engine, const char* transfer_id);

/* Adds or refreshes a peer manually (QR/manual IP fallback). */
WISP_API int wisp_engine_add_peer(wisp_engine* engine,
                                  const char* id,
                                  const char* name,
                                  const char* host,
                                  int port,
                                  const char* fingerprint);

/* QR temporary link. The sender presents link_token in its Hello frame. The
   host sets expected_token; the first matching Hello binds it to that peer
   certificate, and only that peer may reuse it. Pass "" or NULL to clear. */
WISP_API int wisp_engine_set_link_token(wisp_engine* engine, const char* link_token);
WISP_API int wisp_engine_set_expected_link_token(wisp_engine* engine,
                                                 const char* expected_token);

/* Optional source address for outgoing transfer sockets. The Android hotspot
   host calls this with its soft-AP IPv4 address so replies use that interface. */
WISP_API int wisp_engine_set_bind_address(wisp_engine* engine, const char* address);

/* Copies a JSON array of currently known peers into out. */
WISP_API int wisp_engine_get_peers(wisp_engine* engine, char* out, size_t out_size);

/* Copies this device's peer information as JSON. */
WISP_API int wisp_engine_get_self(wisp_engine* engine, char* out, size_t out_size);

WISP_API const char* wisp_version(void);

#ifdef __cplusplus
}
#endif

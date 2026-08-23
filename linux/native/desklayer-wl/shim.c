/*
 * desklayer-wl — a flat C ABI over wayland-client + wlr-layer-shell, consumed
 * from C# via P/Invoke. Exists because reimplementing Wayland's proxy
 * marshalling in C# buys nothing; ~300 lines of C keeps the .NET side to six
 * imports.
 *
 * Layer choice: `bottom`, not `background`. background is where the DE's own
 * wallpaper client (swaybg, plasmashell) lives; bottom composites
 * transparently above it and below every window — which is exactly the
 * DeskLayer contract.
 *
 * Build: make   (needs wayland-scanner, wayland-client headers; the
 * layer-shell XML is fetched into protocol/ on first build — see Makefile)
 */

#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <wayland-client.h>

#include "protocol/wlr-layer-shell-unstable-v1-client-protocol.h"

#define DLWL_MAX_OUTPUTS 16

typedef struct {
    struct wl_output *output;
    uint32_t name;       /* registry name, for removal */
    int32_t scale;
    int32_t width_px, height_px;   /* from mode; 0 until advertised */
    int done;
} dlwl_output;

typedef struct dlwl_surface {
    struct wl_surface *wl;
    struct zwlr_layer_surface_v1 *layer;
    dlwl_output *out;
    /* double buffer */
    struct wl_buffer *buffers[2];
    void *maps[2];
    size_t map_sizes[2];
    int busy[2];
    int width, height, stride;     /* surface-local px */
    int configured;
} dlwl_surface;

static struct wl_display *g_display;
static struct wl_registry *g_registry;
static struct wl_compositor *g_compositor;
static struct wl_shm *g_shm;
static struct zwlr_layer_shell_v1 *g_layer_shell;
static dlwl_output g_outputs[DLWL_MAX_OUTPUTS];
static int g_output_count;

/* ---- output listener -------------------------------------------------- */

static void out_geometry(void *d, struct wl_output *o, int32_t x, int32_t y,
                         int32_t pw, int32_t ph, int32_t sub, const char *make,
                         const char *model, int32_t transform) { (void)d; (void)o; (void)x; (void)y; (void)pw; (void)ph; (void)sub; (void)make; (void)model; (void)transform; }
static void out_mode(void *data, struct wl_output *o, uint32_t flags,
                     int32_t w, int32_t h, int32_t refresh) {
    (void)o; (void)refresh;
    dlwl_output *out = data;
    if (flags & WL_OUTPUT_MODE_CURRENT) { out->width_px = w; out->height_px = h; }
}
static void out_done(void *data, struct wl_output *o) { (void)o; ((dlwl_output *)data)->done = 1; }
static void out_scale(void *data, struct wl_output *o, int32_t s) { (void)o; ((dlwl_output *)data)->scale = s; }
static void out_name(void *d, struct wl_output *o, const char *n) { (void)d; (void)o; (void)n; }
static void out_desc(void *d, struct wl_output *o, const char *n) { (void)d; (void)o; (void)n; }

static const struct wl_output_listener output_listener = {
    .geometry = out_geometry, .mode = out_mode, .done = out_done,
    .scale = out_scale, .name = out_name, .description = out_desc,
};

/* ---- registry --------------------------------------------------------- */

static void reg_global(void *data, struct wl_registry *reg, uint32_t name,
                       const char *iface, uint32_t version) {
    (void)data;
    if (strcmp(iface, wl_compositor_interface.name) == 0)
        g_compositor = wl_registry_bind(reg, name, &wl_compositor_interface, version < 4 ? version : 4);
    else if (strcmp(iface, wl_shm_interface.name) == 0)
        g_shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    else if (strcmp(iface, zwlr_layer_shell_v1_interface.name) == 0)
        g_layer_shell = wl_registry_bind(reg, name, &zwlr_layer_shell_v1_interface, version < 4 ? version : 4);
    else if (strcmp(iface, wl_output_interface.name) == 0 && g_output_count < DLWL_MAX_OUTPUTS) {
        dlwl_output *out = &g_outputs[g_output_count++];
        out->name = name;
        out->scale = 1;
        out->output = wl_registry_bind(reg, name, &wl_output_interface, version < 4 ? version : 4);
        wl_output_add_listener(out->output, &output_listener, out);
    }
}
static void reg_global_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener registry_listener = { reg_global, reg_global_remove };

/* ---- layer surface ---------------------------------------------------- */

static void layer_configure(void *data, struct zwlr_layer_surface_v1 *layer,
                            uint32_t serial, uint32_t w, uint32_t h) {
    dlwl_surface *s = data;
    zwlr_layer_surface_v1_ack_configure(layer, serial);
    if (w > 0 && h > 0) {
        s->width = (int)w * s->out->scale;
        s->height = (int)h * s->out->scale;
        s->stride = s->width * 4;
    }
    s->configured = 1;
}
static void layer_closed(void *data, struct zwlr_layer_surface_v1 *layer) {
    (void)layer;
    ((dlwl_surface *)data)->configured = -1;
}
static const struct zwlr_layer_surface_v1_listener layer_listener = { layer_configure, layer_closed };

/* ---- buffers ---------------------------------------------------------- */

static void buffer_release(void *data, struct wl_buffer *b) {
    dlwl_surface *s = data;
    for (int i = 0; i < 2; i++)
        if (s->buffers[i] == b) s->busy[i] = 0;
}
static const struct wl_buffer_listener buffer_listener = { buffer_release };

static int create_buffer(dlwl_surface *s, int slot) {
    size_t size = (size_t)s->stride * s->height;
    char tmpl[] = "/tmp/desklayer-wl-XXXXXX";
    int fd = mkstemp(tmpl);
    if (fd < 0) return -1;
    unlink(tmpl);
    if (ftruncate(fd, (off_t)size) < 0) { close(fd); return -1; }
    void *map = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (map == MAP_FAILED) { close(fd); return -1; }
    struct wl_shm_pool *pool = wl_shm_create_pool(g_shm, fd, (int)size);
    struct wl_buffer *buf = wl_shm_pool_create_buffer(pool, 0, s->width, s->height,
                                                      s->stride, WL_SHM_FORMAT_ARGB8888);
    wl_shm_pool_destroy(pool);
    close(fd);
    wl_buffer_add_listener(buf, &buffer_listener, s);
    s->buffers[slot] = buf;
    s->maps[slot] = map;
    s->map_sizes[slot] = size;
    s->busy[slot] = 0;
    return 0;
}

/* ---- exported API ------------------------------------------------------ */

int dlwl_connect(void) {
    g_display = wl_display_connect(NULL);
    if (!g_display) return -1;
    g_registry = wl_display_get_registry(g_display);
    wl_registry_add_listener(g_registry, &registry_listener, NULL);
    wl_display_roundtrip(g_display);   /* globals */
    wl_display_roundtrip(g_display);   /* output modes/scales */
    if (!g_compositor || !g_shm) return -2;
    if (!g_layer_shell) return -3;     /* caller falls back to X11/XWayland */
    return 0;
}

int dlwl_output_count(void) { return g_output_count; }

/* px size and scale of output i; returns 0 on success */
int dlwl_output_info(int i, int32_t *width_px, int32_t *height_px, int32_t *scale) {
    if (i < 0 || i >= g_output_count) return -1;
    *width_px = g_outputs[i].width_px;
    *height_px = g_outputs[i].height_px;
    *scale = g_outputs[i].scale;
    return 0;
}

/* Creates a layer-bottom surface covering output i. Returns NULL on failure.
 * Blocks until the first configure so the buffer size is known. */
dlwl_surface *dlwl_surface_create(int i) {
    if (i < 0 || i >= g_output_count) return NULL;
    dlwl_surface *s = calloc(1, sizeof *s);
    s->out = &g_outputs[i];
    s->wl = wl_compositor_create_surface(g_compositor);
    s->layer = zwlr_layer_shell_v1_get_layer_surface(
        g_layer_shell, s->wl, s->out->output,
        ZWLR_LAYER_SHELL_V1_LAYER_BOTTOM, "desklayer");
    zwlr_layer_surface_v1_add_listener(s->layer, &layer_listener, s);
    zwlr_layer_surface_v1_set_anchor(s->layer,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
        ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
    zwlr_layer_surface_v1_set_exclusive_zone(s->layer, -1);
    /* input passes through to whatever the compositor puts underneath */
    struct wl_region *empty = wl_compositor_create_region(g_compositor);
    wl_surface_set_input_region(s->wl, empty);
    wl_region_destroy(empty);
    wl_surface_set_buffer_scale(s->wl, s->out->scale);
    wl_surface_commit(s->wl);
    while (!s->configured && wl_display_dispatch(g_display) != -1) {}
    if (s->configured != 1) { free(s); return NULL; }
    if (create_buffer(s, 0) < 0 || create_buffer(s, 1) < 0) { free(s); return NULL; }
    return s;
}

/* Borrow a writable ARGB8888 premultiplied buffer. Returns slot >= 0, or -1
 * when both buffers are held by the compositor (skip the frame). */
int dlwl_buffer_acquire(dlwl_surface *s, void **pixels, int32_t *width,
                        int32_t *height, int32_t *stride) {
    for (int i = 0; i < 2; i++) {
        if (!s->busy[i]) {
            *pixels = s->maps[i];
            *width = s->width;
            *height = s->height;
            *stride = s->stride;
            return i;
        }
    }
    return -1;
}

void dlwl_commit(dlwl_surface *s, int slot) {
    s->busy[slot] = 1;
    wl_surface_attach(s->wl, s->buffers[slot], 0, 0);
    wl_surface_damage_buffer(s->wl, 0, 0, s->width, s->height);
    wl_surface_commit(s->wl);
    wl_display_flush(g_display);
}

/* Non-blocking event pump; call from the render loop. Returns <0 when the
 * connection died. */
int dlwl_dispatch(void) {
    if (wl_display_prepare_read(g_display) == 0) {
        wl_display_flush(g_display);
        wl_display_read_events(g_display);
    }
    return wl_display_dispatch_pending(g_display);
}

void dlwl_surface_destroy(dlwl_surface *s) {
    if (!s) return;
    for (int i = 0; i < 2; i++) {
        if (s->buffers[i]) wl_buffer_destroy(s->buffers[i]);
        if (s->maps[i]) munmap(s->maps[i], s->map_sizes[i]);
    }
    if (s->layer) zwlr_layer_surface_v1_destroy(s->layer);
    if (s->wl) wl_surface_destroy(s->wl);
    wl_display_flush(g_display);
    free(s);
}

void dlwl_disconnect(void) {
    if (g_display) wl_display_disconnect(g_display);
    g_display = NULL;
}

# Builds libdesklayer-wl.so — the flat C ABI the .NET side P/Invokes for
# Wayland layer-shell presentation.
#
# Needs: gcc, wayland-scanner, wayland-client dev headers, curl (first build
# only, to fetch the layer-shell protocol XML).
#   Debian/Ubuntu: apt install build-essential libwayland-dev wayland-protocols curl
#   Fedora:        dnf install gcc wayland-devel wayland-protocols-devel curl

PROTO_XML = protocol/wlr-layer-shell-unstable-v1.xml
PROTO_URL = https://gitlab.freedesktop.org/wlroots/wlr-protocols/-/raw/master/unstable/wlr-layer-shell-unstable-v1.xml
PROTO_H   = protocol/wlr-layer-shell-unstable-v1-client-protocol.h
PROTO_C   = protocol/wlr-layer-shell-unstable-v1-client-protocol.c

CFLAGS  ?= -O2 -fPIC -Wall -Wextra
LDLIBS   = -lwayland-client

libdesklayer-wl.so: shim.c $(PROTO_H) $(PROTO_C)
	$(CC) $(CFLAGS) -shared -o $@ shim.c $(PROTO_C) $(LDLIBS)

$(PROTO_XML):
	mkdir -p protocol
	curl -fsSL -o $@ $(PROTO_URL)

$(PROTO_H): $(PROTO_XML)
	wayland-scanner client-header $(PROTO_XML) $@

$(PROTO_C): $(PROTO_XML)
	wayland-scanner private-code $(PROTO_XML) $@

clean:
	rm -f libdesklayer-wl.so $(PROTO_H) $(PROTO_C)

.PHONY: clean

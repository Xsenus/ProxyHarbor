FROM postgres:18-alpine@sha256:d3e1620b530c944afa6e887d22eb899824da68e19c52024bf98f5220c88a65b2

# The upstream image currently carries a gosu binary built with an outdated Go
# standard library. PostgreSQL's entrypoint only needs its user-switch syntax,
# which Alpine's small C-based su-exec provides without the vulnerable Go runtime.
RUN apk upgrade --no-cache libcrypto3 libssl3 c-ares && \
    apk add --no-cache su-exec && \
    rm -f /usr/local/bin/gosu && \
    ln -s /sbin/su-exec /usr/local/bin/gosu && \
    gosu postgres true

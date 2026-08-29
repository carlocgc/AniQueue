#!/bin/sh
set -e

# AniQueue's application process runs as an unprivileged user, and a bind-mounted
# /data arrives owned by whoever created it on the host — root, for a directory
# Docker made itself, which is what an Unraid path mapping produces. The
# container therefore starts as root, hands /data to that user and immediately
# drops to it, so a first run needs no chown on the host.
#
# Started with an explicit --user there is nothing to do and nothing that can be
# done: an unprivileged process cannot chown, and the caller has already said who
# this runs as.
if [ "$(id -u)" = "0" ]; then
    # Recursive only when the directory itself is wrong, which is the untouched
    # bind mount. Doing it on every start would walk the whole artwork cache.
    if [ "$(stat -c %u /data)" != "$APP_UID" ]; then
        chown -R "$APP_UID:$APP_UID" /data \
            || echo "aniqueue: could not take ownership of /data; the database may fail to open" >&2
    fi

    # --init-groups rather than a bare uid/gid swap: the image's app user has a
    # supplementary group list, and dropping it silently is a difference nobody
    # would look for.
    exec setpriv --reuid "$APP_UID" --regid "$APP_UID" --init-groups "$@"
fi

exec "$@"

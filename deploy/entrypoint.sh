#!/bin/sh
# Container entrypoint: platform volumes are often mounted root-owned. If started as root, give the object-store directory to
# the unprivileged "app" user and drop privileges before starting the host. Never runs the application as root.
set -eu
STORE="${ObjectStore__RootPath:-/var/lib/serk/objects}"
if [ "$(id -u)" = "0" ]; then
  mkdir -p "$STORE"
  chown -R app:app "$STORE"
  exec setpriv --reuid=app --regid=app --init-groups dotnet /app/Serk.Clinic.Host.dll "$@"
fi
exec dotnet /app/Serk.Clinic.Host.dll "$@"

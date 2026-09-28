#!/bin/sh
set -eu

for attempt in $(seq 1 60); do
    if mongosh --quiet --host mongodb --username swiftdev \
        --password "$MONGO_ROOT_PASSWORD" --authenticationDatabase admin \
        --eval 'const match = db.getSiblingDB("MatchDB"); if (!match.getCollectionNames().includes("__devcontainer_init")) match.createCollection("__devcontainer_init");' \
        >/dev/null 2>&1; then
        echo 'MatchDB ready'
        exit 0
    fi
    sleep 2
done

echo 'Could not initialize MatchDB' >&2
exit 1

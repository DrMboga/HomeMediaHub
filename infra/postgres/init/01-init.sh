#!/bin/bash
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-EOSQL
    CREATE DATABASE homemediahub_db;
    CREATE DATABASE n8n_db;
EOSQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "homemediahub_db" \
    -f /docker-entrypoint-initdb.d/02-schema.sql

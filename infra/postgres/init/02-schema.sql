CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE media_files (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path       TEXT NOT NULL UNIQUE,
    file_hash       TEXT NOT NULL,
    source          TEXT NOT NULL,
    immich_asset_id TEXT,
    mime_type       TEXT NOT NULL,
    file_size       BIGINT,
    width           INT,
    height          INT,
    duration_sec    FLOAT,
    taken_at        TIMESTAMPTZ,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    is_deleted      BOOLEAN DEFAULT FALSE,
    is_favorite     BOOLEAN DEFAULT FALSE,
    origin_class    TEXT,                    -- 'real_photo' | 'document' | 'meme' | 'screenshot' | 'artwork' | NULL (not yet classified)
    origin_confidence FLOAT                  -- CLIP classification confidence [0.0–1.0]
);
CREATE INDEX ON media_files (taken_at DESC);
CREATE INDEX ON media_files (file_hash);
CREATE INDEX ON media_files (is_deleted, taken_at DESC);
CREATE INDEX ON media_files (origin_class) WHERE origin_class IS NOT NULL;

CREATE TABLE media_metadata (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    latitude        FLOAT,
    longitude       FLOAT,
    address         TEXT,
    camera_make     TEXT,
    camera_model    TEXT,
    exif_json       JSONB
);
CREATE INDEX ON media_metadata (latitude, longitude);

CREATE TABLE media_tags (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    media_file_id   UUID NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    tag             TEXT NOT NULL,
    confidence      FLOAT NOT NULL,
    model           TEXT NOT NULL
);
CREATE INDEX ON media_tags (media_file_id);
CREATE INDEX ON media_tags (tag);

CREATE TABLE media_embeddings (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    embedding       vector(512),
    created_at      TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX ON media_embeddings
    USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE TABLE media_thumbnails (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    thumbnail_path  TEXT NOT NULL,
    width           INT,
    height          INT
);

CREATE TABLE albums (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            TEXT NOT NULL,
    description     TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE album_media (
    album_id        UUID REFERENCES albums(id) ON DELETE CASCADE,
    media_file_id   UUID REFERENCES media_files(id) ON DELETE CASCADE,
    added_at        TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (album_id, media_file_id)
);

CREATE TABLE video_transcodes (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    transcode_path  TEXT NOT NULL,
    codec           TEXT NOT NULL,
    container       TEXT NOT NULL,
    file_size       BIGINT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE photo_series (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    series_type     TEXT NOT NULL,
    taken_at_start  TIMESTAMPTZ,
    taken_at_end    TIMESTAMPTZ,
    latitude        FLOAT,
    longitude       FLOAT,
    cover_media_id  UUID REFERENCES media_files(id)
);

CREATE TABLE series_media (
    series_id       UUID REFERENCES photo_series(id) ON DELETE CASCADE,
    media_file_id   UUID REFERENCES media_files(id) ON DELETE CASCADE,
    PRIMARY KEY (series_id, media_file_id)
);

CREATE TABLE processing_jobs (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path        TEXT NOT NULL,
    source           TEXT NOT NULL,
    status           TEXT NOT NULL,
    n8n_execution_id TEXT,
    started_at       TIMESTAMPTZ DEFAULT NOW(),
    completed_at     TIMESTAMPTZ,
    failed_step      TEXT,
    error_message    TEXT,
    retry_count      INT DEFAULT 0
);
CREATE INDEX ON processing_jobs (status);
CREATE INDEX ON processing_jobs (file_path);
CREATE INDEX ON processing_jobs (started_at DESC);

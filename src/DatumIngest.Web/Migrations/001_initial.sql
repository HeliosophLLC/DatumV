-- v1 chat schema: persisted conversations + turns. Graph links
-- (message_links), uploads, and files are still deferred. User-facing
-- preferences (theme, default LLM, …) live in settings.json via
-- ISettingsService — not here.

CREATE TABLE __schema_migrations (
    version Int32 PRIMARY KEY,
    name String NOT NULL,
    applied_at TIMESTAMP NOT NULL DEFAULT now()
);

CREATE TABLE conversations (
    id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title String,
    model String,
    created_at TIMESTAMP NOT NULL DEFAULT now(),
    updated_at TIMESTAMP NOT NULL DEFAULT now()
);

CREATE TABLE messages (
    id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conversation_id Int64 NOT NULL,
    -- 'turn'       — ordinary user / assistant message.
    -- 'checkpoint' — compaction summary; prompt builder uses the most
    --                recent one as a synthetic system message and skips
    --                turns with smaller ids.
    -- 'hidden'     — soft-deleted; UI shows but the prompt builder skips.
    kind String NOT NULL DEFAULT 'turn',
    role String NOT NULL,
    content String NOT NULL,
    model String,
    input_tokens Int32,
    output_tokens Int32,
    created_at TIMESTAMP NOT NULL DEFAULT now()
);

-- v1 chat schema: just enough to persist back-and-forth turns and validate
-- the conversation loop end-to-end. Graph links (message_links), uploads,
-- files, streaming status, and titles are all deferred until their
-- workflows are concrete. See project_message_graph_design + project_files_become_tables
-- memories for the rationale and target shape.

CREATE TABLE __schema_migrations (
    version Int32 PRIMARY KEY,
    name String NOT NULL,
    applied_at DateTime NOT NULL DEFAULT now()
);

CREATE TABLE messages (
    id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role String NOT NULL,
    content String NOT NULL,
    model String,
    input_tokens Int32,
    output_tokens Int32,
    created_at DateTime NOT NULL DEFAULT now()
);

-- ============================================================================
-- TinyLlama 1.1B Chat v1.0 (GGUF Q4_K_M) — small Apache-2.0 chat LLM
-- ============================================================================
--
-- Catalog id:  tinyllama-1.1b-chat-v1.0-gguf   (models/catalog.json)
-- GGUF file:   tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF
--
-- The "previous-era" voice in the LLM zoo — TinyLlama's 2023-vintage
-- chat tuning produces noticeably different prose to modern
-- instruction-tuned models, useful for tonal A/B comparison against
-- newer entries. ~700 MB on disk at Q4_K_M.
--
-- Chat template: Zephyr (`<|user|>` / `<|assistant|>` / `</s>`). The
-- runtime ignores the `template` argument for prompt construction —
-- llama.cpp's native template engine reads the GGUF's embedded Zephyr
-- template — but the template name still selects the stop-sequence list
-- used as a safety net beneath llama.cpp's EOG check.
--
-- Registration pair mirrors the Qwen 2.5 Coder family — see
-- models/sql/qwen2.5-coder-1.5b-instruct-gguf/2026-06-01.sql for the
-- canonical annotated reference.
-- ============================================================================

CREATE OR REPLACE MODEL tinyllama_1_1b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 256
    CHECK (max_tokens BETWEEN 1 AND 2048)
    COMMENT 'Maximum new tokens to generate per call. TinyLlama-1.1B-Chat was trained at 2K context — keep modest.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy (most predictable), 0.7 = balanced default, >=1.5 = very random / creative.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'tinyllama-1.1b-chat-v1.0-gguf/2026-06-01/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'zephyr', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL tinyllama_1_1b(
  prompt String,
  max_tokens Int32 = 256
    CHECK (max_tokens BETWEEN 1 AND 2048)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.tinyllama_1_1b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

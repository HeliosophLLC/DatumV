-- ============================================================================
-- Qwen 2.5 Coder 3B Instruct (GGUF Q4_K_M) — middle Apache-2.0 chat LLM
-- ============================================================================
--
-- Catalog id:  qwen2.5-coder-3b-instruct-gguf   (models/catalog.json)
-- GGUF file:   Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/bartowski/Qwen2.5-Coder-3B-Instruct-GGUF
--
-- Middle rung of the Qwen Coder ladder. Meaningfully more coherent on
-- multi-section HTML and short scripts than the 1.5B, still under 2 GB
-- on disk at Q4_K_M. Defaults to a 2K-token output budget so
-- "write a Geocities page" calls work without per-call overrides.
--
-- Registration pair mirrors the 1.5B and Phi shapes — see
-- models/sql/qwen2.5-coder-1.5b-instruct-gguf/2026-06-01.sql for the
-- canonical annotated reference.
-- ============================================================================

CREATE OR REPLACE MODEL qwen25_coder_3b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 2048
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call. Qwen 2.5 Coder 3B handles up to 32K context.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy, 0.7 = balanced default.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'qwen2.5-coder-3b-instruct-gguf/2026-06-01/Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'chatml', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL qwen25_coder_3b(
  prompt String,
  max_tokens Int32 = 2048
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.qwen25_coder_3b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

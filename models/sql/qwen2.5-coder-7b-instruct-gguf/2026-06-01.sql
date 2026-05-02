-- ============================================================================
-- Qwen 2.5 Coder 7B Instruct (GGUF Q5_K_M) — large Apache-2.0 chat LLM
-- ============================================================================
--
-- Catalog id:  qwen2.5-coder-7b-instruct-gguf   (models/catalog.json)
-- GGUF file:   Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF
--
-- The "real coder" rung — handles full single-file pages with embedded
-- CSS/JS, multi-paragraph rationale, and consistent style across long
-- outputs. Defaults to a 4K output budget and a lower temperature (0.5)
-- to favour determinism over flair when generating code.
--
-- Quant note: this entry uses Q5_K_M (not Q4_K_M like its smaller
-- siblings) — the marginally larger weights deliver visibly better
-- code completion quality at the 7B scale.
-- ============================================================================

CREATE OR REPLACE MODEL qwen25_coder_7b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 4096
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call. Qwen 2.5 Coder 7B is trained at 32K context.',
  temperature Float32 = 0.5
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. Defaults lower (0.5) than smaller siblings to favour deterministic code output.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'qwen2.5-coder-7b-instruct-gguf/2026-06-01/Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'chatml', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL qwen25_coder_7b(
  prompt String,
  max_tokens Int32 = 4096
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.5
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.qwen25_coder_7b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

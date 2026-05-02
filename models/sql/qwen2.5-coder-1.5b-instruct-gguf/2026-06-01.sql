-- ============================================================================
-- Qwen 2.5 Coder 1.5B Instruct (GGUF Q4_K_M) — small Apache-2.0 chat LLM
-- ============================================================================
--
-- Catalog id:  qwen2.5-coder-1.5b-instruct-gguf   (models/catalog.json)
-- GGUF file:   Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF
--
-- LlamaSharp-backed GGUF dispatch via the llama_chat / llama_generate
-- scalars. Two registrations share one weights file:
--
--   * qwen25_coder_1_5b_chat (ChatCompleter) — primary surface, owns the
--     GGUF session via the USING clause. Takes a multi-turn message array.
--   * qwen25_coder_1_5b      (TextGenerator) — simple view that delegates
--     to qwen25_coder_1_5b_chat with a one-element [{role:'user',
--     content:prompt}] message list. No USING clause: the chat model's
--     session is the only loaded copy, so the delegating model costs zero
--     additional VRAM.
--
-- Chat template: ChatML (`<|im_start|>` / `<|im_end|>`). The runtime
-- ignores the `template` argument for prompt construction — llama.cpp's
-- native template engine reads the GGUF's embedded ChatML template — but
-- the template name still selects the stop-sequence list used as a
-- safety net beneath llama.cpp's EOG check.
-- ============================================================================

CREATE OR REPLACE MODEL qwen25_coder_1_5b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 1024
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call. Qwen 2.5 supports up to 32K context natively; keep modest unless you need long outputs.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy (most predictable), 0.7 = balanced default, >=1.5 = very random / creative.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'qwen2.5-coder-1.5b-instruct-gguf/2026-06-01/Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'chatml', max_tokens, temperature)
END;

-- Delegating TextGenerator surface. No USING clause — the body produces
-- its result entirely by calling qwen25_coder_1_5b_chat above, so zero
-- additional weights load.
CREATE OR REPLACE MODEL qwen25_coder_1_5b(
  prompt String,
  max_tokens Int32 = 1024
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.qwen25_coder_1_5b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

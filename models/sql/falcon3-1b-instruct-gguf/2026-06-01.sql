-- ============================================================================
-- Falcon3 1B Instruct (GGUF Q4_K_M) — small TII-licensed chat LLM
-- ============================================================================
--
-- Catalog id:  falcon3-1b-instruct-gguf       (models/catalog.json)
-- GGUF file:   Falcon3-1B-Instruct-q4_k_m.gguf
-- License:     Falcon LLM License 2.0
-- Upstream:    https://huggingface.co/tiiuae/Falcon3-1B-Instruct-GGUF
--
-- Technology Innovation Institute (UAE) trained 1B chat model. Yet
-- another family corner — distinctive vocabulary and style worth
-- comparing against the Meta / Microsoft / IBM / Alibaba entries.
--
-- Chat template: ChatML (`<|im_start|>` / `<|im_end|>`). Same template
-- as Qwen 2.5 Coder — both families fine-tuned on the OpenAI-originated
-- ChatML format. llama.cpp's native template engine drives prompt
-- construction; the `template` argument selects stop-sequences.
-- ============================================================================

CREATE OR REPLACE MODEL falcon3_1b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 256
    CHECK (max_tokens BETWEEN 1 AND 4096)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy, 0.7 = balanced default.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'falcon3-1b-instruct-gguf/2026-06-01/Falcon3-1B-Instruct-q4_k_m.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'chatml', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL falcon3_1b(
  prompt String,
  max_tokens Int32 = 256
    CHECK (max_tokens BETWEEN 1 AND 4096)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.falcon3_1b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

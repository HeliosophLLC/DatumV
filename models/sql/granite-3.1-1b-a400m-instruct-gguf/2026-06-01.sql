-- ============================================================================
-- IBM Granite 3.1 1B A400M Instruct (GGUF Q4_K_M) — small Apache-2.0 MoE LLM
-- ============================================================================
--
-- Catalog id:  granite-3.1-1b-a400m-instruct-gguf   (models/catalog.json)
-- GGUF file:   granite-3.1-1b-a400m-instruct-Q4_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/bartowski/granite-3.1-1b-a400m-instruct-GGUF
--
-- IBM's mixture-of-experts entry: 1B total parameters, 400M active per
-- token. Distinct "enterprise-y" instruction tuning — frequently
-- bullet-pointed, careful with caveats. Fully unencumbered for
-- commercial use under Apache-2.0.
--
-- Chat template: Granite (`<|start_of_role|>` / `<|end_of_role|>` /
-- `<|end_of_text|>`). Verbose role markers but structurally similar to
-- ChatML. llama.cpp's native template engine drives prompt
-- construction; the `template` argument selects stop-sequences.
-- ============================================================================

CREATE OR REPLACE MODEL granite31_1b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 256
    CHECK (max_tokens BETWEEN 1 AND 4096)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy, 0.7 = balanced default.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'granite-3.1-1b-a400m-instruct-gguf/2026-06-01/granite-3.1-1b-a400m-instruct-Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'granite', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL granite31_1b(
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
  RETURN models.granite31_1b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

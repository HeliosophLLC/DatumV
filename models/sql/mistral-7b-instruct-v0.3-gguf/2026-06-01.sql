-- ============================================================================
-- Mistral 7B Instruct v0.3 (GGUF Q5_K_M) — Apache-2.0 general-purpose chat
-- ============================================================================
--
-- Catalog id:  mistral-7b-instruct-v0.3-gguf   (models/catalog.json)
-- GGUF file:   Mistral-7B-Instruct-v0.3-Q5_K_M.gguf
-- License:     Apache-2.0
-- Upstream:    https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF
--
-- Mistral AI's 7B-parameter instruction-tuned model at Q5_K_M (slightly
-- higher fidelity than the Q4 default). The "just give me a good
-- general-purpose local LLM" default — distinctive French-trained voice
-- vs the Meta / Microsoft / Alibaba siblings.
--
-- Chat template: Mistral (`[INST] ... [/INST]`). NB: Mistral has no
-- native system role — its embedded chat template historically rejects
-- 'system' messages. If you need a system prompt, either prepend it to
-- the first user message inline, or use a different model.
-- llama.cpp's native template engine drives prompt construction; the
-- `template` argument selects stop-sequences as a safety net beneath
-- llama.cpp's EOG check.
-- ============================================================================

CREATE OR REPLACE MODEL mistral_7b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 4096
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call. Mistral 7B v0.3 trained at 32K native context.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy, 0.7 = balanced default.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'mistral-7b-instruct-v0.3-gguf/2026-06-01/Mistral-7B-Instruct-v0.3-Q5_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'mistral', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL mistral_7b(
  prompt String,
  max_tokens Int32 = 4096
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.mistral_7b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

-- ============================================================================
-- Meta Llama 3.1 8B Instruct (GGUF Q4_K_M) — gated chat LLM, the zoo flagship
-- ============================================================================
--
-- Catalog id:  llama-3.1-8b-instruct-gguf   (models/catalog.json)
-- GGUF file:   Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf
-- License:     Llama 3.1 Community License (requires acceptance)
-- Upstream:    https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF
--
-- Meta's 8B-parameter instruction-tuned LLM. The most reliable chat
-- discipline in the zoo (Llama 3.1's `<|eot_id|>` end-of-turn marker is
-- consistently emitted on trivial prompts). Defaults sized for long-form
-- creative output: 32K context, 8K output budget. ~2 GB KV cache at
-- 32K — comfortable on a 24 GB card alongside one other model.
--
-- Chat template: Llama 3.1 (`<|start_header_id|>` / `<|end_header_id|>` /
-- `<|eot_id|>`). llama.cpp's native template engine drives prompt
-- construction from the GGUF's embedded template; the `template`
-- argument selects the stop-sequence list used as a safety net beneath
-- llama.cpp's EOG check.
-- ============================================================================

CREATE OR REPLACE MODEL llama31_8b_chat(
  messages Array<ChatMessage>,
  max_tokens Int32 = 8192
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call. Llama 3.1 was trained at 128K native context; defaults sized for chained captioning / multi-shot prompting.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature. 0.0 = greedy, 0.7 = balanced default.'
) RETURNS String
IMPLEMENTS ChatCompleter
USING 'llama-3.1-8b-instruct-gguf/2026-06-01/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf' AS s
AS BEGIN
  RETURN llama_chat('s', messages, 'llama31', max_tokens, temperature)
END;

CREATE OR REPLACE MODEL llama31_8b(
  prompt String,
  max_tokens Int32 = 8192
    CHECK (max_tokens BETWEEN 1 AND 32768)
    COMMENT 'Maximum new tokens to generate per call.',
  temperature Float32 = 0.7
    CHECK (temperature BETWEEN 0.0 AND 2.0) STEP 0.1
    COMMENT 'Sampling temperature.'
) RETURNS String
IMPLEMENTS TextGenerator
AS BEGIN
  RETURN models.llama31_8b_chat(
    [{ role: 'user', content: prompt }],
    max_tokens,
    temperature)
END;

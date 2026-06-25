# Llama 3.1 Community License Agreement

**Applies to:** All Llama 3.1 models (8B, 70B, 405B; base and Instruct variants) and their derivatives.

**Authoritative full text:** https://www.llama.com/llama3_1/license/

---

## Summary

The Llama 3.1 Community License is a **source-available** license — it permits commercial use, modification, and redistribution, but imposes specific obligations and a major-user restriction.

### What you CAN do

- Use Llama 3.1 commercially.
- Modify it, fine-tune it, and distribute your derivatives.
- Use generated outputs in your products.
- Distribute the model weights.

### Key restrictions and obligations

#### 1. The 700 million MAU clause

If, on the Llama 3.1 release date (2024-07-23), the products or services made available by you or your affiliates have **more than 700 million monthly active users** in the preceding calendar month, you must request a separate license from Meta. Meta may grant or deny this in its sole discretion. **Below 700M MAU, no additional license is required** for commercial use.

#### 2. Attribution requirement

You must:

- **Display "Built with Llama"** prominently on a related website, user interface, blog post, about page, or product documentation if you distribute or otherwise make available a Llama Materials, or any derivative works, including a product or service that uses any of them.
- **Include "Llama" at the beginning of the name** of any AI model created by training or fine-tuning Llama Materials (e.g., "Llama-MyFineTune"). This requirement is specific to *model names*, not product names.
- **Reproduce and distribute** the following notice with copies of the Llama Materials:
  > "Llama 3.1 is licensed under the Llama 3.1 Community License, Copyright © Meta Platforms, Inc. All Rights Reserved."

#### 3. Acceptable Use Policy

Use of Llama 3.1 is subject to Meta's [Acceptable Use Policy](https://www.llama.com/llama3_1/use-policy/), which prohibits:

- Violating laws or others' rights.
- Engaging in or facilitating illegal activity.
- CSAM, human trafficking, exploitation, sexual violence content.
- Discrimination, harassment, defamation.
- Unauthorized practice of professional services (legal, medical, financial advice without qualified-professional involvement).
- Critical-infrastructure operations without authorization.
- Generating malware, weapons of mass destruction, content promoting self-harm, deceptive content, etc.
- Failing to disclose AI-generated content where required, or representing it as human-authored.

#### 4. Disclaimer and limitation of liability

The Llama Materials are provided "AS IS." Meta disclaims warranties and limits liability — see canonical text for full disclaimer.

#### 5. Termination

The license terminates automatically if you violate its terms.

---

## HF gating

In addition to this license, Meta gates the Llama 3.1 repositories on HuggingFace. To download the model, users must:

1. Be logged in to HuggingFace.
2. Have accepted Meta's terms on the HF model page.
3. Have a HuggingFace access token configured locally.

The DatumV app handles this by detecting 401/403 responses and prompting the user to complete the HF acceptance flow before retrying.

---

## Acceptance

By downloading or using Llama 3.1 models, you agree to be bound by the Llama 3.1 Community License Agreement and Meta's Acceptable Use Policy. The summary above is for convenience; the legally binding text is the canonical version at the URL at the top of this file.

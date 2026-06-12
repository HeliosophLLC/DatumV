# Security Policy

## Supported versions

DatumV is pre-1.0 and only the latest minor release on the `main` branch
receives security fixes. Older releases are not patched.

| Version | Supported |
|---------|-----------|
| 0.1.x   | ✅        |
| < 0.1   | ❌        |

## Reporting a vulnerability

**Please do not open a public GitHub issue for a suspected security bug.**
That gives attackers the same information defenders have, before a fix is
out.

### Preferred channel — GitHub private vulnerability reporting

1. Go to the [Security tab](https://github.com/HeliosophLLC/DatumV/security).
2. Click **Report a vulnerability**.
3. Fill in the form.

This routes directly to maintainers, stays private until disclosure, and
GitHub assigns a CVE if the issue warrants one.

### Backup channel — email

If GitHub private reporting isn't an option for you, email
**support@heliosoph.net** with:

- A description of the issue
- Steps to reproduce (a proof-of-concept is ideal but not required)
- The version + OS + variant (CUDA / standard) you observed it on
- Your assessment of impact (data exposure, RCE, DoS, etc.)
- Whether you've disclosed to anyone else

You'll get an acknowledgement within 72 hours.

## What to expect

| Step | Timeline |
|------|----------|
| Initial acknowledgement | within 72 hours |
| Triage + severity assessment | within 7 days |
| Patch or mitigation released | depends on severity; critical issues prioritized |
| Coordinated disclosure | after a fix is available, or 90 days from report — whichever is sooner |

We'll credit you in the release notes for the fix unless you prefer to
remain anonymous.

## Scope

In scope:

- The DatumV desktop application (Electron shell + .NET backend) shipped
  via the [Releases page](https://github.com/HeliosophLLC/DatumV/releases)
- This repository's source code and CI/CD pipeline
- Catalog manifests under `models/` and `datasets/` shipped with the app

Out of scope:

- Third-party models or datasets downloaded by the app. Report those to
  their respective publishers.
- Issues that require physical access to the user's machine.
- Social-engineering attacks against maintainers.
- Bugs in upstream dependencies (ONNX Runtime, LLamaSharp, Electron,
  etc.) — please report to the upstream project. If the bug is
  reachable via DatumV in a way the upstream project isn't directly
  affected by, that *is* in scope.

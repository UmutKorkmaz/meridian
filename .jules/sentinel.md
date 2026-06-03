## 2026-06-01 - Sentinel Jitter Upgrade
**Vulnerability:** Weak random number generator (`Random.Shared.NextDouble()`) used for exponential backoff jitter calculation in `RetryPolicy`.
**Learning:** `Random.Shared` is not cryptographically secure, and its seed/state can potentially be predicted or manipulated.
**Prevention:** Used `System.Security.Cryptography.RandomNumberGenerator` to generate secure jitter.

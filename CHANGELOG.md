# Changelog

## 0.1.0

- Exceptions and logs over OTLP/HTTP JSON with exception type, message and stack trace.
- Offline buffer (bounded in memory, spilled to disk on quit, sent on next launch).
- Default redaction of keys and bearer tokens, `BeforeSend` hook, `wt.*` attributes dropped.
- Editor post-build upload of native and IL2CPP symbols to the artifacts API.

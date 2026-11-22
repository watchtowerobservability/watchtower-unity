# Changelog

## 0.1.1

- Sync with the monorepo: RUM persisted across quits and watchdog-thread hang reports.

- RUM to `/v1/rum/events`: scene views, the cold launch vital (`TrackRum`), slow and frozen frames per scene and `app_hang` for a frame of `HangSeconds` or more. Events are also written to disk (newest 200) on pause and quit and sent on the next launch. A watchdog thread reports a hang that never ends once `HangSeconds` pass, with the duration so far.
- Requests carry `X-Watchtower-App-Id` (`AppId`, default `Application.identifier`): list it on the publishable key's app ids or the edge answers 403.

## 0.1.0

- Exceptions and logs over OTLP/HTTP JSON with exception type, message and stack trace.
- Offline buffer (bounded in memory, spilled to disk on quit, sent on next launch).
- Default redaction of keys and bearer tokens, `BeforeSend` hook, `wt.*` attributes dropped.
- Editor post-build upload of native and IL2CPP symbols to the artifacts API.

# BbgBridge

## Goal

Let internal web apps, served over HTTPS from the company cloud, query the Bloomberg Desktop API of the user logged
in on the same PC, with maximum security. The bridge is a small .NET console app listening on loopback. It must stay
minimal and trivial to read for a .NET developer: flat layout, few files, no comments in code (explanations go in
`README.md`).

## Decisions

- .NET 10, ASP.NET Core minimal hosting, `win-x64`, single-file self-contained publish.
- Security gate: listen on `127.0.0.1` only (hard-coded) and refuse non-loopback clients; exact `Origin` allowlist
  (`https://` only outside mock mode); `Host` must be `127.0.0.1:<port>`.
- Closed protocol: `ReferenceDataRequest`, `HistoricalDataRequest`, `Subscribe`, `Unsubscribe`. Unknown JSON
  properties are rejected. Field names `[A-Za-z0-9_]`. A `Subscribe` security cannot contain `?` or start with `//`.
- Reference data: one synchronous session, one request at a time, lazy connection, session reset on any failure.
- Real time: a separate asynchronous session, `interval=1.0`, lock-free event handler, a bounded outbox per
  connection. `SessionTerminated` is pushed to subscribers.
- In-memory cache (`CacheSeconds`, default 3600) keyed by the request without its `id` and `maxAgeSeconds`.
  Replies carry `asOf` (UTC time Bloomberg was queried). `maxAgeSeconds` per request sets the oldest acceptable
  cached answer (`0` forces a Bloomberg call).
- Daily budget of data points (securities × fields), `MaxDataPointsPerDay` default 250000: placeholder, to confirm
  with Bloomberg. It is the bridge's own protection, not Bloomberg's accounting.
- Docs present `Subscribe` as an option, never as the recommended way to get live prices (user's choice).
- Limits: 1000 securities, 25 fields, 20 overrides, 3660 days of history, 1000 live securities in total, 64 KB
  messages.
- Mock mode (`--mock`) exists only in Debug builds.
- `Bloomberg.Blpapi` 3.26.9.1 pinned, from Bloomberg's NuGet feed (classic managed build, `net8.0` lib, not FFI).
  The feed needs `nuget.blpapi.bloomberg.com` and `blpapi.bloomberg.com`.
- Out of scope: the frontend and the cloud backend, any persistence of Bloomberg data, B-PIPE / Server API,
  Linux / macOS.
- Work is committed and pushed on `main` (user's choice).

## Layout

| File | Role |
|---|---|
| `Program.cs` | Host, security gate, WebSocket loop, cache, daily budget |
| `Protocol.cs` | `BridgeRequest` with validation, `BridgeMessages` for replies and pushes |
| `BloombergRequests.cs` | `//blp/refdata` requests |
| `BloombergSubscriptions.cs` | `//blp/mktdata` subscriptions |
| `BloombergJson.cs` | Bloomberg `Element` to JSON |
| `FakeBloomberg.cs` | Mock mode |

## Findings

- SDK 3.26: the `string` name overloads are obsolete, use `Name`. `SessionOptions` clashes with
  `Microsoft.AspNetCore.Builder.SessionOptions`: aliased in both Bloomberg files.
- Without a Terminal, `Session.Start` fails after 6 to 8 s; the bridge answers "Bloomberg unavailable" and retries on
  the next request.
- The publish folder also contains `aspnetcorev2_inprocess.dll` (IIS module from the runtime pack, unused).
- Cloud environment: `git commit` is denied when run directly through Bash; use the `.work/runner.sh` job runner.
- Cloud environment: repository settings (for example the default branch) cannot be changed through `gh`, and
  remote branches cannot be deleted with `git push --delete`: the proxy answers 403. The user does both on GitHub.

## Status

- Done: builds in Debug and Release with zero warnings (`TreatWarningsAsErrors`). Tested in the cloud container:
  security gate, validation, mock mode, cache, `asOf`, `maxAgeSeconds`, daily budget, behaviour without a Terminal.
- Open: a maximal `HistoricalDataRequest` (1000 securities × 25 fields × 10 years) is about 65 million values, far
  more than its 25000 budget points suggest; proposed to the user: cap securities × fields × days per request.
- Not verified: live Terminal behaviour (string format of dates, subscription events, reconnection).
- Next: the user runs `dotnet run` on a Terminal PC with the web app origin in `AllowedOrigins`, then the console
  snippet from `README.md`. Confirm `MaxDataPointsPerDay` with Bloomberg.

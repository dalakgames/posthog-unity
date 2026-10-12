# Dalak's changes to posthog-unity

This is PostHog's Unity SDK with the changes listed here and nothing else. Each `dalak/vX.Y.Z`
branch is PostHog's `vX.Y.Z` tag plus these changes, so the whole difference is always

```bash
git diff vX.Y.Z dalak/vX.Y.Z
```

A release of ours is a tag `vX.Y.Z-dalak.N` on that branch. The same string is the package
version and `SdkInfo.Version`, so every event says which one sent it (`$lib_version`).

## The changes

### 1. The background flush deletes its events without waiting for the player loop

**The fault.** `PostHogSDK.OnAppBackground` flushes the queue with a coroutine. A coroutine
continues only when Unity's player loop runs, and the loop stops while the app is in the
background. The request is sent and answered, but the events are deleted from disk only when the
app is next in the foreground. An app killed in the background keeps events the server already
has and sends them again on its next launch, with the same uuid. PostHog stores the second copy:
its merge of identical events needs an identical timestamp, which a resend does not have
(`sent_at` differs), and is not guaranteed even then.

**How it shows.** Rows in `events` that share a `uuid`, the copies hours apart in arrival, in
every event a game sends shortly before the player leaves it.

**The change.** That one flush runs on a thread pool thread with `HttpClient`, and deletes the
events when the answer arrives. Every other flush is PostHog's own coroutine, untouched.

| File | What |
|---|---|
| `com.posthog.unity/Runtime/Core/BackgroundFlush.cs` (new, with its `.meta`) | `EventQueue.FlushOnBackground`, `EventQueue.FlushBlocking`, `NetworkClient.SendBatchBlocking` |
| `com.posthog.unity/Runtime/Core/EventQueue.cs` | `class` → `partial class` |
| `com.posthog.unity/Runtime/Core/NetworkClient.cs` | `class` → `partial class` |
| `com.posthog.unity/Runtime/PostHogSDK.cs` | `OnAppBackground` calls `FlushOnBackground()` where it called `Flush()` |
| `tests/PostHog.Unity.Tests/BackgroundFlushTests.cs` (new) | its tests |

**What it does not cover.** A flush of the timer or of the batch threshold that is still waiting
for its answer when the app goes to the background is PostHog's coroutine, and can still be sent
twice. On WebGL, which has no threads, the background flush is the coroutine as before.

**Dropping it.** When PostHog's release no longer resends after a background kill, delete the
new files and revert the three lines.

### 2. The version

`com.posthog.unity/package.json` and `com.posthog.unity/Runtime/Utilities/SdkInfo.Generated.cs`
carry the `-dalak.N` version.

## Moving to a new PostHog release

1. `git fetch upstream --tags`, then `git checkout -b dalak/vNEW vNEW`.
2. `git cherry-pick` the commits of the previous `dalak/` branch (`git log vOLD..dalak/vOLD`).
3. `FlushBlocking` mirrors PostHog's `EventQueue.FlushCoroutine`: read that method in the new
   release and carry over whatever changed in how a result is handled.
4. Set the version to `NEW-dalak.1`, run `bin/test`, tag `vNEW-dalak.1`.
5. Update this file if the list of touched files changed.

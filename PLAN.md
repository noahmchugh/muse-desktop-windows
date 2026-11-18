# Improvement Plan — Muse Desktop for Windows (fork)

Baseline: upstream `main` at `faad2d8` (v2.3.1), the exact commit covered by a
full line-by-line source audit on 2026-11-17. This fork exists to fix what that
audit found, make the app buildable and usable on x64 machines, and make it a
good voice client for talking to Muse while doing something else (e.g. a
full-screen game) on the same PC. Work happens on the `improvements` branch.

## A. Build and release

1. Add a `win-x64` publish path (`dotnet publish -c Release -r win-x64
   --self-contained`). The only upstream release asset is ARM64, which does not
   run on x64 machines at all.
2. Pin the toolchain: add `global.json` (SDK version) and a
   `packages.lock.json`, so a build is reproducible instead of depending on
   whatever SDK happens to be installed that day.
3. Add a GitHub Actions workflow on `windows-latest` that builds both
   `win-x64` and `win-arm64` and attaches the zips to a release. This also
   gives every change a real Windows compile check, which a Linux dev box
   cannot do for a WinForms/WebView2 app.

## B. Security fixes from the audit

Each item names the audit finding and the file it lives in.

1. **H1 — Bridge binds every network interface over plaintext HTTP.**
   `BridgeServer.cs`: `IPAddress.Any` → `IPAddress.Loopback`. The documented
   reliable usage (SSH in over a mesh VPN, then call `127.0.0.1:8765` locally)
   keeps working; LAN exposure and on-path token sniffing go away.
2. **M1 — Auth is checked only after the request body is read, with no
   concurrency limit.** `BridgeServer.cs`: check the `Authorization` header as
   soon as headers are parsed, before allocating the body buffer; add a
   semaphore (16 concurrent connections) and a per-connection overall
   deadline.
3. **M2 — Audit log grows forever and is read whole on every `/log` call.**
   `BridgeServer.cs`: rotate `bridge-log.jsonl` at 5 MB, keep 3 files, and
   read only the tail when serving `/log`.
4. **M4 — WebView navigation is unrestricted; new-window links re-navigate
   inside the signed-in profile.** `MainForm.cs`, `ExpressForm.cs`: in
   `NavigationStarting` / `NewWindowRequested`, allow `muse.ai` and the
   sign-in hosts the login flow needs; open everything else in the user's
   default browser. Same pattern for the Express window with Adobe hosts.
5. **M5 — `/resolve/python` concatenates a caller-supplied `action` into a
   command line unquoted.** `BridgeApps.cs`: allow-list the documented
   actions in C# and pass arguments via `ArgumentList`. Same fix shape for
   `explorer.exe /select,"<path>"` in `BridgeServer.cs`.
6. **L1 — Token comparison is not constant-time.** `BridgeServer.cs`: use
   `CryptographicOperations.FixedTimeEquals`.
7. **M3 — Token file uses default ACLs and any pre-planted file of 24+
   characters is adopted as the token.** `BridgeServer.cs`: set an
   owner-only ACL at creation; accept an existing token only if it is
   exactly 64 hex characters.
8. **L7 — No single-instance guard.** `Program.cs` / `MainForm.cs`: named
   mutex; a second launch focuses the existing window instead of starting a
   second window whose bridge silently failed to bind.
9. **L6 — `/files/search` can loop on directory junction cycles.**
   `BridgeServer.cs`: add a visited-set and a depth cap to the walk.

## C. Voice-first improvements (the reason for this fork)

The upstream app is a chat window. These changes make it usable as a
hands-busy voice client:

1. **Explicit microphone permission handling.** Handle WebView2
   `PermissionRequested` in `MainForm.cs`: allow microphone for the Muse
   origin only, deny everything else, and persist the choice so the prompt
   does not reappear every session.
2. **Global hotkeys.** `RegisterHotKey` for (a) push-to-talk / bring-to-front
   and (b) mute toggle, both working while another app (a game) has focus.
   Defaults configurable in a small settings file; no hotkey is grabbed
   until the user enables it.
3. **Tray-first behaviour.** Minimizing goes to the tray and the WebView
   session (and any live voice call) stays alive; the tray menu gains
   Mute and Pause Bridge entries next to the existing items.
4. **Opt-in start on login.** A tray toggle, off by default, that registers
   the app under the user's `Run` key. Deliberately opt-in: the upstream
   design has no persistence, and that stays true unless the user turns
   this on.
5. **Reconnect resilience.** If the WebView navigation fails or the page
   reloads mid-call, restore the prior chat URL instead of landing on a
   blank or generic page.

## D. Testing

1. CI build green on `windows-latest` for both architectures.
2. Bridge smoke tests against a local build: `/health`, bad-token rejection
   before body read, oversized/concurrent connection behaviour, log
   rotation at the size cap, navigation allowlist (external link opens in
   the default browser, not in-app).
3. Manual pass on a real x64 PC: fresh install, sign-in persistence,
   microphone prompt appears once, voice call stays up with the window
   minimized to tray, global hotkeys work while a full-screen app has
   focus, second launch focuses the first window.

## E. Upstream pull requests

Give the fixes back to the upstream project (`treshepard/muse-desktop-windows`)
as small, reviewable PRs against its `main`:

1. **PR 1 — security and robustness:** section B, nothing else. Each commit
   is one finding, with the reasoning in the commit message.
2. **PR 2 — build and voice:** the x64/CI work in section A and the voice
   items in section C, so the maintainer can take the fixes without the
   features if he prefers.

Commits in this repo are authored as Noah McHugh. Nothing in the code,
comments, or commit messages references how the work was produced.

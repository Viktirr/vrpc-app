# installer-ui

VRPC's first-run installer and uninstaller panel: a borderless, alpha-blended
overlay that shows the consent text, the install steps and the result.

It is a **separate process**, not a library. `VRPC.exe` spawns it and speaks one
JSON object per line over stdin/stdout. The parent owns every string the user
reads and every action taken; this process owns the window, the hit-testing and
exactly one decision per session.

## Status

The panel builds and runs. VRPC does not spawn it yet — `SetupEntry` still
constructs `ConsolePresenter` — and nothing puts it in `payload.json`.

## Screens

Consent (layout-sized license text, Install/Cancel) → progress (ordered steps
and a bar) → done or failed (message and Dismiss). Enter activates the primary
button, Escape cancels on the consent screen and dismisses a terminal screen,
and closing the window on the consent screen counts as a cancel.

## Protocol

Parent → child on stdin:

| event | payload |
|---|---|
| `consent` | `text`: license and terms, owned by the parent |
| `progress` | `text`: one completed step |
| `finish` | `text`: success message |
| `fail` | `text`: failure message |

Child → parent on stdout:

| event | payload |
|---|---|
| `hello` | `proto`: protocol major version, always the first line |
| `ready` | none: the first frame has been presented |
| `decision` | `value`: `install` or `cancel`, exactly once |
| `error` | `message`: the UI cannot be used |
| `tick` | none: heartbeat every 2 s after `ready` |

Ordering is `hello` → `ready` → `consent` → `decision` → `progress`…
→ `finish` or `fail` → the user dismisses. `ready` is a promise about pixels, so
the parent shows nothing before it. A `decision` line is the only evidence of a
user's choice; an exit code never is (0 dismissed, 3 canceled, 4 UI failed).

The parent applies three timeouts: 5 s for `ready`, no limit on the decision (a
human is reading the license), and 6 s of stdout silence after `ready` as a
stall — three missed heartbeats. Every failure falls back to the console
installer.

## Build

```
cargo build --release --locked
```

Requires a Vulkan 1.3 device with win32 external-memory support and
DirectComposition, and it is Windows-only. There is no software renderer: a
machine that cannot run it gets the console installer.

`dotnet build` and `dotnet publish` do not touch this crate; only `cargo` and
the packaging script do.

## Dependencies

The overlay framework (Argonsky) is consumed at a pinned git revision in
`Cargo.toml`, and `Cargo.lock` is committed. The framework is not published, so
a revision is the reproducible form; a path dependency would tie the build to
one machine's layout.

To update it: push the framework change, bump the `rev`, run `cargo build` once
to rewrite the lock (a `--locked` build refuses until it is up to date), re-run
the verification hooks below, and commit `Cargo.toml` and `Cargo.lock` together.

To work on the framework and the panel at the same time, override the source
locally rather than editing `Cargo.toml`. Create `.cargo/config.toml` next to
this file:

```toml
paths = [
  "<argonsky>/crates/overlay-render",
  "<argonsky>/crates/overlay-backend-vulkan",
  "<argonsky>/crates/overlay-shell",
  "<argonsky>/crates/overlay-text",
  "<argonsky>/crates/overlay-draw",
  "<argonsky>/crates/overlay-ui",
  "<argonsky>/crates/overlay-verify",
  "<argonsky>/crates/overlay-core",
]
```

List every framework crate in the graph. A partial list compiles two copies of
the unlisted ones and their types stop matching at the boundary. The file is
machine-local and gitignored.

## Verification hooks

Not used by the parent, but the cheapest way to test the panel without a full
install:

```
--probe                  device report as JSON, no window
--dump <png>             read back a presented frame
--demo --auto install    drive the whole flow with no parent
--auto cancel            synthesize the cancel click
--auto-dismiss <ms>      dismiss a terminal screen after a delay
--exit-after <ms>        exit <ms> after the first presented frame
--alpha <0..1>           panel background opacity
--simulate-init-failure  hello, then exit 0 with no UI (the silent-success trap)
--simulate-hang          never become ready
--simulate-stall         become ready, then go silent
```

Environment equivalents: `VRPC_UI_AUTO` (`install` or `cancel`),
`VRPC_UI_AUTO_DISMISS_MS`, `OVERLAY_VALIDATION` (Vulkan validation).

## What it deliberately does not do

No assets (the UI font is read from `%WINDIR%\Fonts` at runtime), no license
text (the parent supplies it), no install logic, no registry or network access,
no `unsafe`, and no `unwrap`/`expect`/`panic!`. On a machine where the renderer
cannot start it reports `error` and exits 4, which is the parent's cue to fall
back to the console.

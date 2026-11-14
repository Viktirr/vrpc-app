# installer-ui

VRPC's installer and uninstaller panel: a borderless, alpha-blended overlay that
shows the consent text, the install steps and the result.

It is a **separate process**, not a library. `VRPC.exe` spawns it and speaks one
JSON object per line over stdin/stdout. The parent owns every string the user
reads, every button label and every action taken; this process owns the window,
the hit-testing and exactly one decision per prompt.

## Status

The panel is wired in. `SetupEntry` creates it on Windows when a human is at the
keyboard, `InstallEngine` drives it, and `build-release.ps1` builds it with cargo
and ships it in the payload next to `VRPC.exe`. Every failure falls back to the
console installer, which stays the accessible path: a Vulkan window has no
accessibility tree, and Linux has no overlay at all.

## Screens

A prompt (consent, or a question asked mid-flow) with up to three buttons, then
progress (ordered steps and a bar), then done or failed. Button labels, styles
and the copy come from the parent, so the same screen is an install, an update, a
repair or an uninstall:

| plan | buttons |
|---|---|
| not installed | `Close` `Install` |
| update available | `Close` `Uninstall` `Update` |
| up to date | `Close` `Uninstall` `Repair` |
| uninstalling | `Close` `Uninstall` |

Enter activates the primary button, Escape cancels a prompt and answers "no" to
a question, and closing the window on a prompt counts as a cancel. Closing the
window on a *question* is also "no": the flow still needs an answer.

## Protocol

Parent → child on stdin:

| event | fields |
|---|---|
| `consent` | `title`, `subtitle`, `text`, `close`, `primary`, optional `secondary` |
| `question` | the same shape; the answer is `yes`/`no` |
| `progress` | `title`, `text`, `done`, `total` |
| `finish` | `title`, `text` |
| `fail` | `title`, `text` |

A button is `{"label": "...", "style": "primary"|"danger"|"ghost"}`.

Child → parent on stdout:

| event | fields |
|---|---|
| `hello` | `proto`: protocol major version, always the first line |
| `ready` | none: the first frame has been presented |
| `decision` | `value`: `proceed` \| `uninstall` \| `cancel`, exactly once |
| `answer` | `value`: `yes` \| `no`, once per question |
| `error` | `message`: the UI cannot be used |
| `tick` | none: heartbeat every 2 s after `ready` |

`proceed` rather than `install` because the same button installs, updates and
repairs. Ordering: `hello` → `ready` → `consent` → `decision` →
(`progress` \| `question` → `answer`)\* → `finish` or `fail` → dismissed. `ready`
is a promise about pixels, so the parent shows nothing before it. A `decision`
line is the only evidence of a user's choice; an exit code never is (0 dismissed,
3 canceled, 4 the UI failed or its failure screen was dismissed).

The parent applies three timeouts: 5 s for `ready`, no limit on a decision or an
answer (a human is reading), and 6 s of stdout silence after `ready` as a stall —
three missed heartbeats. Every failure falls back to the console installer.

## Build

```
cargo build --release --locked
```

`build-release.ps1` runs exactly that and copies the exe into the package before
`payload.json` is hashed. `dotnet build` and `dotnet publish` do not touch this
crate.

Requires a Vulkan 1.3 device with win32 external-memory support and
DirectComposition, and it is Windows-only. There is no software renderer: a
machine that cannot run it gets the console installer.

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
--demo --auto proceed    drive the whole flow with no parent
--auto proceed           synthesize the consent click
--auto uninstall         synthesize the third button (waits for the parent's prompt)
--auto-answer yes|no     answer a question instead of clicking it
--auto-delay <ms>        wait this long after the first present before clicking,
                         so the parent's copy is on screen first
--auto-dismiss <ms>      dismiss a terminal screen after a delay
--exit-after <ms>        exit <ms> after the first presented frame
--alpha <0..1>           panel background opacity
--simulate-init-failure  hello, then exit 0 with no UI (the silent-success trap)
--simulate-hang          never become ready
--simulate-stall         become ready, then go silent
```

Environment equivalents, because the parent's environment is inherited for free:
`VRPC_UI_AUTO` (`proceed` \| `uninstall` \| `cancel`), `VRPC_UI_AUTO_ANSWER`
(`yes` \| `no`), `VRPC_UI_AUTO_DELAY_MS`, `VRPC_UI_AUTO_DISMISS_MS`,
`VRPC_UI_DUMP` (a path), `VRPC_UI_SIMULATE` (`init-failure` \| `hang` \|
`stall`), and `OVERLAY_VALIDATION` (`--probe` ignores `VRPC_UI_SIMULATE` on
purpose: a probe is a probe).

A snapshot dump is taken on the first presented frames, which with `--dump` means
before a parent's `consent` can arrive; pipe the line into the process yourself
if you need a dump of the parent's copy.

## What it deliberately does not do

No assets (the UI font is read from `%WINDIR%\Fonts` at runtime), no license
text (the parent supplies it), no install logic, no registry or network access,
no `unsafe`, and no `unwrap`/`expect`/`panic!`. On a machine where the renderer
cannot start it reports `error` and exits 4, which is the parent's cue to fall
back to the console.

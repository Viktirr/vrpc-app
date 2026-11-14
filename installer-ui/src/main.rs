//! `vrpc-installer-ui` — VRPC's first-run installer and uninstaller panel.
//!
//! A separate process, not a library: `VRPC.exe` spawns it and drives it with
//! one JSON object per line over stdin/stdout. The parent owns every string the
//! user reads (license, steps, results); this side owns the pixels, the
//! hit-testing and exactly one decision per session.
//!
//! Screens: a prompt (consent or a question), progress, done and failed. The
//! window is borderless on a premultiplied-alpha surface, so the desktop shows
//! through at `--alpha`.
//!
//! The wire format, the ordering rules, the timeouts and the failure modes are
//! in README.md. The short version: `hello` comes first, `ready` only after a
//! frame has been presented, a `decision` is the only evidence of a user's
//! choice, and `tick` every two seconds is what lets the parent tell an idle
//! panel from a wedged one.
//!
//! Verification hooks, never used by the parent:
//!
//! ```text
//! --probe                  device report, no window
//! --dump <png>             read back a presented frame
//! --demo --auto install    drive the whole flow with no parent
//! --simulate-init-failure  hello, then exit 0 with no UI
//! --simulate-hang          never become ready
//! --simulate-stall         become ready, then go silent
//! ```

use std::io::{BufRead, Write};
use std::sync::mpsc::{channel, Receiver, Sender, TryRecvError};
use std::time::{Duration, Instant};

use anyhow::Context as _;
use overlay_backend_vulkan::{VulkanBackend, VulkanOptions};
use overlay_draw::{DrawList, Rect};
use overlay_render::{
    BackendPolicy, Extent, FrameRequest, Renderer, Scene, TextureBackend, TextureBindings,
    TextureFormat, Transform,
};
use overlay_shell::{create_overlay_window, move_and_resize, surface_target, OverlayOptions};
use overlay_text::atlas::{Atlas, GrowPolicy};
use overlay_text::text::FontSystem;
use overlay_ui::core::{InputState, Reveal};
use serde_json::json;
use winit::application::ApplicationHandler;
use winit::event::{ElementState, MouseButton, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::WindowId;

/// Protocol major version. The parent refuses to drive a UI it does not
/// recognise, because a mismatched binary would otherwise fail silently.
///
/// 2 added the parent-supplied button triple, the `uninstall` decision and the
/// mid-flow question. The engine asks more than one thing (consent, then a
/// force-close when VRPC is still running), so a single hard-coded
/// Install/Cancel pair could not say "Uninstall" or ask a second question.
const PROTO: u32 = 2;

/// The post-`ready` heartbeat. Process exit and stdout close are the only other
/// liveness signals a parent has, and a lost device produces neither: the window
/// stays alive while `acquire`/`submit` failures drop frames. One line every two
/// seconds is what turns "the installer never finishes" into a timeout.
const TICK_INTERVAL: Duration = Duration::from_secs(2);

const EXIT_OK: i32 = 0;
/// The user chose Cancel. Distinct from 0 because "the window closed" must never
/// be read as "installed".
const EXIT_CANCELLED: i32 = 3;
/// The UI could not be used. The parent must fall back to console.
const EXIT_UI_FAILED: i32 = 4;

/// Window size in physical pixels. The panel is inset by [`MARGIN`] on every
/// side: that band is fully transparent, and it is what makes the rounded
/// corners and the drop shadow visible over the desktop.
const WIN_W: u32 = 640;
const WIN_H: u32 = 520;
const MARGIN: f32 = 20.0;

const TITLE_PX: f32 = 21.0;
const BODY_PX: f32 = 14.0;
const SMALL_PX: f32 = 12.5;

// ---------------------------------------------------------------------------
// Styling. The DComp target is *premultiplied* alpha, so every colour carries
// its alpha in its RGB channels already.
// ---------------------------------------------------------------------------

fn pm(rgb: [f32; 3], a: f32) -> [f32; 4] {
    [rgb[0] * a, rgb[1] * a, rgb[2] * a, a]
}

/// Fade a premultiplied colour. Scaling all four channels is the correct
/// operation on premultiplied values, unlike scaling alpha alone.
fn fade(c: [f32; 4], k: f32) -> [f32; 4] {
    [c[0] * k, c[1] * k, c[2] * k, c[3] * k]
}

const PANEL_RGB: [f32; 3] = [0.055, 0.062, 0.082];
const PRIMARY_RGB: [f32; 3] = [0.34, 0.44, 0.85];
const DANGER_RGB: [f32; 3] = [0.83, 0.25, 0.25];
const GHOST_RGB: [f32; 3] = [0.55, 0.58, 0.66];
const TEXT: [f32; 4] = [0.90, 0.94, 1.00, 1.00];
const TITLE: [f32; 4] = [1.00, 0.78, 0.35, 1.00];
const MUTED: [f32; 4] = [0.62, 0.68, 0.80, 1.00];

// ---------------------------------------------------------------------------
// Arguments
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Decision {
    /// The parent's primary action: install, update or repair.
    Proceed,
    /// The third button: remove an existing installation instead.
    Uninstall,
    Cancel,
}

impl Decision {
    /// What the parent reads on the wire. `proceed` rather than `install`
    /// because the same button updates and repairs.
    fn wire(self) -> &'static str {
        match self {
            Decision::Proceed => "proceed",
            Decision::Uninstall => "uninstall",
            Decision::Cancel => "cancel",
        }
    }

    fn parse(value: &str) -> Option<Decision> {
        match value {
            "proceed" | "install" => Some(Decision::Proceed),
            "uninstall" => Some(Decision::Uninstall),
            "cancel" => Some(Decision::Cancel),
            _ => None,
        }
    }
}

#[derive(Clone)]
struct Args {
    /// Synthesize the consent click instead of waiting for a mouse. Goes through
    /// the same `InputState` path a real click does, so it exercises the geometry.
    auto: Option<Decision>,
    /// Synthesize the answer to a question mid-flow. A verification hook: with
    /// nobody at the keyboard a question would otherwise wait forever.
    auto_answer: Option<bool>,
    /// Wait this long after the first present before the synthesized click. The
    /// click otherwise beats the parent's `consent` line by a few milliseconds,
    /// which makes a dump of the parent's copy impossible.
    auto_delay: Duration,
    /// Auto-dismiss the terminal screen after this long. A verification hook:
    /// without it, an automated run needs a human to click Dismiss.
    auto_dismiss: Option<Duration>,
    /// Drive the progress sequence locally instead of waiting for a parent.
    demo: bool,
    dump: Option<String>,
    /// Panel background opacity. The whole reason for this framework as far as
    /// VRPC is concerned: it is a number, not a theme.
    alpha: f32,
    probe: bool,
    simulate_init_failure: bool,
    simulate_hang: bool,
    /// Become ready, then stop emitting anything: a renderer that is alive but
    /// wedged. The parent's stall timeout is the only way out of this one.
    simulate_stall: bool,
    exit_after: Option<Duration>,
}

impl Default for Args {
    fn default() -> Self {
        Args {
            auto: None,
            auto_answer: None,
            auto_delay: Duration::from_millis(0),
            auto_dismiss: None,
            demo: false,
            dump: None,
            alpha: 0.92,
            probe: false,
            simulate_init_failure: false,
            simulate_hang: false,
            simulate_stall: false,
            exit_after: None,
        }
    }
}

impl Args {
    fn parse() -> Args {
        let argv: Vec<String> = std::env::args().skip(1).collect();
        let mut out = Args::default();
        // Environment first, arguments override. The parent's environment is
        // inherited for free, which makes it the one control channel that works
        // with no protocol change at all — the same reason the framework's own
        // OVERLAY_* switches are environment variables.
        if let Ok(v) = std::env::var("VRPC_UI_AUTO") {
            out.auto = Decision::parse(&v);
        }
        if let Ok(v) = std::env::var("VRPC_UI_AUTO_ANSWER") {
            out.auto_answer = match v.as_str() {
                "yes" => Some(true),
                "no" => Some(false),
                _ => None,
            };
        }
        if let Some(ms) = std::env::var("VRPC_UI_AUTO_DELAY_MS")
            .ok()
            .and_then(|v| v.parse::<u64>().ok())
        {
            out.auto_delay = Duration::from_millis(ms);
        }
        if let Ok(v) = std::env::var("VRPC_UI_DUMP") {
            if !v.is_empty() {
                out.dump = Some(v);
            }
        }
        // The failure modes are environment as well, for the same reason: a
        // parent that wants to exercise its fallback can set the variable in its
        // own environment and the child inherits it, with no protocol support.
        if let Ok(v) = std::env::var("VRPC_UI_SIMULATE") {
            match v.as_str() {
                "init-failure" => out.simulate_init_failure = true,
                "hang" => out.simulate_hang = true,
                "stall" => out.simulate_stall = true,
                _ => {}
            }
        }
        if let Some(ms) = std::env::var("VRPC_UI_AUTO_DISMISS_MS")
            .ok()
            .and_then(|v| v.parse::<u64>().ok())
        {
            out.auto_dismiss = Some(Duration::from_millis(ms));
        }
        let mut i = 0;
        while i < argv.len() {
            let value = argv.get(i + 1);
            match argv[i].as_str() {
                "--auto" => {
                    out.auto = value.and_then(|s| Decision::parse(s));
                    i += 2;
                }
                "--auto-answer" => {
                    out.auto_answer = match value.map(|s| s.as_str()) {
                        Some("yes") => Some(true),
                        Some("no") => Some(false),
                        _ => None,
                    };
                    i += 2;
                }
                "--auto-delay" => {
                    out.auto_delay = value
                        .and_then(|s| s.parse::<u64>().ok())
                        .map(Duration::from_millis)
                        .unwrap_or(out.auto_delay);
                    i += 2;
                }
                "--demo" => {
                    out.demo = true;
                    i += 1;
                }
                "--auto-dismiss" => {
                    out.auto_dismiss = value
                        .and_then(|s| s.parse::<u64>().ok())
                        .map(Duration::from_millis);
                    i += 2;
                }
                "--dump" => {
                    out.dump = value.cloned();
                    i += 2;
                }
                "--alpha" => {
                    out.alpha = value
                        .and_then(|s| s.parse::<f32>().ok())
                        .unwrap_or(out.alpha)
                        .clamp(0.05, 1.0);
                    i += 2;
                }
                "--exit-after" => {
                    out.exit_after = value
                        .and_then(|s| s.parse::<u64>().ok())
                        .map(Duration::from_millis);
                    i += 2;
                }
                "--probe" => {
                    out.probe = true;
                    i += 1;
                }
                "--simulate-init-failure" => {
                    out.simulate_init_failure = true;
                    i += 1;
                }
                "--simulate-hang" => {
                    out.simulate_hang = true;
                    i += 1;
                }
                "--simulate-stall" => {
                    out.simulate_stall = true;
                    i += 1;
                }
                other => {
                    // Loud on purpose: a parent passing an argument this build
                    // does not know has a version mismatch, and a silently
                    // ignored flag is a silently wrong installer.
                    eprintln!("vrpc-installer-ui: unknown argument {other}");
                    i += 1;
                }
            }
        }
        out
    }
}

// ---------------------------------------------------------------------------
// The parent protocol: one JSON object per line.
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ButtonStyle {
    Primary,
    Danger,
    Ghost,
}

impl ButtonStyle {
    fn parse(value: &str) -> ButtonStyle {
        match value {
            "danger" => ButtonStyle::Danger,
            "ghost" => ButtonStyle::Ghost,
            _ => ButtonStyle::Primary,
        }
    }
}

/// A button the parent labelled. The panel carries no product words of its own:
/// the same screen is an install, an update, a repair and an uninstall.
#[derive(Clone, Debug)]
struct Button {
    label: String,
    style: ButtonStyle,
}

impl Button {
    fn new(label: &str, style: ButtonStyle) -> Button {
        Button {
            label: label.to_string(),
            style,
        }
    }
}

/// One prompt: the consent screen or a question asked mid-flow. Both are the
/// same shape, so the parent only ever composes one kind of message.
#[derive(Clone, Debug)]
struct Prompt {
    title: String,
    subtitle: String,
    text: String,
    primary: Button,
    secondary: Option<Button>,
    close: String,
}

impl Prompt {
    /// What the panel shows when it has not been told anything: the `--demo`
    /// and no-parent case. The real copy always comes from the installer.
    fn fallback() -> Prompt {
        Prompt {
            title: "VRPC Setup".to_string(),
            subtitle: format!("protocol {PROTO}"),
            text: CONSENT_BODY.to_string(),
            primary: Button::new("Install", ButtonStyle::Primary),
            secondary: None,
            close: "Close".to_string(),
        }
    }

    /// The same, for a question with no parent: the second button has to mean
    /// "no" or a hand-driven run has no way to decline.
    fn question() -> Prompt {
        Prompt {
            title: "VRPC is still running".to_string(),
            subtitle: String::new(),
            text: "Continue once the running copy has been closed?".to_string(),
            primary: Button::new("Close VRPC", ButtonStyle::Danger),
            secondary: Some(Button::new("Keep running", ButtonStyle::Ghost)),
            close: "Not now".to_string(),
        }
    }
}

// ---------------------------------------------------------------------------
// The parent protocol: one JSON object per line.
// ---------------------------------------------------------------------------

#[derive(Debug)]
enum FromParent {
    /// The consent/license screen. Without this the child would have to
    /// hard-code a copy of the license and drift from it.
    Consent(Prompt),
    /// A second question, asked while the parent is mid-flow.
    Question(Prompt),
    Progress {
        title: Option<String>,
        text: String,
        done: usize,
        total: usize,
    },
    Finish { title: Option<String>, text: String },
    Fail { title: Option<String>, text: String },
    Closed,
}

fn emit(value: serde_json::Value) {
    println!("{value}");
    // Rust's stdout is line-buffered even when piped, but a UI that stalls on a
    // full pipe is worse than one that flushes twice.
    let _ = std::io::stdout().flush();
}

fn child_event(name: &str) {
    emit(json!({ "event": name }));
}

/// Read a parent prompt, keeping the panel's own defaults for anything the
/// parent left out. The parent owns the words; the panel owns the fallbacks
/// used by `--demo` and by a hand-driven run.
fn parse_prompt(value: &serde_json::Value, base: Prompt) -> Prompt {
    let string = |key: &str| {
        value
            .get(key)
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
    };
    let button = |key: &str| {
        let raw = value.get(key)?;
        let label = raw.get("label").and_then(|v| v.as_str())?;
        if label.is_empty() {
            return None;
        }
        let style = raw.get("style").and_then(|v| v.as_str()).unwrap_or("primary");
        Some(Button {
            label: label.to_string(),
            style: ButtonStyle::parse(style),
        })
    };
    Prompt {
        title: string("title").unwrap_or(base.title),
        subtitle: string("subtitle").unwrap_or(base.subtitle),
        text: string("text").unwrap_or(base.text),
        primary: button("primary").unwrap_or(base.primary),
        secondary: match value.get("secondary") {
            None => base.secondary,
            Some(serde_json::Value::Null) => None,
            Some(_) => button("secondary"),
        },
        close: string("close").unwrap_or(base.close),
    }
}

fn parse_count(value: &serde_json::Value, key: &str) -> usize {
    value
        .get(key)
        .and_then(|v| v.as_u64())
        .unwrap_or(0) as usize
}

fn spawn_parent_reader(tx: Sender<FromParent>) {
    std::thread::spawn(move || {
        let stdin = std::io::stdin();
        for line in stdin.lock().lines() {
            let Ok(line) = line else { break };
            let line = line.trim();
            if line.is_empty() {
                continue;
            }
            let Ok(value) = serde_json::from_str::<serde_json::Value>(line) else {
                continue;
            };
            let text = |key: &str| {
                value
                    .get(key)
                    .and_then(|v| v.as_str())
                    .unwrap_or_default()
                    .to_string()
            };
            let title = value
                .get("title")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string());
            let message = match value.get("event").and_then(|v| v.as_str()) {
                Some("consent") => FromParent::Consent(parse_prompt(&value, Prompt::fallback())),
                Some("question") => FromParent::Question(parse_prompt(&value, Prompt::question())),
                Some("progress") => FromParent::Progress {
                    title,
                    text: text("text"),
                    done: parse_count(&value, "done"),
                    total: parse_count(&value, "total"),
                },
                Some("finish") => FromParent::Finish {
                    title,
                    text: text("text"),
                },
                Some("fail") => FromParent::Fail {
                    title,
                    text: text("text"),
                },
                _ => continue,
            };
            if tx.send(message).is_err() {
                return;
            }
        }
        let _ = tx.send(FromParent::Closed);
    });
}

// ---------------------------------------------------------------------------
// Panel state and layout
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Consent,
    Question,
    Progress,
    Done,
    Failed,
}

/// The buttons a screen offers. Every slot is optional because a progress
/// screen offers none and a result screen offers only Dismiss.
#[derive(Clone, Default)]
struct Buttons {
    primary: Option<Button>,
    secondary: Option<Button>,
    /// Always a ghost button: it declines, it never proceeds.
    close: Option<Button>,
}

impl Buttons {
    fn from_prompt(prompt: &Prompt) -> Buttons {
        Buttons {
            primary: Some(prompt.primary.clone()),
            secondary: prompt.secondary.clone(),
            close: Some(Button::new(&prompt.close, ButtonStyle::Ghost)),
        }
    }

    fn dismiss(style: ButtonStyle) -> Buttons {
        Buttons {
            primary: Some(Button::new("Dismiss", style)),
            secondary: None,
            close: None,
        }
    }
}

/// Everything the panel needs to draw, owned, so the drawing function can take
/// it alongside a mutable borrow of the font system.
struct PanelView {
    kind: Kind,
    title: String,
    subtitle: String,
    body: String,
    steps: Vec<String>,
    progress: f32,
    buttons: Buttons,
}

/// Placeholder copy. The real license/terms text belongs with the application
/// and is deliberately not reproduced here; only the load-bearing facts are,
/// because those are what the layout has to fit.
const CONSENT_BODY: &str = "PLACEHOLDER COPY — the real license, terms and privacy policy text is \
supplied by the installer, not by this panel.\n\n\
If you continue, VRPC will:\n\
  ·  extract its files to %LOCALAPPDATA%\\Viktir\\VRPCApp\n\
  ·  write the registry keys that let the browser extension reach the app\n\
  ·  add an entry to Settings so it can be removed later\n\n\
Your settings and listening history are kept when you uninstall.";

/// Where everything is. One source of truth for both drawing and hit-testing:
/// the framework's `draw_button` leaves hit-testing to the caller, so a second
/// copy of this arithmetic would be a bug waiting to happen.
#[derive(Clone, Copy)]
struct Geom {
    panel: Rect,
    primary: Option<Rect>,
    secondary: Option<Rect>,
    close: Option<Rect>,
}

impl Geom {
    fn new(extent: Extent, buttons: &Buttons) -> Geom {
        let w = extent.width as f32;
        let h = extent.height as f32;
        let panel = Rect::new(MARGIN, MARGIN, w - MARGIN * 2.0, h - MARGIN * 2.0);
        let bw = 148.0;
        let bh = 40.0;
        let by = panel.y + panel.h - bh - 26.0;
        let gap = 12.0;
        let mut right = panel.x + panel.w - 28.0;
        let mut place = |present: bool| {
            if !present {
                return None;
            }
            let rect = Rect::new(right - bw, by, bw, bh);
            right -= bw + gap;
            Some(rect)
        };
        // Right to left: the primary action is the rightmost button, the way
        // every other Windows dialog orders its buttons.
        let primary = place(buttons.primary.is_some());
        let secondary = place(buttons.secondary.is_some());
        let close = place(buttons.close.is_some());
        Geom {
            panel,
            primary,
            secondary,
            close,
        }
    }

    /// The row the buttons sit in, for anything that has to line up with them.
    fn button_row_top(&self) -> f32 {
        self.panel.y + self.panel.h - 40.0 - 26.0
    }
}

fn draw_button(
    list: &mut DrawList,
    fonts: &mut FontSystem,
    rect: Rect,
    label: &str,
    bg: [f32; 4],
    fg: [f32; 4],
) {
    list.fill_round_rect(rect, rect.h * 0.5, bg);
    let px = BODY_PX + 0.5;
    let layout = fonts.layout(label, px, 0.0);
    let width = fonts.measure(label, px);
    let x = rect.x + (rect.w - width) * 0.5;
    let y = rect.y + (rect.h - fonts.line_height(px)) * 0.5;
    list.text(&layout, x, y, fg);
}

/// A button named and styled by the parent. A ghost is a low-alpha fill; the
/// other two are solid and only brighten on hover, so the primary action reads
/// as the default even when it is an update rather than an installation.
fn draw_action_button(
    list: &mut DrawList,
    fonts: &mut FontSystem,
    rect: Rect,
    button: &Button,
    hover: bool,
    k: f32,
) {
    let (rgb, alpha) = match button.style {
        ButtonStyle::Ghost => (GHOST_RGB, if hover { 0.34 } else { 0.18 }),
        ButtonStyle::Danger => (DANGER_RGB, if hover { 1.0 } else { 0.85 }),
        ButtonStyle::Primary => (PRIMARY_RGB, if hover { 1.0 } else { 0.85 }),
    };
    draw_button(list, fonts, rect, &button.label, pm(rgb, alpha), fade(TEXT, k));
}

/// Draw the panel. `input` is read only for hover state; clicks are handled
/// separately against the same [`Geom`].
fn draw_panel(
    list: &mut DrawList,
    fonts: &mut FontSystem,
    g: &Geom,
    view: &PanelView,
    input: &InputState,
    alpha: f32,
    k: f32,
) {
    // Drop shadow: six expanding rounded rects of black at low alpha. Only
    // possible because the window is a transparent composition plane with no
    // background of its own to shadow against.
    for i in 0..6 {
        let grow = (i + 1) as f32 * 3.0;
        let a = 0.05 * (1.0 - i as f32 / 6.0);
        list.fill_round_rect(
            Rect::new(
                g.panel.x - grow,
                g.panel.y - grow + 2.0,
                g.panel.w + grow * 2.0,
                g.panel.h + grow * 2.0,
            ),
            18.0 + grow,
            pm([0.0, 0.0, 0.0], a * k),
        );
    }

    // Border first, panel over it: there is no stroke primitive, and the 1 px
    // ring that survives is the whole effect.
    list.fill_round_rect(
        Rect::new(
            g.panel.x - 1.0,
            g.panel.y - 1.0,
            g.panel.w + 2.0,
            g.panel.h + 2.0,
        ),
        19.0,
        fade(pm([1.0, 1.0, 1.0], 0.10), k),
    );
    list.fill_round_rect(g.panel, 18.0, fade(pm(PANEL_RGB, alpha), k));

    let pad = 28.0;
    let inner_w = g.panel.w - pad * 2.0;
    let mut y = g.panel.y + 26.0;

    let title = fonts.layout(&view.title, TITLE_PX, inner_w);
    list.text(&title, g.panel.x + pad, y, fade(TITLE, k));
    y += fonts.line_height(TITLE_PX) + 2.0;

    if !view.subtitle.is_empty() {
        let sub = fonts.layout(&view.subtitle, SMALL_PX, inner_w);
        list.text(&sub, g.panel.x + pad, y, fade(MUTED, k));
        y += fonts.line_height(SMALL_PX) + 14.0;
    } else {
        y += 12.0;
    }

    match view.kind {
        Kind::Consent | Kind::Question | Kind::Done | Kind::Failed => {
            let body = fonts.layout(&view.body, BODY_PX, inner_w);
            list.text(&body, g.panel.x + pad, y, fade(TEXT, k));
        }
        Kind::Progress => {
            let mut rows = 0;
            for step in &view.steps {
                let lay = fonts.layout(step, BODY_PX, inner_w);
                list.text(&lay, g.panel.x + pad, y, fade(TEXT, k));
                y += fonts.line_height(BODY_PX) + 6.0;
                rows += 1;
            }
            if rows == 0 {
                let lay = fonts.layout("Starting…", BODY_PX, inner_w);
                list.text(&lay, g.panel.x + pad, y, fade(MUTED, k));
            }
            // Progress bar: a track plus a fill, sized by the step count.
            let bar = Rect::new(g.panel.x + pad, g.button_row_top() - 30.0, inner_w, 6.0);
            list.fill_round_rect(bar, 3.0, fade(pm([1.0, 1.0, 1.0], 0.10), k));
            let filled = bar.w * view.progress.clamp(0.0, 1.0);
            if filled > 0.5 {
                list.fill_round_rect(
                    Rect::new(bar.x, bar.y, filled, bar.h),
                    3.0,
                    fade(pm(PRIMARY_RGB, 0.95), k),
                );
            }
        }
    }

    if let (Some(rect), Some(button)) = (g.close, view.buttons.close.as_ref()) {
        draw_action_button(list, fonts, rect, button, input.hovering(&rect), k);
    }
    if let (Some(rect), Some(button)) = (g.secondary, view.buttons.secondary.as_ref()) {
        draw_action_button(list, fonts, rect, button, input.hovering(&rect), k);
    }
    if let (Some(rect), Some(button)) = (g.primary, view.buttons.primary.as_ref()) {
        draw_action_button(list, fonts, rect, button, input.hovering(&rect), k);
    }
}

// ---------------------------------------------------------------------------
// The frame loop
// ---------------------------------------------------------------------------

struct Ui {
    window: winit::window::Window,
    backend: VulkanBackend,
    fonts: FontSystem,
    /// The image atlas the scene binds. This panel draws no bitmaps, but a
    /// scene whose image texture is unbound draws nothing at all, and the
    /// binding has to exist for the glyph batches to reach the screen.
    images: Atlas<Box<dyn TextureBackend>>,
    extent: Extent,
    input: InputState,
    reveal: Reveal,
    view: PanelView,
    presented: u64,
    dumped: bool,
    first_present: Option<Instant>,
    last_frame: Option<Instant>,
}

impl Ui {
    fn new(event_loop: &ActiveEventLoop, snapshot: bool) -> anyhow::Result<Ui> {
        let options = OverlayOptions {
            title: "VRPC Setup".to_string(),
            initial_size: (WIN_W, WIN_H),
            // An installer takes clicks and focus, unlike the strip…
            click_through: false,
            activate_on_show: true,
            // …and unlike *both* existing consumers it wants a taskbar button: a
            // phantom window asking for consent is one the user cannot find again
            // after clicking away from it.
            hide_from_taskbar: false,
            exclude_from_capture: false,
            suppress_dwm_animation: true,
            ime: false,
            consume_minimize_as_hide: false,
        };
        let window = create_overlay_window(event_loop, &options)?;

        if let Some(monitor) = event_loop.primary_monitor() {
            let size = monitor.size();
            let x = (size.width.saturating_sub(WIN_W) / 2) as i32;
            let y = (size.height.saturating_sub(WIN_H) / 2) as i32;
            move_and_resize(&window, x, y, WIN_W, WIN_H);
        }
        window.set_visible(true);

        let surface = surface_target(&window).context("the panel needs a window surface")?;
        let backend = VulkanBackend::new(
            &VulkanOptions {
                app_name: "vrpc-installer-ui".to_string(),
                enable_validation: std::env::var("OVERLAY_VALIDATION").is_ok(),
                policy: BackendPolicy::Auto,
            },
            &surface,
            Extent::new(WIN_W, WIN_H),
        )?;
        let extent = backend.extent();

        // Atlases come from the renderer, so the text stack never names a
        // device. The factory is only needed here: every atlas owns its own
        // device handles afterwards, and the renderer resolves their ids when
        // the scene is drawn.
        let atlases = backend.atlas_factory();
        let fonts = FontSystem::new(&*atlases, load_fonts()?, Vec::new(), None)?;
        // A tiny image atlas: this panel draws no bitmaps, but an unbound image
        // texture makes the scene empty, and an empty scene draws nothing.
        let images = Atlas::new(
            atlases.create(TextureFormat::Rgba8, 64, 64)?,
            4096,
            GrowPolicy::SmallerSideFirst,
        );

        Ok(Ui {
            window,
            backend,
            fonts,
            images,
            extent,
            input: InputState::default(),
            // A dump is a snapshot: start fully revealed, so the capture is the
            // settled panel and not a frame of the entrance animation. This is
            // what `Reveal::snap` exists for.
            reveal: if snapshot {
                Reveal::new(1.0)
            } else {
                Reveal::new(0.0)
            },
            view: {
                let prompt = Prompt::fallback();
                PanelView {
                    kind: Kind::Consent,
                    title: prompt.title.clone(),
                    subtitle: prompt.subtitle.clone(),
                    body: prompt.text.clone(),
                    steps: Vec::new(),
                    progress: 0.0,
                    buttons: Buttons::from_prompt(&prompt),
                }
            },
            presented: 0,
            dumped: false,
            first_present: None,
            last_frame: None,
        })
    }

    fn geom(&self) -> Geom {
        Geom::new(self.extent, &self.view.buttons)
    }

    /// Render one frame. Returns whether another frame is owed for the entrance
    /// animation.
    fn frame(&mut self, now: Instant, alpha: f32) -> anyhow::Result<bool> {
        let dt = self
            .last_frame
            .map(|t| now.duration_since(t).as_secs_f32())
            .unwrap_or(0.0)
            .min(0.05);
        self.last_frame = Some(now);
        let animating = self.reveal.tick(1.0, dt);

        // The bindings are read per frame and carried in the scene, so a glyph
        // atlas that grew is picked up by the next frame without a rebind: the
        // renderer resolves the current ids when it draws.
        let (glyph, sampler, _glyph_gen) = self.fonts.atlas();
        let (color_glyph, _color_gen) = self.fonts.color_atlas();

        let g = self.geom();
        let k = self.reveal.value();
        let mut list = DrawList::new(self.extent.width as f32, self.extent.height as f32);
        draw_panel(
            &mut list,
            &mut self.fonts,
            &g,
            &self.view,
            &self.input,
            alpha,
            k,
        );
        let (verts, batches) = list.finish();

        let textures = TextureBindings {
            glyph,
            color_glyph,
            image: self.images.texture(),
            sampler,
        };
        // The panel carries no animation transform: the entrance fade is applied
        // to the colours on the CPU, exactly as before.
        let scene = Scene::new(&verts, &batches, textures, Transform::IDENTITY);

        // The backend owns the fence wait, acquisition, the transparent clear,
        // the barriers, the submit and the present. A skipped frame means there
        // was no target (a suspended presenter); it is not a failure and it is
        // not a present.
        let status = self
            .backend
            .render_frame(FrameRequest::transparent(), &mut |encoder| {
                encoder.draw(&scene)
            })?;

        if status.is_presented() {
            self.presented += 1;
            if self.first_present.is_none() {
                self.first_present = Some(now);
            }
        }
        Ok(animating)
    }

    fn dump(&mut self, path: &str) {
        #[cfg(feature = "verify")]
        if let Err(e) = overlay_verify::write_dump(&self.backend, path) {
            eprintln!("vrpc-installer-ui: dump failed: {e:?}");
        }
        #[cfg(not(feature = "verify"))]
        {
            let _ = path;
            eprintln!("vrpc-installer-ui: dump skipped (built without the `verify` feature)");
        }
    }

    fn destroy(mut self) {
        // Textures first: `backend.destroy()` takes the device with it, and an
        // atlas must not outlive the device that created it.
        self.fonts.destroy();
        self.images.destroy();
        self.backend.destroy();
    }
}

fn load_fonts() -> anyhow::Result<Vec<Vec<u8>>> {
    let candidates = [
        r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\tahoma.ttf",
    ];
    for path in candidates {
        if let Ok(bytes) = std::fs::read(path) {
            return Ok(vec![bytes]);
        }
    }
    anyhow::bail!("no system UI font found")
}

const DEMO_STEPS: [&str; 5] = [
    "Creating %LOCALAPPDATA%\\Viktir\\VRPCApp",
    "Checking free space on C:\\",
    "Copying 12 files",
    "Writing native-messaging manifests",
    "Creating registry keys",
];

struct App {
    args: Args,
    rx: Receiver<FromParent>,
    ui: Option<Ui>,
    exit_code: i32,
    kind: Kind,
    /// The consent prompt. Starts as the panel's own fallback and is replaced by
    /// the parent's `consent` event, so the words have exactly one owner.
    consent: Prompt,
    /// The question on screen, if one is being asked. `None` outside a question.
    question: Option<Prompt>,
    /// The `progress` title: "Installing" and "Uninstalling" are the same screen.
    progress_title: String,
    /// When the flow reached a terminal screen, for `--auto-dismiss`.
    terminal_at: Option<Instant>,
    steps: Vec<String>,
    step_total: usize,
    final_title: Option<String>,
    final_text: Option<String>,
    demo_started: Option<Instant>,
    demo_step: usize,
    decided: bool,
    ready_sent: bool,
    /// When the next heartbeat line is owed. `None` until `ready` has been sent,
    /// so a parent never sees a `tick` for a UI it has not been told about.
    next_tick: Option<Instant>,
    ticks_sent: u64,
}

impl App {
    fn new(args: Args, rx: Receiver<FromParent>) -> App {
        let consent = Prompt::fallback();
        App {
            args,
            rx,
            ui: None,
            exit_code: EXIT_OK,
            kind: Kind::Consent,
            consent,
            question: None,
            progress_title: "Working".to_string(),
            terminal_at: None,
            steps: Vec::new(),
            step_total: 0,
            final_title: None,
            final_text: None,
            demo_started: None,
            demo_step: 0,
            decided: false,
            ready_sent: false,
            next_tick: None,
            ticks_sent: 0,
        }
    }

    /// Enter a phase. Terminal phases stamp the time `--auto-dismiss` counts
    /// from, so the stamp can never drift from the screen it belongs to.
    fn set_kind(&mut self, kind: Kind) {
        self.kind = kind;
        self.terminal_at = match kind {
            Kind::Done | Kind::Failed => Some(Instant::now()),
            _ => None,
        };
        self.set_view();
    }

    fn set_view(&mut self) {
        let view = match self.kind {
            Kind::Consent => PanelView {
                kind: Kind::Consent,
                title: self.consent.title.clone(),
                subtitle: self.consent.subtitle.clone(),
                body: self.consent.text.clone(),
                steps: Vec::new(),
                progress: 0.0,
                buttons: Buttons::from_prompt(&self.consent),
            },
            Kind::Question => {
                let prompt = self.question.clone().unwrap_or_else(Prompt::question);
                PanelView {
                    kind: Kind::Question,
                    title: prompt.title.clone(),
                    subtitle: prompt.subtitle.clone(),
                    body: prompt.text.clone(),
                    steps: Vec::new(),
                    progress: 0.0,
                    buttons: Buttons::from_prompt(&prompt),
                }
            }
            Kind::Progress => PanelView {
                kind: Kind::Progress,
                title: self.progress_title.clone(),
                subtitle: String::new(),
                body: String::new(),
                steps: self.steps.clone(),
                progress: self.progress_fraction(),
                buttons: Buttons::default(),
            },
            Kind::Done => PanelView {
                kind: Kind::Done,
                title: self
                    .final_title
                    .clone()
                    .unwrap_or_else(|| "VRPC setup is complete".to_string()),
                subtitle: String::new(),
                body: self
                    .final_text
                    .clone()
                    .unwrap_or_else(|| "The application is now installed.".to_string()),
                steps: Vec::new(),
                progress: 1.0,
                buttons: Buttons::dismiss(ButtonStyle::Primary),
            },
            Kind::Failed => PanelView {
                kind: Kind::Failed,
                title: self
                    .final_title
                    .clone()
                    .unwrap_or_else(|| "VRPC setup failed".to_string()),
                subtitle: String::new(),
                body: self
                    .final_text
                    .clone()
                    .unwrap_or_else(|| "No changes were made.".to_string()),
                steps: Vec::new(),
                progress: 0.0,
                buttons: Buttons::dismiss(ButtonStyle::Danger),
            },
        };

        if let Some(ui) = self.ui.as_mut() {
            ui.view = view;
        }
    }

    /// How much of the step list is done. Clamped short of 1.0 because the last
    /// fraction of an install is the part that can still fail.
    fn progress_fraction(&self) -> f32 {
        if self.step_total == 0 {
            return 0.0;
        }
        (self.steps.len() as f32 / self.step_total as f32).clamp(0.0, 0.95)
    }

    /// Synthesize a click on `rect` through the same `InputState` path a real
    /// mouse takes, so what gets verified is the geometry and the hit-test.
    fn synth_click(&mut self, rect: Rect) {
        let Some(ui) = self.ui.as_mut() else { return };
        ui.input.set_in_window(true);
        ui.input
            .set_cursor(rect.x + rect.w * 0.5, rect.y + rect.h * 0.5);
        ui.input.on_button(true);
        ui.input.on_button(false);
    }

    fn decide(&mut self, decision: Decision, event_loop: &ActiveEventLoop) {
        if self.decided {
            return;
        }
        self.decided = true;
        emit(json!({ "event": "decision", "value": decision.wire() }));

        match decision {
            Decision::Cancel => {
                self.exit_code = EXIT_CANCELLED;
                event_loop.exit();
            }
            Decision::Proceed | Decision::Uninstall => {
                self.set_kind(Kind::Progress);
                if self.args.demo {
                    self.demo_started = Some(Instant::now());
                    self.demo_step = 0;
                    self.step_total = DEMO_STEPS.len();
                    self.steps = vec![DEMO_STEPS[0].to_string()];
                }
                self.set_view();
            }
        }
    }

    /// Answer the question on screen and go back to waiting for the parent. A
    /// answered question is terminal for itself only: the flow continues, which
    /// is why this cannot go through `decide`.
    fn answer(&mut self, yes: bool, _event_loop: &ActiveEventLoop) {
        if self.kind != Kind::Question {
            return;
        }
        emit(json!({ "event": "answer", "value": if yes { "yes" } else { "no" } }));
        self.question = None;
        self.set_kind(Kind::Progress);
    }

    fn handle_clicks(&mut self, event_loop: &ActiveEventLoop) {
        let geom = self.ui.as_ref().map(|u| u.geom());
        let (primary, secondary, close) = match (self.ui.as_ref(), geom) {
            (Some(ui), Some(g)) => (
                g.primary.map(|r| ui.input.clicked(&r)).unwrap_or(false),
                g.secondary.map(|r| ui.input.clicked(&r)).unwrap_or(false),
                g.close.map(|r| ui.input.clicked(&r)).unwrap_or(false),
            ),
            _ => (false, false, false),
        };

        match self.kind {
            Kind::Consent => {
                if primary {
                    self.decide(Decision::Proceed, event_loop);
                } else if secondary {
                    self.decide(Decision::Uninstall, event_loop);
                } else if close {
                    self.decide(Decision::Cancel, event_loop);
                }
            }
            Kind::Question => {
                if primary {
                    self.answer(true, event_loop);
                } else if secondary || close {
                    self.answer(false, event_loop);
                }
            }
            Kind::Done => {
                if primary || close {
                    self.exit_code = EXIT_OK;
                    event_loop.exit();
                }
            }
            Kind::Failed => {
                if primary || close {
                    self.exit_code = EXIT_UI_FAILED;
                    event_loop.exit();
                }
            }
            Kind::Progress => {}
        }
    }

    fn drain_parent(&mut self, event_loop: &ActiveEventLoop) {
        loop {
            match self.rx.try_recv() {
                Ok(FromParent::Consent(prompt)) => {
                    self.consent = prompt;
                    if self.kind == Kind::Consent {
                        self.set_view();
                    }
                }
                Ok(FromParent::Question(prompt)) => {
                    // A question only means something once the flow is running,
                    // and a second one cannot arrive before this one is answered.
                    if self.decided && self.kind != Kind::Question {
                        self.question = Some(prompt);
                        self.set_kind(Kind::Question);
                    }
                }
                Ok(FromParent::Progress {
                    title,
                    text,
                    done,
                    total,
                }) => {
                    if let Some(title) = title {
                        self.progress_title = title;
                    }
                    if total > 0 {
                        self.step_total = total;
                    }
                    if done > self.steps.len() + 1 {
                        // A gap means the parent skipped a step; trust the count
                        // so the bar does not stall behind the text.
                        self.step_total = self.step_total.max(done);
                    }
                    self.steps.push(text);
                    self.set_kind(Kind::Progress);
                }
                Ok(FromParent::Finish { title, text }) => {
                    self.final_title = title;
                    self.final_text = Some(text);
                    self.set_kind(Kind::Done);
                }
                Ok(FromParent::Fail { title, text }) => {
                    self.final_title = title;
                    self.final_text = Some(text);
                    self.set_kind(Kind::Failed);
                }
                Ok(FromParent::Closed) => {
                    // The parent went away. Before a decision that is fatal: a UI
                    // nobody is listening to must not sit there looking like a
                    // successful install.
                    if !self.decided && !self.args.demo && self.args.auto.is_none() {
                        emit(json!({
                            "event": "error",
                            "message": "parent closed stdin before a decision was made"
                        }));
                        self.exit_code = EXIT_UI_FAILED;
                        event_loop.exit();
                    }
                    return;
                }
                Err(TryRecvError::Empty) => return,
                Err(TryRecvError::Disconnected) => return,
            }
        }
    }

    /// The scripted self-drive `--demo` uses: no parent, so the UI plays the
    /// parent's part. Steps land 260 ms apart, then a finish, then a dismiss.
    fn tick_demo(&mut self, now: Instant, event_loop: &ActiveEventLoop) -> bool {
        if !self.args.demo {
            return false;
        }
        let Some(started) = self.demo_started else {
            return false;
        };
        let elapsed = now.duration_since(started);
        let want = (elapsed.as_millis() / 260) as usize;

        if want < DEMO_STEPS.len() && want != self.demo_step {
            self.demo_step = want;
            self.steps = DEMO_STEPS
                .iter()
                .take(want + 1)
                .map(|s| s.to_string())
                .collect();
            self.set_view();
        } else if want >= DEMO_STEPS.len() + 2 && self.kind == Kind::Progress {
            self.final_title = Some("VRPC is installed".to_string());
            self.final_text = Some(format!(
                "Installed to %LOCALAPPDATA%\\Viktir\\VRPCApp. {} steps completed.",
                DEMO_STEPS.len()
            ));
            self.set_kind(Kind::Done);
        } else if want >= DEMO_STEPS.len() + 6 && self.kind == Kind::Done {
            if let Some(rect) = self.ui.as_ref().and_then(|u| u.geom().primary) {
                self.synth_click(rect);
                self.handle_clicks(event_loop);
            }
        }
        self.kind == Kind::Progress || self.kind == Kind::Done
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.ui.is_some() {
            return;
        }
        match Ui::new(event_loop, self.args.dump.is_some()) {
            Ok(ui) => {
                self.ui = Some(ui);
                self.set_view();
            }
            Err(e) => {
                emit(json!({ "event": "error", "message": format!("{e:?}") }));
                self.exit_code = EXIT_UI_FAILED;
                event_loop.exit();
            }
        }
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        match event {
            WindowEvent::CloseRequested => {
                // Closing the window is a cancel on a prompt and an ordinary
                // dismissal on a result screen. On a question it is the same
                // "no" as the second button, because the flow still needs an
                // answer before it can continue or give up.
                if self.kind == Kind::Question {
                    self.answer(false, event_loop);
                    return;
                }
                self.exit_code = match self.kind {
                    Kind::Consent | Kind::Progress => EXIT_CANCELLED,
                    Kind::Done => EXIT_OK,
                    _ => EXIT_UI_FAILED,
                };
                event_loop.exit();
            }
            WindowEvent::CursorMoved { position, .. } => {
                if let Some(ui) = self.ui.as_mut() {
                    ui.input.set_cursor(position.x as f32, position.y as f32);
                }
            }
            WindowEvent::CursorEntered { .. } => {
                if let Some(ui) = self.ui.as_mut() {
                    ui.input.set_in_window(true);
                }
            }
            WindowEvent::CursorLeft { .. } => {
                if let Some(ui) = self.ui.as_mut() {
                    ui.input.set_in_window(false);
                }
            }
            WindowEvent::MouseInput { state, button, .. } => {
                if button == MouseButton::Left {
                    if let Some(ui) = self.ui.as_mut() {
                        ui.input.on_button(state == ElementState::Pressed);
                    }
                }
            }
            WindowEvent::KeyboardInput { event, .. } => {
                if event.state != ElementState::Pressed {
                    return;
                }
                match event.physical_key {
                    // Escape cancels a prompt, answers "no" to a question and
                    // dismisses a terminal screen.
                    PhysicalKey::Code(KeyCode::Escape) => match self.kind {
                        Kind::Consent => self.decide(Decision::Cancel, event_loop),
                        Kind::Question => self.answer(false, event_loop),
                        Kind::Done => {
                            self.exit_code = EXIT_OK;
                            event_loop.exit();
                        }
                        Kind::Failed => {
                            self.exit_code = EXIT_UI_FAILED;
                            event_loop.exit();
                        }
                        Kind::Progress => {}
                    },
                    // Enter activates the primary button.
                    PhysicalKey::Code(KeyCode::Enter) => match self.kind {
                        Kind::Consent => self.decide(Decision::Proceed, event_loop),
                        Kind::Question => self.answer(true, event_loop),
                        Kind::Done => {
                            self.exit_code = EXIT_OK;
                            event_loop.exit();
                        }
                        Kind::Failed => {
                            self.exit_code = EXIT_UI_FAILED;
                            event_loop.exit();
                        }
                        Kind::Progress => {}
                    },
                    _ => {}
                }
            }
            _ => {}
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        let now = Instant::now();
        if self.ui.is_none() {
            return;
        }

        self.drain_parent(event_loop);
        let demo_running = self.tick_demo(now, event_loop);

        // The synthesized click waits for a presented frame: `ready` promises
        // pixels, so a decision must never precede one. The optional delay lets
        // the parent's own copy arrive first, which is what makes a dump of the
        // real consent screen possible.
        if let (Some(decision), Some(g)) = (self.args.auto, self.ui.as_ref().map(|u| u.geom())) {
            let presented = self.ui.as_ref().map(|u| u.presented).unwrap_or(0);
            let due = self
                .ui
                .as_ref()
                .and_then(|u| u.first_present)
                .map(|t| now.duration_since(t) >= self.args.auto_delay)
                .unwrap_or(false);
            if presented >= 1 && due && !self.decided && self.kind == Kind::Consent {
                let rect = match decision {
                    Decision::Proceed => g.primary,
                    // Wait for the parent's prompt: the panel's own fallback has
                    // no Uninstall button, and clicking the primary instead would
                    // silently test the wrong path.
                    Decision::Uninstall => g.secondary,
                    Decision::Cancel => g.close,
                };
                if let Some(rect) = rect {
                    self.synth_click(rect);
                }
            }
        }

        // The same for a question, which the parent can ask at any point after
        // the decision, and which would otherwise need a human to answer.
        if let (Some(answer), Some(g)) = (self.args.auto_answer, self.ui.as_ref().map(|u| u.geom())) {
            let presented = self.ui.as_ref().map(|u| u.presented).unwrap_or(0);
            if presented >= 1 && self.kind == Kind::Question {
                let rect = if answer {
                    g.primary
                } else {
                    g.secondary.or(g.close)
                };
                if let Some(rect) = rect {
                    self.synth_click(rect);
                }
            }
        }

        let alpha = self.args.alpha;
        let framed = match self.ui.as_mut() {
            Some(ui) => ui.frame(now, alpha),
            None => Ok(false),
        };
        let animating = match framed {
            Ok(a) => a,
            Err(e) => {
                emit(json!({ "event": "error", "message": format!("frame failed: {e:?}") }));
                self.exit_code = EXIT_UI_FAILED;
                event_loop.exit();
                return;
            }
        };

        self.handle_clicks(event_loop);
        if let Some(ui) = self.ui.as_mut() {
            ui.input.end_frame();
            ui.window.request_redraw();
        }

        // A verification hook rather than product behaviour: with nobody at the
        // keyboard, the terminal screen would otherwise wait forever and every
        // automated run would end in a kill instead of an exit code.
        if let (Some(grace), Some(at)) = (self.args.auto_dismiss, self.terminal_at) {
            if now.duration_since(at) >= grace {
                if let Some(rect) = self.ui.as_ref().and_then(|u| u.geom().primary) {
                    self.synth_click(rect);
                    self.handle_clicks(event_loop);
                }
            }
        }

        // `ready` is a promise about pixels, so it is emitted after the first
        // successful present and never before.
        let presented = self.ui.as_ref().map(|u| u.presented).unwrap_or(0);
        if presented >= 1 && !self.ready_sent {
            self.ready_sent = true;
            child_event("ready");
            // The first tick is one interval out, so a parent that receives
            // `tick` knows the child survived a full interval of idling, not
            // merely that it got a frame out of the door.
            self.next_tick = Some(now + TICK_INTERVAL);
        }

        // The heartbeat. It runs until the process exits, including on the
        // terminal screen: after `finish`/`fail` the parent is still waiting for
        // the child to dismiss itself, and that wait deserves the same liveness
        // signal as the consent prompt did.
        if let Some(due) = self.next_tick {
            if now >= due {
                self.ticks_sent += 1;
                child_event("tick");
                self.next_tick = Some(now + TICK_INTERVAL);
            }
        }

        // The dump waits for the entrance animation to settle, or every dump is a
        // half-faded panel.
        let settled = self
            .ui
            .as_ref()
            .map(|u| u.reveal.value() > 0.99)
            .unwrap_or(false);
        let pending_dump = self.ui.as_ref().map(|u| !u.dumped).unwrap_or(false);
        if let (Some(path), Some(ui)) = (self.args.dump.clone(), self.ui.as_mut()) {
            if !ui.dumped && settled && ui.presented >= 2 {
                ui.dump(&path);
                ui.dumped = true;
            }
        }

        let deadline = self
            .args
            .exit_after
            .and_then(|budget| self.ui.as_ref().and_then(|u| u.first_present).map(|t| t + budget));
        if let Some(t) = deadline {
            if now >= t {
                event_loop.exit();
                return;
            }
        }

        let mut next: Option<Instant> = None;
        let sooner = |t: Instant, next: &mut Option<Instant>| {
            *next = Some(next.map_or(t, |n| n.min(t)));
        };
        if animating || !settled {
            sooner(now + Duration::from_millis(16), &mut next);
        }
        if demo_running || (pending_dump && !settled) {
            sooner(now + Duration::from_millis(60), &mut next);
        }
        if let Some(t) = deadline {
            sooner(t, &mut next);
        }
        // Without this the loop would sleep in `ControlFlow::Wait` until a window
        // event arrived, and an idle-but-healthy panel would look exactly like a
        // wedged one to the parent.
        if let Some(t) = self.next_tick {
            sooner(t, &mut next);
        }
        event_loop.set_control_flow(match next {
            Some(t) => ControlFlow::WaitUntil(t),
            None => ControlFlow::Wait,
        });
    }

    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        if self.ready_sent {
            // Lands in the parent's stderr tail, next to its fallback report:
            // "how many heartbeats were actually sent" is the first question
            // asked of a parent that timed out.
            eprintln!(
                "vrpc-installer-ui: exiting with code {} after {} tick(s)",
                self.exit_code, self.ticks_sent
            );
        }
        if let Some(ui) = self.ui.take() {
            ui.destroy();
        }
    }
}

/// The preflight a parent runs before showing anything: no window, no DComp,
/// just "is there a device this framework can use". `VulkanBackend::probe`
/// creates the device without a surface, so the failure text is the framework's
/// own ("vulkan: no Vulkan 1.3 GPU with a graphics queue").
///
/// This checks the device, not the presenter: the win32 external-memory
/// extensions and the LUID are only verified when a real backend is created for
/// a window. A machine that passes here can still fail at spawn, which is why
/// the parent's ready timeout is the load-bearing fallback.
fn probe() -> anyhow::Result<()> {
    let caps = match VulkanBackend::probe(&VulkanOptions {
        app_name: "vrpc-installer-ui-probe".to_string(),
        enable_validation: false,
        policy: BackendPolicy::Auto,
    }) {
        Ok(caps) => caps,
        Err(e) => {
            emit(json!({ "event": "error", "message": format!("{e}") }));
            std::process::exit(EXIT_UI_FAILED);
        }
    };
    #[cfg(feature = "verify")]
    {
        let report = overlay_verify::report::CapabilityReport::collect(&caps, None);
        println!("{}", report.to_json());
        let _ = std::io::stdout().flush();
    }
    #[cfg(not(feature = "verify"))]
    {
        let _ = &caps;
        emit(json!({ "event": "error", "message": "built without the `verify` feature" }));
        std::process::exit(EXIT_UI_FAILED);
    }
    std::process::exit(EXIT_OK);
}

fn main() -> anyhow::Result<()> {
    let args = Args::parse();

    // A probe is a probe: it must answer the capability question even when the
    // inherited environment asks for a simulated failure, because the parent
    // runs it before deciding whether to spawn this binary at all.
    if args.probe {
        return probe();
    }

    // Always the first line, before anything can fail: the parent's timeout then
    // starts from a known point rather than from "the process exists".
    emit(json!({ "event": "hello", "proto": PROTO }));

    if args.simulate_init_failure {
        // A renderer-init failure: hello, then exit 0 with no UI and no decision.
        // A parent that trusts exit codes reads this as success — which is
        // exactly what the C# fallback must not do.
        eprintln!("vrpc-installer-ui: simulating a renderer-init failure (exit 0, no UI)");
        return Ok(());
    }
    if args.simulate_hang {
        eprintln!("vrpc-installer-ui: simulating a hang before ready");
        loop {
            std::thread::sleep(Duration::from_secs(3600));
        }
    }
    if args.simulate_stall {
        // `ready`, then nothing at all: the child is alive and its stdout is
        // open, so only the heartbeat's absence can tell the parent this UI is
        // never going to make progress.
        emit(json!({ "event": "ready" }));
        eprintln!("vrpc-installer-ui: simulating a stall after ready (no ticks)");
        loop {
            std::thread::sleep(Duration::from_secs(3600));
        }
    }

    let (tx, rx) = channel();
    spawn_parent_reader(tx);

    let event_loop = EventLoop::new()?;
    event_loop.set_control_flow(ControlFlow::Wait);
    let mut app = App::new(args, rx);
    event_loop.run_app(&mut app)?;
    std::process::exit(app.exit_code);
}

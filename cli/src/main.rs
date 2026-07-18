// spirescry: minimal CLI for the spirescry HTTP bridge.
//
// Read the board with `obs`, act with `play` / `end-turn`. Output is
// pretty-printed JSON; bridge errors ({ok:false, err, msg}) go to stderr
// with a non-zero exit — 75 (EX_TEMPFAIL) for the retryable `not_ready`,
// 1 for everything else.

use std::io::Write;
use std::process::ExitCode;
use std::time::Duration;

use clap::{Parser, Subcommand};
use serde_json::{json, Value};

const DEFAULT_HTTP_TIMEOUT_MS: u64 = 70_000;
const HTTP_TIMEOUT_GRACE_MS: u64 = 10_000;
const DEFAULT_OBS_WAIT_MS: i32 = 5_000;
const PROTOCOL_VERSION: u64 = 2;

#[derive(Debug, Clone, PartialEq, Eq)]
enum CliError {
    Transient(String),
    Fatal(String),
}

impl CliError {
    fn transient(message: impl Into<String>) -> Self {
        Self::Transient(message.into())
    }

    fn fatal(message: impl Into<String>) -> Self {
        Self::Fatal(message.into())
    }

    fn message(&self) -> &str {
        match self {
            Self::Transient(message) | Self::Fatal(message) => message,
        }
    }

    #[cfg(test)]
    fn contains(&self, needle: &str) -> bool {
        self.message().contains(needle)
    }

    fn exit_status(&self) -> u8 {
        match self {
            Self::Transient(_) => 75,
            Self::Fatal(_) => 1,
        }
    }
}

impl std::fmt::Display for CliError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(self.message())
    }
}

impl std::error::Error for CliError {}

impl From<String> for CliError {
    fn from(message: String) -> Self {
        Self::fatal(message)
    }
}

impl From<&str> for CliError {
    fn from(message: &str) -> Self {
        Self::fatal(message)
    }
}

type CliResult<T> = Result<T, CliError>;

#[derive(Parser)]
#[command(name = "spirescry", version, about)]
struct Cli {
    #[arg(
        long,
        global = true,
        env = "STS2_AGENT_HOST",
        default_value = "127.0.0.1"
    )]
    host: String,
    #[arg(long, global = true, env = "STS2_AGENT_PORT", default_value_t = 7777)]
    port: u16,
    /// Trace each HTTP round-trip (request, status, wall time) on stderr
    #[arg(long, short = 'v', global = true)]
    verbose: bool,
    /// Reject the verb if the revision moved since the board was read
    #[arg(long, global = true)]
    if_rev: Option<u64>,
    /// Reject the verb if the live run id no longer matches
    #[arg(long, global = true)]
    if_run: Option<String>,
    /// Wait for action settlement or the next decision (default 5000 ms)
    #[arg(
        long,
        global = true,
        value_name = "MS",
        num_args = 0..=1,
        default_missing_value = "5000"
    )]
    follow: Option<u32>,
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Bridge liveness + current phase
    Health,
    /// Snapshot of the current phase (combat: you / hand / enemies)
    Obs {
        /// Park until the revision moves past this (from a prior rev field)
        #[arg(long, value_parser = clap::value_parser!(i64).range(0..))]
        since: Option<i64>,
        /// Max milliseconds to wait for a change (with --since)
        #[arg(long, value_parser = clap::value_parser!(i32).range(0..=60_000))]
        wait: Option<i32>,
        /// Elide the big repeats: no map graph, deck as counts-by-model
        #[arg(long)]
        compact: bool,
        /// Compact decision projection with state-derived legal verbs
        #[arg(long)]
        decision: bool,
        /// Card text keys already cached by the caller (repeatable)
        #[arg(long = "known-card", requires = "decision")]
        known_cards: Vec<String>,
    },
    /// Start a singleplayer run from the main menu
    NewRun {
        /// Character model entry (e.g. IRONCLAD)
        character: String,
        /// Run seed for reproducibility (omit for random)
        #[arg(long)]
        seed: Option<String>,
        /// Ascension level (default 0)
        #[arg(long, value_parser = clap::value_parser!(i32).range(0..))]
        ascension: Option<i32>,
    },
    /// Abandon the active run and return to the main menu
    Abandon,
    /// Choose an option by index (events and rest sites, from obs.options)
    #[command(name = "option")]
    EventOption {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        idx: i32,
    },
    /// Buy from the shop: kind is card/colorless/relic/potion/card_removal
    Buy {
        kind: String,
        #[arg(long, value_parser = clap::value_parser!(i32).range(0..))]
        idx: i32,
    },
    /// Leave the shop
    Leave,
    /// Pick an offered relic by index (treasure / relic reward)
    PickRelic {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        idx: i32,
    },
    /// Advance / close event dialogue, or leave the rewards screen
    Proceed,
    /// Claim a combat-reward tile by index (from obs.rewards)
    PickReward {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        idx: i32,
    },
    /// Pick/toggle a card by index (card rewards, deck pickers, hand select)
    PickCard {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        idx: i32,
    },
    /// Confirm the current card selection (deck pickers, hand select)
    Confirm,
    /// Skip a card/relic offer, or cancel a cancelable deck picker
    Skip {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        idx: Option<i32>,
    },
    /// Travel to a map node; in crystal_sphere, click a board cell instead
    MapMove {
        /// Map-node column from obs.next, or crystal_sphere cell column
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        col: i32,
        /// Map-node row from obs.next, or crystal_sphere cell row
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        row: i32,
    },
    /// List model entries: kind is card/relic/potion/event/encounter/character
    Models { kind: String },
    /// Dev/verification cheats.
    ///
    /// Run `cheat --help` for examples. The connected host's `health`
    /// capabilities list is the source of truth for available cheats.
    Cheat { name: String, values: Vec<String> },
    /// Play an exact hand-card selector (MODEL, MODEL+, MODEL@ENCHANTMENT, MODEL!AFFLICTION)
    Play {
        /// Exact obs.hand selector; identical selectors resolve in hand order
        #[arg(value_name = "MODEL")]
        selector: String,
        /// Enemy combat id (omit to auto-target a lone enemy)
        #[arg(long)]
        target: Option<u32>,
    },
    /// End the player turn
    EndTurn,
    /// Use a potion by slot (combat, or Foul Potion in a shop; from obs.potions)
    PotionUse {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        slot: i32,
        /// Enemy combat id for targeted potions
        #[arg(long)]
        target: Option<u32>,
    },
    /// Discard a potion by slot (anywhere in a run)
    PotionDiscard {
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        slot: i32,
    },
    /// Dump the current run's diagnostic reconstruction recipe
    Runlog,
    /// Re-drive and verify a saved diagnostic recipe from a clean menu
    Replay {
        /// JSON file saved from `spirescry runlog`
        file: String,
    },
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let client = Client {
        base: format!("http://{}:{}", cli.host, cli.port),
        verbose: cli.verbose,
        if_rev: cli.if_rev,
        if_run: cli.if_run.clone(),
        follow: cli.follow,
    };
    let result = match &cli.cmd {
        Cmd::Health => client.get("/health", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS)),
        Cmd::Models { kind } => client.compatible_get(
            &format!("/models?kind={}", kind),
            Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS),
        ),
        Cmd::Obs {
            since,
            wait,
            compact,
            decision,
            known_cards,
        } => obs_request(*since, *wait, *compact)
            .map_err(CliError::from)
            .and_then(|(mut path, timeout)| {
                if *decision {
                    let separator = if path.contains('?') { '&' } else { '?' };
                    path = format!("{path}{separator}decision=1");
                    for key in known_cards {
                        path = format!("{path}&known={}", query_component(key));
                    }
                }
                client.compatible_get(&path, timeout)
            }),
        Cmd::NewRun {
            character,
            seed,
            ascension,
        } => {
            let mut args = json!({ "character": character });
            if let Some(s) = seed {
                args["seed"] = json!(s);
            }
            if let Some(a) = ascension {
                args["ascension"] = json!(a);
            }
            client.step("new-run", args)
        }
        Cmd::Abandon => client.step("abandon", json!({})),
        Cmd::EventOption { idx } => client.step("option", json!({ "idx": idx })),
        Cmd::Proceed => client.step("proceed", json!({})),
        Cmd::PickReward { idx } => client.step("pick-reward", json!({ "idx": idx })),
        Cmd::PickCard { idx } => client.step("pick-card", json!({ "idx": idx })),
        Cmd::Confirm => client.step("confirm", json!({})),
        Cmd::Skip { idx } => client.step("skip", skip_args(*idx)),
        Cmd::Buy { kind, idx } => client.step("buy", json!({ "kind": kind, "idx": idx })),
        Cmd::Leave => client.step("leave", json!({})),
        Cmd::PickRelic { idx } => client.step("pick-relic", json!({ "idx": idx })),
        Cmd::Cheat { name, values } => cheat_args(name, values)
            .map_err(CliError::from)
            .and_then(|args| client.step("cheat", args)),
        Cmd::MapMove { col, row } => client.step("map-move", json!({ "col": col, "row": row })),
        Cmd::Play { selector, target } => {
            let mut args = json!({ "model": selector });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            client.step("play", args)
        }
        Cmd::EndTurn => client.step("end-turn", json!({})),
        Cmd::PotionUse { slot, target } => {
            let mut args = json!({ "slot": slot });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            client.step("potion-use", args)
        }
        Cmd::PotionDiscard { slot } => client.step("potion-discard", json!({ "slot": slot })),
        Cmd::Runlog => {
            client.compatible_get("/runlog", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))
        }
        Cmd::Replay { file } => replay(&client, file),
    };
    match result {
        Ok(v) => {
            let text = serde_json::to_string_pretty(&v).unwrap();
            // A plain println! panics on a closed pipe (e.g. `| head -1`);
            // write directly so that just exits quietly instead.
            match writeln!(std::io::stdout(), "{}", text) {
                Ok(()) => ExitCode::SUCCESS,
                Err(e) if e.kind() == std::io::ErrorKind::BrokenPipe => ExitCode::SUCCESS,
                Err(e) => {
                    eprintln!("spirescry: {}", e);
                    ExitCode::FAILURE
                }
            }
        }
        Err(e) => {
            eprintln!("spirescry: {}", e);
            ExitCode::from(e.exit_status())
        }
    }
}

fn obs_request(
    since: Option<i64>,
    wait: Option<i32>,
    compact: bool,
) -> Result<(String, Duration), String> {
    if wait.is_some() && since.is_none() {
        return Err("--wait has no effect without --since".to_string());
    }
    if wait.is_some_and(|wait| !(0..=60_000).contains(&wait)) {
        return Err("--wait must be between 0 and 60000".to_string());
    }

    let mut path = String::from("/obs");
    let timeout_ms = if let Some(since) = since {
        let wait = wait.unwrap_or(DEFAULT_OBS_WAIT_MS);
        path = format!("{path}?since={since}&wait={wait}");
        u64::try_from(wait).map_err(|error| error.to_string())? + HTTP_TIMEOUT_GRACE_MS
    } else {
        DEFAULT_HTTP_TIMEOUT_MS
    };
    if compact {
        path = format!("{path}{}compact=1", if since.is_some() { '&' } else { '?' });
    }
    Ok((path, Duration::from_millis(timeout_ms)))
}

fn skip_args(idx: Option<i32>) -> Value {
    match idx {
        Some(i) => json!({ "idx": i }),
        None => json!({}),
    }
}

// Positional sugar for the known cheat arg shapes; the bridge's own
// per-cheat validation is the source of truth.
fn cheat_args(name: &str, values: &[String]) -> Result<Value, String> {
    let mut args = json!({ "name": name });
    let num = |s: &String| {
        s.parse::<i32>()
            .map_err(|_| format!("invalid number: {}", s))
    };
    match (name, values) {
        ("goto", [col, row]) => {
            args["col"] = json!(num(col)?);
            args["row"] = json!(num(row)?);
        }
        ("gold", [value]) | ("hp", [value]) | ("stars", [value]) | ("energy", [value]) => {
            args["value"] = json!(num(value)?)
        }
        ("event", [id])
        | ("combat", [id])
        | ("card", [id])
        | ("card-upgraded", [id])
        | ("relic", [id])
        | ("potion", [id]) => args["id"] = json!(id),
        _ => {}
    }
    Ok(args)
}

trait ReplayTransport {
    fn get(&self, path: &str) -> CliResult<Value>;
    fn post(&self, path: &str, body: Value) -> CliResult<Value>;
}

fn replay(client: &impl ReplayTransport, file: &str) -> CliResult<Value> {
    let text = std::fs::read_to_string(file).map_err(|e| format!("read {}: {}", file, e))?;
    let log: Value = serde_json::from_str(&text).map_err(|e| format!("parse {}: {}", file, e))?;
    replay_value(client, &log)
}

fn replay_value(client: &impl ReplayTransport, log: &Value) -> CliResult<Value> {
    if log.get("complete").and_then(Value::as_bool) != Some(true) {
        return Err("runlog is incomplete (it must start with new-run, contain one RunId, and fingerprint every followed verb)".into());
    }
    let source_run_id = log
        .get("runId")
        .and_then(Value::as_str)
        .filter(|id| *id != "none")
        .ok_or("runlog has no source runId")?;
    let verbs = log
        .get("verbs")
        .and_then(Value::as_array)
        .ok_or("runlog has no verbs array")?;
    if verbs
        .first()
        .and_then(|v| v.get("action"))
        .and_then(Value::as_str)
        != Some("new-run")
    {
        return Err("runlog does not start with new-run".into());
    }
    if let Some((idx, _)) = verbs
        .iter()
        .enumerate()
        .find(|(_, verb)| verb.get("runId").and_then(Value::as_str) != Some(source_run_id))
    {
        return Err(format!("runlog crosses RunIds at verb {}", idx + 1).into());
    }
    if let Some((idx, _)) = verbs.iter().enumerate().find(|(_, verb)| {
        !matches!(
            verb.get("outcome").and_then(Value::as_str),
            Some("settled" | "next_decision")
        ) || verb
            .get("fingerprint")
            .and_then(Value::as_str)
            .filter(|value| !value.is_empty())
            .is_none()
    }) {
        return Err(format!(
            "runlog verb {} has no verifiable settled fingerprint",
            idx + 1
        )
        .into());
    }

    // Fail before the first state-changing request. A recipe can name a
    // capability that is missing only near its end; discovering that after
    // new-run would leave a partially reconstructed live run behind.
    let health = client.get("/health")?;
    validate_health(&health, None, None)?;
    for (idx, verb) in verbs.iter().enumerate() {
        let action = verb
            .get("action")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("verb {} has no action", idx + 1))?;
        let cheat = if action == "cheat" {
            Some(
                verb.pointer("/args/name")
                    .and_then(Value::as_str)
                    .ok_or_else(|| format!("verb {} cheat has no args.name", idx + 1))?,
            )
        } else {
            None
        };
        validate_health(&health, Some(action), cheat)?;
    }

    let mut current = client.get("/obs")?;
    if current.get("phase").and_then(Value::as_str) != Some("main_menu")
        || current.get("runId").and_then(Value::as_str) != Some("none")
    {
        return Err(format!(
            "replay requires a clean main_menu with runId none; current phase={} runId={}",
            current.get("phase").and_then(Value::as_str).unwrap_or("?"),
            current.get("runId").and_then(Value::as_str).unwrap_or("?"),
        )
        .into());
    }

    let seed = log.get("seed").and_then(Value::as_str);
    let mut verified = 0usize;
    for (idx, verb) in verbs.iter().enumerate() {
        let action = verb
            .get("action")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("verb {} has no action", idx + 1))?;
        let expected_phase = verb
            .get("phaseBefore")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("verb {} has no phaseBefore", idx + 1))?;
        let actual_phase = current.get("phase").and_then(Value::as_str).unwrap_or("?");
        if actual_phase != expected_phase {
            return Err(format!(
                "divergence at verb {} ({}): phase before was {}, reconstructed {}",
                idx + 1,
                action,
                expected_phase,
                actual_phase,
            )
            .into());
        }

        let mut args = verb.get("args").cloned().unwrap_or_else(|| json!({}));
        if action == "new-run" {
            if let Some(seed) = seed {
                args["seed"] = json!(seed);
            }
        }
        let rev = current
            .get("rev")
            .and_then(Value::as_u64)
            .ok_or_else(|| format!("verb {} precondition has no rev", idx + 1))?;
        let run_id = current
            .get("runId")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("verb {} precondition has no runId", idx + 1))?;
        eprintln!("replay {}/{}: {} {}", idx + 1, verbs.len(), action, args);
        let response = client.post(
            "/step",
            json!({
                "action": action,
                "args": args,
                "ifRev": rev,
                "ifRun": run_id,
                "follow": 10_000,
            }),
        )?;
        if response.get("settled").and_then(Value::as_bool) != Some(true) {
            return Err(format!(
                "divergence at verb {} ({}): reconstruction timed out",
                idx + 1,
                action,
            )
            .into());
        }
        let next = response
            .get("obs")
            .cloned()
            .ok_or_else(|| format!("verb {} follow response has no obs", idx + 1))?;

        if let Some(expected) = verb.get("phaseAfter").and_then(Value::as_str) {
            let actual = next.get("phase").and_then(Value::as_str).unwrap_or("?");
            if actual != expected {
                return Err(format!(
                    "divergence at verb {} ({}): phase after was {}, reconstructed {}",
                    idx + 1,
                    action,
                    expected,
                    actual,
                )
                .into());
            }
        }
        let expected = verb
            .get("fingerprint")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("verb {} has no fingerprint", idx + 1))?;
        let actual = state_fingerprint(&next);
        if actual != expected {
            return Err(format!(
                "divergence at verb {} ({}): fingerprint {} != {}",
                idx + 1,
                action,
                expected,
                actual,
            )
            .into());
        }
        verified += 1;
        current = next;
    }

    Ok(json!({
        "ok": true,
        "kind": "diagnostic_reconstruction_result",
        "sourceRunId": source_run_id,
        "reconstructionRunId": current.get("runId").cloned().unwrap_or(Value::Null),
        "verifiedFingerprints": verified,
        "totalVerbs": verbs.len(),
        "attribution": "the final observation belongs to the reconstruction, not the source run",
        "reconstructedFinalObs": current,
    }))
}

fn state_fingerprint(value: &Value) -> String {
    let mut stable = value.clone();
    remove_volatile(&mut stable);
    let bytes = serde_json::to_vec(&stable).unwrap_or_default();
    let mut hash: u64 = 14_695_981_039_346_656_037;
    for byte in bytes {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(1_099_511_628_211);
    }
    format!("{hash:016x}")
}

fn remove_volatile(value: &mut Value) {
    match value {
        Value::Object(map) => {
            map.remove("rev");
            map.remove("runId");
            for child in map.values_mut() {
                remove_volatile(child);
            }
        }
        Value::Array(items) => {
            for child in items {
                remove_volatile(child);
            }
        }
        _ => {}
    }
}

// The HTTP side of every subcommand. --verbose traces round-trips on
// stderr (stdout stays JSON-only for pipes), stamped with the same UTC
// clock the host log uses so the two sides line up.
struct Client {
    base: String,
    verbose: bool,
    if_rev: Option<u64>,
    if_run: Option<String>,
    follow: Option<u32>,
}

impl Client {
    fn get(&self, path: &str, timeout: Duration) -> CliResult<Value> {
        let url = format!("{}{}", self.base, path);
        self.exchange(format!("GET {}", url), || {
            ureq::get(&url).timeout(timeout).call().map_err(Box::new)
        })
    }

    fn post(&self, path: &str, body: Value) -> CliResult<Value> {
        let url = format!("{}{}", self.base, path);
        let request_line = format!("POST {} {}", url, body);
        self.exchange(request_line, || {
            ureq::post(&url)
                .timeout(Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))
                .send_json(body)
                .map_err(Box::new)
        })
    }

    fn step(&self, action: &str, args: Value) -> CliResult<Value> {
        let health = self.get("/health", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))?;
        let cheat = (action == "cheat")
            .then(|| args.get("name").and_then(Value::as_str))
            .flatten();
        validate_health(&health, Some(action), cheat)?;
        let mut body = json!({ "action": action, "args": args });
        if let Some(rev) = self.if_rev {
            body["ifRev"] = json!(rev);
        }
        if let Some(run) = &self.if_run {
            body["ifRun"] = json!(run);
        }
        if let Some(ms) = self.follow {
            body["follow"] = json!(ms);
        }
        self.post("/step", body)
    }

    fn compatible_get(&self, path: &str, timeout: Duration) -> CliResult<Value> {
        let health = self.get("/health", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))?;
        validate_health(&health, None, None)?;
        self.get(path, timeout)
    }

    fn exchange(
        &self,
        request_line: String,
        send: impl FnOnce() -> Result<ureq::Response, Box<ureq::Error>>,
    ) -> CliResult<Value> {
        if self.verbose {
            eprintln!("spirescry: [{}] > {}", utc_clock(), request_line);
        }
        let start = std::time::Instant::now();
        let result = send();
        if self.verbose {
            let status = match &result {
                Ok(resp) => resp.status().to_string(),
                Err(error) => match error.as_ref() {
                    ureq::Error::Status(code, _) => code.to_string(),
                    ureq::Error::Transport(_) => "transport error".to_string(),
                },
            };
            eprintln!(
                "spirescry: [{}] < {} {}ms",
                utc_clock(),
                status,
                start.elapsed().as_millis()
            );
        }
        handle(result)
    }
}

impl ReplayTransport for Client {
    fn get(&self, path: &str) -> CliResult<Value> {
        Client::get(self, path, Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))
    }

    fn post(&self, path: &str, body: Value) -> CliResult<Value> {
        Client::post(self, path, body)
    }
}

fn validate_health(health: &Value, action: Option<&str>, cheat: Option<&str>) -> CliResult<()> {
    let host_protocol = health
        .get("protocolVersion")
        .and_then(Value::as_u64)
        .ok_or_else(|| {
            "incompatible host: /health has no numeric protocolVersion; update the running host"
                .to_string()
        })?;
    if host_protocol != PROTOCOL_VERSION {
        let build = health
            .get("buildHash")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        return Err(format!(
            "incompatible host protocol {} (CLI expects {}, build {})",
            host_protocol, PROTOCOL_VERSION, build
        )
        .into());
    }

    if let Some(action) = action {
        let verbs = health
            .pointer("/capabilities/verbs")
            .and_then(Value::as_array)
            .ok_or_else(|| "incompatible host: /health has no capabilities.verbs".to_string())?;
        if !verbs.iter().any(|verb| verb.as_str() == Some(action)) {
            let supported = verbs
                .iter()
                .filter_map(Value::as_str)
                .collect::<Vec<_>>()
                .join(", ");
            return Err(format!(
                "bad_request: host does not advertise '{}' (supported: {})",
                action, supported
            )
            .into());
        }
    }

    if let Some(cheat) = cheat {
        let cheats = health
            .pointer("/capabilities/cheats")
            .and_then(Value::as_array)
            .ok_or_else(|| "incompatible host: /health has no capabilities.cheats".to_string())?;
        if !cheats.iter().any(|item| item.as_str() == Some(cheat)) {
            let supported = cheats
                .iter()
                .filter_map(Value::as_str)
                .collect::<Vec<_>>()
                .join(", ");
            return Err(format!(
                "bad_request: host does not advertise cheat '{}' (supported: {})",
                cheat, supported
            )
            .into());
        }
    }

    Ok(())
}

// HH:MM:SS.mmm UTC — hand-rolled from the epoch to keep the CLI
// dependency-free.
fn utc_clock() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs() % 86_400;
    format!(
        "{:02}:{:02}:{:02}.{:03}",
        secs / 3600,
        (secs % 3600) / 60,
        secs % 60,
        now.subsec_millis()
    )
}

fn query_component(value: &str) -> String {
    value
        .bytes()
        .flat_map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                vec![b as char]
            }
            _ => format!("%{b:02X}").chars().collect(),
        })
        .collect()
}

fn handle(result: Result<ureq::Response, Box<ureq::Error>>) -> CliResult<Value> {
    let resp = match result {
        Ok(resp) => resp,
        // Bridge errors ride on 4xx/5xx with a JSON body — parse it.
        Err(error) => match *error {
            ureq::Error::Status(_, resp) => resp,
            ureq::Error::Transport(error) => {
                return Err(CliError::fatal(error.to_string()));
            }
        },
    };
    let value: Value = resp
        .into_json()
        .map_err(|e| CliError::fatal(format!("non-json response: {}", e)))?;
    parse_bridge_value(value)
}

fn parse_bridge_value(value: Value) -> CliResult<Value> {
    if value.get("ok").and_then(Value::as_bool) == Some(false) {
        let err = value
            .get("err")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let msg = value.get("msg").and_then(Value::as_str).unwrap_or("");
        let rendered = format!("{}: {}", err, msg);
        return if err == "not_ready" {
            Err(CliError::transient(rendered))
        } else {
            Err(CliError::fatal(rendered))
        };
    }
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::{CommandFactory, Parser};
    use std::cell::{Cell, RefCell};
    use std::net::TcpListener;
    use std::thread;
    use std::time::Instant;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|s| s.to_string()).collect()
    }

    fn health(protocol: u64, verbs: &[&str], cheats: &[&str]) -> Value {
        json!({
            "ok": true,
            "buildHash": "abc1234",
            "protocolVersion": protocol,
            "capabilities": { "verbs": verbs, "cheats": cheats }
        })
    }

    struct ReplaySpy {
        health: Value,
        gets: RefCell<Vec<String>>,
        posts: Cell<usize>,
    }

    impl ReplaySpy {
        fn new(health: Value) -> Self {
            Self {
                health,
                gets: RefCell::new(Vec::new()),
                posts: Cell::new(0),
            }
        }
    }

    impl ReplayTransport for ReplaySpy {
        fn get(&self, path: &str) -> CliResult<Value> {
            self.gets.borrow_mut().push(path.to_string());
            match path {
                "/health" => Ok(self.health.clone()),
                "/obs" => Ok(json!({"phase":"main_menu", "runId":"none", "rev":1})),
                _ => Err(CliError::fatal(format!("unexpected GET {path}"))),
            }
        }

        fn post(&self, _path: &str, _body: Value) -> CliResult<Value> {
            self.posts.set(self.posts.get() + 1);
            Err(CliError::fatal("unexpected POST"))
        }
    }

    fn replay_recipe(verbs: Vec<Value>) -> Value {
        json!({
            "complete": true,
            "runId": "source-run",
            "seed": "REPLAYTEST",
            "verbs": verbs,
        })
    }

    fn followed_verb(action: &str, args: Value) -> Value {
        json!({
            "runId": "source-run",
            "action": action,
            "args": args,
            "phaseBefore": "main_menu",
            "phaseAfter": "event",
            "outcome": "settled",
            "fingerprint": "0000000000000001",
        })
    }

    #[test]
    fn parses_obs_long_poll_options() {
        let cli = Cli::try_parse_from([
            "spirescry",
            "--host",
            "localhost",
            "--port",
            "8888",
            "obs",
            "--since",
            "42",
            "--wait",
            "250",
        ])
        .unwrap();

        assert_eq!(cli.host, "localhost");
        assert_eq!(cli.port, 8888);
        match cli.cmd {
            Cmd::Obs {
                since,
                wait,
                compact,
                decision,
                known_cards,
            } => {
                assert_eq!(since, Some(42));
                assert_eq!(wait, Some(250));
                assert!(!compact);
                assert!(!decision);
                assert!(known_cards.is_empty());
            }
            _ => panic!("expected obs command"),
        }
    }

    #[test]
    fn parses_obs_compact_flag() {
        let cli = Cli::try_parse_from(["spirescry", "obs", "--compact"]).unwrap();

        match cli.cmd {
            Cmd::Obs { compact, .. } => assert!(compact),
            _ => panic!("expected obs command"),
        }
    }

    #[test]
    fn obs_limits_match_bridge_protocol() {
        let cli = Cli::try_parse_from([
            "spirescry",
            "obs",
            "--since",
            "9223372036854775807",
            "--wait",
            "60000",
        ])
        .unwrap();
        match cli.cmd {
            Cmd::Obs { since, wait, .. } => {
                assert_eq!(since, Some(i64::MAX));
                assert_eq!(wait, Some(60_000));
            }
            _ => panic!("expected obs command"),
        }

        assert!(
            Cli::try_parse_from(["spirescry", "obs", "--since", "9223372036854775808"]).is_err()
        );
        assert!(
            Cli::try_parse_from(["spirescry", "obs", "--since", "1", "--wait", "60001"]).is_err()
        );
    }

    #[test]
    fn obs_request_timeout_includes_long_poll_headroom() {
        let (path, timeout) = obs_request(Some(42), Some(60_000), true).unwrap();

        assert_eq!(path, "/obs?since=42&wait=60000&compact=1");
        assert_eq!(timeout, std::time::Duration::from_secs(70));

        let (path, timeout) = obs_request(Some(42), None, false).unwrap();
        assert_eq!(path, "/obs?since=42&wait=5000");
        assert_eq!(timeout, std::time::Duration::from_secs(15));

        let (path, timeout) = obs_request(None, None, true).unwrap();
        assert_eq!(path, "/obs?compact=1");
        assert_eq!(timeout, std::time::Duration::from_secs(70));

        assert_eq!(
            obs_request(None, Some(1), false).unwrap_err(),
            "--wait has no effect without --since"
        );
        assert_eq!(
            obs_request(Some(1), Some(-1), false).unwrap_err(),
            "--wait must be between 0 and 60000"
        );
        assert_eq!(
            obs_request(Some(1), Some(60_001), false).unwrap_err(),
            "--wait must be between 0 and 60000"
        );
    }

    #[test]
    fn client_times_out_when_bridge_stalls() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (_stream, _) = listener.accept().unwrap();
            thread::sleep(Duration::from_millis(250));
        });
        let client = Client {
            base: format!("http://{address}"),
            verbose: false,
            if_rev: None,
            if_run: None,
            follow: None,
        };

        let start = Instant::now();
        let result = client.get("/", Duration::from_millis(25));

        assert!(result.is_err());
        assert!(start.elapsed() < Duration::from_millis(200));
        server.join().unwrap();
    }

    #[test]
    fn parses_decision_and_repeatable_known_card_keys() {
        let cli = Cli::try_parse_from([
            "spirescry",
            "obs",
            "--decision",
            "--known-card",
            "BASH+1@FOO!BAR",
            "--known-card",
            "STRIKE_IRONCLAD+0",
        ])
        .unwrap();

        match cli.cmd {
            Cmd::Obs {
                decision,
                known_cards,
                ..
            } => {
                assert!(decision);
                assert_eq!(known_cards, ["BASH+1@FOO!BAR", "STRIKE_IRONCLAD+0"]);
            }
            _ => panic!("expected obs command"),
        }
        assert_eq!(query_component("BASH+1@FOO!BAR"), "BASH%2B1%40FOO%21BAR");
    }

    #[test]
    fn parses_global_verbose_flag() {
        let cli = Cli::try_parse_from(["spirescry", "obs", "--verbose"]).unwrap();
        assert!(cli.verbose);

        let cli = Cli::try_parse_from(["spirescry", "-v", "health"]).unwrap();
        assert!(cli.verbose);

        let cli = Cli::try_parse_from(["spirescry", "health"]).unwrap();
        assert!(!cli.verbose);
    }

    #[test]
    fn parses_global_guard_flags_after_the_verb() {
        let cli = Cli::try_parse_from([
            "spirescry",
            "end-turn",
            "--if-rev",
            "310",
            "--if-run",
            "abc123",
        ])
        .unwrap();

        assert_eq!(cli.if_rev, Some(310));
        assert_eq!(cli.if_run.as_deref(), Some("abc123"));
    }

    #[test]
    fn client_step_serializes_both_guards() {
        let client = Client {
            base: "http://127.0.0.1:1".to_string(),
            verbose: false,
            if_rev: Some(42),
            if_run: Some("run-1".to_string()),
            follow: None,
        };
        let mut body = json!({ "action": "end-turn", "args": {} });
        if let Some(rev) = client.if_rev {
            body["ifRev"] = json!(rev);
        }
        if let Some(run) = &client.if_run {
            body["ifRun"] = json!(run);
        }

        assert_eq!(body["ifRev"], 42);
        assert_eq!(body["ifRun"], "run-1");
    }

    #[test]
    fn parses_follow_with_default_or_explicit_timeout() {
        let bare = Cli::try_parse_from(["spirescry", "end-turn", "--follow"]).unwrap();
        assert_eq!(bare.follow, Some(5000));

        let timed = Cli::try_parse_from([
            "spirescry",
            "end-turn",
            "--follow",
            "8000",
            "--if-rev",
            "42",
        ])
        .unwrap();
        assert_eq!(timed.follow, Some(8000));
        assert_eq!(timed.if_rev, Some(42));

        let off = Cli::try_parse_from(["spirescry", "end-turn"]).unwrap();
        assert_eq!(off.follow, None);
    }

    #[test]
    fn replay_fingerprint_ignores_revision_and_run_identity_only() {
        let a = json!({"phase":"map", "rev":1, "runId":"source", "hp":[50, 80]});
        let b = json!({"phase":"map", "rev":900, "runId":"replay", "hp":[50, 80]});
        let changed = json!({"phase":"map", "rev":1, "runId":"source", "hp":[49, 80]});

        assert_eq!(state_fingerprint(&a), state_fingerprint(&b));
        assert_ne!(state_fingerprint(&a), state_fingerprint(&changed));
    }

    #[test]
    fn replay_protocol_mismatch_fails_before_obs_or_any_post() {
        let spy = ReplaySpy::new(health(PROTOCOL_VERSION + 1, &["new-run"], &[]));
        let recipe = replay_recipe(vec![followed_verb("new-run", json!({}))]);

        let error = replay_value(&spy, &recipe).unwrap_err();

        assert!(error.contains("incompatible host protocol"), "{error}");
        assert_eq!(*spy.gets.borrow(), ["/health"]);
        assert_eq!(spy.posts.get(), 0);
    }

    #[test]
    fn replay_missing_late_capability_fails_before_obs_or_any_post() {
        let spy = ReplaySpy::new(health(PROTOCOL_VERSION, &["new-run", "cheat"], &[]));
        let recipe = replay_recipe(vec![
            followed_verb("new-run", json!({})),
            followed_verb("cheat", json!({"name":"gold", "value":100})),
        ]);

        let error = replay_value(&spy, &recipe).unwrap_err();

        assert!(error.contains("does not advertise cheat 'gold'"), "{error}");
        assert_eq!(*spy.gets.borrow(), ["/health"]);
        assert_eq!(spy.posts.get(), 0);
    }

    #[test]
    fn parses_play_target_as_optional() {
        let cli =
            Cli::try_parse_from(["spirescry", "play", "StrikeIronclad", "--target", "7"]).unwrap();

        match cli.cmd {
            Cmd::Play { selector, target } => {
                assert_eq!(selector, "StrikeIronclad");
                assert_eq!(target, Some(7));
            }
            _ => panic!("expected play command"),
        }

        let cli = Cli::try_parse_from([
            "spirescry",
            "play",
            "StrikeIronclad",
            "--target",
            "4294967295",
        ])
        .unwrap();
        match cli.cmd {
            Cmd::Play { target, .. } => assert_eq!(target, Some(u32::MAX)),
            _ => panic!("expected play command"),
        }
    }

    #[test]
    fn map_move_help_explains_crystal_sphere_cell_coordinates() {
        let mut command = Cli::command();
        let help = command
            .find_subcommand_mut("map-move")
            .unwrap()
            .render_long_help()
            .to_string();

        assert!(help.contains("crystal_sphere"));
        assert!(help.contains("cell"));
    }

    #[test]
    fn play_help_documents_exact_card_variant_selectors() {
        let mut command = Cli::command();
        let help = command
            .find_subcommand_mut("play")
            .unwrap()
            .render_long_help()
            .to_string();

        assert!(help.contains("MODEL+"));
        assert!(help.contains("@ENCHANTMENT"));
        assert!(help.contains("!AFFLICTION"));
        assert!(help.contains("hand order"));
        assert!(help.contains("<MODEL>"));
    }

    #[test]
    fn wire_int_arguments_reject_values_above_i32() {
        let too_large = "2147483648";
        let invocations = [
            vec!["spirescry", "new-run", "IRONCLAD", "--ascension", too_large],
            vec!["spirescry", "option", too_large],
            vec!["spirescry", "buy", "card", "--idx", too_large],
            vec!["spirescry", "pick-relic", too_large],
            vec!["spirescry", "pick-reward", too_large],
            vec!["spirescry", "pick-card", too_large],
            vec!["spirescry", "map-move", too_large, "0"],
            vec!["spirescry", "potion-use", too_large],
            vec!["spirescry", "potion-discard", too_large],
        ];

        for args in invocations {
            assert!(
                Cli::try_parse_from(&args).is_err(),
                "accepted out-of-range protocol integer: {args:?}"
            );
        }
    }

    #[test]
    fn nonnegative_wire_arguments_still_reject_negative_values() {
        let invocations = [
            vec!["spirescry", "obs", "--since=-1"],
            vec!["spirescry", "new-run", "IRONCLAD", "--ascension=-1"],
            vec!["spirescry", "option", "--", "-1"],
            vec!["spirescry", "buy", "card", "--idx=-1"],
            vec!["spirescry", "pick-relic", "--", "-1"],
            vec!["spirescry", "pick-reward", "--", "-1"],
            vec!["spirescry", "pick-card", "--", "-1"],
            vec!["spirescry", "map-move", "--", "-1", "0"],
            vec!["spirescry", "potion-use", "--", "-1"],
            vec!["spirescry", "potion-discard", "--", "-1"],
        ];

        for args in invocations {
            assert!(
                Cli::try_parse_from(&args).is_err(),
                "accepted negative protocol integer: {args:?}"
            );
        }
    }

    #[test]
    fn parses_skip_alternative_index_as_optional() {
        let indexed = Cli::try_parse_from(["spirescry", "skip", "2"]).unwrap();
        match indexed.cmd {
            Cmd::Skip { idx } => {
                assert_eq!(idx, Some(2));
                assert_eq!(skip_args(idx), json!({ "idx": 2 }));
            }
            _ => panic!("expected skip command"),
        }

        let bare = Cli::try_parse_from(["spirescry", "skip"]).unwrap();
        match bare.cmd {
            Cmd::Skip { idx } => {
                assert_eq!(idx, None);
                assert_eq!(skip_args(idx), json!({}));
            }
            _ => panic!("expected skip command"),
        }
    }

    #[test]
    fn cheat_goto_maps_position_args() {
        let args = cheat_args("goto", &strings(&["3", "5"])).unwrap();

        assert_eq!(args, json!({ "name": "goto", "col": 3, "row": 5 }));
    }

    #[test]
    fn cheat_scalar_values_map_to_value() {
        let gold = cheat_args("gold", &strings(&["100"])).unwrap();
        let hp = cheat_args("hp", &strings(&["12"])).unwrap();

        assert_eq!(gold, json!({ "name": "gold", "value": 100 }));
        assert_eq!(hp, json!({ "name": "hp", "value": 12 }));
    }

    #[test]
    fn cheat_event_maps_id() {
        let args = cheat_args("event", &strings(&["ForgottenAltar"])).unwrap();

        assert_eq!(args, json!({ "name": "event", "id": "ForgottenAltar" }));
    }

    #[test]
    fn cheat_card_maps_id() {
        let args = cheat_args("card", &strings(&["WHIRLWIND"])).unwrap();

        assert_eq!(args, json!({ "name": "card", "id": "WHIRLWIND" }));
    }

    #[test]
    fn cheat_card_upgraded_potion_and_relic_map_id() {
        let upgraded = cheat_args("card-upgraded", &strings(&["WHIRLWIND"])).unwrap();
        let potion = cheat_args("potion", &strings(&["FOUL_POTION"])).unwrap();
        let relic = cheat_args("relic", &strings(&["KUNAI"])).unwrap();

        assert_eq!(
            upgraded,
            json!({ "name": "card-upgraded", "id": "WHIRLWIND" })
        );
        assert_eq!(potion, json!({ "name": "potion", "id": "FOUL_POTION" }));
        assert_eq!(relic, json!({ "name": "relic", "id": "KUNAI" }));
    }

    #[test]
    fn cheat_sweep_helpers_map_args() {
        let combat = cheat_args("combat", &strings(&["KAISER_CRAB"])).unwrap();
        let potion = cheat_args("potion", &strings(&["FLEX_POTION"])).unwrap();
        let stars = cheat_args("stars", &strings(&["99"])).unwrap();
        let energy = cheat_args("energy", &strings(&["99"])).unwrap();

        assert_eq!(combat, json!({ "name": "combat", "id": "KAISER_CRAB" }));
        assert_eq!(potion, json!({ "name": "potion", "id": "FLEX_POTION" }));
        assert_eq!(stars, json!({ "name": "stars", "value": 99 }));
        assert_eq!(energy, json!({ "name": "energy", "value": 99 }));
    }

    #[test]
    fn cheat_rejects_invalid_numbers() {
        let err = cheat_args("goto", &strings(&["x", "5"])).unwrap_err();

        assert_eq!(err, "invalid number: x");
    }

    #[test]
    fn cheat_rejects_numbers_above_protocol_int_range() {
        let err = cheat_args("gold", &strings(&["2147483648"])).unwrap_err();

        assert_eq!(err, "invalid number: 2147483648");
    }

    #[test]
    fn unknown_cheat_passes_name_for_bridge_validation() {
        let args = cheat_args("heal", &[]).unwrap();

        assert_eq!(args, json!({ "name": "heal" }));
    }

    #[test]
    fn compatible_health_accepts_advertised_verb_and_cheat() {
        let value = health(PROTOCOL_VERSION, &["play", "cheat"], &["relic"]);

        assert_eq!(validate_health(&value, Some("play"), None), Ok(()));
        assert_eq!(
            validate_health(&value, Some("cheat"), Some("relic")),
            Ok(())
        );
    }

    #[test]
    fn old_health_without_protocol_fails_fast() {
        let err = validate_health(&json!({ "ok": true }), Some("play"), None).unwrap_err();

        assert!(err.contains("no numeric protocolVersion"), "{err}");
    }

    #[test]
    fn protocol_mismatch_names_both_versions_and_build() {
        let old_protocol = PROTOCOL_VERSION - 1;
        let err =
            validate_health(&health(old_protocol, &["play"], &[]), Some("play"), None).unwrap_err();

        assert!(err.contains(&format!("protocol {old_protocol}")), "{err}");
        assert!(
            err.contains(&format!("expects {PROTOCOL_VERSION}")),
            "{err}"
        );
        assert!(err.contains("abc1234"), "{err}");
    }

    #[test]
    fn missing_advertised_capability_fails_before_the_step() {
        let value = health(PROTOCOL_VERSION, &["cheat"], &["gold"]);

        let verb_err = validate_health(&value, Some("play"), None).unwrap_err();
        assert!(verb_err.contains("does not advertise 'play'"), "{verb_err}");

        let cheat_err = validate_health(&value, Some("cheat"), Some("relic")).unwrap_err();
        assert!(cheat_err.contains("cheat 'relic'"), "{cheat_err}");
    }

    #[test]
    fn bridge_not_ready_is_a_typed_transient_error() {
        let error = parse_bridge_value(json!({
            "ok": false,
            "err": "not_ready",
            "msg": "map intro animation"
        }))
        .unwrap_err();

        assert!(matches!(error, CliError::Transient(_)), "{error}");
        assert_eq!(error.exit_status(), 75);
    }

    #[test]
    fn bad_state_bridge_rejections_are_typed_fatal_errors() {
        let error = parse_bridge_value(json!({
            "ok": false,
            "err": "bad_state",
            "msg": "pick more cards before confirming"
        }))
        .unwrap_err();

        assert!(matches!(error, CliError::Fatal(_)), "{error}");
        assert_eq!(error.exit_status(), 1);
    }
}

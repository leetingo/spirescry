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
use serde_json::{json, Map, Value};

const DEFAULT_HTTP_TIMEOUT_MS: u64 = 70_000;
const HTTP_TIMEOUT_GRACE_MS: u64 = 10_000;

include!(concat!(env!("OUT_DIR"), "/protocol.rs"));

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
        Cmd::Health => client.health(),
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
            let settlement_outcome = v.get("outcome").and_then(SettlementOutcome::from_value);
            // Engine faults logged between acceptance and settlement ride
            // the response's "errors" array and the typed fault outcome.
            if let Some(errors) = v.get("errors").and_then(Value::as_array) {
                if !errors.is_empty() {
                    eprintln!(
                        "spirescry: host logged {} engine error(s) during this action — \
                         outcome={:?}; inspect the 'errors' field before the next verb",
                        errors.len(),
                        settlement_outcome,
                    );
                }
            }
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
        path = format!("{path}?since={since}");
        if let Some(wait) = wait {
            path = format!("{path}&wait={wait}");
            u64::try_from(wait).map_err(|error| error.to_string())? + HTTP_TIMEOUT_GRACE_MS
        } else {
            DEFAULT_HTTP_TIMEOUT_MS
        }
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

// Positional sugar driven by the checked protocol artifact. Unknown cheat
// names still reach the bridge so its supported-list rejection remains the
// source of truth for a host with a different surface.
fn cheat_args(name: &str, values: &[String]) -> Result<Value, String> {
    let mut args = json!({ "name": name });
    let Some(shape) = CHEAT_ARGUMENT_SHAPES
        .iter()
        .find(|shape| shape.name == name)
    else {
        return Ok(args);
    };
    let required = shape
        .arguments
        .iter()
        .filter(|argument| !argument.optional)
        .count();
    if values.len() < required || values.len() > shape.arguments.len() {
        let expected = if required == shape.arguments.len() {
            format!("{} arguments", required)
        } else {
            format!("{}..={} arguments", required, shape.arguments.len())
        };
        return Err(format!(
            "cheat '{}' expects {}, got {}",
            name,
            expected,
            values.len()
        ));
    }
    for (argument, value) in shape.arguments.iter().zip(values) {
        args[argument.name] = match argument.kind {
            ProtocolArgumentKind::Boolean => json!(value
                .parse::<bool>()
                .map_err(|_| format!("invalid boolean: {}", value))?),
            ProtocolArgumentKind::Integer => json!(value
                .parse::<i32>()
                .map_err(|_| format!("invalid number: {}", value))?),
            ProtocolArgumentKind::String => json!(value),
        };
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
        !verb
            .get("outcome")
            .and_then(SettlementOutcome::from_value)
            .is_some_and(SettlementOutcome::is_replayable)
            || verb
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
    if current.get("phase").and_then(Value::as_str) != Some(PHASE_MAIN_MENU)
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
        let outcome = response
            .get("outcome")
            .and_then(SettlementOutcome::from_value)
            .ok_or_else(|| {
                format!(
                    "verb {} ({}) follow response has no valid outcome",
                    idx + 1,
                    action,
                )
            })?;
        if !outcome.reached_boundary() {
            return Err(format!(
                "divergence at verb {} ({}): reconstruction timed out",
                idx + 1,
                action,
            )
            .into());
        }
        if !outcome.is_replayable() {
            return Err(format!(
                "divergence at verb {} ({}): reconstruction faulted",
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
    let stable = consumer_projection(value);
    let bytes = serde_json::to_vec(&stable).unwrap_or_default();
    let mut hash: u64 = 14_695_981_039_346_656_037;
    for byte in bytes {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(1_099_511_628_211);
    }
    format!("{hash:016x}")
}

// These names are the cross-language compile contract. If a C# schema field is
// removed without updating this consumer, generation omits its symbol and the
// Rust build fails instead of silently narrowing replay/settlement semantics.
macro_rules! require_projection_fields {
    ($($field:ident),* $(,)?) => {
        #[allow(dead_code)]
        mod consumed_projection_field_uniqueness {
            $(const $field: () = ();)*
        }
        const CONSUMED_PROJECTION_FIELDS: &[ProjectionField] = &[$($field),*];
        const _: () = assert!(
            CONSUMED_PROJECTION_FIELDS.len() == PROJECTION_FIELD_COUNT,
            "projection field list must include every generated projection field",
        );
    };
}

require_projection_fields!(
    PROJECTION_TOP_PHASE,
    PROJECTION_TOP_ID,
    PROJECTION_TOP_ACT,
    PROJECTION_TOP_CURRENT,
    PROJECTION_TOP_TURN,
    PROJECTION_TOP_OUTCOME,
    PROJECTION_TOP_HP,
    PROJECTION_TOP_GOLD,
    PROJECTION_TOP_SEMANTIC_STATE,
    PROJECTION_TOP_SELECTED,
    PROJECTION_TOP_SIDE,
    PROJECTION_TOP_ACTIONS_DISABLED,
    PROJECTION_TOP_AVAILABLE,
    PROJECTION_TOP_PROCEED_AVAILABLE,
    PROJECTION_TOP_CHEST_OPENED,
    PROJECTION_TOP_CONFIRMABLE,
    PROJECTION_TOP_CANCELABLE,
    PROJECTION_TOP_HAS_TOP_LEVEL_POTIONS,
    PROJECTION_TOP_NEXT,
    PROJECTION_TOP_HAND,
    PROJECTION_TOP_POTIONS,
    PROJECTION_TOP_OPTIONS,
    PROJECTION_TOP_CARDS,
    PROJECTION_TOP_COLORLESS,
    PROJECTION_TOP_RELICS,
    PROJECTION_TOP_REWARDS,
    PROJECTION_TOP_ALTERNATIVES,
    PROJECTION_TOP_BUNDLES,
    PROJECTION_TOP_CELLS,
    PROJECTION_TOP_YOU,
    PROJECTION_TOP_ENEMIES,
    PROJECTION_TOP_PLAYER,
    PROJECTION_TOP_CARD_REMOVAL,
    PROJECTION_TOP_LEGAL,
    PROJECTION_ITEM_INDEX,
    PROJECTION_ITEM_ID,
    PROJECTION_ITEM_MODEL,
    PROJECTION_ITEM_SELECTOR,
    PROJECTION_ITEM_SLOT,
    PROJECTION_ITEM_TARGET,
    PROJECTION_ITEM_COL,
    PROJECTION_ITEM_ROW,
    PROJECTION_ITEM_TYPE,
    PROJECTION_ITEM_SEMANTIC_STATE,
    PROJECTION_ITEM_SELECTED,
    PROJECTION_ITEM_PLAYABLE,
    PROJECTION_ITEM_LOCKED,
    PROJECTION_ITEM_CHOSEN,
    PROJECTION_ITEM_ENABLED,
    PROJECTION_ITEM_PURCHASABLE,
    PROJECTION_ITEM_HIDDEN,
    PROJECTION_ITEM_CARDS,
    PROJECTION_COMBATANT_HP,
    PROJECTION_COMBATANT_BLOCK,
    PROJECTION_COMBATANT_ENERGY,
    PROJECTION_COMBATANT_STARS,
    PROJECTION_COMBATANT_SEMANTIC_STATE,
    PROJECTION_ENEMY_ID,
    PROJECTION_ENEMY_MODEL,
    PROJECTION_ENEMY_HP,
    PROJECTION_ENEMY_BLOCK,
    PROJECTION_ENEMY_ALIVE,
    PROJECTION_ENEMY_SEMANTIC_STATE,
    PROJECTION_PLAYER_HP,
    PROJECTION_PLAYER_GOLD,
    PROJECTION_PLAYER_SEMANTIC_STATE,
    PROJECTION_PLAYER_POTIONS,
);

// protocol.json owns this deliberate semantic replay/settlement contract. The
// generated groups keep wire and output names aligned with C# without copying
// JSON vocabulary into this consumer.
fn consumer_projection(value: &Value) -> Value {
    let mut result = Map::new();
    copy_required_strings(value, &mut result, PROJECTION_TOP_REQUIRED_STRING_FIELDS);
    copy_optional_strings(value, &mut result, PROJECTION_TOP_OPTIONAL_STRING_FIELDS);
    copy_optional_numbers(value, &mut result, PROJECTION_TOP_OPTIONAL_NUMBER_FIELDS);
    copy_optional_int_arrays(value, &mut result, PROJECTION_TOP_OPTIONAL_INT_ARRAY_FIELDS);
    copy_optional_string_arrays(
        value,
        &mut result,
        PROJECTION_TOP_OPTIONAL_STRING_ARRAY_FIELDS,
    );
    copy_optional_booleans(value, &mut result, PROJECTION_TOP_OPTIONAL_BOOLEAN_FIELDS);
    copy_presence_booleans(value, &mut result, PROJECTION_TOP_PRESENCE_BOOLEAN_FIELDS);
    copy_item_arrays(value, &mut result, PROJECTION_TOP_ITEM_ARRAY_FIELDS);
    copy_optional_items(value, &mut result, PROJECTION_TOP_OPTIONAL_ITEM_FIELDS);
    copy_optional_combatants(value, &mut result, PROJECTION_TOP_OPTIONAL_COMBATANT_FIELDS);
    copy_enemy_arrays(value, &mut result, PROJECTION_TOP_ENEMY_ARRAY_FIELDS);
    copy_optional_players(value, &mut result, PROJECTION_TOP_OPTIONAL_PLAYER_FIELDS);
    copy_required_string_arrays(
        value,
        &mut result,
        PROJECTION_TOP_REQUIRED_STRING_ARRAY_FIELDS,
    );
    Value::Object(result)
}

fn item_array(value: Option<&Value>) -> Value {
    Value::Array(
        value
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|item| item.is_object())
            .map(item_projection)
            .collect(),
    )
}

fn item_projection(value: &Value) -> Value {
    let mut result = Map::new();
    copy_optional_strings(value, &mut result, PROJECTION_ITEM_OPTIONAL_STRING_FIELDS);
    copy_optional_numbers(value, &mut result, PROJECTION_ITEM_OPTIONAL_NUMBER_FIELDS);
    copy_optional_string_arrays(
        value,
        &mut result,
        PROJECTION_ITEM_OPTIONAL_STRING_ARRAY_FIELDS,
    );
    copy_optional_booleans(value, &mut result, PROJECTION_ITEM_OPTIONAL_BOOLEAN_FIELDS);
    copy_item_arrays(value, &mut result, PROJECTION_ITEM_ITEM_ARRAY_FIELDS);
    Value::Object(result)
}

fn enemy_array(value: Option<&Value>) -> Value {
    Value::Array(
        value
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|enemy| enemy.is_object())
            .map(enemy_projection)
            .collect(),
    )
}

fn combatant_projection(value: &Value) -> Value {
    let mut result = Map::new();
    copy_required_int_arrays(
        value,
        &mut result,
        PROJECTION_COMBATANT_REQUIRED_INT_ARRAY_FIELDS,
    );
    copy_optional_numbers(
        value,
        &mut result,
        PROJECTION_COMBATANT_OPTIONAL_NUMBER_FIELDS,
    );
    copy_optional_string_arrays(
        value,
        &mut result,
        PROJECTION_COMBATANT_OPTIONAL_STRING_ARRAY_FIELDS,
    );
    Value::Object(result)
}

fn enemy_projection(value: &Value) -> Value {
    let mut result = Map::new();
    copy_required_int_arrays(
        value,
        &mut result,
        PROJECTION_ENEMY_REQUIRED_INT_ARRAY_FIELDS,
    );
    copy_optional_strings(value, &mut result, PROJECTION_ENEMY_OPTIONAL_STRING_FIELDS);
    copy_optional_numbers(value, &mut result, PROJECTION_ENEMY_OPTIONAL_NUMBER_FIELDS);
    copy_optional_string_arrays(
        value,
        &mut result,
        PROJECTION_ENEMY_OPTIONAL_STRING_ARRAY_FIELDS,
    );
    copy_optional_booleans(value, &mut result, PROJECTION_ENEMY_OPTIONAL_BOOLEAN_FIELDS);
    Value::Object(result)
}

fn player_projection(value: &Value) -> Value {
    let mut result = Map::new();
    copy_optional_numbers(value, &mut result, PROJECTION_PLAYER_OPTIONAL_NUMBER_FIELDS);
    copy_optional_int_arrays(
        value,
        &mut result,
        PROJECTION_PLAYER_OPTIONAL_INT_ARRAY_FIELDS,
    );
    copy_optional_string_arrays(
        value,
        &mut result,
        PROJECTION_PLAYER_OPTIONAL_STRING_ARRAY_FIELDS,
    );
    copy_item_arrays(value, &mut result, PROJECTION_PLAYER_ITEM_ARRAY_FIELDS);
    Value::Object(result)
}

fn int_array(value: Option<&Value>) -> Value {
    Value::Array(
        value
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|item| item.is_number())
            .cloned()
            .collect(),
    )
}

fn string_array(value: Option<&Value>) -> Value {
    Value::Array(
        value
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
            .map(|item| Value::String(item.into()))
            .collect(),
    )
}

fn copy_required_strings(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        let value = source
            .get(field.wire)
            .and_then(Value::as_str)
            .map_or(Value::Null, |value| Value::String(value.into()));
        target.insert(field.output.into(), value);
    }
}

fn copy_optional_strings(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).and_then(Value::as_str) {
            target.insert(field.output.into(), Value::String(value.into()));
        }
    }
}

fn copy_optional_numbers(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).filter(|value| value.is_number()) {
            target.insert(field.output.into(), value.clone());
        }
    }
}

fn copy_required_int_arrays(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        target.insert(field.output.into(), int_array(source.get(field.wire)));
    }
}

fn copy_optional_int_arrays(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if source.get(field.wire).is_some_and(Value::is_array) {
            target.insert(field.output.into(), int_array(source.get(field.wire)));
        }
    }
}

fn copy_optional_string_arrays(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if source.get(field.wire).is_some_and(Value::is_array) {
            target.insert(field.output.into(), string_array(source.get(field.wire)));
        }
    }
}

fn copy_optional_booleans(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).and_then(Value::as_bool) {
            target.insert(field.output.into(), Value::Bool(value));
        }
    }
}

fn copy_presence_booleans(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        target.insert(
            field.output.into(),
            Value::Bool(source.get(field.wire).is_some_and(Value::is_array)),
        );
    }
}

fn copy_item_arrays(source: &Value, target: &mut Map<String, Value>, fields: &[ProjectionField]) {
    for field in fields {
        target.insert(field.output.into(), item_array(source.get(field.wire)));
    }
}

fn copy_optional_items(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).filter(|value| value.is_object()) {
            target.insert(field.output.into(), item_projection(value));
        }
    }
}

fn copy_optional_combatants(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).filter(|value| value.is_object()) {
            target.insert(field.output.into(), combatant_projection(value));
        }
    }
}

fn copy_enemy_arrays(source: &Value, target: &mut Map<String, Value>, fields: &[ProjectionField]) {
    for field in fields {
        target.insert(field.output.into(), enemy_array(source.get(field.wire)));
    }
}

fn copy_optional_players(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        if let Some(value) = source.get(field.wire).filter(|value| value.is_object()) {
            target.insert(field.output.into(), player_projection(value));
        }
    }
}

fn copy_required_string_arrays(
    source: &Value,
    target: &mut Map<String, Value>,
    fields: &[ProjectionField],
) {
    for field in fields {
        target.insert(field.output.into(), string_array(source.get(field.wire)));
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
    fn health(&self) -> CliResult<Value> {
        let expected_build = std::env::var("SPIRESCRY_EXPECT_BUILD").ok();
        self.health_expecting(expected_build.as_deref())
    }

    fn health_expecting(&self, expected_build: Option<&str>) -> CliResult<Value> {
        let health = self.get("/health", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))?;
        validate_health_expecting(&health, None, None, expected_build)?;
        Ok(health)
    }

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
    let expected_build = std::env::var("SPIRESCRY_EXPECT_BUILD").ok();
    validate_health_expecting(health, action, cheat, expected_build.as_deref())
}

fn validate_health_expecting(
    health: &Value,
    action: Option<&str>,
    cheat: Option<&str>,
    expected_build: Option<&str>,
) -> CliResult<()> {
    // Protocol compatibility says the host speaks this CLI's contract;
    // it says nothing about which source revision is running. A play
    // session that must not trust a stale host exports
    // SPIRESCRY_EXPECT_BUILD=<buildHash> and every command then hard-fails
    // on a host built from any other revision.
    if let Some(expected) = expected_build.filter(|e| !e.is_empty()) {
        let build = health
            .get("buildHash")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        if build != expected {
            return Err(CliError::fatal(format!(
                "host build mismatch: running '{}' but SPIRESCRY_EXPECT_BUILD is '{}' — \
                 restart the host from the expected build or unset the variable",
                build, expected
            )));
        }
    }

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
                "{}: host does not advertise '{}' (supported: {})",
                REJECTION_BAD_REQUEST, action, supported
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
                "{}: host does not advertise cheat '{}' (supported: {})",
                REJECTION_BAD_REQUEST, cheat, supported
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
        return if err == REJECTION_NOT_READY {
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
    use std::io::{Read, Write};
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
        assert_eq!(path, "/obs?since=42");
        assert_eq!(timeout, std::time::Duration::from_secs(70));

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
    fn health_command_enforces_the_expected_build() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = [0_u8; 1024];
            let _ = stream.read(&mut request).unwrap();
            let body = health(PROTOCOL_VERSION, &["play"], &[]).to_string();
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\
                 Content-Length: {}\r\nConnection: close\r\n\r\n{}",
                body.len(),
                body,
            )
            .unwrap();
        });
        let client = Client {
            base: format!("http://{address}"),
            verbose: false,
            if_rev: None,
            if_run: None,
            follow: None,
        };

        let error = client.health_expecting(Some("wrong-build")).unwrap_err();

        assert!(error.to_string().contains("host build mismatch"), "{error}");
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
    fn replay_fingerprint_uses_the_named_consumer_projection() {
        let a = json!({
            "phase":"combat", "rev":1, "runId":"source", "decorativeFrame":"a",
            "side":"player", "hand":[{"idx":0, "model":"STRIKE_R", "playable":true}],
            "legal":["play", "end-turn"]
        });
        let extension_changed = json!({
            "phase":"combat", "rev":900, "runId":"replay", "decorativeFrame":"b",
            "side":"player", "hand":[{"idx":0, "model":"STRIKE_R", "playable":true}],
            "legal":["play", "end-turn"]
        });
        let card_changed = json!({
            "phase":"combat", "rev":1, "runId":"source", "decorativeFrame":"a",
            "side":"player", "hand":[{"idx":0, "model":"BASH", "playable":true}],
            "legal":["play", "end-turn"]
        });
        let typed_state_changed = json!({
            "phase":"combat", "rev":1, "runId":"source", "decorativeFrame":"a",
            "side":"player", "hand":[{"idx":0, "model":"STRIKE_R", "playable":false}],
            "legal":["play", "end-turn"]
        });

        assert_eq!(state_fingerprint(&a), state_fingerprint(&extension_changed));
        assert_ne!(
            state_fingerprint(&a),
            state_fingerprint(&typed_state_changed)
        );
        assert_ne!(state_fingerprint(&a), state_fingerprint(&card_changed));
    }

    #[test]
    fn replay_fingerprint_tracks_target_identities_and_coordinates() {
        let original = json!({
            "phase":"combat", "act":1, "current":[2,3],
            "next":[{"idx":0,"id":"PATH_A","col":3,"row":4,"type":"monster"}],
            "hand":[{"idx":0,"model":"STRIKE_R","playable":true}],
            "relics":[{"idx":0,"model":"VAJRA"}],
            "enemies":[{"id":7,"model":"CULTIST","hp":[30,40],"block":0,"alive":true}],
            "legal":["play","end-turn"]
        });
        let mut changed = original.clone();

        changed["next"][0]["col"] = json!(4);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["hand"][0]["model"] = json!("BASH");
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["relics"][0]["model"] = json!("ANCHOR");
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["enemies"][0]["id"] = json!(8);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
    }

    #[test]
    fn replay_projection_uses_generated_item_model_wire_key() {
        let mut snapshot = json!({ "phase": "combat", "hand": [{}] });
        snapshot["hand"][0]
            .as_object_mut()
            .unwrap()
            .insert(PROJECTION_ITEM_MODEL.wire.into(), json!("STRIKE_R"));

        let projected = consumer_projection(&snapshot);

        assert_eq!(
            projected["hand"][0].get(PROJECTION_ITEM_MODEL.output),
            Some(&json!("STRIKE_R"))
        );
    }

    #[test]
    fn replay_fingerprint_tracks_hp_gold_and_combat_resources() {
        let original = json!({
            "phase":"combat", "gold":100,
            "you":{"hp":[60,80],"block":5,"energy":[2,3],"stars":1},
            "player":{"hp":[60,80],"gold":100,"potions":[]},
            "enemies":[{"id":7,"model":"CULTIST","hp":[30,40],"block":0,"alive":true}],
            "legal":["play","end-turn"]
        });
        let mut changed = original.clone();

        changed["you"]["hp"][0] = json!(59);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["you"]["energy"][0] = json!(1);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["player"]["gold"] = json!(99);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["enemies"][0]["hp"][0] = json!(29);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
    }

    #[test]
    fn replay_fingerprint_matches_the_typed_host_fixture() {
        let snapshot = json!({
            "phase":"combat", "act":1, "current":[2,3], "gold":100,
            "semanticState":["pile:draw:STRIKE_R"],
            "selected":["STRIKE_R"], "side":"player",
            "next":[{"idx":0,"id":"PATH_A","col":3,"row":4,"type":"monster"}],
            "hand":[{
                "idx":0,"model":"STRIKE_R","playable":true,"selected":false,
                "semanticState":["cost:1"]
            }],
            "relics":[{"idx":0,"model":"VAJRA"}],
            "you":{
                "hp":[60,80],"block":5,"energy":[2,3],"stars":1,
                "semanticState":["power:STRENGTH:1"]
            },
            "enemies":[{
                "id":7,"model":"CULTIST","hp":[30,40],"block":0,"alive":true,
                "semanticState":["intent:attack:6:1"]
            }],
            "player":{
                "hp":[60,80],"gold":100,"potions":[],
                "semanticState":["deck:STRIKE_R"]
            },
            "legal":["play","end-turn"]
        });

        assert_eq!(state_fingerprint(&snapshot), "d4c312db8769179e");
    }

    #[test]
    fn replay_fingerprint_tracks_action_target_grammar() {
        let original = json!({
            "phase":"combat", "id":"BIG_FISH", "turn":2,
            "hand":[{
                "idx":0,"model":"BASH","selector":"BASH+","slot":1,
                "target":"anyenemy"
            }],
            "legal":["play","end-turn"]
        });
        let mut changed = original.clone();

        changed["turn"] = json!(3);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["id"] = json!("SCRAP_OOZE");
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["hand"][0]["selector"] = json!("BASH");
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["hand"][0]["slot"] = json!(2);
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        changed = original.clone();
        changed["hand"][0]["target"] = json!("self");
        assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
    }

    #[test]
    fn replay_game_over_fingerprint_matches_the_typed_host_fixture() {
        let snapshot = json!({
            "phase":"game_over", "outcome":"victory", "hp":[20,80],
            "gold":100
        });

        assert_eq!(state_fingerprint(&snapshot), "c02643081d2c619c");
    }

    #[test]
    fn replay_fingerprint_tracks_typed_semantic_state() {
        let original = json!({
            "phase":"combat", "semanticState":["pile:draw:BASH+"],
            "selected":["BASH+"],
            "hand":[{
                "idx":0, "model":"BASH", "selector":"BASH+",
                "selected":false, "semanticState":["cost:2"]
            }],
            "player":{
                "hp":[60,80], "gold":100, "potions":[],
                "semanticState":["deck:BASH+"]
            },
            "you":{
                "hp":[60,80], "energy":[2,3],
                "semanticState":["power:STRENGTH:1"]
            },
            "enemies":[{
                "id":7, "model":"CULTIST", "hp":[30,40],
                "semanticState":["intent:attack:6:1"]
            }],
            "legal":["play","end-turn"]
        });
        let mut presentation_changed = original.clone();
        presentation_changed["decorativeFrame"] = json!("plain");
        presentation_changed["hand"][0]["title"] = json!("localized title");
        presentation_changed["player"]["deckDescription"] = json!("localized deck");
        presentation_changed["you"]["powerDescription"] = json!("localized power");
        presentation_changed["enemies"][0]["title"] = json!("localized enemy");

        assert_eq!(
            state_fingerprint(&original),
            state_fingerprint(&presentation_changed)
        );

        for (pointer, value) in [
            ("/semanticState/0", json!("pile:draw:STRIKE_R")),
            ("/hand/0/semanticState/0", json!("cost:1")),
            ("/player/semanticState/0", json!("deck:STRIKE_R")),
            ("/you/semanticState/0", json!("power:WEAK:1")),
            ("/enemies/0/semanticState/0", json!("intent:attack:12:1")),
            ("/hand/0/selected", json!(true)),
        ] {
            let mut changed = original.clone();
            *changed.pointer_mut(pointer).unwrap() = value;
            assert_ne!(state_fingerprint(&original), state_fingerprint(&changed));
        }
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
    fn cheat_optional_boolean_is_mapped_from_the_generated_shape() {
        let args = cheat_args("card", &strings(&["WHIRLWIND", "true"])).unwrap();

        assert_eq!(
            args,
            json!({ "name": "card", "id": "WHIRLWIND", "upgraded": true })
        );
    }

    #[test]
    fn known_cheat_cardinality_is_enforced_from_the_generated_shape() {
        let missing = cheat_args("goto", &strings(&["3"])).unwrap_err();
        let extra = cheat_args("heal", &strings(&["surplus"])).unwrap_err();

        assert!(missing.contains("expects 2 arguments"), "{missing}");
        assert!(extra.contains("expects 0 arguments"), "{extra}");
    }

    #[test]
    fn generated_protocol_constants_match_the_checked_artifact() {
        let artifact: Value = serde_json::from_str(include_str!("../../protocol.json")).unwrap();
        let faults = FAULT_EVENT_TOKENS
            .iter()
            .map(|(name, value)| ((*name).to_string(), json!(value)))
            .collect::<serde_json::Map<_, _>>();
        let cheats = CHEAT_ARGUMENT_SHAPES
            .iter()
            .map(|shape| {
                let arguments = shape
                    .arguments
                    .iter()
                    .map(|argument| {
                        let kind = match argument.kind {
                            ProtocolArgumentKind::Boolean => "boolean",
                            ProtocolArgumentKind::Integer => "integer",
                            ProtocolArgumentKind::String => "string",
                        };
                        let mut value = json!({ "name": argument.name, "type": kind });
                        if argument.optional {
                            value["optional"] = json!(true);
                        }
                        value
                    })
                    .collect::<Vec<_>>();
                json!({ "name": shape.name, "arguments": arguments })
            })
            .collect::<Vec<_>>();

        assert_eq!(artifact["protocolVersion"], PROTOCOL_VERSION);
        assert_eq!(artifact["rejectionCodes"], json!(REJECTION_CODES));
        assert_eq!(artifact["phases"], json!(PHASES));
        let outcomes = artifact["settlementOutcomes"]
            .as_array()
            .unwrap()
            .iter()
            .map(SettlementOutcome::from_value)
            .collect::<Option<Vec<_>>>()
            .unwrap();
        assert_eq!(
            outcomes,
            vec![
                SettlementOutcome::Settled,
                SettlementOutcome::NextDecision,
                SettlementOutcome::Fault,
                SettlementOutcome::Timeout,
            ]
        );
        assert_eq!(artifact["faultEventTokens"], Value::Object(faults));
        assert_eq!(artifact["cheatArgumentShapes"], json!(cheats));
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
        let args = cheat_args("future-cheat", &[]).unwrap();

        assert_eq!(args, json!({ "name": "future-cheat" }));
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
    fn expected_build_mismatch_fails_every_health_gate() {
        let value = health(PROTOCOL_VERSION, &["play"], &[]);

        let err =
            validate_health_expecting(&value, Some("play"), None, Some("fff9999")).unwrap_err();
        assert!(err.contains("host build mismatch"), "{err}");
        assert!(err.contains("abc1234"), "{err}");
        assert!(err.contains("fff9999"), "{err}");

        // A host that predates buildHash reporting must not slip through
        // as a silent match.
        let err = validate_health_expecting(
            &json!({ "ok": true, "protocolVersion": PROTOCOL_VERSION }),
            None,
            None,
            Some("abc1234"),
        )
        .unwrap_err();
        assert!(err.contains("'unknown'"), "{err}");
    }

    #[test]
    fn expected_build_match_or_absence_changes_nothing() {
        let value = health(PROTOCOL_VERSION, &["play"], &[]);

        assert_eq!(
            validate_health_expecting(&value, Some("play"), None, Some("abc1234")),
            Ok(())
        );
        assert_eq!(
            validate_health_expecting(&value, Some("play"), None, None),
            Ok(())
        );
        // An empty export (SPIRESCRY_EXPECT_BUILD=) means "not enforcing".
        assert_eq!(
            validate_health_expecting(&value, Some("play"), None, Some("")),
            Ok(())
        );
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

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
    Skip,
    /// Travel to a map node; in crystal_sphere, click a board cell instead
    MapMove {
        /// Map-node column from obs.next, or crystal_sphere cell column
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        col: i32,
        /// Map-node row from obs.next, or crystal_sphere cell row
        #[arg(value_parser = clap::value_parser!(i32).range(0..))]
        row: i32,
    },
    /// Dev/verification cheats: goto <col> <row> | gold <n> | hp <n> | heal | wound-enemies | event <ID> | card <ID> | card-upgraded <ID> | relic <ID>
    Cheat { name: String, values: Vec<String> },
    /// Play an exact hand-card selector (MODEL, MODEL+, MODEL@ENCHANTMENT, MODEL!AFFLICTION)
    Play {
        /// Selector from obs.hand; identical selectors resolve in hand order
        model: String,
        /// Enemy combat id (omit to auto-target a lone enemy)
        #[arg(long)]
        target: Option<u32>,
    },
    /// End the player turn
    EndTurn,
    /// Drink a potion by slot (combat; from obs.potions)
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
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let client = Client {
        base: format!("http://{}:{}", cli.host, cli.port),
        verbose: cli.verbose,
    };
    let result = match &cli.cmd {
        Cmd::Health => client.get("/health", Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS)),
        Cmd::Obs {
            since,
            wait,
            compact,
        } => obs_request(*since, *wait, *compact)
            .and_then(|(path, timeout)| client.get(&path, timeout)),
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
        Cmd::Skip => client.step("skip", json!({})),
        Cmd::Buy { kind, idx } => client.step("buy", json!({ "kind": kind, "idx": idx })),
        Cmd::Leave => client.step("leave", json!({})),
        Cmd::PickRelic { idx } => client.step("pick-relic", json!({ "idx": idx })),
        Cmd::Cheat { name, values } => {
            cheat_args(name, values).and_then(|args| client.step("cheat", args))
        }
        Cmd::MapMove { col, row } => client.step("map-move", json!({ "col": col, "row": row })),
        Cmd::Play { model, target } => {
            let mut args = json!({ "model": model });
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
            // EX_TEMPFAIL: callers can retry on the exit code instead of
            // grepping stderr for the transient signal.
            if e.starts_with("not_ready") {
                ExitCode::from(75)
            } else {
                ExitCode::FAILURE
            }
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
        ("gold", [value]) | ("hp", [value]) => args["value"] = json!(num(value)?),
        ("event", [id]) | ("card", [id]) | ("card-upgraded", [id]) | ("relic", [id]) => {
            args["id"] = json!(id)
        }
        _ => {}
    }
    Ok(args)
}

// The HTTP side of every subcommand. --verbose traces round-trips on
// stderr (stdout stays JSON-only for pipes), stamped with the same UTC
// clock the host log uses so the two sides line up.
struct Client {
    base: String,
    verbose: bool,
}

impl Client {
    fn get(&self, path: &str, timeout: Duration) -> Result<Value, String> {
        let url = format!("{}{}", self.base, path);
        self.exchange(format!("GET {}", url), || {
            ureq::get(&url).timeout(timeout).call().map_err(Box::new)
        })
    }

    fn post(&self, path: &str, body: Value) -> Result<Value, String> {
        let url = format!("{}{}", self.base, path);
        let request_line = format!("POST {} {}", url, body);
        self.exchange(request_line, || {
            ureq::post(&url)
                .timeout(Duration::from_millis(DEFAULT_HTTP_TIMEOUT_MS))
                .send_json(body)
                .map_err(Box::new)
        })
    }

    fn step(&self, action: &str, args: Value) -> Result<Value, String> {
        self.post("/step", json!({ "action": action, "args": args }))
    }

    fn exchange(
        &self,
        request_line: String,
        send: impl FnOnce() -> Result<ureq::Response, Box<ureq::Error>>,
    ) -> Result<Value, String> {
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

fn handle(result: Result<ureq::Response, Box<ureq::Error>>) -> Result<Value, String> {
    let resp = match result {
        Ok(resp) => resp,
        Err(error) => match *error {
            // Bridge errors ride on 4xx/5xx with a JSON body — parse it.
            ureq::Error::Status(_, resp) => resp,
            ureq::Error::Transport(error) => return Err(error.to_string()),
        },
    };
    let value: Value = resp
        .into_json()
        .map_err(|e| format!("non-json response: {}", e))?;
    if value.get("ok").and_then(Value::as_bool) == Some(false) {
        let err = value
            .get("err")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let msg = value.get("msg").and_then(Value::as_str).unwrap_or("");
        return Err(format!("{}: {}", err, msg));
    }
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::{CommandFactory, Parser};
    use std::net::TcpListener;
    use std::thread;
    use std::time::Instant;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|s| s.to_string()).collect()
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
            } => {
                assert_eq!(since, Some(42));
                assert_eq!(wait, Some(250));
                assert!(!compact);
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
        };

        let start = Instant::now();
        let result = client.get("/", Duration::from_millis(25));

        assert!(result.is_err());
        assert!(start.elapsed() < Duration::from_millis(200));
        server.join().unwrap();
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
    fn parses_play_target_as_optional() {
        let cli =
            Cli::try_parse_from(["spirescry", "play", "StrikeIronclad", "--target", "7"]).unwrap();

        match cli.cmd {
            Cmd::Play { model, target } => {
                assert_eq!(model, "StrikeIronclad");
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
    fn cheat_card_upgraded_and_relic_map_id() {
        let upgraded = cheat_args("card-upgraded", &strings(&["WHIRLWIND"])).unwrap();
        let relic = cheat_args("relic", &strings(&["KUNAI"])).unwrap();

        assert_eq!(
            upgraded,
            json!({ "name": "card-upgraded", "id": "WHIRLWIND" })
        );
        assert_eq!(relic, json!({ "name": "relic", "id": "KUNAI" }));
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
}

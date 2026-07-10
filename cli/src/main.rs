// spirescry: minimal CLI for the spirescry HTTP bridge.
//
// Read the board with `obs`, act with `play` / `end-turn`. Output is
// pretty-printed JSON; bridge errors ({ok:false, err, msg}) go to stderr
// with a non-zero exit — 75 (EX_TEMPFAIL) for the retryable `not_ready`,
// 1 for everything else.

use std::io::Write;
use std::process::ExitCode;

use clap::{Parser, Subcommand};
use serde_json::{json, Value};

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
        #[arg(long)]
        since: Option<u64>,
        /// Max milliseconds to wait for a change (with --since)
        #[arg(long)]
        wait: Option<u32>,
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
        #[arg(long)]
        ascension: Option<u32>,
    },
    /// Abandon the active run and return to the main menu
    Abandon,
    /// Choose an option by index (events and rest sites, from obs.options)
    #[command(name = "option")]
    EventOption { idx: u32 },
    /// Buy from the shop: kind is card/colorless/relic/potion/card_removal
    Buy {
        kind: String,
        #[arg(long)]
        idx: u32,
    },
    /// Leave the shop
    Leave,
    /// Pick an offered relic by index (treasure / relic reward)
    PickRelic { idx: u32 },
    /// Advance / close event dialogue, or leave the rewards screen
    Proceed,
    /// Claim a combat-reward tile by index (from obs.rewards)
    PickReward { idx: u32 },
    /// Pick/toggle a card by index (card rewards, deck pickers, hand select)
    PickCard { idx: u32 },
    /// Confirm the current card selection (deck pickers, hand select)
    Confirm,
    /// Skip a card/relic offer, or cancel a cancelable deck picker
    Skip,
    /// Travel to a map node (col/row from obs.next)
    MapMove { col: u32, row: u32 },
    /// Dev/verification cheats: goto <col> <row> | gold <n> | hp <n> | heal | wound-enemies | event <ID> | card <ID>
    Cheat { name: String, values: Vec<String> },
    /// Play a hand card by model entry (e.g. StrikeIronclad)
    Play {
        model: String,
        /// Enemy combat id (omit to auto-target a lone enemy)
        #[arg(long)]
        target: Option<u32>,
    },
    /// End the player turn
    EndTurn,
    /// Drink a potion by slot (combat; from obs.potions)
    PotionUse {
        slot: u32,
        /// Enemy combat id for targeted potions
        #[arg(long)]
        target: Option<u32>,
    },
    /// Discard a potion by slot (anywhere in a run)
    PotionDiscard { slot: u32 },
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let base = format!("http://{}:{}", cli.host, cli.port);
    let result = match &cli.cmd {
        Cmd::Health => get(&base, "/health"),
        Cmd::Obs {
            since,
            wait,
            compact,
        } => {
            if wait.is_some() && since.is_none() {
                Err("--wait has no effect without --since".to_string())
            } else {
                let mut path = String::from("/obs");
                let mut sep = '?';
                if let Some(s) = since {
                    path = format!("{}{}since={}&wait={}", path, sep, s, wait.unwrap_or(5000));
                    sep = '&';
                }
                if *compact {
                    path = format!("{}{}compact=1", path, sep);
                }
                get(&base, &path)
            }
        }
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
            step(&base, "new-run", args)
        }
        Cmd::Abandon => step(&base, "abandon", json!({})),
        Cmd::EventOption { idx } => step(&base, "option", json!({ "idx": idx })),
        Cmd::Proceed => step(&base, "proceed", json!({})),
        Cmd::PickReward { idx } => step(&base, "pick-reward", json!({ "idx": idx })),
        Cmd::PickCard { idx } => step(&base, "pick-card", json!({ "idx": idx })),
        Cmd::Confirm => step(&base, "confirm", json!({})),
        Cmd::Skip => step(&base, "skip", json!({})),
        Cmd::Buy { kind, idx } => step(&base, "buy", json!({ "kind": kind, "idx": idx })),
        Cmd::Leave => step(&base, "leave", json!({})),
        Cmd::PickRelic { idx } => step(&base, "pick-relic", json!({ "idx": idx })),
        Cmd::Cheat { name, values } => {
            cheat_args(name, values).and_then(|args| step(&base, "cheat", args))
        }
        Cmd::MapMove { col, row } => {
            step(&base, "map-move", json!({ "col": col, "row": row }))
        }
        Cmd::Play { model, target } => {
            let mut args = json!({ "model": model });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            step(&base, "play", args)
        }
        Cmd::EndTurn => step(&base, "end-turn", json!({})),
        Cmd::PotionUse { slot, target } => {
            let mut args = json!({ "slot": slot });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            step(&base, "potion-use", args)
        }
        Cmd::PotionDiscard { slot } => step(&base, "potion-discard", json!({ "slot": slot })),
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

// Positional sugar for the known cheat arg shapes; the bridge's own
// per-cheat validation is the source of truth.
fn cheat_args(name: &str, values: &[String]) -> Result<Value, String> {
    let mut args = json!({ "name": name });
    let num = |s: &String| {
        s.parse::<i64>()
            .map_err(|_| format!("invalid number: {}", s))
    };
    match (name, values) {
        ("goto", [col, row]) => {
            args["col"] = json!(num(col)?);
            args["row"] = json!(num(row)?);
        }
        ("gold", [value]) | ("hp", [value]) => args["value"] = json!(num(value)?),
        ("event", [id]) | ("card", [id]) => args["id"] = json!(id),
        _ => {}
    }
    Ok(args)
}

fn get(base: &str, path: &str) -> Result<Value, String> {
    handle(ureq::get(&format!("{}{}", base, path)).call())
}

fn post(base: &str, path: &str, body: Value) -> Result<Value, String> {
    handle(ureq::post(&format!("{}{}", base, path)).send_json(body))
}

fn step(base: &str, action: &str, args: Value) -> Result<Value, String> {
    post(base, "/step", json!({ "action": action, "args": args }))
}

fn handle(result: Result<ureq::Response, ureq::Error>) -> Result<Value, String> {
    let resp = match result {
        Ok(resp) => resp,
        // Bridge errors ride on 4xx/5xx with a JSON body — parse it.
        Err(ureq::Error::Status(_, resp)) => resp,
        Err(ureq::Error::Transport(e)) => return Err(e.to_string()),
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
    use clap::Parser;

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
    fn cheat_rejects_invalid_numbers() {
        let err = cheat_args("goto", &strings(&["x", "5"])).unwrap_err();

        assert_eq!(err, "invalid number: x");
    }

    #[test]
    fn unknown_cheat_passes_name_for_bridge_validation() {
        let args = cheat_args("heal", &[]).unwrap();

        assert_eq!(args, json!({ "name": "heal" }));
    }
}

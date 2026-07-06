// spirescry: minimal CLI for the spirescry HTTP bridge.
//
// Read the board with `obs`, act with `play` / `end-turn`. Output is
// pretty-printed JSON; bridge errors ({ok:false, err, msg}) go to stderr
// with a non-zero exit.

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
    /// Dev/verification cheats: goto <col> <row> | gold <n> | hp <n> | heal | wound-enemies | event <ID>
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
        Cmd::Obs { since, wait } => {
            let mut path = String::from("/obs");
            if let Some(s) = since {
                path = format!("{}?since={}&wait={}", path, s, wait.unwrap_or(5000));
            }
            get(&base, &path)
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
            post(&base, "/step", json!({ "action": "new-run", "args": args }))
        }
        Cmd::Abandon => post(&base, "/step", json!({ "action": "abandon", "args": {} })),
        Cmd::EventOption { idx } => post(
            &base,
            "/step",
            json!({ "action": "option", "args": { "idx": idx } }),
        ),
        Cmd::Proceed => post(&base, "/step", json!({ "action": "proceed", "args": {} })),
        Cmd::PickReward { idx } => post(
            &base,
            "/step",
            json!({ "action": "pick-reward", "args": { "idx": idx } }),
        ),
        Cmd::PickCard { idx } => post(
            &base,
            "/step",
            json!({ "action": "pick-card", "args": { "idx": idx } }),
        ),
        Cmd::Confirm => post(&base, "/step", json!({ "action": "confirm", "args": {} })),
        Cmd::Skip => post(&base, "/step", json!({ "action": "skip", "args": {} })),
        Cmd::Buy { kind, idx } => post(
            &base,
            "/step",
            json!({ "action": "buy", "args": { "kind": kind, "idx": idx } }),
        ),
        Cmd::Leave => post(&base, "/step", json!({ "action": "leave", "args": {} })),
        Cmd::PickRelic { idx } => post(
            &base,
            "/step",
            json!({ "action": "pick-relic", "args": { "idx": idx } }),
        ),
        Cmd::Cheat { name, values } => {
            let mut args = json!({ "name": name });
            let num = |s: &String| s.parse::<i64>().ok();
            match (name.as_str(), values.as_slice()) {
                ("goto", [col, row]) => {
                    args["col"] = json!(num(col));
                    args["row"] = json!(num(row));
                }
                ("gold", [value]) | ("hp", [value]) => args["value"] = json!(num(value)),
                ("event", [id]) => args["id"] = json!(id),
                _ => {}
            }
            post(&base, "/step", json!({ "action": "cheat", "args": args }))
        }
        Cmd::MapMove { col, row } => post(
            &base,
            "/step",
            json!({ "action": "map-move", "args": { "col": col, "row": row } }),
        ),
        Cmd::Play { model, target } => {
            let mut args = json!({ "model": model });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            post(&base, "/step", json!({ "action": "play", "args": args }))
        }
        Cmd::EndTurn => post(&base, "/step", json!({ "action": "end-turn", "args": {} })),
        Cmd::PotionUse { slot, target } => {
            let mut args = json!({ "slot": slot });
            if let Some(t) = target {
                args["target"] = json!(t);
            }
            post(
                &base,
                "/step",
                json!({ "action": "potion-use", "args": args }),
            )
        }
        Cmd::PotionDiscard { slot } => post(
            &base,
            "/step",
            json!({ "action": "potion-discard", "args": { "slot": slot } }),
        ),
    };
    match result {
        Ok(v) => {
            println!("{}", serde_json::to_string_pretty(&v).unwrap());
            ExitCode::SUCCESS
        }
        Err(e) => {
            eprintln!("spirescry: {}", e);
            ExitCode::FAILURE
        }
    }
}

fn get(base: &str, path: &str) -> Result<Value, String> {
    handle(ureq::get(&format!("{}{}", base, path)).call())
}

fn post(base: &str, path: &str, body: Value) -> Result<Value, String> {
    handle(ureq::post(&format!("{}{}", base, path)).send_json(body))
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

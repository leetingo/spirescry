// The published CLI envelope: pretty JSON on stdout and 0 on success,
// failures rendered on stderr with 75 (EX_TEMPFAIL) reserved for the
// retryable not_ready and 1 for everything else — bridge rejection,
// transport failure, malformed response, or local argument error.
//
// These drive the real binary against a canned bridge, so they cover the
// wiring the in-crate value tests cannot: clap's own exit codes, which
// stream each failure lands on, and that a rejected body never reaches
// stdout.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::process::{Command, Output};
use std::thread::JoinHandle;

use serde_json::Value;

const BIN: &str = env!("CARGO_BIN_EXE_spirescry");

fn protocol_version() -> u64 {
    let document: Value = serde_json::from_str(include_str!("../../protocol.json")).unwrap();
    document["protocolVersion"].as_u64().unwrap()
}

fn health_body() -> String {
    serde_json::json!({
        "ok": true,
        "mod": "spirescry",
        "version": "0.1.0",
        "buildHash": "canned",
        "protocolVersion": protocol_version(),
        "capabilities": { "verbs": ["play"], "cheats": [] },
        "phase": "main_menu",
        "rev": 1,
        "runId": "none",
    })
    .to_string()
}

/// A bridge that serves exactly `requests` connections: a compatible
/// `/health` first when `preflight` is set, then this status and body for
/// everything after it. Serving a fixed count means a test that skips a
/// request it promised hangs rather than passing by accident.
fn canned_bridge(
    requests: usize,
    preflight: bool,
    status: u16,
    reason: String,
    body: String,
) -> (u16, JoinHandle<()>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let health = health_body();
    let server = std::thread::spawn(move || {
        for index in 0..requests {
            let (mut stream, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(stream.try_clone().unwrap());
            let mut line = String::new();
            // Drain the request head so the client never sees a reset
            // socket in place of the response we are testing.
            while reader.read_line(&mut line).unwrap_or(0) > 0 {
                if line == "\r\n" || line == "\n" {
                    break;
                }
                line.clear();
            }
            let (status, reason, body) = if preflight && index == 0 {
                (200, "OK".to_string(), health.clone())
            } else {
                (status, reason.clone(), body.clone())
            };
            let head = format!(
                "HTTP/1.1 {} {}\r\nContent-Type: application/json\r\n\
                 Content-Length: {}\r\nConnection: close\r\n\r\n",
                status,
                reason,
                body.len(),
            );
            let _ = stream.write_all(head.as_bytes());
            let _ = stream.write_all(body.as_bytes());
            let _ = stream.flush();
        }
    });
    (port, server)
}

fn spirescry(args: &[&str]) -> Output {
    Command::new(BIN)
        .args(args)
        .env_remove("SPIRESCRY_EXPECT_BUILD")
        .env_remove("STS2_AGENT_HOST")
        .env_remove("STS2_AGENT_PORT")
        .output()
        .unwrap()
}

/// Drive `runlog`: a compatibility preflight on /health, then the canned
/// body on /runlog — a route whose body the CLI would otherwise print, so
/// nothing but the envelope check stands between it and stdout.
fn against_bridge(status: u16, reason: &str, body: &str) -> Output {
    let (port, server) = canned_bridge(2, true, status, reason.to_string(), body.to_string());
    let output = spirescry(&["--port", &port.to_string(), "runlog"]);
    server.join().unwrap();
    output
}

/// Drive `health` itself, so the canned body is the compatibility gate's
/// own response.
fn against_health(status: u16, reason: &str, body: &str) -> Output {
    let (port, server) = canned_bridge(1, false, status, reason.to_string(), body.to_string());
    let output = spirescry(&["--port", &port.to_string(), "health"]);
    server.join().unwrap();
    output
}

fn code(output: &Output) -> i32 {
    output.status.code().expect("the CLI must not be signalled")
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).to_string()
}

#[test]
fn success_help_and_version_exit_zero() {
    let success = against_bridge(200, "OK", r#"{"ok":true,"kind":"recipe","verbs":[]}"#);
    assert_eq!(code(&success), 0, "{}", stderr(&success));
    let rendered: Value = serde_json::from_slice(&success.stdout).unwrap();
    assert_eq!(rendered["ok"], true);
    assert_eq!(rendered["kind"], "recipe");

    let health = against_health(200, "OK", &health_body());
    assert_eq!(code(&health), 0, "{}", stderr(&health));

    for args in [vec!["--help"], vec!["health", "--help"], vec!["--version"]] {
        let output = spirescry(&args);
        assert_eq!(code(&output), 0, "{args:?}: {}", stderr(&output));
        assert!(!output.stdout.is_empty(), "{args:?} printed no help");
        assert!(
            stderr(&output).is_empty(),
            "{args:?} used stderr for output"
        );
    }
}

#[test]
fn not_ready_is_the_only_retryable_exit_status() {
    let not_ready = against_bridge(
        400,
        "Bad Request",
        r#"{"ok":false,"err":"not_ready","msg":"map intro animation"}"#,
    );

    assert_eq!(code(&not_ready), 75, "{}", stderr(&not_ready));
    assert!(
        stderr(&not_ready).contains("not_ready"),
        "{}",
        stderr(&not_ready)
    );
    assert!(not_ready.stdout.is_empty(), "a rejection reached stdout");
}

#[test]
fn bridge_rejections_exit_one_on_stderr() {
    let rejected = against_bridge(
        400,
        "Bad Request",
        r#"{"ok":false,"err":"bad_state","msg":"pick more cards"}"#,
    );

    assert_eq!(code(&rejected), 1, "{}", stderr(&rejected));
    assert!(
        stderr(&rejected).contains("bad_state"),
        "{}",
        stderr(&rejected)
    );
    assert!(rejected.stdout.is_empty(), "a rejection reached stdout");
}

#[test]
fn malformed_success_bodies_exit_one_instead_of_reaching_stdout() {
    for body in [
        "[1,2,3]",
        "\"fine\"",
        "null",
        "{}",
        r#"{"ok":"true"}"#,
        r#"{"kind":"recipe","verbs":[]}"#,
        r#"{"ok":true,"err":"bad_state"}"#,
        "not json at all",
    ] {
        let output = against_bridge(200, "OK", body);

        assert_eq!(code(&output), 1, "{body}: {}", stderr(&output));
        assert!(output.stdout.is_empty(), "{body} reached stdout");
        assert!(
            stderr(&output).contains("malformed bridge response"),
            "{body}: {}",
            stderr(&output),
        );
    }
}

#[test]
fn malformed_error_bodies_exit_one() {
    for (status, reason, body) in [
        (400, "Bad Request", r#"{"err":"bad_state","msg":"no ok"}"#),
        (400, "Bad Request", r#"{"ok":false}"#),
        (500, "Internal Server Error", "{}"),
        (500, "Internal Server Error", "<html>proxy ate it</html>"),
    ] {
        let output = against_bridge(status, reason, body);

        assert_eq!(code(&output), 1, "{body}: {}", stderr(&output));
        assert!(output.stdout.is_empty(), "{body} reached stdout");
        assert!(
            stderr(&output).contains(&format!("HTTP {status}")),
            "{body}: {}",
            stderr(&output),
        );
    }
}

#[test]
fn http_error_status_survives_a_body_that_claims_success() {
    // The failure marker lives only in the status line here — a consumer
    // that dropped it would print this body as a result.
    let output = against_bridge(
        500,
        "Internal Server Error",
        r#"{"ok":true,"kind":"recipe"}"#,
    );
    assert_eq!(code(&output), 1, "{}", stderr(&output));
    assert!(output.stdout.is_empty(), "a 500 reached stdout");
    assert!(stderr(&output).contains("HTTP 500"), "{}", stderr(&output));

    // Same on the compatibility gate: a 500 that happens to carry a valid
    // health body is still a failed request.
    let gate = against_health(500, "Internal Server Error", &health_body());
    assert_eq!(code(&gate), 1, "{}", stderr(&gate));
    assert!(gate.stdout.is_empty(), "a 500 reached stdout");
    assert!(stderr(&gate).contains("HTTP 500"), "{}", stderr(&gate));
}

#[test]
fn transport_failures_exit_one() {
    // Port 1 answers nothing on a loopback interface.
    let output = spirescry(&["--port", "1", "health"]);

    assert_eq!(code(&output), 1, "{}", stderr(&output));
    assert!(
        stderr(&output).starts_with("spirescry: "),
        "{}",
        stderr(&output)
    );
}

#[test]
fn clap_validation_failures_exit_one_not_clap_two() {
    for args in [
        vec!["option", "--", "-1"],
        vec!["map-move", "0"],
        vec!["obs", "--known-card", "BASH"],
        vec!["--port", "70000", "health"],
        vec!["no-such-verb"],
        vec!["health", "--no-such-flag"],
        vec![],
    ] {
        let output = spirescry(&args);

        assert_eq!(code(&output), 1, "{args:?}: {}", stderr(&output));
        assert!(
            !stderr(&output).is_empty(),
            "{args:?} said nothing on stderr"
        );
        assert!(output.stdout.is_empty(), "{args:?} wrote to stdout");
    }
}

#[test]
fn local_validation_failures_exit_one_before_any_request() {
    // Rejected by the CLI itself, so no bridge is listening on this port.
    let output = spirescry(&["--port", "1", "obs", "--wait", "5"]);
    assert_eq!(code(&output), 1, "{}", stderr(&output));
    assert!(
        stderr(&output).contains("--wait has no effect without --since"),
        "{}",
        stderr(&output),
    );

    let unbalanced = spirescry(&["--port", "1", "cheat", "goto", "3"]);
    assert_eq!(code(&unbalanced), 1, "{}", stderr(&unbalanced));
    assert!(
        stderr(&unbalanced).contains("expects 2 arguments"),
        "{}",
        stderr(&unbalanced),
    );
    assert!(unbalanced.stdout.is_empty());
}

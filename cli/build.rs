use std::env;
use std::fs;
use std::path::PathBuf;

use serde_json::Value;

mod protocol_generator;

fn main() {
    let manifest = PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));
    let artifact = manifest.join("../protocol.json");
    println!("cargo:rerun-if-changed={}", artifact.display());

    let document: Value =
        serde_json::from_str(&fs::read_to_string(&artifact).expect("read protocol.json"))
            .expect("parse protocol.json");
    let generated = protocol_generator::generate(&document);
    let output = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR")).join("protocol.rs");
    fs::write(output, generated).expect("write generated protocol constants");
}

#[path = "../protocol_generator.rs"]
mod protocol_generator;

use serde_json::Value;

fn document() -> Value {
    serde_json::from_str(include_str!("../../protocol.json")).unwrap()
}

fn item_model<'a>(document: &'a mut Value) -> &'a mut Value {
    document["consumerProjection"]["item"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .find(|field| field["symbol"] == "model")
        .unwrap()
}

#[test]
fn projection_schema_generates_named_fields_and_kind_groups() {
    let generated = protocol_generator::generate(&document());

    assert!(generated.contains(
        "const PROJECTION_ITEM_MODEL: ProjectionField = ProjectionField { wire: \"model\", output: \"model\" };"
    ));
    assert!(
        generated.contains("const PROJECTION_ITEM_OPTIONAL_STRING_FIELDS: &[ProjectionField] = &[")
    );
    assert!(generated.contains("    PROJECTION_ITEM_MODEL,"));
}

#[test]
fn projection_wire_rename_updates_the_existing_generated_symbol() {
    let mut renamed = document();
    item_model(&mut renamed)["wire"] = Value::String("cardModel".into());

    let generated = protocol_generator::generate(&renamed);

    assert!(generated.contains(
        "const PROJECTION_ITEM_MODEL: ProjectionField = ProjectionField { wire: \"cardModel\", output: \"model\" };"
    ));
    assert!(!generated.contains(
        "const PROJECTION_ITEM_MODEL: ProjectionField = ProjectionField { wire: \"model\", output: \"model\" };"
    ));
}

#[test]
fn projection_output_rename_updates_the_existing_generated_symbol() {
    let mut renamed = document();
    item_model(&mut renamed)["output"] = Value::String("cardIdentity".into());

    let generated = protocol_generator::generate(&renamed);

    assert!(generated.contains(
        "const PROJECTION_ITEM_MODEL: ProjectionField = ProjectionField { wire: \"model\", output: \"cardIdentity\" };"
    ));
    assert!(!generated.contains(
        "const PROJECTION_ITEM_MODEL: ProjectionField = ProjectionField { wire: \"model\", output: \"model\" };"
    ));
}

#[test]
fn removing_a_projection_field_removes_its_compile_time_symbol() {
    let mut removed = document();
    removed["consumerProjection"]["item"]
        .as_array_mut()
        .unwrap()
        .retain(|field| field["symbol"] != "model");

    let generated = protocol_generator::generate(&removed);

    assert!(!generated.contains("PROJECTION_ITEM_MODEL"));
}

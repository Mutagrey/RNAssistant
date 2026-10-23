"""Independent schema/codec parity over the exported production codec fixtures."""
import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator


def main(directory):
    root = Path(directory)
    schema = json.loads((root / "schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    assert schema["type"] == "object" and "anyOf" not in schema
    assert schema["required"] == ["response"] and not schema["additionalProperties"]
    validator = Draft202012Validator(schema)
    checked = 0
    projections = 0
    for case in json.loads((root / "cases.json").read_text()):
        # Duplicate keys, JSON extensions and depth are lexical parser obligations.
        if case["syntax_only"]:
            continue
        value = json.loads(case["raw"])
        errors = list(validator.iter_errors({"response": value}))
        valid = not errors
        assert valid == (case["schema_valid"] or case["name"] == "duplicate evidence"), (case["name"], [error.message for error in errors])
        if valid != case["parser_valid"]:
            assert case["name"] in ("omitted optional logical form", "duplicate evidence"), case["name"]
            projections += 1
        checked += 1
    no_tools = json.loads((root / "schema-no-tools.json").read_text())
    Draft202012Validator.check_schema(no_tools)
    empty_validator = Draft202012Validator(no_tools)
    for action in ("execute", "reflection", "finish"):
        case = next(case for case in json.loads((root / "cases.json").read_text()) if case["name"] == action)
        assert empty_validator.is_valid({"response": json.loads(case["raw"])}) == (action != "execute")
    print(f"PASS independent JSON Schema: {checked} cases; {projections} explicit optional-field transport projection; no-tools union.")


if __name__ == "__main__":
    main(sys.argv[1])

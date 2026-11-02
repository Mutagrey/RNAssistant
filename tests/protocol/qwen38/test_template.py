"""Offline chat-template contract checks. Never loads model weights or calls an API."""
import hashlib
import json
from pathlib import Path

from jinja2.sandbox import SandboxedEnvironment

ROOT = Path(__file__).parent
source = json.loads((ROOT / "source.json").read_text())
template_bytes = (ROOT / "chat_template.jinja").read_bytes()
assert hashlib.sha256(template_bytes).hexdigest() == source["sha256"]
env = SandboxedEnvironment()


def fail(message):
    raise ValueError(message)


env.globals["raise_exception"] = fail
# This pinned upstream snapshot names a nonstandard test. Preserve the downloaded
# source and declare the local compatibility assumption; this is not a server
# qualification. All call/result rendering below is otherwise unmodified.
assert "content is tiger_royal" in template_bytes.decode()
env.tests["tiger_royal"] = env.tests["string"]
template = env.from_string(template_bytes.decode())


def render(messages):
    # HF templates consume parsed function.arguments; OpenAI transport uses a
    # JSON string. This fixture explicitly adapts that boundary before rendering; the deployed server must be checked separately.
    messages = json.loads(json.dumps(messages))
    for message in messages:
        for call in message.get("tool_calls", []):
            function = call["function"]
            if isinstance(function["arguments"], str):
                function["arguments"] = json.loads(function["arguments"])
    return template.render(messages=messages, tools=[], add_generation_prompt=True,
                           enable_thinking=False, preserve_thinking=False)


call = {"id": "T2", "type": "function", "function": {
    "name": "diagnostic_read", "arguments": '{"target":"ModuleB"}'}}
messages = [
    {"role": "user", "content": "Read ModuleB."},
    {"role": "assistant", "content": "", "tool_calls": [call]},
    {"role": "tool", "tool_call_id": "T2", "content": '{"marker":"BLUE"}'}]
prompt = render(messages)
assert "BLUE" in prompt and "ModuleB" in prompt
assert "<tool_response>" in prompt and "<function=diagnostic_read>" in prompt
assert "T2" not in prompt and "tool_call_id" not in prompt
messages[1]["tool_calls"][0]["id"] = "call_long_external_identifier"
messages[2]["tool_call_id"] = "call_long_external_identifier"
assert render(messages) == prompt, "ID length cannot restore omitted information"

messages[2]["content"] = '{"marker":"BLUE","visible_reference":"E1"}'
assert "E1" in render(messages), "References explicitly in content remain visible"

decision = {"action": "execute", "reason": "Read both modules.", "tool_calls": [
    {"name": "diagnostic_read", "arguments": {"target": "ModuleA"}},
    {"name": "diagnostic_read", "arguments": {"target": "ModuleB"}}]}
results = {"type": "tool_results", "results": [
    {"call_index": 0, "name": "diagnostic_read", "status": "ok", "data": {"marker": "RED"}},
    {"call_index": 1, "name": "diagnostic_read", "status": "ok", "data": {"marker": "BLUE"}}]}
json_prompt = render([messages[0], {"role": "assistant", "content": json.dumps(decision)},
                      {"role": "user", "content": json.dumps(results)}])
assert json.dumps(decision) in json_prompt and json.dumps(results) in json_prompt
assert "call_index" in json_prompt and "tool_call_id" not in json_prompt
assert "<tool_response>" not in json_prompt, "user JSON and native tool markup are distinct"
print("PASS with the declared local Jinja adapter: visible content, hidden outer IDs, ID-length invariance, evidence, JSON batch mapping")

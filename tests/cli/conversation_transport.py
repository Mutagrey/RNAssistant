"""Scripted endpoints emit the selected production conversation transport."""
import json
import re


def conversation_content(request, logical):
    if isinstance(logical, dict) and logical.get("finish_from_request"):
        logical = finish(request, logical.get("message", "Finished."), logical.get("assessment", "satisfied"))
    content = logical if isinstance(logical, str) else json.dumps(logical)
    response_format = request.get("response_format", {})
    if (response_format.get("type") == "json_schema" and
            response_format.get("json_schema", {}).get("name") == "rnassistant_conversation_response_v8"):
        # Preserve intentionally invalid fixture bytes, including duplicate keys.
        return '{"response":' + content + '}'
    return content


def finish(request, message="Finished.", assessment="satisfied", direct=False):
    """Cite an actual terminal receipt, never the native transport call ID."""
    evidence = []
    last_outcome = None
    if not direct:
        for item in request["messages"]:
            content = item.get("content") or ""
            if not isinstance(content, str):
                continue
            if content.startswith("TOOL_RESULT:\n") or content.startswith("TOOL_INTERACTION ("):
                content = content.split("\n", 1)[1]
            try:
                receipt = json.loads(content)
            except (ValueError, TypeError):
                if item.get("role") == "user" and not content.startswith(("RUNTIME_", "SELECTED_SKILL", "FORMAT_REPAIR:", "TOOL_")):
                    evidence.clear()
                    last_outcome = None
                continue
            if not isinstance(receipt, dict):
                continue
            reference = receipt.get("tool_call_id", "")
            if str(receipt.get("status", receipt.get("outcome", ""))).lower() in ("ok", "error", "unknown") and re.fullmatch(r"E[1-9][0-9]*", reference):
                evidence.append(reference)
                last_outcome = str(receipt.get("status", receipt.get("outcome", ""))).lower()
    if assessment == "satisfied" and last_outcome in ("error", "unknown"):
        assessment = "not_satisfied"
    return {"action": "finish", "reason": "Report the observed outcome.",
            "outcome": {"satisfied": "completed", "partially_satisfied": "partial", "unverified": "unable_to_complete", "not_satisfied": "unable_to_complete"}[assessment],
            "results": [{"summary": message, "evidence": [evidence[-1]] if evidence else [], "assessment": assessment}] if evidence or assessment != "satisfied" else [],
            "message": message}


def reply_to_title(handler, request, capture=None, raw=None):
    """Title generation is not a conversation step and consumes no scripted decision."""
    if "response_format" in request:
        return False
    body = json.dumps({"choices": [{"message": {"role": "assistant", "content": "Fixture title"}}]}).encode()
    if capture is not None:
        capture(raw, body)
    handler.send_response(200)
    handler.send_header("Content-Type", "application/json")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)
    return True

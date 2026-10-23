"""Scripted endpoints emit the selected production conversation transport."""
import json


def conversation_content(request, logical):
    content = logical if isinstance(logical, str) else json.dumps(logical)
    response_format = request.get("response_format", {})
    if (response_format.get("type") == "json_schema" and
            response_format.get("json_schema", {}).get("name") == "rnassistant_conversation_response_v8"):
        # Preserve intentionally invalid fixture bytes, including duplicate keys.
        return '{"response":' + content + '}'
    return content

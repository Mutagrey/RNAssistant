(function (root) {
    "use strict";
    const fail = root.RNTabularSelectors.fail;

    // Template input must not silently accept duplicate keys or non-finite JSON numbers.
    function parse(text) {
        if (typeof text !== "string" || !text.length || text.length > 1000000)
            fail("INPUT_LIMIT", "", "Expected at most 1000000 UTF-16 code units of JSON.");
        let position = 0, nodes = 0;
        const error = message => fail("JSON_INVALID", "@" + position, message);
        const space = () => { while (/[\t\n\r ]/.test(text[position] || "") && position < text.length) position++; };
        function string() {
            const start = position++;
            while (position < text.length) {
                const character = text[position++];
                if (character === "\\") position++;
                else if (character === '"') {
                    try { return JSON.parse(text.slice(start, position)); }
                    catch (_) { error("Invalid JSON string."); }
                }
            }
            error("Unterminated JSON string.");
        }
        function value(depth) {
            space();
            if (++nodes > 200000 || depth > 32) fail("INPUT_LIMIT", "@" + position, "JSON structure exceeds its budget.");
            const character = text[position];
            if (character === '"') return string();
            if (character === "{" || character === "[") {
                const object = character === "{", result = object ? Object.create(null) : [], end = object ? "}" : "]";
                position++; space();
                if (text[position] === end) { position++; return result; }
                while (position < text.length) {
                    let key;
                    if (object) {
                        if (text[position] !== '"') error("Expected a JSON property name.");
                        key = string(); space();
                        if (["__proto__", "constructor", "prototype"].includes(key)) fail("SCHEMA_INVALID", "@" + position, "Reserved JSON property: " + key);
                        if (Object.prototype.hasOwnProperty.call(result, key)) error("Duplicate JSON property: " + key);
                        if (text[position++] !== ":") error("Expected a colon.");
                    }
                    const item = value(depth + 1);
                    if (object) result[key] = item; else result.push(item);
                    space();
                    if (text[position] === end) { position++; return result; }
                    if (text[position++] !== ",") error("Expected a comma.");
                    space();
                }
                error("Unterminated JSON structure.");
            }
            for (const literal of ["true", "false", "null"]) if (text.startsWith(literal, position)) {
                position += literal.length; return literal === "null" ? null : literal === "true";
            }
            const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(position));
            if (!match) error("Expected a JSON value.");
            position += match[0].length;
            const number = Number(match[0]);
            if (!Number.isFinite(number)) error("Non-finite JSON number.");
            return number;
        }
        const result = value(0); space();
        if (position !== text.length) error("Unexpected JSON suffix.");
        return result;
    }
    root.RNTabularJson = Object.freeze({ parse });
}(globalThis));

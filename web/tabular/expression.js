(function (root) {
    "use strict";
    const geometry = root.RNTabularSelectors, fail = geometry.fail;
    const freeze = Object.freeze;
    const months = ["январь|янв", "февраль|фев", "март|мар", "апрель|апр", "май|мая", "июнь|июн",
        "июль|июл", "август|авг", "сентябрь|сен|сент", "октябрь|окт", "ноябрь|ноя|нояб", "декабрь|дек"];

    // Syntax nodes retain offsets. Only names/calls/literals exist in this first language slice.
    function parse(text, path) {
        if (typeof text !== "string" || !text.length || text.length > 1024) fail("EXPRESSION_LIMIT", path, "Expression exceeds 1024 characters.");
        let position = 0, count = 0;
        const error = message => fail("EXPRESSION_INVALID", path + "@" + position, message);
        const space = () => { while (/\s/.test(text[position] || "") && position < text.length) position++; };
        function node(depth) {
            space();
            if (++count > 128 || depth > 16) fail("EXPRESSION_LIMIT", path, "Expression structure exceeds its budget.");
            const start = position;
            let result;
            if (text[position] === '"') {
                position++;
                while (position < text.length) {
                    const char = text[position++];
                    if (char === "\\") position++;
                    else if (char === '"') break;
                }
                let value;
                try { value = JSON.parse(text.slice(start, position)); } catch (_) { error("Invalid string literal."); }
                result = { kind: "literal", value };
            } else {
                const match = /^(?:[0-9]+|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)/.exec(text.slice(position));
                if (!match) error("Expected a literal, variable or registered function.");
                const name = match[0]; position += name.length; space();
                if (/^[0-9]/.test(name)) {
                    const value = Number(name);
                    if (!Number.isSafeInteger(value)) error("Integer literal is out of range.");
                    result = { kind: "literal", value };
                } else if (text[position] === "(") {
                    position++; space();
                    const args = [];
                    if (text[position] !== ")") while (true) {
                        args.push(node(depth + 1)); space();
                        if (text[position] !== ",") break;
                        position++;
                    }
                    if (text[position++] !== ")") error("Expected closing parenthesis.");
                    result = { kind: "call", name, args: freeze(args) };
                } else result = { kind: "name", name };
            }
            return freeze({ ...result, start, end: position });
        }
        const ast = node(0); space();
        if (position !== text.length) error("Unexpected expression suffix.");
        return ast;
    }

    function compile(text, path, scope) {
        const ast = parse(text, path), reads = [], contexts = new Set();
        function bind(node) {
            const location = path + "@" + node.start;
            const reject = message => fail("EXPRESSION_TYPE", location, message);
            if (node.kind === "literal") return freeze({ op: "literal", type: typeof node.value === "string" ? "text" : "integer", value: node.value, path: location });
            if (node.kind === "name") {
                if ((node.name === "row" || node.name === "col") && scope[node.name])
                    return freeze({ op: "variable", name: node.name, type: "integer", path: location });
                if (/^ctx\.[A-Za-z_][A-Za-z0-9_]*$/.test(node.name)) {
                    const name = node.name.slice(4), context = scope.context(name);
                    contexts.add(name);
                    return freeze({ op: "context", name, type: context.type, path: location });
                }
                reject("Unknown or unavailable variable: " + node.name);
            }
            const args = node.args.map(bind), types = args.map(arg => arg.type);
            const exact = expected => types.length === expected.length && types.every((type, index) => type === expected[index]);
            let type;
            if (node.name === "cell") {
                if (args.length !== 2) reject("cell requires a column and row.");
                let column, row;
                if (args[0].op === "variable" && args[0].name === "col") column = "col";
                else if (args[0].op === "literal" && types[0] === "text" && /^[A-Za-z]{1,3}$/.test(args[0].value))
                    column = geometry.rectangle(args[0].value + "1").firstColumn;
                else reject("A cell column must be a literal letter name or col.");
                if (args[1].op === "variable" && args[1].name === "row") row = "row";
                else if (args[1].op === "literal" && types[1] === "integer" && args[1].value >= 1 && args[1].value <= 1048576) row = args[1].value;
                else reject("A cell row must be a positive literal or row.");
                const read = freeze({ op: "cell", type: "cell", column, row, path: location }); reads.push(read); return read;
            }
            if (node.name === "text" && exact(["cell"])) type = "text";
            else if (node.name === "raw" && exact(["cell"])) type = "raw";
            else if (node.name === "parse.year" && exact(["text"])) type = "year";
            else if (node.name === "parse.monthRu" && exact(["text"])) type = "month";
            else if (node.name === "period.month" && exact(["year", "month"])) type = "period";
            else if (node.name === "period.year" && exact(["year"])) type = "period";
            else if (node.name === "decimal" && exact(["raw", "text"]) && args[1].op === "literal" && ["ru-RU", "en-US"].includes(args[1].value)) type = "value";
            else if (node.name === "lookup" && exact(["text", "text", "text"]) && args[0].op === "literal" && args[2].op === "literal" &&
                ["parameters", "units"].includes(args[0].value)) type = args[0].value === "parameters" ? "parameter" : "unit";
            else reject("Unknown function or invalid arguments: " + node.name);
            return freeze({ op: node.name, args: freeze(args), type, path: location });
        }
        const program = bind(ast);
        return freeze({ type: program.type, program, reads: freeze(reads), contexts: freeze(Array.from(contexts)), path });
    }

    // Exact lexical conversion, no arithmetic/rounding. Numeric cells retain their already-rounded IEEE-754 value.
    function decimal(cell, locale, path) {
        if (cell.kind === "blank") return freeze({ kind: "missing", reason: "blank" });
        if (cell.kind === "unavailable") return freeze({ kind: "missing", reason: "unavailable", detail: cell.reason });
        if (cell.kind === "error") return freeze({ kind: "missing", reason: "source-error", detail: cell.value });
        if (cell.kind === "text" && cell.value === "") return freeze({ kind: "missing", reason: "empty-text" });
        let text;
        if (cell.kind === "number") {
            if (Number.isInteger(cell.value) && !Number.isSafeInteger(cell.value)) fail("DECIMAL_PRECISION", path, "Unsafe integer source; supply an exact text value.");
            text = String(cell.value);
            if (/[eE]/.test(text)) {
                const [coefficient, exponent] = text.toLowerCase().split("e");
                const negative = coefficient[0] === "-", parts = coefficient.replace(/^-/, "").split(".");
                const digits = parts.join(""), point = parts[0].length + Number(exponent);
                if (Math.abs(point) > 38) fail("DECIMAL_PRECISION", path, "Decimal expansion exceeds its budget.");
                text = (negative ? "-" : "") + (point <= 0 ? "0." + "0".repeat(-point) + digits
                    : point >= digits.length ? digits + "0".repeat(point - digits.length) : digits.slice(0, point) + "." + digits.slice(point));
            }
        } else if (cell.kind === "text") {
            text = cell.value.trim();
            const pattern = locale === "ru-RU" ? /^[+-]?(?:\d+|\d{1,3}(?:[ \u00a0\u202f]\d{3})+)(?:,\d+)?$/
                : /^[+-]?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$/;
            if (!pattern.test(text)) fail("DECIMAL_INVALID", path, "Value does not match the declared numeric locale.");
            text = locale === "ru-RU" ? text.replace(/[ \u00a0\u202f]/g, "").replace(",", ".") : text.replace(/,/g, "");
        } else fail("DECIMAL_INVALID", path, "A decimal requires a text or number cell.");
        const negative = text[0] === "-", parts = text.replace(/^[+-]/, "").split(".");
        const integer = parts[0].replace(/^0+(?=\d)/, ""), fraction = (parts[1] || "").replace(/0+$/, "");
        if (integer.length + fraction.length > 38 || fraction.length > 18) fail("DECIMAL_PRECISION", path, "Decimal exceeds 38 digits or 18 fractional places; no rounding applied.");
        const value = integer + (fraction ? "." + fraction : "");
        return freeze({ kind: "decimal", value: (negative && value !== "0" ? "-" : "") + value });
    }

    function evaluate(expression, environment) {
        const evidence = new Map(), mappings = new Map();
        const merge = result => {
            result.evidence.forEach(item => evidence.set(item.rule + ":" + item.location.address, item));
            result.mappings.forEach(item => mappings.set(JSON.stringify(item), item));
        };
        function run(node) {
            if (node.op === "literal") return node.value;
            if (node.op === "variable") return environment[node.name];
            if (node.op === "context") { const value = environment.contexts.get(node.name); merge(value); return value.value; }
            if (node.op === "cell") {
                const item = environment.read(node.row === "row" ? environment.row : node.row,
                    node.column === "col" ? environment.col : node.column, node.path);
                evidence.set(node.path + ":" + item.location.address, freeze({ ...item, rule: node.path })); return item.cell;
            }
            const args = node.args.map(run);
            if (node.op === "raw") return args[0];
            if (node.op === "text") {
                const cell = args[0];
                if (cell.kind !== "text" && cell.kind !== "number") fail("CELL_TEXT_UNAVAILABLE", node.path, "Required header or key has no usable text/number value.");
                if (cell.kind === "number" && Number.isInteger(cell.value) && !Number.isSafeInteger(cell.value)) fail("DECIMAL_PRECISION", node.path, "Unsafe integer cannot be used as a key.");
                return String(cell.value);
            }
            if (node.op === "parse.year") {
                if (!/^[1-9]\d{3}$/.test(args[0].trim())) fail("PERIOD_INVALID", node.path, "Expected an explicit four-digit year.");
                return Number(args[0].trim());
            }
            if (node.op === "parse.monthRu") {
                const month = months.findIndex(names => names.split("|").includes(args[0].trim().toLowerCase().replace(/\.$/, ""))) + 1;
                if (!month) fail("PERIOD_INVALID", node.path, "Unknown Russian month name.");
                return month;
            }
            if (node.op === "period.month") return freeze({ kind: "month", year: args[0], month: args[1] });
            if (node.op === "period.year") return freeze({ kind: "year", year: args[0] });
            if (node.op === "decimal") return decimal(args[0], args[1], node.path);
            if (node.op === "lookup") {
                const value = environment.lookup(args[0], args[1], args[2], node.path);
                const mapping = freeze({ dictionary: args[0], scope: args[2], source: args[1], targetId: value.id, rule: node.path });
                mappings.set(JSON.stringify(mapping), mapping); return value;
            }
            fail("EXPRESSION_INVALID", node.path, "Unsupported instruction.");
        }
        let value;
        try { value = run(expression.program); }
        catch (error) {
            const source = Array.from(evidence.values()).pop();
            if (!error.location && source) error.location = source.location;
            throw error;
        }
        return freeze({ value, evidence: freeze(Array.from(evidence.values())), mappings: freeze(Array.from(mappings.values())) });
    }
    root.RNTabularExpression = freeze({ parse, compile, evaluate });
}(globalThis));

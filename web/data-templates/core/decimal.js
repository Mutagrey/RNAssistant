(function (root) {
    "use strict";
    const freeze = Object.freeze, fail = root.RNTabularSelectors.fail;
    // Exact lexical conversion. Already-rounded IEEE-754 sources cannot regain precision.
    function decimal(cell, locale, path) {
        if (["null", "absent"].includes(cell.kind)) return freeze({ kind: "missing", reason: cell.kind });
        if (cell.kind === "blank") return freeze({ kind: "missing", reason: "blank" });
        if (cell.kind === "unavailable") return freeze({ kind: "missing", reason: "unavailable", detail: cell.reason });
        if (cell.kind === "error") return freeze({ kind: "missing", reason: "source-error", detail: cell.value });
        if (cell.kind === "text" && cell.value === "") return freeze({ kind: "missing", reason: "empty-text" });
        let text;
        if (cell.kind === "number" || cell.kind === "numeric") {
            if (cell.kind === "number" && Number.isInteger(cell.value) && !Number.isSafeInteger(cell.value)) fail("DECIMAL_PRECISION", path, "Unsafe integer source; supply an exact numeric lexeme.");
            text = cell.kind === "numeric" ? cell.lexeme : String(cell.value);
            text = text.replace(/^\+/, "").replace(/^(-?)\./, (_, sign) => sign + "0.");
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

    function parts(value) {
        const [integer, fraction = ""] = value.split(".");
        return { coefficient: BigInt(integer + fraction), scale: fraction.length };
    }
    function from(coefficient, scale, path) {
        const negative = coefficient < 0n, digits = (negative ? -coefficient : coefficient).toString().padStart(scale + 1, "0");
        const lexeme = (negative ? "-" : "") + (scale ? digits.slice(0, -scale) + "." + digits.slice(-scale) : digits);
        return decimal({ kind: "numeric", lexeme }, "en-US", path);
    }
    function scale(value, factor, path) {
        if (!/^[+-]?\d+(?:\.\d+)?$/.test(factor) || factor.length > 40) fail("DECIMAL_INVALID", path, "Scale requires bounded exact decimal text.");
        const b = parts(decimal({ kind: "numeric", lexeme: factor }, "en-US", path).value);
        if (value.kind === "missing") return value;
        const a = parts(value.value);
        return from(a.coefficient * b.coefficient, a.scale + b.scale, path);
    }
    function round(value, places, mode, path) {
        if (value.kind === "missing") return value;
        const a = parts(value.value);
        if (a.scale <= places) return value;
        const divisor = 10n ** BigInt(a.scale - places), negative = a.coefficient < 0n;
        const absolute = negative ? -a.coefficient : a.coefficient;
        let quotient = absolute / divisor;
        const remainder = absolute % divisor;
        if (mode !== "truncate" && (remainder * 2n > divisor || remainder * 2n === divisor &&
            (mode === "half-away" || quotient % 2n === 1n))) quotient++;
        return from(negative ? -quotient : quotient, places, path);
    }
    root.RNTabularDecimal = freeze({ convert: decimal, scale, round });
}(globalThis));

(function (root) {
    "use strict";

    const selectors = root.RNTabularSelectors, snapshots = root.RNTabularSnapshot;
    const fail = selectors.fail;
    const revision = "tabular-m2-1";
    const maxInputCharacters = 1000000;

    // A bounded capture probe, not the template DSL or a publication operation.
    // Scheduling and cancellation belong to the execution adapter, not the reader.
    async function inspect(requestText, hooks) {
        const check = () => {
            if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Execution was cancelled.");
        };
        check();
        if (typeof requestText !== "string" || !requestText.length || requestText.length > maxInputCharacters)
            fail("INPUT_LIMIT", "", "Input must be at most 1000000 UTF-16 code units.");
        const input = root.RNTabularJson.parse(requestText);
        snapshots.object(input, ["version", "snapshot", "bounds", "selection", "maxCells"], "request");
        if (input.version !== 1) fail("VERSION_UNSUPPORTED", "version", "Unsupported probe request version.");
        const reader = snapshots.open(input.snapshot);
        const selected = selectors.select(input.selection, input.bounds, reader.sheet, input.maxCells);
        const cells = [], counts = { number: 0, text: 0, boolean: 0, blank: 0, error: 0, unavailable: 0 };
        for (let index = 0; index < selected.length; index++) {
            if (index % 128 === 0) {
                check();
                hooks.onProgress({ completedCells: index, totalCells: selected.length });
                await hooks.yieldControl();
                check();
            }
            const coordinate = selected[index], value = reader.read(coordinate);
            cells.push(Object.freeze({ ...coordinate, cell: value }));
            counts[value.kind]++;
        }
        check();
        return Object.freeze({ version: 1, engineRevision: revision, source: reader.source,
            coverage: Object.freeze({ sheet: reader.sheet, bounds: input.bounds, selection: input.selection,
                capturedCells: reader.count, selectedCells: selected.length, returnedCells: cells.length, complete: true }),
            counts: Object.freeze(counts), cells: Object.freeze(cells) });
    }

    async function extract(requestText, hooks) {
        if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Extraction was cancelled.");
        const input = root.RNTabularJson.parse(requestText);
        root.RNTabularTemplate.fields(input, ["version", "kind", "template", "snapshot", "dictionaries", "dataset"], "/");
        if (input.version !== 1 || input.kind !== "matrix-extraction") fail("VERSION_UNSUPPORTED", "/", "Expected matrix-extraction version 1.");
        const plan = root.RNTabularTemplate.compile(input.template);
        const dictionaries = root.RNTabularTemplate.openDictionaries(input.dictionaries), reader = snapshots.open(input.snapshot);
        const result = await root.RNTabularMatrix.execute(plan, reader, dictionaries, input.dataset, hooks);
        return Object.freeze({ ...result, engineRevision: revision });
    }

    root.RNTabularEngine = Object.freeze({ revision, protocol: 2, maxInputCharacters, inspect, extract });
}(globalThis));

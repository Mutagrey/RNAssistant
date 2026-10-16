(function (root) {
    "use strict";

    const selectors = root.RNTabularSelectors, snapshots = root.RNTabularSnapshot;
    const fail = selectors.fail;
    const revision = "tabular-core-2";
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
        const cells = [], counts = { number: 0, numeric: 0, text: 0, boolean: 0, blank: 0, error: 0, unavailable: 0 };
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
        const input = root.RNTabularJson.parse(requestText, "/snapshot/document");
        root.RNTabularTemplate.fields(input, ["version", "kind", "template", "snapshot", "dictionaries", "dataset"], "/");
        if (input.version !== 2 || input.kind !== "extraction") fail("VERSION_UNSUPPORTED", "/", "Expected extraction version 2.");
        const plan = root.RNTabularTemplate.compile(input.template);
        const dictionaries = root.RNTabularTemplate.openDictionaries(input.dictionaries), source = root.RNTabularSources.open(plan, input.snapshot);
        const result = await root.RNTabularTransform.execute(plan, source, dictionaries, input.dataset, hooks);
        return Object.freeze({ ...result, engineRevision: revision });
    }

    async function extractXlsx(requestText, hooks) {
        if (!root.RNTabularXlsxReader) fail("XLSX_CAPABILITY_UNAVAILABLE", "", "The XLSX reader must run in its dedicated Worker.");
        if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Extraction was cancelled.");
        const input = root.RNTabularJson.parse(requestText);
        root.RNTabularTemplate.fields(input, ["version", "kind", "template", "source", "dictionaries", "dataset"], "/");
        if (input.version !== 2 || input.kind !== "xlsx-extraction") fail("VERSION_UNSUPPORTED", "/", "Expected xlsx-extraction version 2.");
        root.RNTabularTemplate.fields(input.source, ["formulaPolicy"], "/source");
        const plan = root.RNTabularTemplate.compile(input.template), dictionaries = root.RNTabularTemplate.openDictionaries(input.dictionaries);
        if (plan.source.kind !== "matrix") fail("SOURCE_MODEL_MISMATCH", "/template/source", "XLSX requires a matrix source plan.");
        const reader = await root.RNTabularXlsxReader.open(hooks.byteSource,
            { sheet: plan.source.sheet, bounds: plan.source.bounds, formulaPolicy: input.source.formulaPolicy }, hooks);
        const source = root.RNTabularSources.matrix(plan, reader);
        const result = await root.RNTabularTransform.execute(plan, source, dictionaries, input.dataset, hooks);
        return Object.freeze({ ...result, engineRevision: revision, sourceCapabilities: reader.capabilities, sourceDiagnostics: reader.diagnostics });
    }

    root.RNTabularEngine = Object.freeze({ revision, protocol: 5, maxInputCharacters, maxSourceBytes: 96 * 1048576, inspect, extract, extractXlsx });
}(globalThis));

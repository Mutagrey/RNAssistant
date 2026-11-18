(function (root) {
  'use strict';
  var maximumSourceCharacters = 262144;
  var maximumMessageCharacters = 2048;
  var maximumResultCharacters = 8192; // At most 24 KiB UTF-8, below the native 32 KiB cap.
  var parserVersion = '5.0.0';

  function notChecked(request, reason) {
    return { requestId: request.requestId, sourceSha256: request.sourceSha256,
      syntax: 'not_checked', parserVersion: parserVersion, diagnostics: [], reason: reason };
  }

  function finish(request, result) {
    return JSON.stringify(result).length <= maximumResultCharacters
      ? result : notChecked(request, 'parser_output_limit');
  }

  function exactOffset(value, length) {
    return Number.isInteger(value) && value >= 0 && value <= length ? value : null;
  }

  function diagnostic(parser, task, source) {
    var error = task.error;
    var inner = error && error.innerError;
    var start, end, token, code;
    if (parser.TaskUtils.isLexStageError(task) && parser.Lexer.LexError.isLexError(error)) {
      start = exactOffset(inner && inner.graphemePosition && inner.graphemePosition.codeUnit, source.length);
      if (start === null) return null;
      end = inner instanceof parser.Lexer.LexError.UnterminatedMultilineTokenError
        ? source.length : Math.min(source.length, start + 1);
      code = 'm_lex_error';
    } else if (parser.TaskUtils.isParseStageError(task) && parser.Parser.ParseError.isParseError(error)) {
      token = parser.Parser.ParseError.tokenFrom(inner);
      if (token) {
        start = exactOffset(token.positionStart && token.positionStart.codeUnit, source.length);
        end = exactOffset(token.positionEnd && token.positionEnd.codeUnit, source.length);
        if (start === null || end === null || end < start) return null;
      } else {
        // Upstream tokenFrom returns undefined for an expected token at EOF.
        start = source.length;
        end = source.length;
      }
      code = 'm_parse_error';
    } else return null;
    if (!error || typeof error.message !== 'string' || !error.message.length) return null;
    return { code: code, message: error.message.slice(0, maximumMessageCharacters),
      startOffset: start, endOffset: end };
  }

  async function analyze(request) {
    if (!request || Array.isArray(request) || typeof request !== 'object' ||
      typeof request.requestId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(request.requestId) ||
      typeof request.sourceSha256 !== 'string' || !/^[a-fA-F0-9]{64}$/.test(request.sourceSha256) ||
      typeof request.source !== 'string' ||
      Object.keys(request).some(function (key) { return key !== 'requestId' && key !== 'sourceSha256' && key !== 'source'; }))
      throw new Error('Invalid Power Query analysis request.');
    if (request.source.length > maximumSourceCharacters) return notChecked(request, 'parser_source_limit');
    var parser = root.RNPowerQueryParser;
    if (!parser || parser.parserVersion !== parserVersion) return notChecked(request, 'parser_unavailable');
    try {
      var task = await parser.TaskUtils.tryLexParse(Object.assign({}, parser.DefaultSettings, {
        parseBehavior: parser.Parser.ParseBehavior.ParseExpression
      }), request.source);
      if (parser.TaskUtils.isParseStageOk(task)) return finish(request, {
        requestId: request.requestId, sourceSha256: request.sourceSha256,
        syntax: 'passed', parserVersion: parserVersion, diagnostics: [], reason: null
      });
      var item = diagnostic(parser, task, request.source);
      // CommonError includes cancellation/resource/internal failure. It is never
      // evidence that the supplied M has invalid syntax.
      if (!item) return notChecked(request, 'parser_analysis_unavailable');
      return finish(request, { requestId: request.requestId, sourceSha256: request.sourceSha256,
        syntax: 'failed', parserVersion: parserVersion, diagnostics: [item], reason: null });
    } catch (error) {
      return notChecked(request, 'parser_internal_error');
    }
  }

  root.RNPowerQueryAnalysis = Object.freeze({ analyze: analyze });
}(globalThis));

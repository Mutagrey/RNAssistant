#!/usr/bin/env python3
"""Compare manual and structured patch CLI builds with the same model and tasks.

Uses the existing HTTP evidence runner without request/response transformations.
This is workspace/model evidence, not Windows/VBE or native tool-call qualification.
"""
import argparse
import json
from pathlib import Path
from conversation_v8_model_compare import run_case, model_digest, binary_hashes


def tasks():
    repeated = "# First\nvalue = 1\n\n# Second\nvalue = 1\n\n# Last\nvalue = 1\n"
    quoted = 'Sub Run()\n    Debug.Print "C:\\temp\\report.txt"\n    Debug.Print "keep"\nEnd Sub\n'
    long_source = ''.join('unchanged_%04d = 0\n' % i for i in range(1800)) + 'TARGET = 1\n'
    def task(initial, expected, goal):
        return dict(initial=initial, expected=expected, min_reads=1, min_writes=1,
                    iterations=10, tool_steps=10, prompt=goal +
                    ' Read the current files first. Make edits using only core.patch; use its advertised schema. '
                    'Do not use core.write, core.replace or executable tools. Preserve all other text and line endings. '
                    'Read the edited file(s) back and report the verified result.')
    return {
        'repeated_empty': task({'a.txt': repeated}, {'a.txt': repeated.replace('# Second\nvalue = 1', '# Second\nvalue = 2')},
                               'In a.txt change value = 1 to value = 2 only in the Second section.'),
        'quotes_insert': task({'module.bas': quoted}, {'module.bas': quoted.replace('report.txt', 'result.txt').replace('End Sub', '    Debug.Print "ready"\nEnd Sub')},
                             'In module.bas change report.txt to result.txt in the printed path and insert a line with four spaces followed by Debug.Print "ready" immediately before End Sub.'),
        'multi_target': task({'a.txt': 'alpha = 1\n', 'b.txt': 'beta = 1\n'}, {'a.txt': 'alpha = 2\n', 'b.txt': 'beta = 3\n'},
                            'In a.txt change alpha from 1 to 2; in b.txt change beta from 1 to 3. Use one core.patch call for both targets.'),
        'long_source': task({'a.txt': long_source}, {'a.txt': long_source.replace('TARGET = 1', 'TARGET = 2')},
                           'In a.txt change only the final line TARGET = 1 to TARGET = 2. Read a bounded excerpt near the end, not the entire file.'),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manual', type=Path, required=True)
    parser.add_argument('--structured', type=Path, required=True)
    parser.add_argument('--manual-revision', required=True)
    parser.add_argument('--model', required=True)
    parser.add_argument('--origin', default='http://127.0.0.1:11434')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--tasks', default=','.join(tasks()))
    parser.add_argument('--repeats', type=int, default=1)
    args = parser.parse_args()
    digest = model_digest(args.origin, args.model)
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = dict(model=args.model, digest=digest, origin=args.origin, manual_revision=args.manual_revision,
                    builds={name: binary_hashes(path) for name, path in [('manual', args.manual), ('structured', args.structured)]},
                    limits=dict(context=32768, iterations=10, tool_steps=10),
                    limitation='Workspace structured-response transport; no native tool-call, VBE, stale concurrency or cross-model qualification.')
    (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2))
    summaries = []
    for repeat in range(args.repeats):
        for index, name in enumerate(args.tasks.split(',')):
            pair = [('manual', args.manual), ('structured', args.structured)]
            if (index + repeat) % 2: pair.reverse()
            for variant, binary in pair:
                task = tasks()[name]
                folder = args.output / ('%02d-%s-%s' % (repeat, name, variant))
                summary = run_case(variant, binary.resolve(), name, task, folder, args.origin, args.model)
                # The reused runner treats initial files as immutable inputs. These tasks intentionally edit them.
                correct = all(summary['external_exact_files'].values())
                summary['external_artifact_success'] = correct
                summary['completed_without_external_artifact'] = summary['lifecycle'] == 'Completed' and not correct
                summary.pop('satisfied_claim_without_external_artifact', None)
                summary['unexpected_final_mutation'] = any(not summary['external_inputs_unchanged'][p] and
                    not summary['external_exact_files'][p] for p in task['initial'])
                summary['patch_calls'] = (summary.get('tool_counts') or {}).get('core.patch', 0)
                summary['extra_patch_calls'] = max(0, summary['patch_calls'] - 1)
                summary['first_patch_final_correct'] = correct and summary['patch_calls'] == 1
                (folder / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
                summaries.append(summary)
                (args.output / 'summary.json').write_text(json.dumps(summaries, ensure_ascii=False, indent=2) + '\n')
                print(variant, name, 'correct=' + str(correct), 'patches=' + str(summary['patch_calls']), 'seconds=' + str(summary['seconds']), flush=True)


if __name__ == '__main__':
    main()

# Conversation v8 kernel smoke

Run `sh tests/RNAssistant.V8KernelSmoke/run.sh` from any directory. The script
source-links the host-neutral production Core/Office/Workspace files through the
existing net8 Harness project, excludes its unmigrated v7 test sources, and runs
focused v8 kernel/evidence cases. No Office/VSTO validation is performed.

This temporary phase-2 runner checks direct finish, current-run terminal evidence,
forged/pending/naked references, failed/unknown assessments, reflection limits
after checkpoint restore, and reflection → execute → terminal result → finish after
restore. The full Harness remains an integration gate for phases 3–4.

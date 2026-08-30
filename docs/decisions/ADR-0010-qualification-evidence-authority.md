# ADR-0010: Qualification evidence belongs to runtime and domain verifiers

Status: accepted for Milestone WQ-A; implementation pending.

## Context

Windows qualification сейчас описана ручным runbook и отдельным Excel PowerShell
probe. Пользователь должен выполнять сложную последовательность, вручную сопоставлять
JSON и переносить evidence. Простые prompt suggestions нового чата подходят для
начала разговора, но не владеют setup, document safety, multi-step checkpoints,
assertions, restart или итоговым report.

Один большой in-app «self test» также опасен: модель может объявить собственный ответ
успешным, UI — вывести pass из текста, а тестовый executor — обойти production
confirmation, HostRuntime или persistence. Отдельный mutable result store создаст
второй источник истины относительно chat stream и domain journals.

## Decision

- Ввести Qualification Center как application-level orchestrator над versioned
  declarative packs.
- Agent tasks проходят только через обычный conversation/kernel/tool/domain path.
  Runner не имеет своего executor и не меняет production policy.
- Pass/fail рассчитывают allowlisted deterministic verifiers по typed outcomes,
  read-back, host observations и source event evidence. Model narrative никогда не
  является assertion authority.
- Manual visual observation допустимо только с явной `manual` evidence strength;
  `blocked`, missing или `unknown` не повышаются до pass.
- Qualification runs сохраняют closed typed operations в существующем document chat
  stream и CAS. Dashboard/report являются replayable projection/export, без второго
  durable index/store.
- Built-in pack manifest содержит только versioned data и allowlisted step/action/
  assertion IDs. Arbitrary scripts, command lines, URLs, CLR/JS types и raw tool IDs
  запрещены.
- Host probes/fault hooks — узкие host-owned capabilities. WQ0 использует same-build
  local helper с одним typed contract, explicit target и local one-time channel;
  generic process execution не публикуется.
- Host-neutral harness остаётся build/CI contour. Его exact BuildEvidenceManifest
  показывается в приложении, но VSTO не запускает compiler, shell или test runner.
- Новая карточка пустого чата открывает Qualification Center и не превращает pack в
  обычный prompt.

## Consequences

Пользователь получает один управляемый UI для реальных Office сценариев и может
расширять pack catalog без копирования orchestration. Один source of truth и causal
correlation сохраняются. Добавление runner/contracts/UI/WQ0 требует отдельных
подэтапов и Windows проверки; этот ADR сам не квалифицирует COM identity и не
разрешает production 5B2 switch.

Полный контракт: [qualification.md](../qualification.md).

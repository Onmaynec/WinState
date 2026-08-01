# 🗺️ План реализации WinState

## ✅ Этап 1 — архитектура (`0.1.0-alpha.1`)

- доменная модель и provider contracts;
- модель транзакций;
- dependency graph;
- solution, тесты и CI.

## ✅ Этап 2 — каркас приложения (`0.2.0-alpha.1`)

- проекты `App`, `Infrastructure`, `Storage`;
- dependency injection и console logging;
- `winstate.json`, environment overrides и portable paths;
- SQLite и идемпотентные миграции;
- `doctor`, `config`, `storage`.

## ✅ Этап 3 — Terminal UI и Profile Engine (`0.3.0-alpha.1`)

- самостоятельный Control Center вместо обычного списка команд;
- стрелочное управление, панели, логотип и анимации;
- отдельный проект `WinState.Terminal`;
- полноценная YAML-модель через YamlDotNet;
- includes, extends и защита от циклов;
- профильные, environment и CLI variables;
- нормализация environment и PATH;
- Profile Center в интерактивной панели.

## ✅ Этап 4 — Environment Provider (`0.4.0-alpha.1`)

Первый полный Windows vertical slice:

```text
Discover → Diff → Plan → Confirm → Checkpoint → Apply → Verify → Rollback
```

Реализовано:

- User/Machine environment discovery;
- variable create/modify;
- PATH add/remove/reorder;
- deterministic actions и dependency ordering;
- User/Machine risk policy;
- отдельное подтверждение Machine scope;
- checkpoint каждого действия;
- post-apply verification;
- автоматический и ручной rollback;
- SQLite transaction/action/backup history;
- Environment Center и CLI;
- unit-тесты и настоящий Windows CI vertical slice.

## ✅ Этап 5 — Cyber Control Center (`0.5.0-alpha.1`)

Визуальный и UX-слой, вдохновлённый NexRoute:

- новый `CyberTerminalShell`;
- зелёная high-contrast cyber-палитра;
- boot и shutdown traces;
- номерные operation channels;
- Control Node telemetry;
- Profile Vault и auto-discovery repository samples;
- Environment Ops и Checkpoint Vault;
- animated pipeline `handshake → operation → seal result`;
- live event feed;
- action-by-action transaction stream;
- Deep Scan и Data Core;
- cyber demo mode для Ubuntu/Windows CI;
- отдельное SVG-превью и документация.

Safety boundaries не изменены: frontend вызывает только `WinStateApplication` и не содержит системной реализации.

## ⏭️ Этап 6 — общий Apply Engine (`0.6.0-alpha.1`)

- единая cross-provider transaction model;
- централизованная risk policy;
- confirmation groups;
- dependency-aware parallel/sequential execution;
- cancellation и on-error policies;
- resume после перезапуска процесса;
- reboot-pending state;
- единый rollback нескольких providers;
- live execution graph в Cyber Control Center.

## Этап 7 — Packages и Windows Features

- WinGet provider;
- optional features;
- WSL prerequisites;
- package ownership и remove policy;
- reboot planning.

## Этап 8 — Services, Registry, Git, PowerShell и Files

- allowlisted registry provider;
- services/startup/tasks;
- Git configuration;
- PowerShell modules;
- managed files and directories.

## Этап 9 — Capture, drift и отчёты

- capture/export;
- profile snapshots;
- drift scan;
- transaction history UI;
- Markdown/JSON reports.

## Этап 10 — release pipeline

- localization;
- portable/self-contained Windows builds;
- signing and checksums;
- migration compatibility;
- release notes и стабильный `1.0` pipeline.

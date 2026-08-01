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
- Profile Center в интерактивной панели;
- CI smoke test неинтерактивного dashboard.

## ⏭️ Этап 4 — Environment Provider

Первый полный vertical slice:

```text
Discover → Diff → Plan → Confirm → Checkpoint → Apply → Verify → Rollback
```

Цель — безопасное управление user/machine environment variables и PATH с сохранением предыдущего состояния.

## Этап 5 — Apply Engine

- risk policy;
- confirmation;
- transactions и checkpoints;
- dependency execution;
- cancellation и on-error policy.

## Этапы 6–9

Packages, Features, Services, Registry, Git, PowerShell и Files providers; capture/export; история и отчёты; localization, portable release и полноценный release pipeline.

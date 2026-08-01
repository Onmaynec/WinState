# 🗺️ План реализации WinState

## ✅ Этап 1 — архитектура (`0.1.0-alpha.1`)

- доменная модель;
- provider contracts;
- модель транзакций;
- bootstrap YAML reader;
- dependency graph;
- solution, тесты и CI.

## ✅ Этап 2 — каркас приложения (`0.2.0-alpha.1`)

- отдельные проекты `App`, `Infrastructure`, `Storage`;
- dependency injection и console logging;
- `winstate.json`, переменные окружения и portable paths;
- SQLite и идемпотентные миграции;
- команды `doctor`, `config`, `storage`;
- unit-тесты конфигурации и схемы;
- Linux/Windows smoke tests.

## ⏭️ Этап 3 — Profile Engine

- полноценная YAML-модель;
- includes и защита от циклов;
- наследование;
- переменные и приоритеты;
- нормализация путей;
- JSON Schema validation.

## Этап 4 — Environment Provider

Первый полный vertical slice: discovery → diff → plan → apply → verify → rollback.

## Этапы 5–9

Apply Engine, остальные провайдеры, capture/export, TUI/отчёты и полноценный release pipeline.

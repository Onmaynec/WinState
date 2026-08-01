# Changelog

Все заметные изменения WinState документируются в этом файле.

## [0.2.0-alpha.1] — 2026-08-01

### Добавлено

- проекты `WinState.App`, `WinState.Infrastructure`, `WinState.Storage`;
- dependency injection и структурированное console logging;
- загрузка `winstate.json` и переменных окружения `WINSTATE_*`;
- вычисление user-data и portable путей;
- SQLite-хранилище с транзакционными идемпотентными миграциями;
- начальные таблицы профилей, ownership, baseline, транзакций и drift;
- CLI-команды `doctor`, `config show/path`, `storage migrate/status`;
- unit-тесты конфигурации и SQLite-схемы;
- smoke tests `doctor` и `storage` на Ubuntu и Windows;
- документация конфигурации и локального хранилища;
- новое SVG-превью команды `doctor`.

### Ограничения

- Profile Engine пока остаётся bootstrap-реализацией;
- Environment Provider и изменение Windows будут реализованы после полного Profile Engine;
- SQLite пока хранит только схему и migration history.

## [0.1.0-alpha.1] — 2026-08-01

### Добавлено

- архитектурное ядро и доменные контракты;
- базовый CLI, YAML reader и dependency graph;
- unit-тесты и GitHub Actions;
- русская документация и SVG-превью.

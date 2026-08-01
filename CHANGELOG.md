# Changelog

Все заметные изменения WinState документируются в этом файле.

## [0.3.0-alpha.1] — 2026-08-01

### Добавлено

- интерактивный WinState Control Center, запускаемый без аргументов;
- стрелочное управление через `↑`, `↓` и `Enter`;
- большой символьный логотип, отдельные панели и статусная строка;
- spinner-анимации загрузки, диагностики, Profile Engine и SQLite;
- экраны System Overview, Profile Center, Doctor, Storage, Configuration и Roadmap;
- отдельный проект `WinState.Terminal` без бизнес-логики;
- `winstate ui` и неинтерактивный `winstate ui --demo` для CI;
- полноценный YAML Profile Engine на YamlDotNet;
- `includes`, `extends` и обнаружение циклов;
- переменные `{{name}}`, `${name}`, `WINSTATE_VAR_*` и `--var name=value`;
- объединение environment-секций и нормализация PATH;
- sample-профили inheritance/variables;
- unit-тесты Profile Engine и новые smoke tests интерфейса.

### Изменено

- запуск `winstate` без параметров теперь открывает панель вместо обычной справки;
- CLI оставлен как совместимый слой для автоматизации;
- документация и README переработаны под формат полноценной terminal utility.

### Ограничения

- Control Center пока не применяет системные настройки;
- следующий этап — Environment Provider vertical slice;
- conditions, secrets adapter и expression language ещё не реализованы.

## [0.2.0-alpha.1] — 2026-08-01

- application skeleton с DI и logging;
- конфигурация, portable paths, SQLite и миграции;
- команды `doctor`, `config` и `storage`;
- Linux/Windows CI.

## [0.1.0-alpha.1] — 2026-08-01

- архитектурное ядро и доменные контракты;
- bootstrap YAML reader и dependency graph;
- тесты, документация и release scripts.

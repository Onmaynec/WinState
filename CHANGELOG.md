# Changelog

Все заметные изменения WinState документируются в этом файле.

## [0.5.0-alpha.1] — 2026-08-01

### Добавлено

- новый `CyberTerminalShell` как основной интерактивный frontend;
- визуальный язык, вдохновлённый NexRoute: плотный control-node layout, зелёная cyber-палитра и номерные каналы;
- boot trace с загрузкой Kernel, Profile Engine, Data Core, Provider и Safeguards;
- shutdown trace при завершении сессии;
- animated progress pipeline `handshake → operation → seal result`;
- live event feed на главном экране;
- live transaction action stream после apply и rollback;
- Control Node telemetry: host, OS, PID, architecture, uptime, provider counters, checkpoint и SQLite;
- разделы Profile Vault, Environment Ops, Checkpoint Vault, Deep Scan, Data Core, Node Config и System Map;
- автоматическая индексация YAML-профилей из пользовательского vault и repository `samples`;
- отдельное SVG-превью Cyber Control Center;
- русская документация нового terminal frontend;
- неинтерактивный cyber demo mode для CI.

### Изменено

- запуск без аргументов и команда `ui` теперь открывают Cyber Control Center;
- версия приложения и CLI обновлена до `0.5.0-alpha.1`;
- интерфейс Environment workflow показывает risk plan и реальные action results в едином визуальном стиле;
- roadmap сдвинут: общий multi-provider Apply Engine запланирован на `0.6.0-alpha.1`.

### Безопасность

- UI по-прежнему не содержит системной бизнес-логики;
- все apply/rollback операции проходят через `WinStateApplication` и `EnvironmentWorkflow`;
- новый frontend не изменяет confirmation policy;
- Machine scope сохраняет отдельное elevated-подтверждение;
- success отображается только после фактической verification;
- demo mode не выполняет системные изменения.

## [0.4.0-alpha.1] — 2026-08-01

### Добавлено

- первый реальный системный provider `WinState.Providers.Environment`;
- discovery пользовательских и машинных переменных окружения;
- discovery, добавление, удаление и перестановка PATH entries;
- детерминированный environment diff и execution plan;
- уровни риска: User scope — Low, Machine scope — Medium;
- отдельное подтверждение Machine scope и требование elevated terminal;
- checkpoint каждого действия до изменения системы;
- post-apply verification для variables и PATH;
- автоматический rollback при ошибке применения или проверки;
- ручной rollback по сохранённому `manifest.json`;
- SQLite history транзакций, action results и backup references;
- CLI-команды `environment status/plan/apply/checkpoints/rollback`;
- интерактивный Environment Center в Control Center;
- unit-тесты provider vertical slice на in-memory store;
- Windows CI-сценарий `plan → apply → verify → rollback`;
- безопасный User-scope sample и русская документация.

### Безопасность

- `apply` не запускается без явного `--yes`;
- Machine scope дополнительно требует `--allow-machine`;
- checkpoint создаётся до первого изменения;
- при ошибке автоматический rollback включён по умолчанию;
- provider не удаляет переменные, которые не описаны как управляемые ресурсы;
- CI проверяет полное восстановление тестовой переменной и PATH.

### Ограничения

- Environment Provider работает с User/Machine environment только на Windows;
- Machine scope зависит от прав текущего процесса;
- изменения становятся видны новым процессам после системного broadcast;
- общий cross-provider Apply Engine, resume и reboot orchestration будут добавлены следующим этапом.

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

## [0.2.0-alpha.1] — 2026-08-01

- application skeleton с DI и logging;
- конфигурация, portable paths, SQLite и миграции;
- команды `doctor`, `config` и `storage`;
- Linux/Windows CI.

## [0.1.0-alpha.1] — 2026-08-01

- архитектурное ядро и доменные контракты;
- bootstrap YAML reader и dependency graph;
- тесты, документация и release scripts.

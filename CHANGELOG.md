# Changelog

Все заметные изменения WinState документируются в этом файле.

## [0.9.0] — 2026-08-02

### Добавлено

- стабильная команда `capture` для экспорта текущего состояния Windows в YAML;
- JSON-манифест снимка с SHA-256, временем создания, версией приложения и количеством ресурсов;
- экспорт User/Machine environment variables и PATH;
- экспорт установленных пакетов WinGet с точными версиями;
- экспорт включённых Windows Optional Features;
- команда `drift` для read-only сравнения системы с профилем;
- JSON-отчёт drift с actions, risks, admin requirements, rollback capability и diagnostics;
- стабильные exit codes `0`, `3`, `6` и `10` для CI/автоматизации;
- unit-тест безопасного снимка через fake WinGet/DISM clients;
- Windows CI vertical slice `capture → validate → drift`;
- русское руководство `docs/CAPTURE_DRIFT.md`;
- русские release notes `docs/releases/v0.9.0.md`.

### Изменено

- версия приложения и CLI обновлена до `0.9.0` без prerelease-суффикса;
- README и CLI help переработаны на русском языке;
- release workflow использует русское описание из репозитория;
- обычные теги без `-` публикуются как стабильные и помечаются `Latest`;
- release pipeline создаёт общий файл `SHA256SUMS`;
- package smoke test использует версию `0.9.0`.

### Безопасность

- Capture пропускает переменные с признаками паролей, токенов, ключей, credentials и строк подключения;
- снимки и отчёты записываются атомарно через временный файл;
- Drift выполняет только discovery и plan, не применяя изменения;
- captured package scope требует ручной проверки перед apply;
- массовое удаление неизвестных ресурсов по-прежнему запрещено.

### Ограничения

- WinGet inventory не всегда сообщает scope установки;
- Capture экспортирует только включённые Optional Features;
- Registry, Services, Startup и Scheduled Tasks не сканируются глобально и остаются exact-resource providers;
- Authenticode signing и stable `1.0` pipeline остаются следующим этапом.

## [0.8.0-alpha.1] — 2026-08-01

### Добавлено

- production provider `windows.system`;
- allowlisted Registry values в `HKCU\Software` и `HKLM\SOFTWARE`;
- Registry types `string`, `expandString`, `dword`, `qword`, `multiString` и `binary`;
- управление состоянием и start mode существующих Windows Services;
- Startup entries через стандартный Registry Run key;
- Scheduled Tasks со schedules `logon`, `startup` и `daily`;
- XML backup/restore существующих Scheduled Tasks;
- отдельный совместимый system-control YAML loader с includes, extends и variables;
- targeted discovery и deterministic action identities;
- per-resource checkpoint payloads для Registry, Services, Startup и Tasks;
- post-operation verification и rollback через Unified Apply Engine;
- dependency links через общий action graph;
- cross-platform client factory с безопасным unsupported adapter;
- unit-тесты через `IWindowsSystemClient`;
- безопасный sample `samples/system-control/safe-workstation.yaml`;
- руководство `docs/SYSTEM_CONTROL.md`;
- SCM и Task Scheduler prerequisite scan в Windows CI.

### Изменено

- версия приложения и CLI обновлена до `0.8.0-alpha.1`;
- Unified Apply Engine регистрирует четвёртый production adapter `windows.system`;
- Profile validation проверяет system-control секции;
- Forge demo публикует сигнатуру нового provider;
- README и roadmap обновлены под Windows System Control Plane;
- release package smoke test использует версию `0.8.0-alpha.1`.

### Безопасность

- Registry paths вне Software allowlist блокируются до построения плана;
- HKLM, Services, machine Startup и elevated/startup Tasks требуют administrator policy;
- удаление Registry/Startup/Task и остановка/отключение Service имеют повышенный risk;
- системные утилиты запускаются через `ProcessStartInfo.ArgumentList`;
- checkpoints создаются до первой мутации;
- success фиксируется только после повторного чтения состояния;
- скрытое elevation и автоматическая перезагрузка отсутствуют;
- CI использует fake client и не изменяет реальные системные ресурсы.

### Ограничения

- WinState не создаёт и не удаляет service definitions;
- Registry намеренно ограничен Software allowlist;
- dependency references для System Control пока задаются точными action IDs;
- persistent ownership store перенесён в этап Capture/Drift;
- Authenticode signing запланирован для stable pipeline.

## [0.7.0-alpha.1] — 2026-08-01

### Добавлено

- новый production provider `packages.winget`;
- discovery установленных WinGet packages и доступных updates;
- exact-ID install, upgrade и uninstall без shell-конкатенации аргументов;
- package verification повторным чтением WinGet inventory;
- checkpoint и rollback новых установок через удаление package, установленного транзакцией;
- честные irreversible boundaries для package upgrade и uninstall;
- новый production provider `windows.features`;
- DISM inventory Optional Features через `/Online /Get-Features /English`;
- enable/disable через `/NoRestart`;
- feature checkpoint, verification и rollback исходного состояния;
- обработка DISM exit code `3010` как reboot-pending;
- YAML-секции `packages` и `features`;
- inheritance, variables, overlay и дедупликация новых секций;
- JSON Schema для package и feature resources;
- три production adapters в одном Unified Apply Engine;
- `CyberForgeShell` и Package & Feature Forge;
- provider telemetry, inventory counters, risk plan и execution trace;
- unit-тесты через `IWingetClient` и `IWindowsFeatureClient`;
- sample developer workstation;
- Windows CI prerequisite scan для winget и DISM;
- русское руководство `docs/PACKAGES_FEATURES.md`;
- SVG-превью Package & Feature Forge.

### Изменено

- версия приложения и CLI обновлена до `0.7.0-alpha.1`;
- запуск без аргументов открывает Forge Control Fabric;
- Nexus Control Fabric сохранён как отдельный канал;
- Unified Apply Workflow собирает Environment, WinGet и Optional Features actions;
- README, архитектура, безопасность и roadmap обновлены под три production providers;
- release package smoke test использует версию `0.7.0-alpha.1`.

### Безопасность

- WinGet запускается через `ProcessStartInfo.ArgumentList`;
- package IDs передаются с `--exact` и disabled interactivity;
- upgrade/uninstall не получают ложный rollback capability;
- irreversible package actions требуют отдельного policy gate;
- Optional Features всегда требуют administrator policy;
- DISM всегда получает `/NoRestart`;
- все обратимые checkpoints создаются до первого apply;
- success фиксируется только после provider verification;
- массовое удаление unmanaged packages не выполняется;
- Windows CI не устанавливает packages и не включает features.

### Ограничения

- WinGet inventory parser зависит от табличного формата современного App Installer;
- downgrade после package upgrade не гарантируется;
- package ownership и `removeUnmanagedPackages` будут расширены позже;
- reboot-resume после входа в Windows пока не создаёт startup task.

## [0.6.0-alpha.1] — 2026-08-01

### Добавлено

- новый проект `WinState.Apply` с общим multi-provider transaction engine;
- единый dependency-aware execution graph;
- deterministic topological sort, missing-dependency и cycle validation;
- risk groups для Low/Medium/High/Critical actions;
- отдельные policy gates для admin, Critical и irreversible actions;
- checkpoint barrier: backup всех providers до первой мутации;
- atomic persisted `transaction.json`;
- progress persistence после каждого verified action;
- resume незавершённых транзакций;
- `SucceededRebootPending` без скрытой перезагрузки;
- automatic и manual cross-provider rollback;
- `EnvironmentApplyExecutor` как первый production adapter;
- новый проект `WinState.Update`;
- GitHub Releases discovery и stable/prerelease channels;
- Semantic Version comparison;
- автоматическая проверка обновлений при запуске;
- режимы `off`, `check`, `prompt`, `install`;
- загрузка release ZIP и `.sha256`;
- SHA-256 verification;
- защита ZIP extraction от path traversal;
- обязательный `winstate.release.json` marker;
- отдельный updater process после завершения WinState;
- self-contained release packages `win-x64` и `win-arm64`;
- Nexus Control Fabric, Transaction Matrix и Update Uplink;
- unit-тесты execution graph, cross-provider rollback, reboot pending и updater semver;
- package smoke test на Windows CI;
- русские руководства Apply Engine и автообновления;
- SVG-превью Nexus Control Fabric.

### Изменено

- версия App и CLI обновлена до `0.6.0-alpha.1`;
- запуск без аргументов открывает `CyberNexusShell`;
- прежний Cyber Control Center доступен как первый Nexus channel;
- release pipeline сначала выполняет тесты, затем публикует два Windows runtime package;
- package script создаёт self-contained ZIP, marker и SHA-256;
- roadmap сдвинут на Packages и Windows Features в `0.7`.

### Безопасность

- все reversible checkpoints создаются до первого apply;
- success фиксируется только после provider verification;
- manifest обновляется после каждого verified action;
- source checkout никогда не перезаписывается updater-ом;
- self-install требует `winstate.exe` и release marker;
- скачанный ZIP сверяется с опубликованным SHA-256;
- unsafe ZIP paths блокируются;
- updater не заменяет пользовательские `.winstate`, `profiles` и `logs`;
- automatic rollback остаётся включённым по умолчанию.

### Ограничения

- общий engine поддерживает несколько providers, но production adapter пока один — Environment;
- automatic resume после входа в Windows ещё не создаёт startup task;
- Authenticode signing запланирован для позднего release-этапа;
- updater работает только в Windows release package.

## [0.5.0-alpha.1] — 2026-08-01

### Добавлено

- `CyberTerminalShell` и NexRoute-inspired visual language;
- номерные operation channels, boot/shutdown trace;
- animated pipeline `handshake → operation → seal result`;
- live event feed и transaction action stream;
- Profile Vault, Environment Ops, Checkpoint Vault, Deep Scan, Data Core, Node Config и System Map;
- автоматическая индексация repository sample-профилей;
- cyber demo mode для CI.

### Безопасность

- UI остаётся presentation-only;
- apply/rollback проходят через application workflow;
- Machine scope требует отдельного elevated-подтверждения;
- demo mode не выполняет системные изменения.

## [0.4.0-alpha.1] — 2026-08-01

- первый реальный Environment Provider;
- User/Machine variables и PATH;
- discovery, diff и risk-aware plan;
- checkpoint, apply, verification и rollback;
- SQLite transaction history;
- Environment CLI/UI и настоящий Windows CI vertical slice.

## [0.3.0-alpha.1] — 2026-08-01

- интерактивный Control Center;
- стрелочный управление, панели, логотип и анимации;
- полный YAML Profile Engine;
- includes, extends, variables и normalization;
- Profile Center и UI smoke tests.

## [0.2.0-alpha.1] — 2026-08-01

- application skeleton с DI и logging;
- конфигурация, portable paths, SQLite и миграции;
- команды `doctor`, `config` и `storage`;
- Linux/Windows CI.

## [0.1.0-alpha.1] — 2026-08-01

- архитектурное ядро и доменные контракты;
- bootstrap YAML reader и dependency graph;
- тесты, документация и release scripts.

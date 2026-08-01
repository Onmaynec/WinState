# Changelog

Все заметные изменения WinState документируются в этом файле.

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
- стрелочное управление, панели, логотип и анимации;
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

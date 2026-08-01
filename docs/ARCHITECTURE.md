# 🏗️ Архитектура WinState

> Текущий статус: **`0.5.0-alpha.1` — Cyber Control Center поверх безопасного Windows provider vertical slice**.

WinState строится вокруг безопасного конвейера:

```text
Profile → Validation → Discovery → Diff → Plan → Confirmation
              │                                  │
              └──────── Diagnostics              ▼
                                           Checkpoint
                                                ▼
                                              Apply
                                                ▼
                                             Verify
                                                ▼
                                      SQLite Transaction
                                                ▼
                                             Rollback
```

Cyber Control Center визуализирует этот pipeline, но не реализует его заново.

## Границы модулей

| Модуль | Ответственность | Запрещённые зависимости |
|---|---|---|
| `WinState.Domain` | ресурсы, состояния, действия, транзакции, provider contracts | Windows API, YAML, SQLite, CLI/UI |
| `WinState.Core` | Profile Engine, validation, dependency graph, plan primitives | UI и конкретные Windows adapters |
| `WinState.Infrastructure` | конфигурация, platform paths и прикладные adapters | Terminal UI |
| `WinState.Storage` | SQLite migrations, transaction/action/backup history | CLI и Spectre.Console |
| `WinState.Providers.Environment` | Windows environment discovery/apply/verify/rollback | UI и общий workflow |
| `WinState.App` | composition root и безопасные application workflows | terminal rendering |
| `WinState.Terminal` | Cyber UI, меню, telemetry, traces, confirmations и анимации | прямой Windows API и SQLite SQL |
| `WinState.Cli` | команды, flags, exit codes и automation output | системная реализация provider |

## Направление зависимостей

```text
WinState.Cli ───────┐
                    ▼
CyberTerminalShell → WinState.App
                         │
             ┌───────────┼────────────┐
             ▼           ▼            ▼
        WinState.Core  Storage   Providers.Environment
             │           │            │
             └───────────┴────────────┘
                         ▼
                  WinState.Domain
```

`Domain` не знает о верхних слоях. `CyberTerminalShell` и automation CLI вызывают один application workflow и не могут обойти safeguards.

## Cyber frontend boundary

`CyberTerminalShell` отвечает только за presentation:

```text
boot trace
operation channel menu
telemetry tables
animated progress pipeline
action-by-action result stream
confirmation prompts
demo rendering
```

Он получает данные только через публичные методы `WinStateApplication`:

```text
GetEnvironmentStatusAsync
PlanEnvironmentAsync
ApplyEnvironmentAsync
ListEnvironmentCheckpointsAsync
RollbackEnvironmentAsync
ValidateProfileAsync
RunDoctorAsync
GetStorageStatusAsync
```

Frontend не создаёт `PlannedAction`, backup payload или SQL-запросы самостоятельно.

## Animation truthfulness

Анимированный pipeline выглядит так:

```text
handshake → operation → seal result
```

Средняя фаза оборачивает настоящий async-вызов application layer. Она не переводится в completed до возврата результата. После транзакции action stream строится по `EnvironmentExecutionReport.Actions`.

Это исключает ложный UI-success: зелёный verified result появляется только после provider verification.

## Environment Provider vertical slice

### Adapter boundary

Системный доступ скрыт за `IEnvironmentStore`:

```text
EnvironmentStateProvider
        ├── WindowsEnvironmentStore  → реальный User/Machine environment
        └── InMemoryEnvironmentStore → unit-тесты
```

Это позволяет тестировать plan, apply, verify и rollback без изменения машины разработчика.

### Планирование

1. Profile Engine нормализует YAML.
2. `EnvironmentProfileMapper` преобразует environment-секцию в `StateResource`.
3. Provider выполняет discovery.
4. `PlanAsync` создаёт только необходимые `PlannedAction`.
5. PATH actions одного scope связываются dependencies для детерминированного порядка.
6. `DependencyGraph` возвращает итоговый execution order.

Plan не изменяет систему и может выполняться сколько угодно раз.

### Исполнение

`EnvironmentWorkflow` оркестрирует provider:

```text
PlanAsync
→ validate risk/scope
→ PrepareRollbackAsync for every action
→ write manifest
→ ApplyAsync
→ VerifyAsync
→ record SQLite history
→ RollbackAsync on failure/request
```

Checkpoint создаётся для всего плана до первого изменения.

### Хранилище

```text
Transactions       → итог workflow
TransactionActions → status/message каждого action
ActionBackups      → путь к checkpoint JSON
```

Сами backup payloads хранятся в файловом каталоге, а SQLite содержит ссылки и историю.

## Resource identity

Переменная и PATH entry получают стабильный identity:

```text
environment://user/variable/<sha-token>
environment://machine/path/<sha-token>
```

Identity строится из нормализованных scope/name/path и не содержит само значение переменной.

## Главные правила

1. **Сначала план.** Изменение системы без execution plan запрещено.
2. **Идемпотентность.** Совпадающее состояние создаёт пустой план.
3. **Checkpoint до изменения.** Rollback data подготавливается заранее.
4. **Проверка результата.** Успешный API call не считается доказательством.
5. **Минимальные права.** User и Machine actions разделены.
6. **Rollback — свойство действия.** Provider указывает поддержку честно.
7. **Unmanaged остаётся unmanaged.** Неописанные ресурсы не удаляются.
8. **Нет секретов в логах/history.** Обычная environment-секция не предназначена для секретов.
9. **Один workflow для UI и CLI.** Safeguards нельзя обойти другим frontend.
10. **Platform boundary.** Реальный apply Environment Provider выполняется только на Windows.
11. **UI не является источником истины.** Статус берётся из application/provider result.

## Следующий архитектурный этап

Версия `0.6.0-alpha.1` должна вынести логику одного provider workflow в общий Apply Engine:

- несколько providers в одной транзакции;
- risk/confirmation groups;
- dependency-aware execution;
- cancellation policy;
- resume после перезапуска;
- reboot-pending state;
- cross-provider rollback;
- live execution graph в Cyber Control Center.

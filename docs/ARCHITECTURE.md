# 🏗️ Архитектура WinState

> Текущий статус: **`0.6.0-alpha.1` — Nexus UI, Unified Apply Engine и Update Uplink**.

## Общий pipeline

```text
YAML Profile
    ↓
Profile Engine → Validation
    ↓
Provider Discovery → Provider Plans
    ↓
WinState.Apply
    ├─ merge execution graph
    ├─ dependency validation/sort
    ├─ risk groups and policy gates
    ├─ checkpoint barrier
    ├─ apply + verify
    ├─ persisted manifest / resume
    └─ cross-provider rollback
    ↓
SQLite history + provider backup payloads
```

Update pipeline существует отдельно от system apply:

```text
Cyber Nexus → WinState.Update → GitHub Releases
                           → ZIP + SHA-256
                           → safe staging
                           → external updater process
```

## Модули

| Модуль | Ответственность | Не должен содержать |
|---|---|---|
| `WinState.Domain` | resources, actions, risks, provider/transaction contracts | YAML, Windows API, UI, HTTP, SQLite |
| `WinState.Core` | Profile Engine, validation и plan primitives | concrete providers и UI |
| `WinState.Apply` | unified graph, transaction manifest, resume и rollback | YAML, Spectre.Console, provider-specific API |
| `WinState.Update` | release discovery, SemVer, download, SHA-256, staging/updater | system configuration apply |
| `WinState.Infrastructure` | settings и platform paths | Terminal UI |
| `WinState.Storage` | SQLite migrations и transaction history | UI и provider implementation |
| `WinState.Providers.Environment` | User/Machine variables и PATH | UI и common transaction orchestration |
| `WinState.App` | composition root и workflow adapters | terminal rendering |
| `WinState.Terminal` | Nexus/Cyber UI, prompts, traces и animations | Windows API и raw SQL |
| `WinState.Cli` | entry point, flags и automation output | provider implementation |

## Dependency direction

```text
WinState.Cli
    ↓
WinState.Terminal ───────────────→ WinState.Update
    ↓
WinState.App
    ├──────────────→ WinState.Apply
    ├──────────────→ WinState.Core
    ├──────────────→ WinState.Storage
    └──────────────→ Providers.Environment
                          ↓
                    WinState.Domain
```

`Domain` остаётся нижним слоем. `Apply` зависит только от Domain contracts. `Update` — самостоятельная библиотека без зависимости от application state engine.

## Provider adapter boundary

Common engine работает через:

```csharp
IApplyProviderExecutor
```

Adapter предоставляет:

```text
PrepareRollbackAsync
ApplyAsync
VerifyAsync
RollbackAsync
```

В `0.6`:

```text
EnvironmentApplyExecutor
        ↓
EnvironmentStateProvider
        ├─ WindowsEnvironmentStore
        └─ InMemoryEnvironmentStore
```

Будущие WinGet и Optional Features providers зарегистрируют собственные executors.

## Unified execution graph

`ApplyEngine.BuildPlan`:

1. проверяет `ActionId`;
2. проверяет `DependsOn`;
3. обнаруживает cycle;
4. делает deterministic topological sort;
5. выделяет provider set;
6. строит risk groups;
7. вычисляет admin/reboot/irreversible flags.

Provider-specific порядок может быть выражен только dependencies, а не скрытым UI-поведением.

## Checkpoint barrier

```text
Prepare provider A action 1
Prepare provider B action 2
Prepare provider A action 3
Persist manifest
============================ no mutation above this line
Apply action 1
```

Отсутствующий обязательный checkpoint блокирует всю транзакцию.

## Persisted transaction

```text
<WINSTATE_HOME>/backups/transactions/<transaction-id>/
├── transaction.json
└── providers/
    ├── environment/
    └── <provider-id>/
```

Manifest обновляется после каждого verified action через temporary file и replacement. Это позволяет продолжить graph после controlled interruption.

## Transaction statuses

Используются domain statuses:

```text
Planned
Running
Succeeded
SucceededRebootPending
Partial
Failed
Cancelled
RolledBack
RollbackFailed
VerificationFailed
```

`SucceededRebootPending` не означает автоматическую перезагрузку.

## Nexus frontend boundary

`CyberNexusShell` показывает:

- Nexus telemetry;
- Transaction Matrix;
- execution graph/risk tables;
- action trace;
- Update Uplink;
- boot/shutdown animations.

Он не создаёт provider actions, backups, SQL или update checksum. Presentation вызывает публичные services и отображает фактические results.

Прежний `CyberTerminalShell` сохранён как вложенный `[01] CYBER CONTROL CENTER`.

## Update boundary

`UpdateService`:

- читает только GitHub Releases API;
- сравнивает SemVer;
- выбирает runtime ZIP;
- проверяет SHA-256;
- безопасно распаковывает staging payload;
- требует release marker;
- запускает updater process только в Windows release build.

Source mode не может заменить repository files.

## Storage

SQLite по-прежнему хранит:

```text
Transactions
TransactionActions
ActionBackups
```

Common Apply workflow записывает provider ID, status, message/timestamps и backup reference. Полный resumable manifest остаётся файловым, поскольку обновляется чаще и должен быть доступен до и независимо от SQLite history commit.

## Архитектурные инварианты

1. **Plan before mutation.**
2. **All checkpoints before first apply.**
3. **Verification before success.**
4. **Policy gates outside providers.**
5. **Provider identity on every action.**
6. **Deterministic dependency order.**
7. **Persist progress after each verified action.**
8. **Reverse graph rollback.**
9. **No silent elevation or reboot.**
10. **UI is never the source of truth.**
11. **Source checkout is never self-overwritten.**
12. **Update package must pass SHA-256 and marker checks.**

## Следующий этап

`0.7.0-alpha.1` подключит к common engine новые adapters:

```text
WinGet Provider
Windows Optional Features Provider
WSL prerequisite planner
package ownership policy
reboot grouping
```

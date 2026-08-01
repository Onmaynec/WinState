# 🏗️ Архитектура WinState

> Текущий статус: **`0.7.0-alpha.1` — Forge UI, три production providers, Unified Apply Engine и Update Uplink**.

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
    ├─ risk/admin/irreversible policy gates
    ├─ checkpoint barrier
    ├─ apply + verify
    ├─ persisted manifest / resume
    └─ cross-provider rollback
    ↓
SQLite history + provider backup payloads
```

Зарегистрированные production providers:

```text
environment        → Windows environment variables и PATH
packages.winget    → winget.exe package lifecycle
windows.features   → dism.exe Optional Features
```

Update pipeline отделён от system apply:

```text
Forge/Nexus → WinState.Update → GitHub Releases
                           → ZIP + SHA-256
                           → safe staging
                           → external updater process
```

## Модули

| Модуль | Ответственность | Не должен содержать |
|---|---|---|
| `WinState.Domain` | resources, profile records, actions, risks и provider contracts | YAML, Windows API, UI, HTTP, SQLite |
| `WinState.Core` | Profile Engine, validation и plan primitives | concrete providers и UI |
| `WinState.Apply` | unified graph, policy validation, manifests, resume и rollback | YAML, Spectre.Console, provider-specific API |
| `WinState.Update` | release discovery, SemVer, SHA-256 и staging/updater | system configuration apply |
| `WinState.Infrastructure` | settings и platform paths | Terminal UI |
| `WinState.Storage` | SQLite migrations и transaction history | UI и provider implementation |
| `WinState.Providers.Environment` | variables и PATH | common orchestration |
| `WinState.Providers.Packages` | WinGet discovery/apply/verify/rollback boundary | UI и transaction engine |
| `WinState.Providers.Features` | DISM discovery/enable/disable/verify/rollback | UI и transaction engine |
| `WinState.App` | composition root, adapters и workflows | terminal rendering |
| `WinState.Terminal` | Forge/Nexus/Cyber UI, prompts и traces | Windows API и raw SQL |
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
    ├──────────────→ Providers.Environment
    ├──────────────→ Providers.Packages
    └──────────────→ Providers.Features
                           ↓
                     WinState.Domain
```

`Domain` остаётся нижним слоем. `Apply` зависит только от Domain contracts. Provider projects не знают о UI, SQLite и application workflow.

## Provider adapter boundary

Common engine работает через:

```csharp
IApplyProviderExecutor
```

Каждый adapter реализует:

```text
PrepareRollbackAsync
ApplyAsync
VerifyAsync
RollbackAsync
```

В `0.7` зарегистрированы:

```text
EnvironmentApplyExecutor     → EnvironmentStateProvider
WingetApplyExecutor          → WingetPackageProvider
WindowsFeatureApplyExecutor  → WindowsFeatureProvider
```

Системный доступ дополнительно скрыт за тестируемыми interfaces:

```text
IEnvironmentStore
IWingetClient
IWindowsFeatureClient
```

Production implementations вызывают Windows API/CLI. Unit-тесты используют in-memory/fake implementations.

## Profile composition

`ProfileEngine` объединяет sections из `extends` и `includes`:

```text
environment → dictionary/path merge
packages    → overlay by source + package ID
features    → overlay by feature name
```

После variable resolution данные преобразуются в immutable domain records и проходят `ProfileValidator`.

## WinGet boundary

```text
WingetPackageProvider
       ↓
ProcessWingetClient
       ↓
winget.exe + ProcessStartInfo.ArgumentList
```

Provider создаёт actions:

```text
Install   → reversible for packages absent before transaction
Update    → irreversible
Uninstall → irreversible
```

Upgrade/uninstall не получают фиктивный checkpoint. Общий engine видит `SupportsRollback = false` и включает irreversible policy gate.

## Optional Features boundary

```text
WindowsFeatureProvider
       ↓
DismWindowsFeatureClient
       ↓
dism.exe /Online /English /NoRestart
```

Checkpoint сохраняет исходное Enabled/Disabled state. Rollback выполняет противоположную DISM-операцию при необходимости. Exit code `3010` фиксирует reboot requirement, но не инициирует reboot.

## Unified execution graph

`ApplyEngine.BuildPlan`:

1. проверяет уникальность `ActionId`;
2. проверяет `DependsOn`;
3. обнаруживает cycles;
4. делает deterministic topological sort;
5. вычисляет provider set;
6. строит risk groups;
7. вычисляет admin/reboot/irreversible flags.

Provider-specific порядок выражается dependencies, а не скрытым поведением UI.

## Checkpoint barrier

```text
Prepare environment action
Prepare WinGet install action
Prepare Optional Feature action
Persist transaction.json
================================ no mutation above this line
Apply first action
```

Отсутствующий checkpoint обратимого action блокирует всю транзакцию. Необратимое действие допускается только после отдельного policy authorization.

## Persisted transaction

```text
<WINSTATE_HOME>/backups/transactions/<transaction-id>/
├── transaction.json
└── providers/
    ├── environment/
    ├── packages.winget/
    └── windows.features/
```

Manifest обновляется после каждого verified action через temporary file и atomic replacement.

## Forge frontend boundary

`CyberForgeShell` показывает:

- верхний Forge Control Fabric;
- Package & Feature Forge;
- provider support и inventory counters;
- unified action/risk table;
- admin/reboot/irreversible gates;
- фактический execution trace;
- переход в прежний Nexus Control Fabric.

Frontend не создаёт actions, backup payloads, DISM/WinGet commands или SQL. Он вызывает только `WinStateApplication`.

## Storage

SQLite хранит:

```text
Transactions
TransactionActions
ActionBackups
```

File manifest остаётся источником resume, а SQLite — долговременной history.

## Архитектурные инварианты

1. **Plan before mutation.**
2. **All reversible checkpoints before first apply.**
3. **Verification before success.**
4. **Irreversible operations are declared honestly.**
5. **Policy gates live outside providers.**
6. **Provider identity exists on every action.**
7. **Deterministic dependency order.**
8. **Persist progress after every verified action.**
9. **Reverse graph rollback.**
10. **No silent elevation or reboot.**
11. **UI is never the source of truth.**
12. **Unknown packages are not removed without ownership data.**
13. **Source checkout is never self-overwritten.**
14. **Update payload must pass SHA-256 and marker checks.**

## Следующий этап

`0.8.0-alpha.1` расширит provider set:

```text
allowlisted Registry
Windows Services
Startup entries
Scheduled Tasks
→ ownership markers → backup payloads → unified verification/rollback
```

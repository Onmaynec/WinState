# 🧠 Unified Apply Engine

## Назначение

`WinState.Apply` — общий transaction engine для всех системных providers WinState. Он не знает о YAML, Spectre.Console, SQLite SQL или конкретных Windows API. На вход engine получает готовые `PlannedAction` и зарегистрированные `IApplyProviderExecutor`.

В версии `0.6.0-alpha.1` подключён первый реальный adapter:

```text
EnvironmentApplyExecutor → EnvironmentStateProvider
```

Архитектура уже поддерживает несколько provider IDs в одном execution graph.

## Основные модели

- `IApplyProviderExecutor` — adapter apply/verify/rollback конкретного provider;
- `ApplyEngineOptions` — подтверждённые policy-флаги;
- `ApplyEngineRequest` — профиль, working directory, backup root и actions;
- `UnifiedApplyPlan` — отсортированный graph, providers и risk groups;
- `ApplyTransactionManifest` — persisted transaction state;
- `ApplyEngineReport` — итог выполнения, resume или rollback.

## Построение graph

`BuildPlan` выполняет:

1. проверку уникальности `ActionId`;
2. проверку существования всех `DependsOn`;
3. deterministic topological sort;
4. обнаружение dependency cycles;
5. список участвующих providers;
6. risk groups;
7. flags admin/reboot/irreversible;
8. вычисление максимального риска.

При одинаковом входном плане порядок выполнения остаётся стабильным.

## Risk policy

До checkpoints engine проверяет:

- elevated actions требуют `AllowAdministrator`;
- `Critical` actions требуют `AllowCritical`;
- actions без rollback требуют `AllowIrreversible`;
- reboot не выполняется автоматически только потому, что action его допускает.

UI собирает эти разрешения отдельными подтверждениями. Provider не может самостоятельно обойти policy.

## Checkpoint barrier

До первого системного изменения engine вызывает `PrepareRollbackAsync` для **всех** reversible actions.

```text
prepare action A
prepare action B
prepare action C
write transaction.json
------------------------- checkpoint barrier
apply action A
```

Если хотя бы один обязательный checkpoint не создан, apply не начинается.

## Выполнение и verification

Для каждого action:

```text
ApplyAsync
→ проверить ActionStatus.Succeeded
→ VerifyAsync
→ сохранить verified result в transaction.json
→ перейти к dependents
```

Успешный API call без verification не считается успехом.

## Persisted manifest

```text
<WINSTATE_HOME>/backups/transactions/<transaction-id>/
├── transaction.json
└── providers/
    ├── environment/
    └── <future-provider>/
```

Manifest содержит:

- transaction/profile ID;
- working directory;
- start/completion time;
- transaction status;
- policy options;
- полный ordered plan;
- backup references;
- результаты выполненных actions;
- reboot-required flag.

Запись выполняется во временный файл с последующей заменой итогового manifest.

## Resume

`ResumeAsync` загружает manifest и пропускает actions со статусом `Succeeded`. Остальная часть graph выполняется в исходном dependency order.

Resume разрешён для незавершённых или ошибочных состояний. Завершённые `Succeeded`, `RolledBack` и `RollbackFailed` повторно не запускаются.

## Cross-provider rollback

Automatic и manual rollback:

1. определяют успешно применённые actions;
2. обходят общий plan в обратном порядке;
3. находят executor по `ProviderId`;
4. передают provider backup reference;
5. сохраняют `RolledBack` или `RollbackFailed` result.

Таким образом, provider B может откатиться раньше provider A даже при одной общей транзакции.

## Reboot pending

Action может поставить `MayRequireReboot`. После успешной verification transaction получает:

```text
SucceededRebootPending
```

если reboot ещё не разрешён и не оркестрирован. Автоматический reboot в `0.6` не выполняется.

## Transaction Matrix

Cyber Nexus предоставляет каналы:

- `[11] BUILD EXECUTION GRAPH`;
- `[12] EXECUTE VERIFIED GRAPH`;
- `[13] RESUME INTERRUPTED`;
- `[14] CROSS-PROVIDER ROLLBACK`.

Экран показывает providers, actions, risk groups, dependencies и persisted history.

## Тестирование

`WinState.Apply.Tests` использует два fake provider executors и проверяет:

- dependency ordering;
- risk grouping;
- cycle detection;
- cross-provider rollback после ошибки второго provider;
- reboot-pending status;
- сохранение transaction manifest.

## Текущие ограничения

- в production зарегистрирован только Environment adapter;
- нет параллельного выполнения независимых actions;
- нет Windows startup task для автоматического resume после reboot;
- manual action и irreversible policy пока являются блокирующими primitives;
- distributed/remote execution не входит в scope проекта.

# 🗄️ SQLite-хранилище

WinState хранит служебное состояние локально и не требует внешнего сервера. База создаётся по вычисленному пути `state/winstate.db`.

## Идемпотентные миграции

```powershell
winstate storage migrate
winstate storage status
```

Повторный запуск миграций безопасен: применённые версии фиксируются в `MigrationHistory` и не выполняются повторно.

## Начальная схема

Первая миграция создаёт:

- `Profiles` и `ProfileVersions`;
- `ManagedResources` и `CurrentBaselines`;
- `Transactions`, `TransactionActions`, `ActionBackups`;
- `ProviderStates` и `DriftResults`;
- `ApplicationSettings`;
- `MigrationHistory`.

SQL-схема не хранит секреты. Данные действий и состояний в следующих версиях будут записываться в JSON-полях только после маскирования чувствительных значений.

## Транзакционность

Каждая миграция выполняется внутри SQLite-транзакции. Версия записывается в историю только после успешного применения всей схемы.

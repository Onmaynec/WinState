# 🗄️ SQLite-хранилище

WinState хранит служебное состояние локально и не требует внешнего сервера. База создаётся по вычисленному пути `state/winstate.db`.

## Идемпотентные миграции

```powershell
winstate storage migrate
winstate storage status
```

Повторный запуск миграций безопасен: применённые версии фиксируются в `MigrationHistory` и не выполняются повторно.

## Схема

Первая миграция создаёт:

- `Profiles` и `ProfileVersions`;
- `ManagedResources` и `CurrentBaselines`;
- `Transactions`, `TransactionActions`, `ActionBackups`;
- `ProviderStates` и `DriftResults`;
- `ApplicationSettings`;
- `MigrationHistory`.

## Environment Provider history

Начиная с `0.4.0-alpha.1`, таблицы транзакций используются реальным provider workflow.

### `Transactions`

Содержит:

- transaction ID;
- profile ID/name;
- время начала и завершения;
- итоговый status;
- mode: `apply` или `rollback`;
- признак pending reboot.

### `TransactionActions`

Для каждого provider action хранит:

- action ID;
- provider ID;
- status;
- компактный JSON result с сообщением.

### `ActionBackups`

Связывает action с файлом checkpoint. Сам rollback payload хранится в файловой системе:

```text
<WINSTATE_HOME>/backups/environment/<transaction-id>/
```

SQLite хранит ссылку, но не дублирует backup content.

## Транзакционность

Каждая миграция выполняется внутри SQLite-транзакции. Версия записывается в историю только после успешного применения всей схемы.

Запись результата provider workflow также выполняется одной SQLite-транзакцией: transaction row, action results и backup references фиксируются вместе.

## Безопасность данных

- база не предназначена для хранения секретов;
- обычная environment-секция профиля не должна содержать токены и пароли;
- секретные values должны появиться только после отдельного secrets adapter;
- backup-файлы наследуют права каталога WinState;
- удаление базы не откатывает Windows — для восстановления используется checkpoint manifest;
- удаление checkpoint делает соответствующий rollback технически невозможным.

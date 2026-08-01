# 🛡️ Модель безопасности

## Запреты по умолчанию

- нет скрытых системных изменений;
- нет apply без execution plan;
- нет apply без явного подтверждения;
- нет массового удаления неизвестных ресурсов;
- нет запуска произвольных скриптов;
- нет автоматической перезагрузки;
- нет сохранения токенов, паролей и приватных ключей;
- нет Machine/critical операций без отдельного разрешения.

## Доверительная граница профиля

Профиль считается входными данными, а не доверенным кодом. Profile Engine нормализует пути, ограничивает includes каталогом профиля и обнаруживает циклы наследования. Скрипты в будущих версиях потребуют SHA-256 и явного разрешения.

Environment Provider обрабатывает только декларативные variables и PATH entries. Значения не выполняются как команды.

## Environment Provider safeguards

### Plan before apply

`environment plan` выполняет discovery и diff без побочных эффектов. `environment apply` повторно строит план и не доверяет ранее напечатанному выводу.

### Explicit confirmation

CLI требует `--yes`. Control Center показывает интерактивное подтверждение с default `No`.

### Scope separation

| Scope | Risk | Дополнительное условие |
|---|---|---|
| User | Low | явное подтверждение |
| Machine | Medium | подтверждение + `--allow-machine` + elevated process |

### Checkpoint before mutation

Checkpoint для **всех** действий плана создаётся до первого изменения. Если хотя бы один checkpoint не подготовлен, apply не начинается.

### Verify after mutation

После каждой операции provider повторно читает variable/PATH. Несовпадение переводит action в `VerificationFailed` и запускает rollback.

### Automatic rollback

Автоматический rollback включён по умолчанию. Применённые действия восстанавливаются в обратном порядке. Флаг `--no-auto-rollback` предназначен только для диагностики.

### Unmanaged resources

- неописанные variables не удаляются;
- неизвестные PATH entries сохраняются;
- provider изменяет только ресурсы, identity которых создан из profile environment section.

## Повышение прав

WinState не должен постоянно работать от администратора. User plan можно строить и применять с обычными правами. Machine actions помечаются `RequiresAdministrator`; frontend не пытается скрыто перезапустить процесс с elevation.

## Секреты

Environment values, checkpoint и SQLite history не предназначены для секретов. До появления secrets adapter запрещено помещать в профиль:

- токены;
- пароли;
- private keys;
- connection strings с credentials;
- recovery codes.

## Логи и history

В логах допустимы provider ID, resource identity, operation, risk и безопасные диагностические данные. Новые providers обязаны маскировать чувствительные properties до записи plan/history.

## Backup boundary

Checkpoint Environment Provider восстанавливает только variables и PATH соответствующего scope. Он не является system restore point и не заменяет полный backup Windows.

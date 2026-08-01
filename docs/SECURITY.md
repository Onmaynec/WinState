# 🛡️ Модель безопасности

## Запреты по умолчанию

- нет скрытых системных изменений;
- нет apply без execution graph;
- нет elevated/Critical/irreversible actions без отдельного разрешения;
- нет первой мутации до подготовки обязательных checkpoints;
- нет success без verification;
- нет массового удаления unmanaged resources;
- нет автоматической перезагрузки;
- нет сохранения токенов, паролей и private keys;
- нет self-update source checkout;
- нет установки release ZIP без SHA-256 и marker validation.

## Доверительная граница профиля

Профиль считается входными данными, а не доверенным кодом. Profile Engine:

- ограничивает includes;
- обнаруживает inheritance cycles;
- нормализует пути;
- не выполняет значения Environment как команды.

Будущие script resources должны иметь отдельный allowlist/hash policy.

## Unified Apply Engine safeguards

### Plan before mutation

Provider plans объединяются в общий graph. Engine повторно проверяет:

- уникальность action IDs;
- существование dependencies;
- отсутствие cycles;
- наличие executor для каждого provider;
- platform support каждого executor.

### Central risk policy

| Группа | Требование |
|---|---|
| User/Low | обычное явное подтверждение graph |
| Administrator | отдельное `AllowAdministrator` |
| Critical | отдельное `AllowCritical` |
| No rollback | отдельное `AllowIrreversible` |
| Reboot | status pending; автоматический reboot не выполняется |

Provider не принимает решение о подтверждении самостоятельно.

### Checkpoint barrier

Для всех reversible actions вызывается `PrepareRollbackAsync` до первого apply. Неудача одного обязательного checkpoint блокирует весь graph.

### Persisted progress

`transaction.json` записывается через temporary file и replacement. После каждого verified action progress сохраняется, чтобы resume не повторял уже подтверждённое действие.

### Verify after apply

`ActionExecutionResult.Succeeded` недостаточно. После него обязателен `VerifyAsync`. Несовпадение переводит action в `VerificationFailed`.

### Automatic cross-provider rollback

Automatic rollback включён по умолчанию. Engine:

1. выбирает успешно применённые actions;
2. проходит общий graph в обратном порядке;
3. вызывает executor соответствующего provider;
4. сохраняет результат каждого rollback action.

### Cancellation

При `Ctrl+C` engine сохраняет manifest. При включённой automatic rollback policy уже применённые действия восстанавливаются с `CancellationToken.None`, чтобы пользовательская отмена не обрывала аварийный rollback посередине.

## Environment Provider safeguards

- User scope — `Low`;
- Machine scope — `Medium` и `RequiresAdministrator`;
- неописанные variables не удаляются;
- неизвестные PATH entries сохраняются;
- identity не содержит само значение variable;
- provider повторно читает Windows после изменения;
- checkpoints восстанавливают исходное existence/value или полный PATH scope.

## Повышение прав

WinState не пытается скрыто перезапустить себя с UAC. Elevated actions должны быть показаны заранее, отдельно подтверждены и выполнены уже elevated process.

## Update Uplink threat model

### Источник

По умолчанию используется:

```text
https://api.github.com/repos/Onmaynec/WinState/releases
```

Repository можно переопределить для development/testing, поэтому production release должен сохранять default source.

### Version selection

- draft releases игнорируются;
- stable channel игнорирует prerelease;
- version сравнивается локальным Semantic Version parser;
- downgrade автоматически не выполняется.

### Asset integrity

- ZIP и checksum являются отдельными release assets;
- checksum должен содержать 64 hex-символа;
- actual SHA-256 вычисляется локально;
- mismatch удаляет ZIP и блокирует staging.

SHA-256 подтверждает соответствие asset опубликованному checksum, но не заменяет Authenticode signing. Signing запланирован до stable `1.0`.

### ZIP extraction

Каждый entry обязан оставаться внутри staging directory. Path traversal, абсолютные/выходящие пути и отсутствующий `winstate.release.json` блокируют установку.

### Self-install boundary

Self-install разрешён только если рядом с процессом есть:

```text
winstate.exe
winstate.release.json
```

`dotnet run`, DLL launch и обычный Git checkout не удовлетворяют этому условию. Source tree никогда не заменяется updater-ом.

### Separate updater process

Updater запускается только после verified staging. Он ждёт текущий PID, делает temporary backup install directory, копирует payload и перезапускает `winstate.exe`.

Updater не должен заменять пользовательские state directories:

```text
.winstate
profiles
logs
```

Ошибки записываются в `%TEMP%\WinState\update-error.log`.

### Automatic modes

`WINSTATE_AUTO_UPDATE=install` разрешает установку без UI prompt, но не отключает:

- HTTPS;
- release channel filtering;
- SemVer comparison;
- SHA-256;
- safe extraction;
- marker validation;
- release-build boundary.

## Секреты

Обычные profiles, environment checkpoints, transaction manifests, logs и SQLite history не предназначены для секретов. Запрещено помещать туда:

- access tokens;
- пароли;
- private keys;
- connection strings с credentials;
- recovery codes.

## Backup boundary

Provider checkpoint и updater backup не являются Windows System Image. Они покрывают только управляемые ресурсы или install directory текущей программы.

## Reporting

Для security issue используйте root [`SECURITY.md`](../SECURITY.md). Не публикуйте exploit details и secrets в открытом issue.

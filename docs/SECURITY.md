# 🛡️ Модель безопасности

## Запреты по умолчанию

- нет скрытых системных изменений;
- нет apply без execution graph;
- нет elevated/Critical/irreversible actions без отдельного разрешения;
- нет первой мутации до подготовки обязательных checkpoints;
- нет success без verification;
- нет массового удаления unmanaged resources;
- нет автоматической перезагрузки;
- нет выполнения profile values как shell-команд;
- нет сохранения токенов, паролей и private keys;
- нет self-update source checkout;
- нет установки release ZIP без SHA-256 и marker validation.

## Доверительная граница профиля

Профиль считается входными данными, а не доверенным кодом. Profile Engine:

- ограничивает includes;
- обнаруживает inheritance cycles;
- нормализует пути;
- валидирует package/feature states и scopes;
- не выполняет environment/package/feature values как команды.

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

Provider описывает свойства action, но не принимает решение о пользовательском подтверждении.

### Checkpoint barrier

Для всех reversible actions вызывается `PrepareRollbackAsync` до первого apply. Неудача одного обязательного checkpoint блокирует весь graph.

### Persisted progress

`transaction.json` записывается через temporary file и replacement. После каждого verified action progress сохраняется, чтобы resume не повторял уже подтверждённое действие.

### Verify after apply

`ActionExecutionResult.Succeeded` недостаточно. После него обязателен `VerifyAsync`. Несовпадение переводит action в `VerificationFailed`.

### Automatic cross-provider rollback

Automatic rollback включён по умолчанию. Engine проходит успешно применённые actions в обратном порядке и вызывает executor соответствующего provider.

### Cancellation

При `Ctrl+C` engine сохраняет manifest. Аварийный rollback выполняется с `CancellationToken.None`, чтобы повторная отмена не оставила восстановление незавершённым.

## Environment Provider safeguards

- User scope — `Low`;
- Machine scope — `Medium` и `RequiresAdministrator`;
- неописанные variables не удаляются;
- неизвестные PATH entries сохраняются;
- identity не содержит само значение variable;
- provider повторно читает Windows после изменения;
- checkpoints восстанавливают исходное existence/value или полный PATH scope.

## WinGet Provider safeguards

### Без shell-интерпретации

`ProcessWingetClient` использует:

```csharp
ProcessStartInfo.ArgumentList
```

Package ID, source, version и scope передаются отдельными arguments. Строка profile не превращается в PowerShell/cmd command line.

### Exact package identity

Install/upgrade/uninstall используют:

```text
--id <id> --exact --disable-interactivity --silent
```

Это снижает риск выбора package по частичному имени. Source передаётся явно, когда указан в profile.

### Agreements и non-interactive execution

Для install/upgrade передаются package/source agreements. Переменная `WINGET_DISABLE_INTERACTIVITY=1` и CLI flag запрещают неожиданный interactive prompt внутри transaction engine.

### Rollback truthfulness

| Operation | `SupportsRollback` | Причина |
|---|---:|---|
| новая установка | true | package отсутствовал и может быть удалён |
| upgrade | false | старая версия/installer могут быть недоступны |
| uninstall | false | точная версия и source могут исчезнуть |

WinState не создаёт фиктивный checkpoint для upgrade/uninstall. Такие actions попадают в irreversible group и требуют `AllowIrreversible`.

### Remove-unmanaged protection

Поле `removeUnmanagedPackages` не запускает массовое удаление в `0.7`. Пока нет надёжного ownership store, неизвестные packages остаются нетронутыми.

### Verification

После изменения provider повторно выполняет inventory lookup по exact ID/source. Exact-version profile дополнительно проверяет установленную версию.

## Windows Optional Features safeguards

### Administrator boundary

Все feature actions получают `RequiresAdministrator = true`. WinState не выполняет скрытый UAC restart: пользователь заранее видит admin group и должен запустить elevated process самостоятельно.

### DISM invocation

Production client вызывает только фиксированный набор операций:

```text
/Online /Get-Features /Format:Table /English
/Online /Enable-Feature  /FeatureName:<name> /NoRestart /Quiet /English [/All]
/Online /Disable-Feature /FeatureName:<name> /NoRestart /Quiet /English
```

Произвольные DISM flags из profile не поддерживаются.

### No silent reboot

`/NoRestart` обязателен. Exit code `3010` считается успешной операцией с reboot requirement и переводит transaction в `SucceededRebootPending`, если reboot policy не разрешена.

### Checkpoint и rollback

До apply сохраняется точное исходное состояние Enabled/Disabled. Rollback возвращает его обратной DISM-операцией. После apply и rollback provider повторно читает inventory.

### Unknown features

Feature, отсутствующая в DISM inventory, создаёт `Unsupported` action. Она не передаётся в DISM как предполагаемое корректное имя без отображения в плане и irreversible authorization.

## Повышение прав

WinState не должен постоянно работать от администратора. Environment User и package User actions можно выполнять обычным процессом. Machine packages и все Optional Features требуют отдельной admin policy.

## Update Uplink threat model

### Источник и version selection

- по умолчанию используется GitHub Releases `Onmaynec/WinState`;
- draft releases игнорируются;
- stable channel игнорирует prerelease;
- version сравнивается локальным Semantic Version parser;
- downgrade автоматически не выполняется.

### Asset integrity

- ZIP и checksum являются отдельными release assets;
- checksum должен содержать 64 hex-символа;
- actual SHA-256 вычисляется локально;
- mismatch блокирует staging.

SHA-256 подтверждает соответствие опубликованному asset, но не заменяет Authenticode signing. Signing запланирован до stable `1.0`.

### ZIP extraction и self-install

Каждый entry обязан оставаться внутри staging directory. Path traversal и отсутствующий `winstate.release.json` блокируют установку.

Self-install разрешён только при наличии рядом с процессом:

```text
winstate.exe
winstate.release.json
```

`dotnet run`, DLL launch и Git checkout не удовлетворяют этому условию.

### Separate updater process

Updater ждёт текущий PID, создаёт temporary backup install directory, копирует verified payload и перезапускает `winstate.exe`. Пользовательские `.winstate`, `profiles` и `logs` не заменяются.

## Секреты

Обычные profiles, checkpoints, transaction manifests, logs и SQLite history не предназначены для секретов. Запрещено помещать туда access tokens, пароли, private keys, credential-bearing connection strings и recovery codes.

## Backup boundary

Provider checkpoint и updater backup не являются Windows System Image. Они покрывают только конкретные управляемые resources или install directory WinState.

## Reporting

Для security issue используйте root [`SECURITY.md`](../SECURITY.md). Не публикуйте exploit details и secrets в открытом issue.

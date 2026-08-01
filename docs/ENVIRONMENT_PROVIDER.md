# 🌿 Environment Provider

`WinState 0.4.0-alpha.1` добавляет первый полностью рабочий системный provider. Он управляет только секцией `environment` профиля: пользовательскими и машинными переменными, а также отдельными элементами `PATH`.

> [!IMPORTANT]
> Provider работает с User/Machine environment только на Windows. Сначала всегда выполняйте `plan`. Машинная область требует повышенных прав и отдельного подтверждения.

## 🔄 Жизненный цикл

```text
Profile Engine
      ↓
Discover current User/Machine environment
      ↓
Diff → deterministic execution plan → risk summary
      ↓
Explicit confirmation
      ↓
Checkpoint every action
      ↓
Apply → verify each action
      ↓
SQLite history + checkpoint manifest
      ↓
Rollback on failure or explicit request
```

WinState не считает успешный вызов API достаточным результатом. После каждого изменения provider повторно читает значение и проверяет соответствие профилю.

## 🧭 Environment Center

В интерактивном режиме откройте:

```text
WinState Control Center
└── Environment Center
    ├── План и применение
    ├── Текущее состояние
    └── Rollback checkpoint
```

Панель показывает risk level, scope, операцию, ресурс и объяснение каждого изменения. `Apply` недоступен до подтверждения плана. Для Machine scope появляется второе предупреждение.

## 🧪 Безопасный пример

Файл [`samples/environment-provider/user-sandbox.yaml`](../samples/environment-provider/user-sandbox.yaml) использует только User scope:

```yaml
schemaVersion: 1

metadata:
  name: Environment Provider Sandbox

environment:
  user:
    WINSTATE_ENV_PROVIDER_SAMPLE: "enabled"

  userPath:
    - path: "{{profileDirectory}}/tools"
      state: present
      position: append
```

Построить план:

```powershell
winstate environment plan .\samples\environment-provider\user-sandbox.yaml
```

Применить после просмотра:

```powershell
winstate environment apply `
  .\samples\environment-provider\user-sandbox.yaml `
  --yes
```

Показать checkpoint:

```powershell
winstate environment checkpoints
```

Восстановить конкретный manifest:

```powershell
winstate environment rollback `
  .\.winstate\backups\environment\<transaction>\manifest.json `
  --yes
```

## 🧾 Поддерживаемый профиль

### Переменные

```yaml
environment:
  user:
    DEV_MODE: "true"
    TOOL_CACHE: "D:\\Cache"

  machine:
    COMPANY_MODE: "managed"
```

Текущая версия трактует объявленную переменную как `Configured`: она создаётся, если отсутствует, или изменяется, если значение отличается. Неописанные переменные не удаляются.

### PATH entries

```yaml
environment:
  userPath:
    - path: "C:\\Dev\\bin"
      state: present
      position: prepend

    - path: "C:\\Legacy"
      state: absent
      position: append
```

Поддерживаются:

| Поле | Значения | Значение |
|---|---|---|
| `state` | `present`, `absent` | добавить/сохранить или удалить конкретный entry |
| `position` | `prepend`, `append` | начало или конец управляемого PATH |

Сравнение PATH выполняется без учёта регистра, с нормализацией `/` и `\`, кавычек и завершающего разделителя. Provider не перестраивает неизвестные элементы PATH и не удаляет их.

## 🛡️ Safeguards

### User scope

- risk level: `Low`;
- не требует elevation;
- всё равно требует `--yes` в CLI;
- checkpoint создаётся до изменения.

### Machine scope

- risk level: `Medium`;
- действие помечается `RequiresAdministrator`;
- CLI требует одновременно `--yes` и `--allow-machine`;
- терминальная панель показывает отдельное подтверждение;
- процесс должен быть запущен от имени администратора.

Пример:

```powershell
winstate environment apply .\profiles\workstation.yaml `
  --yes `
  --allow-machine
```

## 💾 Checkpoint

Для каждого действия создаётся отдельный JSON-файл с исходным значением. Рядом хранится `manifest.json`:

```text
<WINSTATE_HOME>/backups/environment/<transaction-id>/
├── manifest.json
├── env-create-....json
├── env-modify-....json
└── env-remove-....json
```

Manifest содержит:

- ID транзакции;
- имя и путь профиля;
- время создания;
- список действий;
- ссылки на резервные данные;
- итоговый статус: `prepared`, `succeeded`, `failed`, `rolled-back` или `rollback-failed`.

Для переменной checkpoint сохраняет факт существования и старое значение. Для PATH сохраняется полный список entries соответствующего scope.

## 🗄️ SQLite history

После apply или rollback WinState записывает:

- транзакцию в `Transactions`;
- результат каждого действия в `TransactionActions`;
- backup reference в `ActionBackups`.

Секретные значения в checkpoint и history не поддерживаются: секреты нельзя помещать в обычную environment-секцию профиля.

## ⚙️ CLI

```text
winstate environment status
winstate environment plan <profile> [--var name=value]
winstate environment apply <profile> --yes [--allow-machine]
winstate environment checkpoints
winstate environment rollback <manifest|directory> --yes
```

Сокращение `env` эквивалентно `environment`.

Автоматический rollback включён по умолчанию. Флаг `--no-auto-rollback` предназначен только для диагностики и оставляет checkpoint для ручного восстановления.

## ✅ Проверки

Unit-тесты используют `InMemoryEnvironmentStore` и проверяют:

- variable create/modify;
- PATH add/remove/reorder;
- no-op при совпадении;
- Machine risk/elevation flag;
- checkpoint;
- apply;
- verification;
- rollback и восстановление исходного состояния.

GitHub Actions дополнительно выполняет настоящий User-scope vertical slice на временном Windows runner:

```text
plan → apply → checkpoint → verify → rollback → assert clean state
```

На Ubuntu CI проверяет сборку, unit-тесты и корректный Windows-only status без попытки системного изменения.

## ⚠️ Ограничения 0.4

- нет удаления обычной переменной через profile state;
- нет secrets adapter;
- нет условий по версии Windows;
- нет reboot/resume orchestration;
- нет общего transaction engine для нескольких providers;
- уже запущенные процессы не получают новые значения автоматически — provider отправляет системный broadcast, но приложение должно перечитать environment или быть перезапущено.

Следующий этап — общий Apply Engine с транзакциями между несколькими providers, resume и согласованным rollback.

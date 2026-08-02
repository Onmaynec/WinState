# 🔄 Политика совместимости и миграций WinState 1.0

## Цель

WinState хранит состояние, необходимое для безопасного resume, rollback, ownership и обновления. Версия 1.0 фиксирует правила изменения этих форматов, чтобы новая версия не интерпретировала старые данные опасным образом.

## Версионируемые данные

Политика распространяется на:

- SQLite migrations;
- Environment checkpoint manifests;
- Unified Apply transaction manifests;
- Workspace transaction manifests;
- Workspace ownership ledger;
- Capture snapshot manifests;
- updater check ledger;
- `winstate.release.json`.

## Основные правила

1. Каждый persisted формат содержит `schemaVersion`.
2. Отсутствующая или повреждённая обязательная версия блокирует mutation.
3. Более новая неизвестная schema никогда не понижается автоматически.
4. Миграции выполняются только вперёд и до первого изменения системы.
5. Миграция должна быть идемпотентной.
6. Исходный файл сохраняется до необратимого преобразования.
7. После миграции данные повторно валидируются.
8. Failure миграции не должен оставлять частично записанный manifest.

## Workspace ownership ledger

Текущий формат:

```text
schemaVersion: 1
```

Ledger хранит точные нормализованные resource identifiers. При чтении WinState:

- отклоняет schema новее поддерживаемой;
- удаляет пустые записи;
- дедуплицирует идентификаторы без учёта регистра;
- сортирует записи детерминированно;
- записывает результат атомарно;
- фиксирует `migratedAt` и `updatedAt`.

Ownership не восстанавливается предположением по существующим файлам или настройкам. Ресурс считается принадлежащим WinState только после успешного apply или подтверждённой миграции ledger.

## Transaction manifests

Transaction manifest является журналом фактически выполненных действий, а не повторно вычисляемым планом. Новая версия обязана использовать persisted action order и backup references.

Если transaction schema новее текущей версии приложения:

```text
rollback/resume → blocked
system mutation → blocked
```

Пользователь получает диагностическое сообщение с требованием использовать совместимую или более новую WinState.

## Атомарная запись

JSON/YAML/Markdown state files создаются так:

```text
serialize → temporary file → flush/write → atomic move/replace
```

Временный файл находится в том же каталоге, чтобы операция перемещения оставалась в пределах одного filesystem volume.

## SQLite

SQLite использует последовательные numbered migrations. Версия приложения:

- применяет отсутствующие известные migrations по порядку;
- не удаляет пользовательские данные при обычном upgrade;
- не открывает базу на запись, если обнаружена неизвестная более новая schema;
- не выполняет downgrade schema автоматически.

## Release marker

`winstate.release.json` является частью доверенной структуры официального ZIP. В 1.0 marker содержит:

- `schemaVersion`;
- `product`;
- `version`;
- `runtime`;
- `repository`;
- `packagedAtUtc`;
- `authenticodeSigned`.

Updater обязан отклонить package с несовместимым product/runtime/version или повреждённым marker.

## Backward compatibility

В пределах major version `1.x`:

- существующие manifest поля не меняют смысл;
- новые необязательные поля получают безопасные defaults;
- удаление поля требует отдельной migration;
- CLI exit codes сохраняют значение;
- stable release не должен требовать ручного удаления `.winstate`.

## Breaking changes

Breaking change допускается только при повышении major version или через заранее документированную staged migration. Release notes должны содержать:

- затронутые форматы;
- минимальную совместимую версию;
- способ создания backup;
- способ rollback;
- необратимые последствия, если они есть.

## Recovery

Перед восстановлением updater backup WinState создаёт safety backup текущей установки. Пользовательские каталоги `.winstate`, `profiles` и `logs` не копируются из старого package поверх актуальных данных и не включаются в замену release payload.

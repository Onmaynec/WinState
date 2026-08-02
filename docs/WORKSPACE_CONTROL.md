# 🧰 Workspace Control

Workspace Control в WinState 1.0 управляет пользовательским рабочим окружением как кодом:

- глобальными настройками Git;
- PowerShell modules в scope `CurrentUser`;
- управляемыми файлами;
- управляемыми каталогами;
- постоянным ownership ledger;
- планами, транзакциями, отчётами и откатом.

## Формат manifest

Workspace manifest использует JSON schema version 1:

```json
{
  "schemaVersion": 1,
  "name": "Рабочее окружение разработчика",
  "git": [
    {
      "key": "user.name",
      "value": "Developer",
      "state": "present"
    }
  ],
  "powerShellModules": [
    {
      "name": "Pester",
      "minimumVersion": "5.7.1",
      "repository": "PSGallery",
      "state": "present"
    }
  ],
  "directories": [
    {
      "path": "~/Workspace/tools",
      "state": "present"
    }
  ],
  "files": [
    {
      "path": "~/Workspace/tools/settings.txt",
      "state": "present",
      "encoding": "utf-8",
      "content": "managed by WinState\n"
    }
  ]
}
```

Для файла необходимо указать ровно одно из полей:

- `content` — встроенный UTF-8 текст;
- `source` — путь к исходному файлу относительно manifest.

Поддерживаются состояния `present` и `absent`.

## Команды

Проверка manifest:

```powershell
.\winstate.exe workspace validate .\workspace.json
```

Построение плана:

```powershell
.\winstate.exe workspace plan .\workspace.json --report .\reports
```

Применение после просмотра плана:

```powershell
.\winstate.exe workspace apply .\workspace.json --yes --report .\reports
```

Установка PowerShell modules требует отдельного разрешения:

```powershell
.\winstate.exe workspace apply .\workspace.json --yes --allow-modules
```

Управляемые удаления требуют отдельного разрешения:

```powershell
.\winstate.exe workspace apply .\workspace.json --yes --allow-delete
```

Состояние ownership:

```powershell
.\winstate.exe workspace status
```

Откат транзакции:

```powershell
.\winstate.exe workspace rollback .\.winstate\backups\workspace\<id>\transaction.json --yes
```

## Ownership ledger

Ledger хранится в:

```text
<WINSTATE_HOME>/ownership/workspace.json
```

WinState записывает туда точные идентификаторы ресурсов, созданных или принятых под управление.

Правила удаления:

- чужая Git-настройка не удаляется;
- чужой файл не удаляется;
- чужой каталог не удаляется;
- удаляется только пустой управляемый каталог;
- PowerShell modules автоматически не удаляются.

Повреждённый или созданный более новой версией ledger блокирует изменение системы. Старые поддерживаемые версии схемы мигрируются перед записью.

## Файлы и каталоги

Относительные пути вычисляются от каталога manifest. Поддерживаются:

- абсолютные пути;
- относительные пути;
- `%ENVIRONMENT_VARIABLES%`;
- `~` и `~/...` для домашнего каталога пользователя.

Перед заменой или удалением существующего файла создаётся backup в каталоге транзакции. Новое содержимое сначала записывается во временный файл, затем атомарно перемещается на целевой путь.

## Git configuration

Используется официальный `git config --global` без shell-конкатенации. Ключ проверяется до запуска процесса. При rollback восстанавливается исходное значение или удаляется настройка, которой до транзакции не было.

Для изоляции автоматизации можно использовать стандартную переменную Git:

```powershell
$env:GIT_CONFIG_GLOBAL = "$PWD\.isolated.gitconfig"
```

## PowerShell modules

WinState использует `Install-Module` со следующими ограничениями:

- scope всегда `CurrentUser`;
- установка требует `--allow-modules`;
- имя module проходит allowlist-проверку символов;
- `minimumVersion` проверяется как `System.Version`;
- uninstall не выполняется;
- установка или upgrade помечаются как необратимые в рамках автоматического rollback.

## Транзакции и откат

Transaction manifest сохраняется после каждого действия:

```text
<WINSTATE_HOME>/backups/workspace/<transaction-id>/transaction.json
```

Для обратимых действий сохраняются:

- исходное значение Git config;
- признак существования ресурса;
- backup заменяемого файла;
- исходное ownership-состояние.

При ошибке WinState автоматически откатывает уже выполненные обратимые действия в обратном порядке.

## Отчёты

Каждый `plan` и `apply` создаёт:

- JSON-отчёт для CI и автоматизации;
- Markdown-отчёт для ревью человеком.

Каталог по умолчанию:

```text
<WINSTATE_HOME>/reports/workspace
```

Его можно изменить через `--report <каталог>`.

## Безопасность

- plan не изменяет систему;
- apply требует `--yes`;
- module installation и deletion имеют независимые gates;
- неизвестные ресурсы не удаляются;
- непустые каталоги не удаляются;
- файлы записываются атомарно;
- rollback использует persisted transaction manifest;
- секреты не следует хранить в открытом `content` manifest.

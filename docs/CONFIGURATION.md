# ⚙️ Конфигурация WinState

Версия `0.2.0-alpha.1` добавляет вычисляемую конфигурацию приложения. Настройки загружаются из `winstate.json`, после чего безопасно переопределяются переменными окружения и параметром `--home`.

## Приоритет значений

1. встроенные значения по умолчанию;
2. `winstate.json` рядом с текущим рабочим каталогом или исполняемым файлом;
3. переменные окружения `WINSTATE_*`;
4. параметр CLI `--home`.

## Пример

```json
{
  "$schema": "./schemas/config.schema.json",
  "portable": false,
  "storage": {
    "database": "state/winstate.db"
  },
  "profiles": {
    "directory": "profiles"
  },
  "logging": {
    "directory": "logs",
    "minimumLevel": "Information"
  }
}
```

Относительные пути базы, профилей и логов разрешаются относительно домашнего каталога WinState.

## Переменные окружения

| Переменная | Назначение |
|---|---|
| `WINSTATE_HOME` | корневой каталог данных |
| `WINSTATE_PROFILES` | каталог профилей |
| `WINSTATE_DATABASE` | путь к SQLite |
| `WINSTATE_LOGS` | каталог логов |
| `WINSTATE_LOG_LEVEL` | минимальный уровень логирования |
| `WINSTATE_PORTABLE` | включает portable-режим |

## Команды

```powershell
winstate config show
winstate config path
winstate doctor --home .\.winstate-dev
```

Конфигурация не должна содержать токены, пароли и другие секреты.

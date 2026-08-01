# 🧩 Profile Engine

## Возможности

Profile Engine загружает YAML, разрешает связи между файлами, подставляет переменные и выдаёт нормализованную доменную модель.

```text
Read → Resolve references → Merge → Variables → Normalize → Validate
```

## Includes и extends

```yaml
extends:
  - base.yaml

includes:
  - team/environment.yaml
```

Пути вычисляются относительно файла, в котором они объявлены. Engine хранит список всех прочитанных файлов и отклоняет циклы.

Порядок наложения:

1. профили из `extends`;
2. профили из `includes`;
3. текущий файл.

Словари объединяются по ключам без учёта регистра. Значения более позднего слоя заменяют предыдущие. PATH entries объединяются и затем дедуплицируются.

## Переменные

Поддерживаются две формы:

```yaml
metadata:
  name: "{{developerName}} Workstation"

environment:
  user:
    DEV_MODE: "${mode}"
```

Приоритет значений:

1. `variables` из профиля;
2. переменные окружения `WINSTATE_VAR_<name>`;
3. аргументы CLI `--var name=value`.

Встроенные переменные:

- `profileFile` — полный путь корневого профиля;
- `profileDirectory` — каталог корневого профиля.

Неизвестная переменная приводит к ошибке загрузки. Значение не остаётся молча неразрешённым.

## Нормализация PATH

- относительные пути вычисляются от каталога корневого профиля;
- `%ENVIRONMENT%` разворачивается средствами ОС;
- Windows drive и UNC paths сохраняют Windows-семантику;
- завершающие разделители удаляются;
- дубликаты сравниваются без учёта регистра;
- `state` приводится к `present` / `absent`;
- `position` приводится к `prepend` / `append`.

## CLI

```powershell
winstate validate .\samples\profile-engine\workstation.yaml `
  --var developerName=Roman `
  --var mode=true
```

Результат содержит имя, schema version, количество исходных файлов, переменных, environment values и PATH entries.

## Ограничения

- conditions и expression language ещё не реализованы;
- секреты не подставляются из основного профиля;
- merge не выполняет удаление ключей специальным оператором;
- Profile Engine пока не вызывает системные провайдеры.

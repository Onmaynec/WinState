# 🖥️ WinState Control Center

## Назначение

Control Center превращает WinState из набора аргументов командной строки в самостоятельную консольную утилиту. Панель запускается, когда `winstate` вызван без аргументов.

```powershell
winstate
```

Для явного запуска:

```powershell
winstate ui
```

## Управление

- `↑` / `↓` — выбор раздела;
- `Enter` — открыть раздел;
- подтверждения `Y/N` — только перед изменением системы;
- любая клавиша — вернуться в предыдущее меню;
- `Ctrl+C` — отменить текущую операцию.

## Экраны

### System Overview

Показывает версию, платформу, архитектуру процесса, режим данных, количество найденных профилей, версию SQLite-схемы и рабочий каталог. В `0.4` дополнительно отображается готовность Environment Provider и количество обнаруженных variables/PATH entries.

### Profile Center

Сканирует `ProfilesDirectory`, показывает `.yaml` и `.yml`, загружает выбранный профиль через Profile Engine и отображает число источников, переменных, environment values и PATH entries.

### Environment Center

Первый экран, который может безопасно изменять Windows. Он содержит:

- **План и применение** — выбор профиля, discovery, diff, risk table, подтверждение, checkpoint, apply и verify;
- **Текущее состояние** — количество User/Machine variables и PATH entries;
- **Rollback checkpoint** — выбор сохранённой транзакции и восстановление в обратном порядке.

Перед `apply` панель всегда показывает таблицу:

```text
Risk | Scope | Operation | Resource | Explanation
```

User scope требует одного подтверждения. Если план содержит Machine scope, появляется отдельное красное подтверждение elevated-операций. При ошибке application workflow запускает автоматический rollback и показывает итог каждого действия.

### Doctor

Запускает те же прикладные проверки, что и `winstate doctor`, но показывает их в отдельной таблице с цветными статусами и анимацией.

### Storage Center

Применяет ожидающие миграции и показывает путь базы, версию схемы, размер и список пользовательских таблиц. Environment Provider записывает сюда transaction/action history и backup references.

### Configuration

Отображает вычисленные `Home`, `Profiles`, `Database`, `Logs`, `Config`, portable mode и log level.

### Architecture & Roadmap

Показывает цепочку `Terminal → App workflows → Core → Providers/Storage` и следующий этап разработки.

## Анимации операций

Spinner используется только вокруг действий, для которых действительно нужно ожидание:

- запуск модулей;
- discovery и построение diff;
- Profile Engine;
- checkpoint/apply/verify;
- rollback;
- Doctor;
- SQLite migrations.

Анимация не заменяет результат: после неё всегда отображается отдельная таблица с фактическим статусом.

## Неинтерактивный режим

Для CI существует безопасный снимок панели, который не ждёт нажатия клавиш:

```powershell
winstate ui --demo --home .\.ci-winstate
```

Обычные команды `doctor`, `validate`, `environment`, `config` и `storage` сохранены. Это позволяет использовать WinState в скриптах, тестах и автоматизации.

## Визуальный стиль

- голубой акцент для навигации и структуры;
- зелёный для успешного состояния;
- жёлтый для предупреждений, Medium risk и roadmap;
- красный для ошибок и Machine confirmation;
- закруглённые панели;
- большой символьный логотип `WINSTATE`;
- короткие spinner-анимации только вокруг реальных операций;
- единая status line с версией и активными safeguards.

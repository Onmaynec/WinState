# 🖥️ WinState Terminal UI

## Текущий frontend

Начиная с `0.5.0-alpha.1`, основной интерактивный интерфейс — **Cyber Control Center**.

Полное руководство: [`CYBER_CONTROL_CENTER.md`](CYBER_CONTROL_CENTER.md).

```powershell
winstate
```

Для явного запуска:

```powershell
winstate ui
```

## Управление

- `↑` / `↓` — выбор operation channel;
- `Enter` — открыть выбранный канал;
- подтверждения `Y/N` — только перед системной операцией;
- любая клавиша — вернуться в предыдущий экран;
- `Ctrl+C` — отменить текущий workflow.

## Presentation contract

Terminal-проект отвечает только за:

- layout и визуальную иерархию;
- menu navigation;
- boot/shutdown traces;
- progress-анимации;
- отображение plan, telemetry и action results;
- интерактивные подтверждения;
- безопасный demo mode.

Terminal-проект не должен:

- напрямую вызывать Windows API;
- выполнять SQL;
- изменять environment variables или PATH;
- создавать rollback payload самостоятельно;
- обходить `WinStateApplication`;
- объявлять успех до получения verification result.

## Cyber channels

```text
[01] CONTROL NODE
[02] PROFILE VAULT
[03] ENVIRONMENT OPS
[04] CHECKPOINT VAULT
[05] DEEP SCAN
[06] DATA CORE
[07] NODE CONFIG
[08] SYSTEM MAP
[00] DISCONNECT
```

## Анимации

Основной pipeline:

```text
handshake → operation → seal result
```

Анимация оборачивает реальную async-операцию. Средняя стадия завершается только после возврата результата из application layer.

Transaction trace строится по `EnvironmentExecutionReport.Actions`, поэтому каждый `PASS`, `VERIFY-FAIL`, `ROLLBACK` или `ROLLBACK-FAIL` соответствует реальному статусу provider action.

## Цветовой язык

- зелёный — online, verified, safe channel;
- тёмно-зелёный — структура, рамки, вторичные данные;
- белый — активные значения;
- серый — trace и пояснения;
- жёлтый — warning, Medium risk, limited platform;
- красный — failure и Machine confirmation.

## Demo mode

```powershell
winstate ui --demo --home .\.ci-winstate
```

Demo mode:

- не ожидает ввода;
- отключает искусственные задержки;
- не выполняет apply/rollback;
- рендерит Control Node snapshot;
- используется CI на Ubuntu и Windows.

CLI-команды `doctor`, `validate`, `environment`, `config` и `storage` остаются отдельным automation frontend и не зависят от интерактивного rendering.

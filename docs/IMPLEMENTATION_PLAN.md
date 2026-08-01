# 🗺️ План реализации

## ✅ Этап 1 — архитектурное ядро

- solution и границы проектов;
- доменные модели;
- формат профиля и схема;
- provider contracts;
- transaction model;
- dependency graph;
- bootstrap CLI;
- документация и CI.

## Этап 2 — полный Profile Engine

- полноценный YAML parser;
- includes и extends;
- переменные и secret references;
- конфликт-детектор;
- нормализация путей;
- privacy filters.

## Этап 3 — Environment vertical slice

- discovery user/machine environment;
- отдельная модель PATH;
- diff и plan;
- apply и verify;
- checkpoint и rollback;
- интеграционные тесты на Windows runner.

## Этап 4 — Apply Engine

- подтверждение плана;
- risk policy;
- DAG scheduler;
- cancellation;
- transaction persistence;
- policies `stop`, `continue`, `rollback`.

## Этап 5 — основные провайдеры

Packages/WinGet, Windows Features, Services, Registry, Git Config, PowerShell Modules и Files.

## Этап 6 — продуктовый слой

SQLite, capture, drift, JSON/HTML reports, TUI, RU/EN, portable mode и подписываемые релизы.

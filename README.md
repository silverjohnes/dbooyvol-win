# DBooyvol для Windows

Windows-порт SQL-клиента [DBooyvol](https://github.com/silverjohnes/dbooyvol-mac).
Цель — полный паритет функций с macOS-версией при нативной скорости WPF и
горячих клавишах DBeaver для Windows.

## Что уже есть

- **Подключения**: PostgreSQL, Oracle (EZ Connect, TNS, LDAP), ClickHouse, SQLite;
  SSH-туннель; произвольный JDBC URL и путь к драйверу.
- **Дерево объектов**: ленивая загрузка схем, таблиц, представлений; F5, F4,
  контекстное меню.
- **Редактор SQL**: AvalonEdit, подсветка, автодополнение (Ctrl+Space),
  форматирование (Ctrl+Shift+F), комментарии (Ctrl+/ и Ctrl+Alt+/).
- **Выполнение**: Ctrl+Enter — выделение или statement под курсором;
  Alt+X — весь скрипт; отмена Ctrl+Shift+C; параметры `:name` через диалог.
- **Результаты**: постраничная загрузка (Ctrl+Alt+N / Ctrl+Shift+=),
  сортировка, копирование 9 форматов, экспорт CSV/XML/XLSX.
- **Редактирование по PK**: изменение, добавление, дублирование и удаление строк,
  Ctrl+S — применить; значения остаются строками от драйвера до записи.
- **Одиночная строка**: Tab переключает таблицу и вертикальный просмотр.
- **История запросов**, восстановление вкладок, 22 языка, настраиваемые
  сочетания клавиш.
- **Секреты**: DPAPI (CurrentUser) + AES-256-GCM, подтверждение Windows Hello
  один раз за запуск.

Полный поведенческий контракт — `docs/WINDOWS-HANDOFF.md`.
Разбор решений и отличий от macOS — `docs/WINDOWS-PORT-OPTIONS.md`.
Устройство форматтера — `docs/SQL-FORMATTER.md`.

## Архитектура

```
DBooyvol.exe (WPF, .NET 9)
   │  NDJSON по stdio
   ▼
sidecar.jar (Java 11+, неизменённый движок macOS-версии)
   │  JDBC
   ▼
PostgreSQL / Oracle / ClickHouse / SQLite
```

- `src/DBooyvol.Core` — модели, хранилища, протокол, SQL, экспорт, сочетания
  клавиш. Покрыт xUnit-тестами (60 тестов).
- `src/DBooyvol.App` — WPF-оболочка: окна, ViewModel, AvalonEdit, DataGrid.
- `sidecar/` — Java-исходники движка; собираются в `sidecar/build/sidecar.jar`.
- `drivers/` — JDBC-драйверы и `sidecar.jar` из macOS-поставки.
- `resources/locales/` — 22 языковых пакета.

## Сборка

Требуется .NET 9 SDK и JDK 11+.

```powershell
# 1. Движок
powershell -ExecutionPolicy Bypass -File sidecar\build.ps1

# 2. Тесты
dotnet test DBooyvol.sln

# 3. Запуск из исходников
dotnet run --project src\DBooyvol.App

# 4. Публикация (unpackaged, self-contained)
dotnet publish src\DBooyvol.App -c Release -r win-x64 --self-contained -o publish

# 5. Встроенная Java (на Windows)
powershell -ExecutionPolicy Bypass -File runtime\make-jlink.ps1

# 6. Установщик (Inno Setup)
iscc installer\DBooyvol.iss /DPublishDir=publish
```

На macOS/Linux приложение **собирается** благодаря
`EnableWindowsTargeting`, но **не запускается** — WPF требует Windows.
CI `.github/workflows/build.yml` собирает, тестирует и упаковывает на
`windows-latest`.

## Горячие клавиши

Сочетания соответствуют DBeaver для Windows и читаются по виртуальному коду
клавиши, поэтому русская раскладка их не ломает.

| Действие | Клавиши |
|---|---|
| Выполнить выделение / statement | `Ctrl+Enter` |
| Выполнить весь скрипт | `Alt+X` |
| Отменить запрос | `Ctrl+Shift+C` |
| Форматировать SQL | `Ctrl+Shift+F` |
| Комментировать | `Ctrl+/` |
| Автодополнение | `Ctrl+Space` |
| Новый запрос | `Ctrl+T` |
| Закрыть вкладку | `Ctrl+F4` |
| Найти | `Ctrl+F` |
| Сохранить / применить изменения | `Ctrl+S` |
| Загрузить ещё / все | `Ctrl+Alt+N` / `Ctrl+Shift+=` |
| Экспорт CSV / XML / XLSX | `Ctrl+Alt+1/2/3` |
| Таблица ↔ строка | `Tab` |
| Копировать значение сверху | `Ctrl+D` |
| Подключиться / отключиться | `Alt+C` / `Alt+D` |
| Обновить дерево | `F5` |
| История | `Alt+2` |
| Настройки сочетаний | `F1` |

Все действия переназначаются в «Вид → Горячие клавиши», кроме `Tab`.

## Секреты

1. Мастер-ключ создаётся один раз и шифруется DPAPI в контексте пользователя.
2. Пароли — AES-256-GCM, конверт `enc2:<keyid>:<base64>`.
3. Перед первым чтением за запуск запрашивается Windows Hello / PIN.
4. Ошибка доступа **не** считается отсутствием ключа: приложение не создаёт
   новый ключ и не затирает `secrets.json`.
5. Ротация и удаление — явные операции; «осиротевшие» записи видны в
   диагностике.

## Проверено

- `dotnet build` решения — успешно (кросс-сборка WPF).
- `dotnet test` — 60/60.
- Форматтер: 10 представительных запросов + идемпотентность.
- Требует проверки на Windows: запуск окна, реальные JDBC-подключения,
  Windows Hello, DPI, установщик и подпись.

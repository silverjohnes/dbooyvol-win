# Передача проекта: DBooyvol для Windows

Документ для того, кто продолжит работу. Здесь: что сделано, как собрать,
как устроен код, что осталось, и где лежат все решения.

Дата: 27.11.2026. Состояние: `main`, сборка и тесты зелёные, WPF вживую
**не проверялся** (разработка шла на macOS).

---

## 1. Коротко

- Это порт macOS-клиента **DBooyvol** на Windows: WPF + .NET 9, движок —
  тот же Java-sidecar, что и в оригинале, общение по NDJSON через stdio.
- Оригинал: <https://github.com/silverjohnes/dbooyvol-mac>
- Порт: <https://github.com/silverjohnes/dbooyvol-win>
- Цель: полный паритет функций, горячие клавиши DBeaver для Windows,
  нативная скорость и надёжность, аккуратный «маковский» вид.
- Локальная сборка возможна на macOS (`EnableWindowsTargeting`), запуск —
  только на Windows.

Проверено локально:

```
dotnet build DBooyvol.sln   -> 0 ошибок, 0 предупреждений
Core-тесты                  -> 60/60
SAST-тесты                  -> 30/30
```

---

## 2. Как забрать и собрать

```bash
git clone https://github.com/silverjohnes/dbooyvol-win.git
cd dbooyvol-win
```

На Windows (нужен .NET 9 SDK и JDK 11+):

```powershell
powershell -ExecutionPolicy Bypass -File sidecar\build.ps1
dotnet test DBooyvol.sln
dotnet run --project src\DBooyvol.App
```

Публикация и установщик:

```powershell
dotnet publish src\DBooyvol.App -c Release -r win-x64 --self-contained -o publish
powershell -ExecutionPolicy Bypass -File runtime\make-jlink.ps1   # встроенная Java
iscc installer\DBooyvol.iss /DPublishDir=publish
```

На macOS (только сборка, без запуска):

```bash
export PATH="$HOME/.dotnet:$PATH"
./sidecar/build.sh
dotnet build DBooyvol.sln
dotnet test DBooyvol.sln
```

CI: `.github/workflows/build.yml` — build + тесты + упаковка артефакта
`DBooyvol-win-x64` на `windows-latest`. Пуш изменений в workflow требует
PAT со scope `workflow` (обычный пуш workflow-файла отклоняется).

---

## 3. Архитектура

```
DBooyvol.exe (WPF, .NET 9)
   │  NDJSON по stdin/stdout
   ▼
sidecar.jar (Java 11+, движок из macOS-версии)
   │  JDBC
   ▼
PostgreSQL / Oracle / ClickHouse / SQLite
```

| Каталог | Что внутри |
|---|---|
| `src/DBooyvol.Core` | модели, протокол, сессии, SQL-парсер/форматтер, экспорт, история, секреты, сочетания клавиш |
| `src/DBooyvol.App` | WPF: окна, ViewModel, темы, AvalonEdit, DataGrid |
| `tests/DBooyvol.Core.Tests` | 60 модульных тестов |
| `tests/DBooyvol.Security.Tests` | SAST-гейт (30 тестов), см. `docs/SAST.md` |
| `sidecar/` | Java-исходники движка, сборка в `sidecar/build/sidecar.jar` |
| `drivers/` | JDBC-драйверы и `sidecar.jar` из macOS-поставки |
| `resources/locales/` | 22 языковых пакета |
| `installer/`, `runtime/` | Inno Setup и jlink-runtime |

Ключевые инварианты, которые нельзя ломать:

1. **Значения — строки от драйвера до записи.** `null` = SQL NULL,
   `""` = пустая строка. Никакого парсинга чисел/дат «по дороге».
2. **Точность и часовые пояса** — форматирование только на отображении,
   `TimestampDisplay` / `TimeZones` в Core.
3. **SAST** — новые опасные места (запуск процессов, SQL-конкатенация,
   XML/XLSX, секреты) должны проходить `DBooyvol.Security.Tests` или иметь
   комментарий `sast:allow DBYxxx` с объяснением.
4. **Драйверы** передаются в JVM как `-Ddbooyvol.drivers=<каталог>`;
   sidecar ищет там, затем в `drivers/` рядом с jar, затем рядом с jar.
5. **SSH-туннель** поднимается в `Session.OpenAsync`, host/port (и явный
   JDBC URL) переписываются на `127.0.0.1:<localPort>`, туннель закрывается
   в `DisposeAsync`. Для Oracle TNS/LDAP + SSH — явная ошибка (нужен EZ Connect).

---

## 4. Что уже работает

- Подключения: PostgreSQL, Oracle (EZ Connect / TNS / LDAP), ClickHouse,
  SQLite; SSH-туннель; произвольный JDBC URL и путь к драйверу.
- Кнопка **«Тест»** в диалоге подключения: проверяет текущие значения формы,
  ничего не сохраняет, пароль спрашивает только если он не сохранён,
  сессию и туннель сразу закрывает.
- Дерево объектов: ленивая загрузка схем/таблиц/представлений, F5, F4,
  контекстное меню с подсказками сочетаний, правый клик выделяет узел.
- Редактор: AvalonEdit, подсветка SQL в цветах оригинала, автодополнение
  (Ctrl+Space), форматирование (Ctrl+Shift+F), комментарии (Ctrl+/,
  Ctrl+Alt+/), переход по statement (Alt+↑/↓).
- Выполнение: Ctrl+Enter, Alt+X, отмена Ctrl+Shift+C, параметры `:name`.
- Результаты: постраничная загрузка, сортировка, копирование в 9 форматов,
  экспорт CSV/XML/XLSX, редактирование по PK, одиночная строка (Tab).
- История запросов, восстановление вкладок, 22 языка, переназначаемые
  сочетания клавиш.
- Секреты: DPAPI (CurrentUser) + AES-256-GCM (`enc2:<keyid>:<base64>`),
  Windows Hello один раз за запуск.

Подробный поведенческий контракт — `docs/WINDOWS-HANDOFF.md`.
Разбор решений и отличий от macOS — `docs/WINDOWS-PORT-OPTIONS.md`.
Форматтер — `docs/SQL-FORMATTER.md`. SAST — `docs/SAST.md`.

---

## 5. Тема оформления (в работе)

Начата замена «стандартного WPF» на собственный вид, повторяющий оригинал.
Сделано и собирается:

| Файл | Роль |
|---|---|
| `src/DBooyvol.App/Themes/Light.xaml` | светлая палитра, все ключи |
| `src/DBooyvol.App/Themes/Dark.xaml` | тёмная палитра, те же ключи |
| `src/DBooyvol.App/Themes/Controls.xaml` | все стили контролов, только `DynamicResource` |
| `src/DBooyvol.App/Services/ThemeService.cs` | выбор Light/Dark, слежение за системной темой |
| `src/DBooyvol.App/App.xaml` | подключает `Controls.xaml`; палитру вставляет ThemeService |
| `src/DBooyvol.App/Views/MainWindow.xaml` | новый каркас: кастомный title bar, меню в нём, chip-вкладки, sidebar, нижняя полоса |
| `src/DBooyvol.App/Views/MainWindow.xaml.cs` | кнопки окна, перетаскивание, Snap Layouts через `WM_NCHITTEST`, выравнивание чисел, NULL-стиль |
| `src/DBooyvol.App/Controls/SqlEditor.cs` | цвета редактора и SQL-подсветка из палитры, перестройка при смене темы |
| `src/DBooyvol.Core/Storage/AppSettings.cs` | настройка `Theme`: `auto` / `light` / `dark` |
| `src/DBooyvol.App/Views/SettingsWindow.xaml(.cs)` | переключатель темы с живым предпросмотром |

Токены (Light → Dark):

| Роль | Light | Dark |
|---|---|---|
| Контент | `#FFFFFF` | `#1E1E1E` |
| Sidebar | `#F5F5F7` | `#232325` |
| Chrome / title bar | `#F3F3F5` | `#252527` |
| Разделитель | `#E2E2E6` | `#3A3A3C` |
| Текст | `#1D1D1F` | `#F5F5F7` |
| Вторичный | `#6E6E73` | `#98989D` |
| Акцент | `#0A84FF` | `#0A84FF` |
| Строка сетки | 22 px | 22 px |
| Заголовок колонки | 24 px | 24 px |
| Вкладка | 28 px, радиус 6 | то же |

Как менять: правьте `Light.xaml`/`Dark.xaml` (ключи должны совпадать
один-в-один) и `Controls.xaml`. В XAML используйте только
`{DynamicResource ...}` — `StaticResource` не переживёт смену темы.
В коде — `ThemeService.Brush("Brush.Accent")`, `ThemeService.Color(...)`.

### Что в теме осталось

1. **Диалоги** (`ConnectionDialog`, `SettingsWindow`, `ShortcutsWindow`,
   `AboutWindow`, диалог параметров) — берут цвета и шрифт из неявного стиля
   `Window`, но заголовок у них ещё нативный. Для единообразия нужен тот же
   `WindowChrome`, что в `MainWindow`.
2. **Перетаскивание вкладок** — в оригинале есть drag-and-drop с акцентной
   чертой; в WPF пока нет.
3. **Точка состояния запроса** на вкладке (серая пульсация / зелёная /
   красная) — в модели уже есть состояние выполнения, в шаблоне вкладки
   пока только маркер несохранённости.
4. **Пустые состояния** — добавлено для дерева; для панели результатов,
   истории и поиска пока нет.
5. **Анимации** 120–180 ms на hover/появление панелей; отключать при
   `SystemParameters.ClientAreaAnimation == false`.
6. **High contrast** — отдельная ветка, сейчас не обрабатывается.
7. **`ScrollBar`** стилизован только вертикальный/горизонтальный трек;
   кнопки-стрелки убраны намеренно.
8. **`MessageBox`** остался системным. Если нужен единый вид — свой диалог.
9. Проверить на Windows: тень попапов, DPI 125/150/200 %, Snap Layouts,
   поведение при разворачивании перетаскиванием, `Segoe Fluent Icons` на Win10
   (fallback `Segoe MDL2 Assets` прописан).

---

## 6. Горячие клавиши

Каталог — `src/DBooyvol.Core/Shortcuts/Shortcuts.cs`, отображение —
`docs/WINDOWS-HANDOFF.md` §клавиши. Сочетания соответствуют DBeaver для
Windows, а не механическому `⌘ → Ctrl`. Русская раскладка учитывается
(сопоставление по виртуальным клавишам).

Последние изменения:

- `connection.change` = **F9** (было Ctrl+9), область `Tree`;
- `connection.rename` = **F2**, область `Tree` (пункт «Переименовать…»);
- `object.open` = F4 остался, но пункт «Открыть» из контекстного меню убран —
  открытие по двойному клику и F4;
- контекстное меню дерева показывает актуальные сочетания через
  `ApplyGestures(TreeMenu)` (учитывает пользовательские переназначения).

---

## 7. Что нельзя проверить на macOS

Это главный список для проверки на Windows:

- запуск окна, кастомный title bar, кнопки, Snap Layouts, DPI;
- кнопка «Тест» в диалоге подключения;
- реальное подключение к PostgreSQL/Oracle/ClickHouse/SQLite;
- SSH-туннель (`ssh -N -L`);
- Windows Hello и чтение/запись секретов;
- экспорт XLSX и открытие файла в Excel;
- установщик Inno Setup и подпись `signtool`;
- тёмная/светлая тема, переключение на лету, системная тема;
- поведение `DataGrid` на больших выборках (виртуализация включена).

---

## 8. Известные ограничения и долги

- **Oracle TNS/LDAP + SSH** не поддерживается — выдаётся понятная ошибка.
- **Пустой `ConnectionStatus`** в нижней полосе — статичная зелёная точка;
  стоит привязать цвет к состоянию (`Brush.Success` / `Brush.Danger`).
- **`ResultsGrid`**: NULL показывается пустым с серым курсивом через
  `DBNull`-триггер; пустая строка остаётся пустой. Проверить, что
  редактирование ячейки не теряет `DBNull` (Core это гарантирует, UI — нет).
- **`connection.rename`** есть в `MainViewModel`, но пункта в главном меню
  «Подключение» нет — только контекстное меню и F2.
- **`ThemeService`** подписан на `SystemEvents.UserPreferenceChanged` на всё
  время жизни процесса; отписки нет (процесс один).
- **Артефакт** `artifacts/DBooyvol-1.0.0-win-x64.zip` собран до темы —
  пересобрать на Windows.
- **CI-воркфлоу** правится через веб GitHub (см. §2).

---

## 9. Карта файлов

```
src/DBooyvol.App/
  App.xaml, App.xaml.cs           точка входа, тема, sidecar
  Services/ThemeService.cs        палитра, системная тема
  Themes/{Light,Dark,Controls}.xaml
  Views/MainWindow.xaml(.cs)      каркас, меню, дерево, вкладки, сетка
  Views/ConnectionDialog.xaml(.cs) подключение + кнопка «Тест»
  Views/SettingsWindow.xaml(.cs)  настройки, тема, язык, часовой пояс
  Views/ShortcutsWindow.xaml(.cs) редактор сочетаний
  Controls/SqlEditor.cs           AvalonEdit: подсветка, автодополнение, тема
  ViewModels/MainViewModel.cs     действия, диалоги, тест соединения
  ViewModels/QueryTabViewModel.cs вкладка: результат, статус, строки

src/DBooyvol.Core/
  Db/Session.cs                   подключение, SSH, запросы, fetch
  Protocol/SidecarClient.cs       запуск JVM, NDJSON, -Ddbooyvol.drivers
  Shortcuts/Shortcuts.cs          каталог и парсинг сочетаний
  Storage/AppSettings.cs          настройки, включая Theme
  Storage/SecretStore.cs          DPAPI + AES-GCM
  Export/, Sql/, History/, Text/  экспорт, парсер, форматтер, история

sidecar/src/main/java/silver/dbooyvol/Engine.java   поиск драйверов, JDBC
docs/{WINDOWS-HANDOFF,WINDOWS-PORT-OPTIONS,SAST,SQL-FORMATTER}.md
```

---

## 10. С чего продолжить

1. Собрать и запустить на Windows, пройти чеклист §7.
2. Довести тему в диалогах (общий `WindowChrome`), drag-and-drop вкладок,
   индикатор выполнения на вкладке.
3. Пересобрать артефакт/установщик, проверить подпись.
4. Закрыть долги из §8.

# SAST-гейт

`tests/DBooyvol.Security.Tests` — статический анализ исходников, который
запускается как обычный `dotnet test` и блокирует CI при находках.

Проверяются технологии, из которых реально состоит продукт:

- **WPF / .NET 9** — запуск процессов, DPAPI, криптография, XML, десериализация;
- **Java-sidecar** — JDBC, `PreparedStatement`, `TrustManager`, десериализация;
- **OpenSSH** — проверка host key;
- **GitHub Actions** — секреты в `run`, опасные триггеры;
- **Inno Setup / PowerShell / bash** — приватные ключи, `curl | bash`.

## Запуск

```bash
dotnet test tests/DBooyvol.Security.Tests/DBooyvol.Security.Tests.csproj
```

В CI шаг называется `Test security (SAST)`.

## Правила

| ID | Что ищет |
|---|---|
| DBY001 | захардкоженные пароли/токены |
| DBY002 | MD5, SHA-1, DES, RC4, ECB |
| DBY003 | `BinaryFormatter`, `ObjectInputStream`, небезопасный `TypeNameHandling` |
| DBY004 | XXE: `DtdProcessing.Parse`, внешний `XmlResolver` |
| DBY005 | `UseShellExecute = true`, `Runtime.exec`, shell-строка в `ProcessBuilder` |
| DBY006 | аргументы процесса строкой вместо `ArgumentList` |
| DBY007 | отключение проверки TLS-сертификата или hostname |
| DBY008 | `Random` для nonce/ключа |
| DBY009 | SQL интерполяцией/конкатенацией без параметров |
| DBY010 | пароль/токен в логе или консоли |
| DBY011 | DPAPI `LocalMachine` вместо `CurrentUser` |
| DBY012 | `StrictHostKeyChecking=no`, пустой `known_hosts` |
| DBY013 | пустой Java `TrustManager` |
| DBY014 | приватный ключ в репозитории |
| DBY015 | `${{ secrets.* }}` прямо в `run:` |
| DBY016 | `pull_request_target` |
| DBY017 | `curl ... | bash` |

Дополнительно тесты проверяют инварианты, которые нельзя выразить одной
строкой:

- секреты шифруются AES-GCM, мастер-ключ — DPAPI `CurrentUser`;
- sidecar запускается без shell и через `ArgumentList`;
- SSH использует `StrictHostKeyChecking=accept-new`;
- JDBC-записи идут через `prepareStatement`, идентификаторы экранируются,
  транзакция с `rollback`;
- в репозитории нет `.pfx`, `.p12`, `.pem`, `.key`, `.env`, `id_rsa`;
- `.gitignore` закрывает секреты и build-output;
- нет запрещённых зависимостей (`Newtonsoft.Json`, `System.Data.SqlClient`,
  `log4j-core`, `commons-collections`);
- CI действительно запускает этот набор.

## Подавление

Если динамический SQL или иное срабатывание осознанно, ставится комментарий
непосредственно перед строкой (допускается одна строка пояснения между ними):

```csharp
// sast:allow DBY009 — это текст для буфера обмена, приложение его не исполняет.
lines.Add($"INSERT INTO {table} ...");
```

Подавление без объяснения причины не приветствуется: оно видно в diff и
ревьюится вместе с кодом.

## Ограничения

Это line-oriented правила, а не полноценный dataflow-анализатор. Они не
заменяют Roslyn-анализаторы и SCA-сканирование зависимостей, но закрывают
специфичные для DBooyvol риски: динамические идентификаторы в JDBC, запуск
Java/SSH, хранение паролей и упаковку. Правила проверяются собственными
positive/negative-примерами, поэтому «зелёный» прогон не является пустым.

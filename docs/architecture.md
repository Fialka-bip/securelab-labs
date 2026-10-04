# Карта архітектури

Карта доповнена власним трасуванням запиту (перегляд деталей інциденту та
реалізований підсумок за severity), конкретними файлами та спостереженнями
з DevTools і журналу PostgreSQL під час виконання ЛР 1.

## Компоненти

| Компонент | Розташування | Відповідальність |
|---|---|---|
| Browser client | `src/SecureLab.Api/Client/` | Надсилає HTTP-запити, безпечно показує відповідь через DOM API |
| Presentation | `Presentation/` | Описує endpoints, читає зовнішні параметри, формує HTTP-відповідь |
| Application | `Application/` | Виконує сценарій отримання списку, деталей інциденту або підсумку за severity |
| Data | `Data/` | Відображає C#-сутності на PostgreSQL через EF Core/Npgsql |
| PostgreSQL | `infra/compose.yaml` | Зберігає навчальні дані у локальному контейнері |

## Досліджений маршрут: деталі інциденту

```text
натискання картки інциденту у Client/app.js
  → loadIncidentDetails(id)
  → GET /api/incidents/{id}
  → Presentation/Endpoints/IncidentEndpoints.cs → GetDetailsAsync
  → Application/Incidents/IncidentQueries.cs → GetDetailsAsync
  → Data/SecureLabDbContext.cs → DbSet<Incident> Incidents
  → PostgreSQL: таблиця incidents
  → Presentation/Contracts/IncidentResponses.cs → IncidentDetailsResponse
  → JSON
  → renderIncidentDetails (textContent / document.createTextNode)
```

Гілка відсутнього ресурсу: синтаксично коректний, але неіснуючий UUID
проходить маршрутне обмеження `:guid`, query повертає `null`, і
`IncidentEndpoints.GetDetailsAsync` формує 404 Problem Details з власним
`traceId`.

## Реалізований маршрут: підсумок за severity

```text
натискання кнопки «Показати підсумок» у Client/app.js (summaryButton)
  → loadSeveritySummary()
  → GET /api/incidents/severity-summary
  → Presentation/Endpoints/IncidentEndpoints.cs → GetSeveritySummaryAsync
  → Application/Incidents/IncidentQueries.cs → GetSeveritySummaryAsync
  → Data/SecureLabDbContext.cs → DbSet<Incident> Incidents (GroupBy за Severity)
  → PostgreSQL: таблиця incidents
  → Presentation/Contracts/IncidentResponses.cs → IncidentSeveritySummaryResponse
  → JSON
  → renderSeveritySummary (textContent)
```

Endpoint замінив baseline-заглушку, що повертала `501 Not Implemented`.
Query виконує `AsNoTracking()`, групує інциденти через `GroupBy`, рахує
кожну групу через `Count()` і виконує запит через `ToListAsync`. Застосовано
політику повного переліку рівнів: відсутні в даних severity (наприклад
`Critical`, якщо жоден інцидент не має такого рівня) додаються до відповіді
з `count: 0`, а порядок елементів фіксований за критичністю
(`Critical, High, Medium, Low`), а не лексикографічно, як вийшло б із
прямого SQL-сортування текстового поля `Severity`.

## Межі довіри

| Межа | Чому даним ще не можна довіряти | Де перевіряємо або обмежуємо |
|---|---|---|
| Користувач → Browser client | Користувач контролює введення (значення `<select>`, натискання кнопок) | Клієнт лише формує зручний запит; це не серверний контроль |
| Browser client → API | Клієнт і сам HTTP-запит можна змінити поза UI (curl, інший клієнт, Console) | Маршрутне обмеження `:guid` для `id`; серверна перевірка `status` через `Enum.TryParse`/`Enum.IsDefined` з відповіддю 400 для некоректного значення |
| API → PostgreSQL | Параметри запиту не можна напряму підставляти в SQL; збережений текст не є автоматично безпечним | Параметризовані запити EF Core, `AsNoTracking()` для read-only сценаріїв (деталі, список, підсумок), явна проєкція полів у DTO замість повернення entity |
| PostgreSQL → API → DOM | У БД може зберігатися раніше введений недовірений текст (наприклад, `description` із буквальним `<script>` у seed-даних) | Response DTO (`IncidentDetailsResponse`, `IncidentSeveritySummaryResponse`) обмежує набір полів; виведення в DOM лише через `textContent`/`document.createTextNode`, без `innerHTML` |
| Конфігурація → API | Локальна конфігурація (`appsettings.Development.json`, `infra/compose.yaml`) не придатна для іншого середовища | Навчальні credentials прив'язані до `127.0.0.1`; реальний connection string передається через змінну середовища `ConnectionStrings__SecureLab`, а `infra/.env` виключено з Git через `.gitignore` |

## Конфігураційні входи

- `global.json` — версія .NET SDK (смуга 10.0.3xx, політика `rollForward: latestPatch`);
- `src/SecureLab.Api/appsettings*.json` — режим міграцій і локальний connection string;
- `infra/compose.yaml` — версія PostgreSQL, порт і локальні навчальні облікові дані;
- змінна середовища `ConnectionStrings__SecureLab` — безпечний спосіб перевизначити connection string поза репозиторієм.

## Повернення до відомого стану

Команда `dotnet run --no-build --project src/SecureLab.Api -- --reset-database`
застосовує наявні міграції, очищує лише відомі навчальні таблиці та повторно
заповнює їх seed-значеннями. Команда працює лише в явно налаштованому
Development-середовищі й не видаляє саму базу даних чи її схему.
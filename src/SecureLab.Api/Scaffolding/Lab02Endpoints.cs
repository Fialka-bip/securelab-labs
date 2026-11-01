using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

public static class Lab02Endpoints
{
    public static void MapLab02Endpoints(this WebApplication app)
    {
        // Безпечний пошук інцидентів
        app.MapGet("/api/incidents/search", async (
            string? q,
            string? sortBy,
            SecureLabDbContext db,
            CancellationToken ct) =>
        {
            var allowedSorts = new[]
            {
                "createdAtUtc",
                "severity",
                "status"
            };

            if (!string.IsNullOrEmpty(sortBy) &&
                !allowedSorts.Contains(sortBy))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["sortBy"] = ["Недопустиме поле сортування."]
                    });
            }

            var query = db.Incidents.AsNoTracking();

            // Пошук із екрануванням спеціальних символів LIKE
            if (!string.IsNullOrEmpty(q))
            {
                var escaped = q
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");

                var pattern = $"%{escaped}%";

                query = query.Where(x =>
                    EF.Functions.ILike(x.Title, pattern, "\\") ||
                    EF.Functions.ILike(x.Description, pattern, "\\"));
            }

            // Сортування лише за дозволеними полями
            query = sortBy switch
            {
                "severity" => query
                    .OrderBy(x => x.Severity == IncidentSeverity.Critical ? 0 :
                                  x.Severity == IncidentSeverity.High ? 1 :
                                  x.Severity == IncidentSeverity.Medium ? 2 : 3)
                    .ThenBy(x => x.Id),

                "status" => query
                    .OrderBy(x => x.Status == IncidentStatus.New ? 0 :
                                  x.Status == IncidentStatus.Triaged ? 1 :
                                  x.Status == IncidentStatus.InProgress ? 2 :
                                  x.Status == IncidentStatus.Resolved ? 3 : 4)
                    .ThenBy(x => x.Id),

                _ => query
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ThenBy(x => x.Id)
            };

            var rows = await query
                .Take(50)
                .ToListAsync(ct);

            return Results.Ok(rows.Select(row => new
            {
                row.Id,
                row.Title,
                row.Description,
                Severity = row.Severity.ToString(),
                Status = row.Status.ToString(),
                row.CreatedAtUtc
            }));
        });

        // Створення інциденту з серверною валідацією
        app.MapPost("/api/incidents", HandleCreateIncident);
    }

    private static async Task<IResult> HandleCreateIncident(
        CreateIncidentRequest request,
        SecureLabDbContext db,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var errors = new Dictionary<string, string[]>();

        // Перевірка Title
        var rawTitle = request.Title ?? "";
        var title = rawTitle.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            errors["title"] = ["Назва інциденту є обов'язковою."];
        }
        else if (rawTitle.Length > 160)
        {
            errors["title"] = ["Назва не може перевищувати 160 символів."];
        }

        // Перевірка Description
        var rawDescription = request.Description ?? "";
        var description = rawDescription.Trim();

        if (string.IsNullOrWhiteSpace(description))
        {
            errors["description"] = ["Опис інциденту є обов'язковим."];
        }
        else if (rawDescription.Length > 4000)
        {
            errors["description"] = ["Опис не може перевищувати 4000 символів."];
        }

        // Перевірка Severity
        IncidentSeverity severity = IncidentSeverity.Low;
        var severityValid =
            Enum.TryParse<IncidentSeverity>(
                request.Severity,
                ignoreCase: true,
                out severity)
            && Enum.IsDefined(severity);

        if (!severityValid)
        {
            errors["severity"] =
                ["Допустимі значення: Low, Medium, High, Critical."];
        }

        // Перевірка OccurredAtUtc
        if (request.OccurredAtUtc is null)
        {
            errors["occurredAtUtc"] =
                ["Дата та час виникнення інциденту є обов'язковими."];
        }
        else if (request.OccurredAtUtc.Value > now.AddMinutes(5))
        {
            errors["occurredAtUtc"] =
                ["Дата виникнення не може бути більш ніж на 5 хвилин у майбутньому."];
        }

        // Міжпольова перевірка: High і Critical потребують опису від 40 символів
        if (severityValid &&
            (severity == IncidentSeverity.High ||
             severity == IncidentSeverity.Critical) &&
            !string.IsNullOrWhiteSpace(description) &&
            description.Length < 40)
        {
            errors["description"] =
                ["Для рівня High або Critical опис має містити щонайменше 40 символів."];
        }

        // Повернення помилок до звернення до БД
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Перевірка конфлікту за назвою та активним статусом
        var activeStatuses = new[]
        {
            IncidentStatus.New,
            IncidentStatus.Triaged,
            IncidentStatus.InProgress,
            IncidentStatus.Resolved
        };

        var duplicate = await db.Incidents.AnyAsync(
            x => x.Title == title && activeStatuses.Contains(x.Status),
            ct);

        if (duplicate)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Конфлікт створення інциденту",
                detail: "Активний інцидент із такою назвою вже існує.");
        }

        // Створення сутності. Server-managed поля встановлюються сервером
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            OwnerUserId = DbSeeder.AliceId,
            Title = title,
            Description = description,
            Severity = severity,
            Status = IncidentStatus.New,
            OccurredAtUtc = request.OccurredAtUtc!.Value.ToUniversalTime(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);

        // Відповідь після успішного створення
        var response = new CreatedIncidentResponse(
            incident.Id,
            incident.Title,
            incident.Severity.ToString(),
            incident.Status.ToString(),
            incident.OccurredAtUtc,
            incident.CreatedAtUtc,
            incident.UpdatedAtUtc);

        return Results.Created(
            $"/api/incidents/{incident.Id}",
            response);
    }
}

// Request DTO: лише поля, які дозволено передавати клієнту
public sealed record CreateIncidentRequest(
    string? Title,
    string? Description,
    string? Severity,
    DateTimeOffset? OccurredAtUtc);

// Response DTO
public sealed record CreatedIncidentResponse(
    Guid Id,
    string Title,
    string Severity,
    string Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Presentation.Endpoints;

public static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents")
            .RequireAuthorization();

        group.MapGet("/", GetListAsync)
            .WithName("GetIncidents")
            .Produces<IReadOnlyList<IncidentListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetDetailsAsync)
            .WithName("GetIncidentDetails")
            .Produces<IncidentDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateIncident");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteIncident");
        group.MapPost("/{id:guid}/triage", TriageAsync).WithName("TriageIncident");

        group.MapGet("/severity-summary", GetSeveritySummaryAsync)
            .WithName("GetIncidentSeveritySummary")
            .Produces<IReadOnlyList<IncidentSeveritySummaryResponse>>();

        endpoints.MapGroup("/api/incident-comments")
            .WithTags("Incidents")
            .RequireAuthorization()
            .MapGet("/{id:guid}", GetCommentAsync)
            .WithName("GetIncidentComment");

        return endpoints;
    }

    private static IResult IncidentNotFound() => Results.Problem(
        title: "Інцидент не знайдено",
        statusCode: StatusCodes.Status404NotFound);

    private static IResult CommentNotFound() => Results.Problem(
        title: "Коментар не знайдено",
        statusCode: StatusCodes.Status404NotFound);

    // null — дію дозволено; інакше — узгоджена відмова (404 або 403).
    private static async Task<IResult?> DecideAsync(
        HttpContext http,
        IAuthorizationService authorization,
        Incident incident,
        OperationAuthorizationRequirement operation,
        ILogger? auditLogger = null,
        string? auditAction = null)
    {
        var canRead = await authorization.AuthorizeAsync(http.User, incident, IncidentOperations.Read);
        if (!canRead.Succeeded)
        {
            LogDenied(auditLogger, http, auditAction, incident.Id);
            return IncidentNotFound();
        }

        var decision = await authorization.AuthorizeAsync(http.User, incident, operation);
        if (decision.Succeeded)
        {
            return null;
        }

        LogDenied(auditLogger, http, auditAction, incident.Id);
        return Results.Forbid();
    }

    private static void LogDenied(ILogger? logger, HttpContext http, string? action, Guid resourceId)
    {
        if (logger is null || action is null)
        {
            return;
        }

        logger.LogWarning(
            "authorization.denied subject={SubjectId} action={Action} resource={ResourceId} "
            + "result={Result} occurredAtUtc={OccurredAtUtc:O} traceId={TraceId}",
            http.User.FindFirstValue(ClaimTypes.NameIdentifier),
            action,
            resourceId,
            "denied",
            DateTimeOffset.UtcNow,
            Activity.Current?.Id ?? http.TraceIdentifier);
    }

    private static async Task<IResult> GetListAsync(
        string? status,
        HttpContext http,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        IncidentStatus? parsedStatus = null;
        if (status is not null)
        {
            if (!Enum.TryParse<IncidentStatus>(status, ignoreCase: true, out var candidate)
                || !Enum.IsDefined(candidate))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Допустимі значення: New, Triaged, InProgress, Resolved, Closed."]
                });
            }

            parsedStatus = candidate;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Results.Unauthorized();
        }

        var isAnalyst = http.User.IsInRole(DbSeeder.AnalystRole);
        return Results.Ok(await queries.GetListAsync(parsedStatus, userId, isAnalyst, cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        HttpContext http,
        IAuthorizationService authorization,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var incident = await queries.FindForAuthorizationAsync(id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var denied = await DecideAsync(http, authorization, incident, IncidentOperations.Read);
        if (denied is not null)
        {
            return denied;
        }

        var includeInternal = (await authorization.AuthorizeAsync(
            http.User, incident, IncidentOperations.ReadInternalComments)).Succeeded;

        var details = await queries.GetDetailsAsync(id, includeInternal, cancellationToken);
        return details is null ? IncidentNotFound() : Results.Ok(details);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateIncidentRequest request,
        HttpContext http,
        IAuthorizationService authorization,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 160)
        {
            errors["title"] = ["Заголовок: від 1 до 160 символів."];
        }
        if (request.Description is null || request.Description.Length > 4000)
        {
            errors["description"] = ["Опис: до 4000 символів."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var denied = await DecideAsync(http, authorization, incident, IncidentOperations.Update);
        if (denied is not null)
        {
            return denied;
        }

        // Лише дозволені поля; Status і OwnerUserId із запиту ігноруються (A-08a).
        incident.Title = request.Title.Trim();
        incident.Description = request.Description!;
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext http,
        IAuthorizationService authorization,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var denied = await DecideAsync(http, authorization, incident, IncidentOperations.Delete);
        if (denied is not null)
        {
            return denied;
        }

        db.Incidents.Remove(incident);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TriageAsync(
        Guid id,
        HttpContext http,
        IAuthorizationService authorization,
        SecureLabDbContext db,
        ILogger<IncidentQueries> logger,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var denied = await DecideAsync(
            http, authorization, incident, IncidentOperations.Triage, logger, "incident.triage");
        if (denied is not null)
        {
            return denied;
        }

        if (incident.Status != IncidentStatus.New)
        {
            return Results.Problem(
                title: "Перехід недопустимий",
                statusCode: StatusCodes.Status409Conflict);
        }

        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = DateTimeOffset.UtcNow;

        incident.Status = IncidentStatus.Triaged; // наступний status визначає сервер
        incident.UpdatedAtUtc = now;
        db.Set<IncidentStatusHistory>().Add(new IncidentStatusHistory
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            ChangedByUserId = userId,
            OldStatus = IncidentStatus.New,
            NewStatus = IncidentStatus.Triaged,
            Note = "Triage",
            CreatedAtUtc = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCommentAsync(
        Guid id,
        HttpContext http,
        IAuthorizationService authorization,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var comment = await queries.FindCommentAsync(id, cancellationToken);
        if (comment is null)
        {
            return CommentNotFound();
        }

        var canRead = await authorization.AuthorizeAsync(http.User, comment.Incident, IncidentOperations.Read);
        if (!canRead.Succeeded)
        {
            return CommentNotFound();
        }

        if (comment.IsInternal)
        {
            var canInternal = await authorization.AuthorizeAsync(
                http.User, comment.Incident, IncidentOperations.ReadInternalComments);
            if (!canInternal.Succeeded)
            {
                return CommentNotFound();
            }
        }

        return Results.Ok(new IncidentCommentResponse(
            comment.Id, comment.Author.DisplayName, comment.Text, comment.CreatedAtUtc));
    }

    private static async Task<IResult> GetSeveritySummaryAsync(
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var summary = await queries.GetSeveritySummaryAsync(cancellationToken);
        return Results.Ok(summary);
    }
}
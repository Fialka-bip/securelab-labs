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
            .WithTags("Incidents");

        group.MapGet("/", GetListAsync)
            .WithName("GetIncidents")
            .Produces<IReadOnlyList<IncidentListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetDetailsAsync)
            .WithName("GetIncidentDetails")
            .Produces<IncidentDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // VULNERABLE (ЛР4, стан «до»): ресурс шукається лише за id.
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateIncident");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteIncident");
        group.MapPost("/{id:guid}/triage", TriageAsync).WithName("TriageIncident");

        group.MapGet("/severity-summary", GetSeveritySummaryAsync)
            .WithName("GetIncidentSeveritySummary")
            .Produces<IReadOnlyList<IncidentSeveritySummaryResponse>>();

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        string? status,
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

        return Results.Ok(await queries.GetListAsync(parsedStatus, cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var incident = await queries.GetDetailsAsync(id, cancellationToken);
        return incident is null
            ? Results.Problem(
                title: "Інцидент не знайдено",
                detail: $"Інцидент '{id}' не існує.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(incident);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateIncidentRequest request,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null) return Results.NotFound();

        incident.Title = request.Title;
        incident.Description = request.Description;
        if (request.Status is not null
            && Enum.TryParse<IncidentStatus>(request.Status, ignoreCase: true, out var newStatus))
        {
            incident.Status = newStatus;
        }
        if (request.OwnerUserId is not null)
        {
            incident.OwnerUserId = request.OwnerUserId.Value;
        }
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null) return Results.NotFound();

        db.Incidents.Remove(incident);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TriageAsync(
        Guid id,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null) return Results.NotFound();

        incident.Status = IncidentStatus.Triaged;
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSeveritySummaryAsync(
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var summary = await queries.GetSeveritySummaryAsync(cancellationToken);
        return Results.Ok(summary);
    }
}
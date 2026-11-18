using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Application.Incidents;

public sealed class IncidentQueries(SecureLabDbContext dbContext, ILogger<IncidentQueries> logger)
{
    private static readonly string[] SeverityOrder = ["Critical", "High", "Medium", "Low"];

    // Query scoping: Analyst бачить усі інциденти, інші користувачі — лише власні.
    public async Task<IReadOnlyList<IncidentListItemResponse>> GetListAsync(
        IncidentStatus? status,
        Guid userId,
        bool isAnalyst,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading incidents with status filter {Status}", status);

        var query = dbContext.Incidents
            .AsNoTracking()
            .Where(incident => isAnalyst || incident.OwnerUserId == userId);

        if (status is not null)
        {
            query = query.Where(incident => incident.Status == status);
        }

        return await query
            .OrderByDescending(incident => incident.CreatedAtUtc)
            .Select(incident => new IncidentListItemResponse(
                incident.Id,
                incident.Title,
                incident.Severity.ToString(),
                incident.Status.ToString(),
                incident.OccurredAtUtc,
                incident.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    // Entity лише для рішення авторизації; назовні не серіалізується.
    public Task<Incident?> FindForAuthorizationAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Incidents
            .AsNoTracking()
            .SingleOrDefaultAsync(incident => incident.Id == id, cancellationToken);
    }

    // Викликається лише після того, як handler дозволив доступ до інциденту.
    public Task<IncidentDetailsResponse?> GetDetailsAsync(
        Guid id,
        bool includeInternalComments,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading incident {IncidentId}", id);

        return dbContext.Incidents
            .AsNoTracking()
            .Where(incident => incident.Id == id)
            .Select(incident => new IncidentDetailsResponse(
                incident.Id,
                incident.Title,
                incident.Description,
                incident.Severity.ToString(),
                incident.Status.ToString(),
                incident.OccurredAtUtc,
                incident.CreatedAtUtc,
                incident.Owner.DisplayName,
                incident.Comments
                    .Where(comment => includeInternalComments || !comment.IsInternal)
                    .OrderBy(comment => comment.CreatedAtUtc)
                    .Select(comment => new IncidentCommentResponse(
                        comment.Id,
                        comment.Author.DisplayName,
                        comment.Text,
                        comment.CreatedAtUtc))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Коментар разом із батьківським інцидентом для рішення Read.
    public Task<IncidentComment?> FindCommentAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Set<IncidentComment>()
            .AsNoTracking()
            .Include(comment => comment.Incident)
            .Include(comment => comment.Author)
            .SingleOrDefaultAsync(comment => comment.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<IncidentSeveritySummaryResponse>> GetSeveritySummaryAsync(
        CancellationToken cancellationToken)
    {
        var grouped = await dbContext.Incidents
            .AsNoTracking()
            .GroupBy(incident => incident.Severity)
            .Select(group => new IncidentSeveritySummaryResponse(
                group.Key.ToString(),
                group.Count()))
            .ToListAsync(cancellationToken);

        var countsBySeverity = grouped.ToDictionary(item => item.Severity, item => item.Count);

        var result = SeverityOrder
            .Select(severity => new IncidentSeveritySummaryResponse(
                severity,
                countsBySeverity.GetValueOrDefault(severity, 0)))
            .ToList();

        logger.LogInformation("Severity summary computed for {GroupCount} groups", result.Count);

        return result;
    }
}
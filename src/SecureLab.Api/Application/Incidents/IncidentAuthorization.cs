using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Application.Incidents;

public static class IncidentOperations
{
    public static readonly OperationAuthorizationRequirement Read =
        new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement ReadInternalComments =
        new() { Name = nameof(ReadInternalComments) };
    public static readonly OperationAuthorizationRequirement Update =
        new() { Name = nameof(Update) };
    public static readonly OperationAuthorizationRequirement Delete =
        new() { Name = nameof(Delete) };
    public static readonly OperationAuthorizationRequirement Triage =
        new() { Name = nameof(Triage) };
}

public sealed class IncidentAuthorizationHandler
    : AuthorizationHandler<OperationAuthorizationRequirement, Incident>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Incident incident)
    {
        var isOwner = Guid.TryParse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && incident.OwnerUserId == userId;
        var isAnalyst = context.User.IsInRole(DbSeeder.AnalystRole);

        var allowed = requirement.Name switch
        {
            nameof(IncidentOperations.Read) => isOwner || isAnalyst,
            nameof(IncidentOperations.ReadInternalComments) => isAnalyst,
            nameof(IncidentOperations.Update) => isOwner,
            nameof(IncidentOperations.Delete) => isOwner && incident.Status == IncidentStatus.New,
            nameof(IncidentOperations.Triage) => isAnalyst,
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
namespace SecureLab.Api.Presentation.Contracts;

//навмисно містить protected-поля для PoC.
public sealed record UpdateIncidentRequest(
    string Title,
    string Description,
    string? Status,
    Guid? OwnerUserId);
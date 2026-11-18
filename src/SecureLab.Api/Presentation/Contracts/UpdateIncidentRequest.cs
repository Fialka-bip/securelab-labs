namespace SecureLab.Api.Presentation.Contracts;

// Лише дозволені поля. Status, OwnerUserId, role з клієнта не приймаються.
public sealed record UpdateIncidentRequest(string Title, string Description);
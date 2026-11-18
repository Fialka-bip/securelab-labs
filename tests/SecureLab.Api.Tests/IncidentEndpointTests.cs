using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Tests;

public sealed class IncidentEndpointTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static readonly Guid AliceNewIncident = Guid.Parse("20000000-0000-0000-0000-000000000005");
    private static readonly Guid BobComment = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid BobUserId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static string Password =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("SeedUsers__Password is not set.");

    private Task<HttpClient> LoginAs(string user) => factory.LoginAsync(user, Password);

    // A-01
    [Fact]
    public async Task GetDetails_Anonymous_Returns401_WithoutRedirect()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // A-05
    [Fact]
    public async Task GetList_AsAlice_ReturnsOnlyOwnIncidents()
    {
        using var alice = await LoginAs("alice");

        var incidents = await alice.GetFromJsonAsync<List<IncidentListItemResponse>>("/api/incidents");

        Assert.NotNull(incidents);
        Assert.Contains(incidents, i => i.Id == DbSeeder.AliceIncidentId);
        Assert.DoesNotContain(incidents, i => i.Id == DbSeeder.BobIncidentId);
    }

    // A-02 (read)
    [Fact]
    public async Task GetDetails_AsOwner_ReturnsOk_WithoutOwnerFields()
    {
        using var alice = await LoginAs("alice");

        using var response = await alice.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(document.RootElement.TryGetProperty("ownerUserId", out _));
        Assert.False(document.RootElement.TryGetProperty("email", out _));
        Assert.Equal("Аліса Коваль", document.RootElement.GetProperty("ownerDisplayName").GetString());
    }

    [Fact]
    public async Task GetDetails_ForUnknownId_ReturnsProblemDetails404()
    {
        using var alice = await LoginAs("alice");

        using var response = await alice.GetAsync($"/api/incidents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // A-03
    [Fact]
    public async Task GetDetails_AsOtherReporter_ReturnsNotFound_WithoutForeignData()
    {
        using var alice = await LoginAs("alice");

        using var response = await alice.GetAsync($"/api/incidents/{DbSeeder.BobIncidentId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Невідома", body);
    }

    // A-10
    [Fact]
    public async Task GetDetails_ForeignAndUnknownId_AreIndistinguishable()
    {
        using var alice = await LoginAs("alice");

        using var foreign = await alice.GetAsync($"/api/incidents/{DbSeeder.BobIncidentId}");
        using var unknown = await alice.GetAsync($"/api/incidents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(
            unknown.Content.Headers.ContentType?.ToString(),
            foreign.Content.Headers.ContentType?.ToString());

        var f = await foreign.Content.ReadFromJsonAsync<JsonObject>();
        var u = await unknown.Content.ReadFromJsonAsync<JsonObject>();
        f!.Remove("traceId");
        u!.Remove("traceId");
        Assert.True(JsonNode.DeepEquals(u, f));
    }

    // A-03R
    [Fact]
    public async Task GetDetails_AsAnalyst_ReturnsForeignIncident()
    {
        using var morgan = await LoginAs("morgan");

        using var response = await morgan.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A-04 (update)
    [Fact]
    public async Task Update_AsNonOwner_ReturnsNotFound_AndDoesNotChangeResource()
    {
        using var alice = await LoginAs("alice");
        using var bob = await LoginAs("bob");
        var url = $"/api/incidents/{DbSeeder.BobIncidentId}";
        var before = await bob.GetFromJsonAsync<JsonObject>(url);

        using var response = await alice.PutAsJsonAsync(
            url, new { title = "changed by alice", description = "changed" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await bob.GetFromJsonAsync<JsonObject>(url);
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    // A-04 (delete)
    [Fact]
    public async Task Delete_AsNonOwner_ReturnsNotFound_AndKeepsResource()
    {
        using var alice = await LoginAs("alice");
        using var bob = await LoginAs("bob");
        var url = $"/api/incidents/{DbSeeder.BobIncidentId}";

        using var response = await alice.DeleteAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var stillThere = await bob.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    // A-02 (update) + A-08a
    [Fact]
    public async Task Update_AsOwner_ChangesOnlyContent_AndIgnoresForgedFields()
    {
        using var alice = await LoginAs("alice");
        var url = $"/api/incidents/{AliceNewIncident}";
        var before = await alice.GetFromJsonAsync<JsonObject>(url);

        try
        {
            using var response = await alice.PutAsJsonAsync(url, new
            {
                title = "Updated by test",
                description = "updated",
                status = "Closed",
                ownerUserId = BobUserId,
                role = "Analyst"
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var after = await alice.GetFromJsonAsync<JsonObject>(url);
            Assert.Equal("Updated by test", after!["title"]!.GetValue<string>());
            Assert.Equal(before!["status"]!.GetValue<string>(), after["status"]!.GetValue<string>());
            Assert.Equal(
                before["ownerDisplayName"]!.GetValue<string>(),
                after["ownerDisplayName"]!.GetValue<string>());
        }
        finally
        {
            using var restore = await alice.PutAsJsonAsync(url, new
            {
                title = before!["title"]!.GetValue<string>(),
                description = before["description"]!.GetValue<string>()
            });
        }
    }

    // A-06
    [Fact]
    public async Task Triage_AsReporter_ReturnsForbidden_AndDoesNotChangeStatus()
    {
        using var alice = await LoginAs("alice");
        var url = $"/api/incidents/{AliceNewIncident}";

        using var response = await alice.PostAsync(url + "/triage", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await alice.GetFromJsonAsync<JsonObject>(url);
        Assert.Equal("New", after!["status"]!.GetValue<string>());
    }

    // A-07
    [Fact]
    public async Task Triage_AsAnalyst_FromNew_SetsTriaged()
    {
        using var morgan = await LoginAs("morgan");
        var url = $"/api/incidents/{AliceNewIncident}";

        try
        {
            using var first = await morgan.PostAsync(url + "/triage", content: null);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

            var after = await morgan.GetFromJsonAsync<JsonObject>(url);
            Assert.Equal("Triaged", after!["status"]!.GetValue<string>());

            using var second = await morgan.PostAsync(url + "/triage", content: null);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }
        finally
        {
            await RestoreNewStatusAsync(AliceNewIncident);
        }
    }

    // A-12 (child comment)
    [Fact]
    public async Task GetComment_ForOtherIncident_ReturnsNotFound()
    {
        using var alice = await LoginAs("alice");
        using var morgan = await LoginAs("morgan");

        using var denied = await alice.GetAsync($"/api/incident-comments/{BobComment}");
        using var allowed = await morgan.GetAsync($"/api/incident-comments/{BobComment}");

        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ClientScript_DoesNotUseDangerousInnerHtmlSink()
    {
        using var client = factory.CreateClient();
        var script = await client.GetStringAsync("/app.js");

        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.Contains("textContent", script, StringComparison.Ordinal);
    }

    // Повертає інцидент у початковий стан після тесту triage.
    private async Task RestoreNewStatusAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        var incident = await db.Incidents.SingleAsync(i => i.Id == id);
        incident.Status = IncidentStatus.New;
        var history = db.Set<IncidentStatusHistory>()
            .Where(h => h.IncidentId == id && h.Note == "Triage");
        db.Set<IncidentStatusHistory>().RemoveRange(history);
        await db.SaveChangesAsync();
    }
}
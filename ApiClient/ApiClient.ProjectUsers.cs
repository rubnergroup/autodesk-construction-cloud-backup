using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace ACC.ApiClient;

/*
 * Minimal ACC Admin API (project user management) support.
 * Endpoints and headers follow Autodesk's official 2-legged tutorial collection:
 * https://github.com/autodesk-platform-services/aps-acc.admin.api-postman.collection
 *
 * Requires the token to be requested with "account:read account:write" (see ApiClientOptions.Scope).
 * Write calls with a 2-legged token must also carry a "User-Id" header naming an account admin;
 * pure 2-legged without a user is not supported for writes. Reads do not need it.
 */
public partial class ApiClient
{
    private const string AdminApiBase = "https://developer.api.autodesk.com/construction/admin";

    private const string AdminMoreDetailsUrl =
        "https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projects-project-Id-users-GET/";

    // BIM 360 user id of an account admin, sent as the User-Id header on Admin API writes.
    public string? AdminUserId { get; set; }

    // The Data Management api returns project ids prefixed with "b."; the Admin api wants the raw id.
    private static string StripProjectIdPrefix(string projectId)
    {
        return projectId.StartsWith("b.") ? projectId[2..] : projectId;
    }

    // Resolves the User-Id value for an account admin from their email address.
    // GET hq/v1/accounts/{accountId}/users/search?email=...
    public async Task<string?> FindAccountAdminId(string email)
    {
        Config.Logger?.Trace("Top");
        var endpoint =
            $"https://developer.api.autodesk.com/hq/v1/accounts/{StripProjectIdPrefix(Config.AccountId)}/users/search?email={Uri.EscapeDataString(email)}";
        const string moreDetailsUrl =
            "https://aps.autodesk.com/en/docs/bim360/v1/reference/http/users-search-GET/";
        var responseString = string.Empty;
        await Config.RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureAccessToken();
            var response = await Config.HttpClient.GetAsync(endpoint);
            responseString = await response.Content.ReadAsStringAsync();
            HandleUnsuccessfulStatusCode(responseString, response, moreDetailsUrl);
        });
        var results = JsonConvert.DeserializeObject<List<AccountUser>>(responseString);
        var adminId = results?.FirstOrDefault()?.Id;
        Config.Logger?.Debug($"Resolved account admin id for {email}: {adminId ?? "<not found>"}");
        return adminId;
    }

    public async Task<List<ProjectUser>> GetProjectUsers(string projectId)
    {
        Config.Logger?.Trace("Top");
        var endpoint = $"{AdminApiBase}/v1/projects/{StripProjectIdPrefix(projectId)}/users?limit=200";
        var users = new List<ProjectUser>();
        var offset = 0;
        do
        {
            var responseString = string.Empty;
            var pageEndpoint = $"{endpoint}&offset={offset}";
            await Config.RetryPolicy.ExecuteAsync(async () =>
            {
                await EnsureAccessToken();
                using var request = new HttpRequestMessage(HttpMethod.Get, pageEndpoint);
                var response = await Config.HttpClient.SendAsync(request);
                responseString = await response.Content.ReadAsStringAsync();
                HandleUnsuccessfulStatusCode(responseString, response, AdminMoreDetailsUrl);
            });
            var page = JsonConvert.DeserializeObject<ProjectUsersResponse>(responseString);
            if (page?.Results is null || page.Results.Count == 0) break;
            users.AddRange(page.Results);
            offset += page.Results.Count;
            if (page.Pagination is null || offset >= page.Pagination.TotalResults) break;
        } while (true);

        Config.Logger?.Debug($"Returning {users.Count} users for project {projectId}");
        return users;
    }

    public async Task<ProjectUser?> GetProjectUserByEmail(string projectId, string email)
    {
        var users = await GetProjectUsers(projectId);
        return users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    // Returns true when the user was added, false when they were already a member of the project.
    public async Task<bool> AddProjectUser(string projectId, string email, string productKey, string productAccess)
    {
        Config.Logger?.Trace("Top");
        var endpoint = $"{AdminApiBase}/v1/projects/{StripProjectIdPrefix(projectId)}/users";
        var payload = JsonConvert.SerializeObject(new
        {
            email,
            products = new[] { new { key = productKey, access = productAccess } }
        });
        var alreadyMember = false;
        await Config.RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureAccessToken();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            AddUserIdHeader(request);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await Config.HttpClient.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            // The Admin API answers 409 Conflict when the user already belongs to the project.
            // That is the state we asked for, so treat it as success rather than an error to
            // throw on -- otherwise the retry policy would back off and retry a permanent answer.
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                Config.Logger?.Debug($"{email} already belongs to project {projectId}: {responseString}");
                alreadyMember = true;
                return;
            }

            HandleUnsuccessfulStatusCode(responseString, response, AdminMoreDetailsUrl);
        });
        if (!alreadyMember) Config.Logger?.Debug($"Added {email} to project {projectId}");
        return !alreadyMember;
    }

    public async Task RemoveProjectUser(string projectId, string projectUserId)
    {
        Config.Logger?.Trace("Top");
        var endpoint = $"{AdminApiBase}/v1/projects/{StripProjectIdPrefix(projectId)}/users/{projectUserId}";
        await Config.RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureAccessToken();
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            AddUserIdHeader(request);
            var response = await Config.HttpClient.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            HandleUnsuccessfulStatusCode(responseString, response, AdminMoreDetailsUrl);
        });
        Config.Logger?.Debug($"Removed user {projectUserId} from project {projectId}");
    }

    private void AddUserIdHeader(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(AdminUserId))
            throw new InvalidOperationException(
                "AdminUserId is required for ACC Admin API writes with a 2-legged token. " +
                "Pass --adminemail or --adminuserid.");
        request.Headers.Add("User-Id", AdminUserId);
    }
}

public class AccountUser
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("uid")] public string? Uid { get; set; }

    [JsonProperty("email")] public string? Email { get; set; }
}

public class ProjectUsersResponse
{
    [JsonProperty("pagination")] public ProjectUsersPagination? Pagination { get; set; }

    [JsonProperty("results")] public List<ProjectUser>? Results { get; set; }
}

public class ProjectUsersPagination
{
    [JsonProperty("totalResults")] public int TotalResults { get; set; }
}

public class ProjectUser
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("email")] public string? Email { get; set; }

    [JsonProperty("name")] public string? Name { get; set; }

    [JsonProperty("autodeskId")] public string? AutodeskId { get; set; }
}

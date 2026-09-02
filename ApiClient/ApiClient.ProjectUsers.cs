using System.Text;
using Newtonsoft.Json;

namespace ACC.ApiClient;

/*
 * Minimal ACC Admin API (project user management) support.
 * https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projects-project-Id-users-GET/
 *
 * Requires the token to be requested with "account:read account:write" (see ApiClientOptions.Scope)
 * and, for a 2-legged token, an x-user-id of an account admin (see ApiClientOptions/AdminUserId below).
 */
public partial class ApiClient
{
    private const string AdminApiBase = "https://developer.api.autodesk.com/construction/admin";

    private const string AdminMoreDetailsUrl =
        "https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projects-project-Id-users-GET/";

    // Autodesk id of an account admin, sent as x-user-id on Admin API calls made with a 2-legged token.
    public string? AdminUserId { get; set; }

    // The Data Management api returns project ids prefixed with "b."; the Admin api wants the raw id.
    private static string StripProjectIdPrefix(string projectId)
    {
        return projectId.StartsWith("b.") ? projectId[2..] : projectId;
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
                AddAdminUserIdHeader(request);
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

    public async Task AddProjectUser(string projectId, string email, string productKey, string productAccess)
    {
        Config.Logger?.Trace("Top");
        var endpoint = $"{AdminApiBase}/v2/projects/{StripProjectIdPrefix(projectId)}/users";
        var payload = JsonConvert.SerializeObject(new
        {
            email,
            products = new[] { new { key = productKey, access = productAccess } }
        });
        await Config.RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureAccessToken();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            AddAdminUserIdHeader(request);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await Config.HttpClient.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            HandleUnsuccessfulStatusCode(responseString, response, AdminMoreDetailsUrl);
        });
        Config.Logger?.Debug($"Added {email} to project {projectId}");
    }

    public async Task RemoveProjectUser(string projectId, string projectUserId)
    {
        Config.Logger?.Trace("Top");
        var endpoint = $"{AdminApiBase}/v1/projects/{StripProjectIdPrefix(projectId)}/users/{projectUserId}";
        await Config.RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureAccessToken();
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            AddAdminUserIdHeader(request);
            var response = await Config.HttpClient.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            HandleUnsuccessfulStatusCode(responseString, response, AdminMoreDetailsUrl);
        });
        Config.Logger?.Debug($"Removed user {projectUserId} from project {projectId}");
    }

    private void AddAdminUserIdHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(AdminUserId)) request.Headers.Add("x-user-id", AdminUserId);
    }
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

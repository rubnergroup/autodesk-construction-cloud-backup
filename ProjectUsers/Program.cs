using ACC.ApiClient;
using ACC.ProjectUsers;
using CommandLine;
using Library.Logger;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using ILogger = Library.Logger.ILogger;

var parsed = Parser.Default.ParseArguments<CommandLineArgs>(args);
if (parsed.Errors.Any()) return 1;
var options = parsed.Value;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(options.DebugLogging ? LogEventLevel.Debug : LogEventLevel.Information)
    .WriteTo.Console(
        theme: AnsiConsoleTheme.Code,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

ILogger logger = new SerilogLogger(Log.Logger, new SerilogLoggerConfiguration
{
    LogLevel = options.DebugLogging ? LogLevel.Debug : LogLevel.Info,
    LogToConsole = true,
    LogToFile = false
});

var apiClient = TwoLeggedApiClient
    .Configure()
    .WithClientId(options.ClientId)
    .AndClientSecret(options.ClientSecret)
    .ForAccount(options.AccountId)
    .WithOptions(o =>
    {
        o.Logger = logger;
        o.HubId = options.HubId;
        // The ACC Admin API needs account scopes on top of the data scope used for backups.
        o.Scope = "data:read account:read account:write";
    })
    .Create();

// Admin API writes with a 2-legged token must name an account admin via the User-Id header.
apiClient.AdminUserId = options.AdminUserId;
if (string.IsNullOrWhiteSpace(apiClient.AdminUserId) && !string.IsNullOrWhiteSpace(options.AdminEmail))
{
    apiClient.AdminUserId = await apiClient.FindAccountAdminId(options.AdminEmail);
    if (apiClient.AdminUserId is null)
    {
        logger.Error($"No account user found for --adminemail {options.AdminEmail}");
        return 1;
    }

    logger.Info($"Acting on behalf of {options.AdminEmail} (User-Id {apiClient.AdminUserId})");
}

if (string.IsNullOrWhiteSpace(apiClient.AdminUserId) && !options.DryRun)
{
    logger.Error("--adminemail or --adminuserid is required: the ACC Admin API rejects writes from a " +
                 "2-legged token that does not name an account admin.");
    return 1;
}

var products = new List<ProjectUserProduct>();
foreach (var entry in options.Products.Select(p => p.Trim()).Where(p => p.Length > 0))
{
    var parts = entry.Split(':', 2);
    var access = parts.Length == 2 ? parts[1] : options.ProductAccess;
    if (access is not ("member" or "administrator"))
    {
        logger.Error($"Invalid access level '{access}' for product '{parts[0]}': use member or administrator");
        return 1;
    }

    products.Add(new ProjectUserProduct { Key = parts[0], Access = access });
}

if (!options.Remove && products.Count == 0)
{
    logger.Error("--product needs at least one product key");
    return 1;
}

var projects = await apiClient.GetProjects();
if (options.NumberOfProjects > 0) projects = projects.Take(options.NumberOfProjects).ToList();

var action = options.Remove ? "Removing" : "Adding";
var direction = options.Remove ? "from" : "to";
logger.Info($"=> {action} {options.UserEmail} {direction} {projects.Count} project(s)" +
            $"{(options.Remove ? "" : $" with {string.Join(", ", products)}")}{(options.DryRun ? " (dry run)" : "")}");

var succeeded = 0;
var failed = 0;
foreach (var project in projects)
    try
    {
        if (options.Remove)
        {
            var existing = await apiClient.GetProjectUserByEmail(project.ProjectId, options.UserEmail);
            if (existing is null)
            {
                logger.Info($"[skip] {project.Name}: {options.UserEmail} is not a member");
                continue;
            }

            if (!options.DryRun) await apiClient.RemoveProjectUser(project.ProjectId, existing.Id);
            logger.Info($"[ok] {project.Name}: removed {options.UserEmail}");
        }
        else
        {
            var result = options.DryRun
                ? AddProjectUserResult.Added
                : await apiClient.AddProjectUser(project.ProjectId, options.UserEmail, products);
            logger.Info(result switch
            {
                AddProjectUserResult.Added => $"[ok] {project.Name}: added {options.UserEmail}",
                AddProjectUserResult.AccessUpdated =>
                    $"[ok] {project.Name}: {options.UserEmail} was already a member, granted missing products",
                _ => $"[skip] {project.Name}: {options.UserEmail} is already a member with these products"
            });
        }

        succeeded++;
    }
    catch (Exception ex)
    {
        failed++;
        logger.Error(ex, $"[fail] {project.Name} ({project.ProjectId})");
    }

logger.Info($"=> Done. {succeeded} succeeded, {failed} failed.");
Log.CloseAndFlush();
return failed == 0 ? 0 : 1;

using CommandLine;

namespace ACC.ProjectUsers;

public class CommandLineArgs
{
    [Option(Required = true, HelpText = "Client ID of Autodesk Forge app.")]
    public string ClientId { get; set; } = string.Empty;

    [Option(Required = true, HelpText = "Client secret of Autodesk Forge app.")]
    public string ClientSecret { get; set; } = string.Empty;

    [Option(Required = true, HelpText = "Autodesk Construction Cloud account ID.")]
    public string AccountId { get; set; } = string.Empty;

    [Option(Required = false, HelpText = "Autodesk Construction Cloud HubId, defaults to b.AccountId.")]
    public string HubId { get; set; } = string.Empty;

    [Option('n', "number", Required = false, Default = 0,
        HelpText = "Only act on the first N projects. 0 (default) means all projects.")]
    public int NumberOfProjects { get; set; }

    [Option('u', "user", Required = true, HelpText = "Email address of the user to add to / remove from the projects.")]
    public string UserEmail { get; set; } = string.Empty;

    [Option("remove", Required = false, Default = false,
        HelpText = "Remove the user from the projects instead of adding them.")]
    public bool Remove { get; set; }

    [Option("adminemail", Required = false,
        HelpText = "Email of an account admin to act on behalf of. Resolved to a User-Id via the HQ users search. " +
                   "Required for add/remove with a 2-legged token unless --adminuserid is given.")]
    public string? AdminEmail { get; set; }

    [Option("adminuserid", Required = false,
        HelpText = "BIM 360 user id of an account admin, sent as the User-Id header. Alternative to --adminemail.")]
    public string? AdminUserId { get; set; }

    [Option("product", Required = false, Default = "docs",
        HelpText = "Product key to grant when adding, e.g. docs, projectAdministration, build.")]
    public string ProductKey { get; set; } = "docs";

    [Option("access", Required = false, Default = "member",
        HelpText = "Product access level to grant when adding: member or administrator.")]
    public string ProductAccess { get; set; } = "member";

    [Option("dryrun", Required = false, Default = false,
        HelpText = "Log what would happen without calling the add/remove endpoints.")]
    public bool DryRun { get; set; }

    [Option(Required = false, Default = false, HelpText = "Enable debug logging.")]
    public bool DebugLogging { get; set; }
}

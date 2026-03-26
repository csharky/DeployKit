namespace DeployKit.DeployServer;

public static class Permissions
{
    public const string JobsRun = "jobs:run";
    public const string JobsRead = "jobs:read";
    public const string ProfilesRead = "profiles:read";
    public const string ProfilesWrite = "profiles:write";
    public const string ApiKeysManage = "apikeys:manage";

    public static readonly string[] All =
        [JobsRun, JobsRead, ProfilesRead, ProfilesWrite, ApiKeysManage];
}

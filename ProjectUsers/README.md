# ACCProjectUsers

Proof-of-concept console tool that adds a user to, or removes a user from, every project in an Autodesk Construction
Cloud (ACC) / BIM 360 account in one run. It reuses the backup's `ApiClient` for authentication and project listing, and
calls the [ACC Admin API](https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projects-project-Id-users-GET/)
to manage project users.

## Required permissions

The tool uses a **2-legged (client credentials) token**. To work, it needs the right app, scopes and account integration,
and it has to act on behalf of an account admin.

### 1. APS app

Create the app as described in the main [README](../README.md#prerequities). Enable these APIs on it:

| API                              | Used for                                                                 |
|----------------------------------|--------------------------------------------------------------------------|
| `Data Management API`            | Listing the account's projects (`project/v1/hubs/{hubId}/projects`)      |
| `Autodesk Construction Cloud API`| Reading, adding and removing project users (`construction/admin/v1/...`) |
| `BIM 360 API`                    | Resolving `--adminemail` to a user id (`hq/v1/accounts/{id}/users/search`). Only needed if you use `--adminemail` |

If you already have an app for the backup, you can reuse it, but you may have to enable the `BIM 360 API` on it.

### 2. OAuth scopes

The tool requests the token with these scopes (set in `Program.cs`):

| Scope           | Why                                                                           |
|-----------------|-------------------------------------------------------------------------------|
| `data:read`     | List projects through the Data Management API                                 |
| `account:read`  | List project users (for `--remove` and member checks) and search account users |
| `account:write` | Add (`POST .../projects/{id}/users`) and remove (`DELETE .../users/{userId}`) project users |

The backup tool only requests `data:read`, so a token that works for backups is not enough here.

### 3. Custom integration in Account Admin

The app's Client ID must be registered as a custom integration in your account (Account Admin → Settings → Custom
Integrations). The integration needs **Account Administration** access, not only Document Management access, or the
Admin API answers `403`. If you registered the app for the backup with Document Management access only, edit the
integration and add Account Administration.

### 4. Acting user (`User-Id` header)

The ACC Admin API rejects writes made with a 2-legged token unless the request names a user to act as, in the `User-Id`
header. That user must be an **account admin** (or a project admin on every project you change). Pass either:

- `--adminemail admin@example.com`, which the tool resolves to a user id through the HQ user search, or
- `--adminuserid <id>`, the BIM 360 user id of that admin.

Reads don't need this header, so `--dryrun` works without it.

## Usage

```
ACCProjectUsers --clientid <id> --clientsecret <secret> --accountid <accountId> \
    --user new.person@example.com --adminemail admin@example.com
```

Remove the user from every project instead:

```
ACCProjectUsers --clientid <id> --clientsecret <secret> --accountid <accountId> \
    --user old.person@example.com --adminemail admin@example.com --remove
```

### Options

| Option            | Default  | Description                                                                 |
|-------------------|----------|-----------------------------------------------------------------------------|
| `--clientid`      |          | Required. Client ID of the APS app                                         |
| `--clientsecret`  |          | Required. Client secret of the APS app                                     |
| `--accountid`     |          | Required. ACC / BIM 360 account ID                                         |
| `--hubid`         | `b.<accountid>` | Hub ID, if it differs from the default                              |
| `-u`, `--user`    |          | Required. Email of the user to add or remove                               |
| `--remove`        | `false`  | Remove the user instead of adding them                                     |
| `--adminemail`    |          | Email of the account admin to act as. Required for writes unless `--adminuserid` is given |
| `--adminuserid`   |          | BIM 360 user id of the account admin to act as                             |
| `--product`       | `docs`   | Product key to grant when adding, e.g. `docs`, `projectAdministration`, `build` |
| `--access`        | `member` | Access level to grant when adding: `member` or `administrator`. `administrator` also grants `projectAdministration`, making the user a project admin, and upgrades users who are already members |
| `-n`, `--number`  | `0`      | Only act on the first N projects. `0` means all projects                   |
| `--dryrun`        | `false`  | Log what would happen without calling the add/remove endpoints             |
| `--debuglogging`  | `false`  | Enable debug logging                                                       |

The tool skips projects where the user is already a member (when adding with `--access member`) or not a member (when removing). It exits with
code `1` if any project failed.

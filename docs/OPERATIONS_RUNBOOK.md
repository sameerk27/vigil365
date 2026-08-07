# Vigil365 Operations Runbook

This runbook covers the data required to recover Vigil365: the SQL Server
database, the Data Protection key ring, and deployment configuration. Test this
procedure on a non-production host before relying on it during an incident.

## Recovery objective

- **Database:** alert records, audit trail, policies, collection history, and
  encrypted notification/Graph settings.
- **Data Protection keys:** required to decrypt settings encrypted by Vigil365.
- **Production configuration:** Entra IDs, redirect URI, SQL connection, and TLS
  settings. Treat this as secret material.
- **Not required for recovery:** application logs; retain them according to the
  host's incident-response policy.

Take an encrypted, access-controlled backup at least daily and before every
application upgrade. Keep the SQL backup and its matching Data Protection key
backup together; restoring only the database can leave saved encrypted settings
unreadable.

## Windows / SQL Server backup

1. Create a restricted backup directory, for example `D:\Vigil365Backups`.
2. Use a SQL login or Windows account permitted to back up the database.
3. Run the following from an elevated PowerShell prompt, changing the SQL Server
   instance and output path for the deployment:

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "D:\Vigil365Backups\M365SecurityDashboard-$stamp.bak"
sqlcmd -S '.\SQLEXPRESS' -E -Q "BACKUP DATABASE [M365SecurityDashboard] TO DISK = N'$backup' WITH COPY_ONLY, COMPRESSION, CHECKSUM, STATS = 10"
```

4. Verify the backup before treating it as successful:

```powershell
sqlcmd -S '.\SQLEXPRESS' -E -Q "RESTORE VERIFYONLY FROM DISK = N'$backup' WITH CHECKSUM"
```

5. Copy these files to the same protected backup set:

```powershell
Copy-Item 'C:\Apps\Vigil365\keys' "D:\Vigil365Backups\keys-$stamp" -Recurse
Copy-Item 'C:\Apps\Vigil365\appsettings.Production.json' "D:\Vigil365Backups\appsettings.Production-$stamp.json"
```

Use the actual publish directory if it differs from `C:\Apps\Vigil365`. Do not
place backup sets in the application directory or a source-control checkout.

## Docker backup

The compose deployment persists SQL backups through the `./backups` bind mount.
Create it before the first backup and restrict its host permissions.

```powershell
New-Item -ItemType Directory -Force backups | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $env:MSSQL_SA_PASSWORD `
  -Q "BACKUP DATABASE [M365SecurityDashboard] TO DISK = N'/var/opt/mssql/backup/M365SecurityDashboard-$stamp.bak' WITH COPY_ONLY, COMPRESSION, CHECKSUM, STATS = 10"
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $env:MSSQL_SA_PASSWORD `
  -Q "RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/M365SecurityDashboard-$stamp.bak' WITH CHECKSUM"
docker compose cp app:/keys "backups/keys-$stamp"
Copy-Item .env "backups/.env-$stamp" # keep only in encrypted storage
```

`MSSQL_SA_PASSWORD` must be available in the current shell; do not put it in
shell history or source control. The `.env` file and key ring are secret
material.

## Restore drill

Perform restores first to an isolated SQL database/server and an isolated app
host. Never point a test restore at the production database.

1. Stop the Vigil365 service/container that uses the target database.
2. Use `RESTORE FILELISTONLY FROM DISK = N'<backup path>'` to obtain the logical
   data and log names from the chosen backup.
3. Restore to a new database name with `RESTORE DATABASE ... WITH MOVE ...` in
   SQL Server Management Studio or with `sqlcmd`. Confirm `RESTORE VERIFYONLY`
   succeeded before this step.
4. Restore the matching `keys` directory and production configuration with
   restricted file permissions.
5. Start the app with the restored database connection. It will apply any
   pending EF Core migrations automatically.
6. Open `/health`, sign in as an administrator, verify alert policies and recent
   collection history, then test a non-production notification channel.

Record the elapsed restore time and any manual corrections. The recovery process
is only proven after a documented restore drill succeeds.

## Upgrade procedure

1. Read the release notes and make a verified backup set.
2. Stop the Windows service or scale the container down.
3. Publish the new Windows build with `install.ps1` / `deploy.ps1`, or run:

```powershell
docker compose build app
docker compose up -d
```

4. On startup Vigil365 applies pending EF Core migrations. Confirm the logs show
   no migration failure, then verify `/health` and the **Collection Runs** page.
5. Keep the previous application artifact until the post-upgrade checks pass.
   Database rollback requires restoring the verified backup; do not use an
   arbitrary migration downgrade against production.

## Post-incident checks

- Review JSON logs using the correlation ID returned in `X-Correlation-Id`.
- Confirm the next scheduled collection completes without source failures.
- Rotate credentials if an app host, backup set, or Data Protection key ring may
  have been exposed.
- Record the incident and recovery decision in Vigil365's audit trail and the
  organization incident system.

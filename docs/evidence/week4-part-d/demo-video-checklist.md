# Week 4 Part D demo video checklist

Preparation for the [demo video script](demo-video-script.md). The commands are for Windows PowerShell, run from the repository root.

## 1. Before you start

- [ ] Build once: `dotnet build ApexPost.slnx`. The commands below use `--no-build`, so restarts are quick.
- [ ] Close other apps. Turn off notifications (Focus assist). Use a large terminal font, and keep the windows at a size where the text is readable in the video.
- [ ] Use fictional data only (section 4).
- [ ] No token, key or connection string may appear on screen (section 5).

## 2. Database

Successful syncs and requests 50 and 51 need a database migrated up to `AddTrackedShipments`. Use a temporary database, as the recorded evidence did. Do not use the developer database `ApexPostDb` or the LocalDB copy.

Creating and dropping the database is a step you take yourself. The script does not need it for anything else.

```powershell
$db  = "ApexPost_Test_DemoVideo"
$dir = "E:\SqlData\ApexPost\Tests"
sqlcmd -S . -E -b -Q "CREATE DATABASE [$db] ON PRIMARY (NAME = N'$db', FILENAME = N'$dir\$db.mdf') LOG ON (NAME = N'${db}_log', FILENAME = N'$dir\${db}_log.ldf')"
$conn = "Server=.;Database=$db;Trusted_Connection=True;TrustServerCertificate=True"
dotnet ef database update --connection $conn --project src/ApexPost.Infrastructure --startup-project src/ApexPost.Web --context ApexPostDbContext
```

## 3. Keys, API, `.http` file and console

Run these before you start recording. They create random keys in variables and never print them. The keys exist only in this PowerShell session.

```powershell
function New-RandomKey { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) }
$key1 = New-RandomKey; $key2 = New-RandomKey; $partnerKey = New-RandomKey
```

**Terminal 1, API.** Use the same PowerShell session, so it has `$conn`, `$key1`, `$key2` and `$partnerKey`:

```powershell
$env:ConnectionStrings__ApexPostDb = $conn
$env:Authentication__Schemes__Bearer__SigningKeys__0__Issuer = "dotnet-user-jwts"
$env:Authentication__Schemes__Bearer__SigningKeys__0__Value  = $key1
$env:Authentication__Schemes__Bearer__SigningKeys__0__Length = "32"
$env:PartnerApiKeys__DemoVideo = $partnerKey
dotnet run --project src/ApexPost.Api --launch-profile https --no-build
```

- **Restart in step 04 of the script:** press Ctrl+C, then up-arrow and Enter. It restarts with the same key.
- **Restart in step 06 of the script**, with the second key:

```powershell
$env:Authentication__Schemes__Bearer__SigningKeys__0__Value = $key2; dotnet run --project src/ApexPost.Api --launch-profile https --no-build
```

The screen shows only the variable names, never the values.

**Editor with `src/ApexPost.Api/ApexPost.Api.http`.**
1. Close every editor window. Then start the editor from the same session, so it receives the partner key: `$env:APEXPOST_PARTNER_API_KEY = $partnerKey; code .`
2. With the API running, send these requests **before recording**: **0a** (Courier token), **0b** (Admin token) and **25** (creates `APX-00000011`).
3. Close their response tabs. The 0a and 0b responses contain the tokens.
4. Leave the file scrolled to request 43.

**Terminal 2, console.**
1. Check that no token is stored. This must print `False`:

   ```powershell
   Test-Path "$env:LOCALAPPDATA\ApexPost\Courier\access-token.bin"
   ```

2. Start the console. The separate device folder keeps the demo scans out of the default store:

   ```powershell
   dotnet run --project src/ApexPost.Courier.Console --no-build -- --Courier:DataDirectory=out\demo-device
   ```

3. Expect the signed-out menu with `pending scans (live): 0`.

**Terminal 3:** used for the `curl.exe` redirect check and `Format-Hex`.

**Browser:** `https://localhost:7280/swagger`, with document "v2" selected and the page scrolled to the top.

**Rehearsal:** run the whole script once without recording. Then reset before the real take:
- In the console, choose Logout and Exit.
- Delete `out\demo-device`.
- Restart the API with `$key1`.
- On the second run, request 50 answers with `duplicatesSkipped` 1 instead of `accepted` 1. Both are correct.

## 4. Demo data (fictional)

| Item | Value |
|---|---|
| Courier | `COURIER-DEMO-07` |
| Shipment, created by request 25 | `APX-00000011` |
| Unknown shipment, for the 404 | `APX-99999999` |
| Locations | `Pune Hub`, `Mumbai Gateway` |
| Scan types | `1` Pickup, `2` InTransit |

## 5. Keeping secrets off the screen

- **Variables:** never print `$key1`, `$key2`, `$partnerKey` or `$conn`, and never open User Secrets.
- **`.http` file:** do not show the responses of 0a and 0b. The requests shown in the video send `{{courierToken}}` and `{{adminToken}}` as variables; the editor displays the names, not the tokens.
- **Swagger UI:** open the **Authorize** dialog only to show the schemes. Do not enter a token or key.
- **Console:** it never displays the token. Paste-login (menu option 2) is not used.
- **`Format-Hex`:** use exactly the command in script step 03:

  ```powershell
  [IO.File]::ReadAllBytes("$env:LOCALAPPDATA\ApexPost\Courier\access-token.bin")[0..47] | Format-Hex
  ```

  It reads the bytes first, so the output's `Path:` line stays empty. The command text contains `$env:LOCALAPPDATA` unexpanded, so neither your Windows user name nor the full path appears. Do not use `Format-Hex -Path`: it prints the full path. Do not type `$env:LOCALAPPDATA` alone, or run `dir` or `Get-Item` on the token file.
- **Signing-key rotation (script step 06):**
  - The restart command shows only the variable name, `$key2`, never its value.
  - Terminal 1 then shows only the API's startup lines: the listening URLs, `Hosting environment: Development` and the content root path inside the repository.
  - The console shows the 401 notice, which contains no token.
  - Checked beforehand with throwaway keys and no database: after a rotation, the API's output, the 401 response body and its headers contained no token, signing key, partner key or connection string.
  - Do not scroll terminal 1 back to the setup commands of section 3.
- **Before publishing:** watch the whole recording once and check every frame for token-like text (`eyJ…`) or keys.

## 6. Windows and transitions

| Script step | Window |
|---|---|
| 00 | Editor: solution explorer |
| 01 | Editor: `ApexPost.Api.http` |
| 02 | Terminal 3 (`curl.exe`), editor (`Program.cs`), browser (Swagger UI) |
| 03 | Terminal 2 (console), terminal 3 (`Format-Hex`) |
| 04, 05 | Terminal 1 (stop and start the API), terminal 2 (console) |
| 06 | Terminal 1 (restart with `$key2`), terminal 2 (console) |
| 07 | Editor or browser: `docs/evidence/week4-part-d/README.md` |

Tip: place terminals 1 and 2 side by side for steps 04–06, so the API stopping and starting and the console's reaction are visible together.

## 7. Final recording checklist

- [ ] Length between 3 and 5 minutes (target 4 min 40 s).
- [ ] Every script step 00–07 recorded. In step 05, Sync Now waited at least 30 s after the circuit opened.
- [ ] The console showed: login, the scan synced, the 404 `Failed`, retries, the circuit opening, the banner, the scan saved while open, recovery, the 401 back to Login, Logout.
- [ ] Not claimed as live: sync on the network's return (airplane mode), timeouts, HSTS, batches of 20.
- [ ] No token, key or connection string visible anywhere.
- [ ] Audio clear; no personal or real data on screen.

## 8. After recording

1. Stop the console (Logout, then Exit) and the API (Ctrl+C). Close the editor that was started with the partner key.
2. Delete `out\demo-device`. Check that `access-token.bin` does not exist.
3. Drop the temporary database:

   ```powershell
   sqlcmd -S . -E -b -Q "ALTER DATABASE [ApexPost_Test_DemoVideo] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [ApexPost_Test_DemoVideo];"
   ```

4. Close the PowerShell session, so the keys and variables are gone.

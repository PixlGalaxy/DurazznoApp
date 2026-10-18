# Webin Cloud Server: update files

How Durazzno App gets its updates from Webin Cloud Server and how to publish a new version.
The signing key and how signatures work are in [`crypto/README.md`](../crypto/README.md).

## Where the files live

Webin Cloud Server is a plain file host. Durazzno uses two **path names**:

| Path name | Contents |
|---|---|
| `durazznoapp.main` | The app: `manifest.json`, `manifest.json.sig`, `DurazznoAppDaemon.exe` |
| `durazznoapp.apps` | Reserved for future apps (not used yet) |

Every file has a raw download link of this form:

```
{server}/backend/api/public/{path name}/{file}/download?inline=1&path=
```

For example:
`http://192.168.0.150:5002/backend/api/public/durazznoapp.main/manifest.json/download?inline=1&path=`

## Switching servers

The server address is one constant, `WebinCloud.Server` in
[`src/DurazznoApp/Network/WebinCloud.cs`](../src/DurazznoApp/Network/WebinCloud.cs). Durazzno Terminal compiles the
same file, so it changes too.

| | `WebinCloud.Server` |
|---|---|
| Test | `http://192.168.0.150:5002` |
| Production | `https://webincloud.itzgalaxy.com` |

Every link (manifest, signature, exe) is built from that constant: never write a server URL anywhere else.
To point a single PC somewhere else without rebuilding, set `"manifestUrl"` in
`%LOCALAPPDATA%\DurazznoApp\settings.json`, or run `DurazznoTerminal server <url>`. A bare `https://host` is enough.
`DurazznoTerminal server default` goes back to the built-in server.

## Publishing a new version

1. Raise `<Version>` in `src/DurazznoApp/DurazznoApp.csproj`, or pass `-Version`.
2. Build and sign:
   ```powershell
   .\scripts\publish.ps1 -Version 3.0.1 -Notes "What changed"
   # optional: -MinimumSupportedVersion 3.0.0  (older versions are told they are no longer supported)
   ```
   This needs `crypto\update-signing-key.xml` (see `crypto/README.md`). The script writes:
   - `dist\DurazznoApp\`: the ready-to-run app and DurazznoTerminal.exe.
   - `dist\server\`: the three files to upload.
3. Upload the three files from `dist\server\` to `durazznoapp.main`, **overwriting** the old ones with the **same
   names**, so the links do not change:
   - `manifest.json`
   - `manifest.json.sig`
   - `DurazznoAppDaemon.exe`

   Wait until the exe upload has finished. A half-uploaded file shows up as `DurazznoAppDaemon.exe.<id>.part` and
   returns 404.
4. Check it: `DurazznoTerminal status` should say `ONLINE · latest Durazzno App <version>`.

The upload order does not matter much. If a client reads a new manifest before the new exe is there, the download
fails its SHA-256 check, nothing is installed, and the client tries again later.

## What the app does with them

- **manifest.json** gives the version, the exe file name (relative to the manifest), its SHA-256 and size, and
  optionally `notes` and `minimumSupportedVersion`.
- **manifest.json.sig** is the RSA signature of the manifest. Unsigned or tampered manifests are ignored.
- **Every 10 s** a light HEAD request to the manifest drives the Online/Offline status. The manifest itself is read
  at start, every 15 minutes, when the connection comes back, and on *Check for updates*.
- **At Windows startup** with *Download and install updates automatically* on, a newer version is downloaded and
  installed before the app opens. If anything fails, the current version simply starts.
- **While running**, a newer version is downloaded in the background and installs at the next start. *Apply update*
  (title bar, Settings, tray) installs it right away, with a progress window.
- **With auto-update off**, nothing is downloaded. The user only gets the *Durazzno App Update* notification and the
  APPLY UPDATE button.
- **Installing**: the new exe is started with `--finish-update`. It waits for the old one to exit, keeps it as
  `DurazznoAppDaemon.exe.old` (Durazzno Terminal can roll back to it), takes its place and restarts. If something
  goes wrong, the old exe is put back.
- **Versions are only upgraded, never downgraded.** Uploading an older version does not roll clients back.

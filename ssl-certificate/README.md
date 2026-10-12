# ssl-certificate — local code-signing certificate

This folder holds a **code-signing certificate** (Authenticode) for `Build.cmd`. It is not a TLS/HTTPS certificate: the app is a desktop program, so a TLS certificate would not sign anything.

| File | Secret? | Purpose |
|---|---|---|
| `fvs-codesign.pfx` | **yes** | Signing certificate + private key (CN = Alon Reich (FVS local development)), valid 3 years |
| `fvs-codesign.password.txt` | **yes** | Password for the PFX |
| `fvs-dev-root-ca.key.pem` | **yes** | Private key of the local root CA (AES-256, same password). The build uses it to renew the signing certificate by itself. **Never delete it** |
| `fvs-dev-root-ca.cer` / `.pem` | no | Public certificate of the local root CA |
| `fvs-codesign.pem` | no | Public signing certificate |
| `install-dev-root.cmd` | no | Trusts the local root for YOUR Windows account (run once) |
| `remove-dev-root.cmd` | no | Removes that trust |
| `create-dev-codesign-cert.ps1` | no | Creates a NEW root. Refuses while the root exists (a new root breaks updates for every installed copy) |
| `*.bak-*` | **yes** | Backups made when the build renews the certificate (git-ignored) |

`.gitignore` here keeps every secret file out of git.

## How the build uses it
1. Run `ssl-certificate\install-dev-root.cmd` once and confirm the Windows prompt.
2. Run `Build.cmd`. When `FVS_SIGN_PFX` is not set, the build signs with this certificate, and `signtool verify /pa` passes on this machine.
3. Both `Build.cmd` and `dev_build.cmd` use it (one shared step).

## Automatic updates without buying a certificate (UPDATEPIN_01 / UPDATEPIN_02)
- The app has this folder's **root certificate built in** (`PublisherRoot.cs`). It trusts an update only when the installer and the signed update list (`update.json` + `update.sig.json`) chain to that exact root. So in-app updates install automatically on every PC, with no purchase and no trust prompt.
- The build checks the signing certificate on every run. Missing, broken, or under 90 days left → it issues a new one from the root key, with the same password, and keeps a `.bak-<time>` copy. No human step.
- **Never replace the root.** A new root means every copy already installed refuses all future updates.
- Copies released before UPDATEPIN_01 must be updated by hand once (download and run the installer).

## What a bought certificate still adds
Only the first download from the web: Windows SmartScreen shows "unknown publisher" for this root. To remove that, use a **publicly trusted** code-signing certificate (CA-issued OV/EV, or Azure Trusted Signing) and set:

```
set FVS_SIGN_PFX=C:\path\to\real-cert.pfx
set FVS_SIGN_PASS=********
```

Those variables take precedence over this folder. The update list is still signed with this folder's certificate, so the pin keeps working.

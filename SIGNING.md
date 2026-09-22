# Signing Windows releases

Tagged releases use Azure Artifact Signing with a **Public Trust** certificate profile.
The workflow signs `ClaudeUsage.exe` and `ClaudeUsage.Core.dll`, requests SHA-256 timestamps,
runs the application self-test, and verifies the packaged binaries before publishing.
Missing configuration, invalid signatures, missing timestamps, and unexpected publisher
identities stop publication. Branch and pull-request builds remain unsigned development artifacts.

## One-time Azure setup

1. Sign in to the [Azure portal](https://portal.azure.com/) with the account that will own
   the publisher identity and billing. Create or select an Azure subscription.
2. Follow Microsoft's [Artifact Signing setup guide](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)
   to register `Microsoft.CodeSigning`, create a signing account, complete identity validation,
   and create a **Public Trust** certificate profile. A Private Trust or Public Trust Test
   profile does not provide the public trust needed for this release.
3. Identity verification must be completed by the publisher in the Azure portal. For an
   individual publisher, ensure the Azure billing account has the Individual account type
   and the legal name/address match the identity documents. Service eligibility and billing
   are described in Microsoft's guide; creating the signing account is a paid-service setup.
4. Create a dedicated Microsoft Entra app registration and service principal for this repository.
   Grant it **Artifact Signing Certificate Profile Signer** at the certificate-profile scope.
   Follow Microsoft's [OIDC setup](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md).
   No client secret or certificate private key needs to be stored in GitHub.
5. Add a federated credential to the Entra app for the next release:

   | Field | Value |
   | --- | --- |
   | Issuer | `https://token.actions.githubusercontent.com` |
   | Subject | `repo:p-m-sousa/Claudeometer:ref:refs/tags/v0.2.4` |
   | Audience | `api://AzureADTokenExchange` |

   In the GitHub federated-credential form, choose repository `p-m-sousa/Claudeometer`,
   entity type **Tag**, and tag **v0.2.4**. This exact subject deliberately excludes branch
   and pull-request builds. Add a new exact-tag credential before each future release;
   remove credentials for old tags when rerunning those releases is no longer needed.

## GitHub configuration

Add these **repository variables** under
[Settings → Secrets and variables → Actions → Variables](https://github.com/p-m-sousa/Claudeometer/settings/variables/actions).
These are identifiers, not passwords. Do not add private keys, client secrets, or identity documents.

| Variable | Value from Azure |
| --- | --- |
| `AZURE_CLIENT_ID` | Entra app's Application (client) ID |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Signing account's subscription ID |
| `SIGNING_ENDPOINT` | Signing account's regional HTTPS endpoint |
| `SIGNING_ACCOUNT_NAME` | Artifact Signing account name |
| `SIGNING_CERTIFICATE_PROFILE` | Public Trust certificate profile name |
| `SIGNING_PUBLISHER_SUBJECT` | Exact subject distinguished name of the signing certificate |

For the publisher subject, download the profile's public certificate from Azure and read its
`Subject` with Windows PowerShell; retain the order, punctuation, spacing, and case:

```powershell
(Get-PfxCertificate -FilePath .\publisher.cer).Subject
```

The workflow compares the complete subject, rather than a short-lived certificate thumbprint,
so automatic certificate rotation can retain the same publisher identity.

## Publish after setup

Before creating `v0.2.4`, confirm identity validation is complete, the Public Trust profile is
active, the signer role and federated credential are assigned, and all seven variables are set.
Update the project, packaging script, README build example, and CI version to `0.2.4`, then commit,
push, and create/push the `v0.2.4` tag. Do not overwrite the existing unsigned `v0.2.3` assets.

The tag workflow authenticates with OIDC, signs, verifies, tests, packages, and publishes.
It has no unsigned fallback. On a clean Windows machine, verify the downloaded/extracted release:

```powershell
Get-AuthenticodeSignature .\ClaudeUsage.exe, .\ClaudeUsage.Core.dll |
    Format-List Path, Status, SignerCertificate, TimeStamperCertificate
```

Both binaries must have `Valid` signatures with the expected publisher and timestamp.
The `.cmd` installation scripts are not Authenticode-signable executables; signing the app does
not sign those scripts. Installation remains per-user without elevation.

Signing establishes publisher identity, but a new publisher or binary may still receive
[SmartScreen reputation warnings](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
No automated test here verifies SmartScreen reputation or replaces manual Windows UI checks.

## Local packaging and checks

Ordinary local packages remain available without signing. To enforce signatures on already-signed
build output, use:

```powershell
./scripts/package-release.ps1 -NoBuild -Version 0.2.4 -RequireSigned -ExpectedPublisherSubject '<exact certificate subject>'
```

`scripts/test-release-signatures.ps1` runs on Windows CI before signing. It verifies that missing
binaries and unsigned builds are rejected, and that signed-release packaging cannot emit an
unsigned ZIP. Positive signature verification runs on every configured release, including an
extracted copy of the final ZIP.

# Publishing a TalkToMe release

Each stable version produces one public GitHub Release with downloads for Windows x64, Apple silicon, and Intel Macs. Use a stable version tag such as `v1.2.3`. Each of the three numbers must be between 0 and 65534, without leading zeros.

Publish a stable release with all three platforms by default, including for Mac-specific fixes. Use a preview or a platform-only release only when explicitly requested. Do not publish until all three packages have passed their checks.

## Publish from the GitHub UI

1. Open **Actions → Publish release → Run workflow**. [Open the workflow](https://github.com/SergeyMakeev/TalkToMe/actions/workflows/release.yml)
2. Select the branch containing the commit you want to ship (normally **main**). Enter the new tag in **Version tag**, for example `v1.2.3`.
3. Leave **Publish to GitHub Releases after all checks pass** checked and click **Run workflow**. To rehearse, clear the checkbox. A rehearsal runs the same builds and archive checks, and leaves the downloads in the Actions run.
4. Wait for the **release** job to finish. It tags the exact commit that was built, uses `.github/releases/<tag>.md` when present (otherwise generated notes), uploads all three archives to a draft, and publishes them together. [View releases](https://github.com/SergeyMakeev/TalkToMe/releases)

Running this workflow requires write access to the repository. The publishing job uses the built-in GitHub Actions token. Branch pushes and pull requests run the usual build checks.

## Publish by pushing a tag

To ship a particular commit, use its SHA in place of `origin/main`. Replace the example version with the one you intend to publish.

```sh
git fetch origin main
git tag -a v1.2.3 origin/main -m "TalkToMe v1.2.3"
git push origin refs/tags/v1.2.3
```

Pushing the tag starts the same workflow with publishing enabled. Choose either the GitHub UI or the tag push for a given release.

## Downloads

| Release asset | Installation |
| --- | --- |
| `TalkToMe-Windows-x64.zip` | Extract the whole archive, open `TalkToMe.exe`, choose speech and optional translation models, and click **Download and save**. The app includes its .NET, Whisper, Parakeet and llama.cpp runtimes. |
| `TalkToMe-macOS-arm64.dmg` | For Apple silicon. Open the DMG, drag TalkToMe to Applications, and eject the disk image. |
| `TalkToMe-macOS-x86_64.dmg` | For Intel Macs. Open the DMG, drag TalkToMe to Applications, and eject the disk image. |

See the [app README](../../README.md) for microphone permissions, speech models, and llama-server setup.

## Mac previews

For a Mac-only fix, run **Actions → macOS build → Run workflow**, select the source branch, leave **test_release** empty, and enter an optional app version such as `0.2.2`. This builds and checks both Mac architectures without rebuilding the Windows GPU engines. The workflow uploads DMGs as Actions artifacts; it does not publish a release.

To share a preview, publish only artifacts whose architecture-specific build and installer checks passed. Point its tag to the exact commit recorded in that run, use a separate tag such as `macos-v0.2.2-preview.1`, and mark it as a **pre-release**, not the latest stable release. This tag prefix does not trigger the stable `v*` release workflow. Identify any missing architecture or unfinished physical-Mac tests in the release notes. Do not replace assets from an already published version with a different build.

## Apple signing for public distribution

By default the disk images contain ad-hoc signed apps for personal use. Gatekeeper can ask users to approve the app in **Privacy & Security**. For normal first launch on other Macs, enable Developer ID signing and Apple notarization for versioned builds.

1. With an Apple Developer Program membership, export a **Developer ID Application** certificate and its private key as a password-protected `.p12` from Keychain Access. Create a **team App Store Connect API key** authorized to submit software for notarization. Keep its issuer ID, key ID and downloaded `.p8` file. [Apple’s notarization instructions](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
2. In the GitHub repository, open **Settings → Secrets and variables → Actions → Secrets** and add the following repository secrets.

   | Secret | Value |
   | --- | --- |
   | `MACOS_DEVELOPER_ID_P12` | Base64 contents of the exported `.p12`. On a Mac, `base64 -i DeveloperID.p12 \| pbcopy` copies this for pasting into the secret field. |
   | `MACOS_P12_PASSWORD` | Password used when exporting the `.p12` |
   | `MACOS_NOTARY_KEY_P8` | Entire text of the team API private key, including its BEGIN and END lines |
   | `MACOS_NOTARY_KEY_ID` | App Store Connect key ID |
   | `MACOS_NOTARY_ISSUER_ID` | App Store Connect issuer ID |

3. On the **Variables** tab, set the repository variable `MACOS_NOTARIZE_RELEASES` to `true`. Run a versioned rehearsal with **Publish** cleared. Each Mac job signs the app with the hardened runtime and microphone entitlement, signs the disk image, submits it to Apple, staples the accepted ticket, and checks the image and installed app with Gatekeeper. A signing or notarization failure stops publication.
4. Once the rehearsal passes, run **Publish release** again with **Publish** selected. Regular push and pull-request builds continue to produce personal-use DMGs.

## Retry a failed release

Open the failed Actions run, fix the reported problem, and select **Re-run failed jobs** to retry the same commit. If an upload was interrupted, the next attempt fills in the draft and publishes it once all three archives are present. If the fix requires a code change, commit it and use a new version. The workflow rejects an existing tag that points at another commit and protects an already published version from replacement.

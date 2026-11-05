# Publishing a TalkToMe release

Each version produces one public GitHub Release with downloads for Windows x64, Apple silicon, and Intel Macs. Use a stable version tag such as `v1.2.3`. Each of the three numbers must be between 0 and 65534, without leading zeros.

## Publish from the GitHub UI

1. Open **Actions → Publish release → Run workflow**. [Open the workflow](https://github.com/SergeyMakeev/TalkToMe/actions/workflows/release.yml)
2. Select the branch containing the commit you want to ship (normally **main**). Enter the new tag in **Version tag**, for example `v1.2.3`.
3. Leave **Publish to GitHub Releases after all checks pass** checked and click **Run workflow**. To rehearse, clear the checkbox. A rehearsal runs the same builds and archive checks, and leaves the downloads in the Actions run.
4. Wait for the **release** job to finish. It tags the exact commit that was built, generates release notes, uploads all three archives to a draft, and publishes them together. [View releases](https://github.com/SergeyMakeev/TalkToMe/releases)

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
| `TalkToMe-Windows-x64.zip` | Extract the whole archive. Install x64 Python 3.11 with the Windows `py` launcher, run `.\setup.ps1` (or `.\setup.ps1 -Cpu`), then open `TalkToMe.cmd`. The app and .NET runtime are bundled. |
| `TalkToMe-macOS-arm64.zip` | For Apple silicon. Extract and move `TalkToMe.app` to Applications. |
| `TalkToMe-macOS-x86_64.zip` | For Intel Macs. Extract and move `TalkToMe.app` to Applications. |

See the [app README](../../README.md) for microphone permissions, speech models, and llama-server setup.

## Retry a failed release

Open the failed Actions run, fix the reported problem, and select **Re-run failed jobs** to retry the same commit. If an upload was interrupted, the next attempt fills in the draft and publishes it once all three archives are present. If the fix requires a code change, commit it and use a new version. The workflow rejects an existing tag that points at another commit and protects an already published version from replacement.

---
name: Publish New Version
description: Bumps version and triggers a new Lumi release via GitHub Actions
---

# Publish New Version

When the user asks to publish or release a new version of Lumi:

1. **Check current version**: Read the `<Version>` tag in `src/Lumi/Lumi.csproj`
2. **Determine new version**: Bump the patch version (e.g., 0.1.0 → 0.2.0), or ask the user if they want a specific version
3. **Update csproj**: Edit the `<Version>` tag in `src/Lumi/Lumi.csproj` to the new version
4. **Commit and push**: 
   ```
   git add src/Lumi/Lumi.csproj
   git commit -m "bump version to X.Y.Z"
   git push origin main
   ```
5. **Trigger release workflow**:
   ```powershell
   gh workflow run release.yml -f version=X.Y.Z
   ```
6. **Monitor the build**: 
   ```powershell
   gh run list --workflow="release.yml" -L 1
   gh run watch <run-id> --exit-status
   ```
7. **Verify**: Once complete, confirm the release is published:
   ```powershell
   gh release view vX.Y.Z
   ```

## Important Notes
- The version must be valid SemVer (e.g., 1.0.0, 0.2.0)
- The release workflow builds for Windows x64, signs with Azure Trusted Signing, and publishes to GitHub Releases
- The `gh` CLI must be authenticated (`gh auth status`)
- Always bump version in csproj BEFORE triggering the workflow

# Contributing to MissionView

You can contribute from a public fork. You do not need write access to this repository.

1. For a bug, open an [issue](https://github.com/JUANES545/missionview/issues) with your Windows build, MissionView version, and steps to reproduce it. Check screenshots and logs for window titles, file paths, and other private information before attaching them. For security issues, follow the instructions below.
2. Fork the repository, sync your fork with [upstream `develop`](https://github.com/JUANES545/missionview/tree/develop), and create a branch such as `feature/improve-window-search`. Use `feature/<name>` for fixes and features: one lowercase name with letters, digits, or hyphens, and no username prefix.
3. Open a pull request from your feature branch to `JUANES545/missionview:develop`. Describe what changed, how to verify it, and any related issue. Keep the change focused and include test coverage for behavior you change.
4. Wait for the Delivery **Validate** check and maintainer review. A maintainer may need to approve workflow runs for a first-time contributor. If you retarget a PR and Delivery does not run, ask a maintainer to review its CI state before merging.

Maintainers promote `develop` from this repository to `main` by pull request. Use a merge commit for that promotion to preserve history. A merge to `main` starts the Windows release pipeline; do not target `main` with a contribution.

## Reporting security issues

Do not post vulnerability details, proof-of-concept code, credentials, or affected user data in public issues, discussions, or pull requests. GitHub private vulnerability reporting is not enabled for MissionView. Ask a maintainer for a private reporting channel before sharing details.

# Contributing

## Branching model

```
feature/<name> ─┐
fix/<name> ─────┴─▶ develop ──▶ release/x.y.z ──▶ main  (tag vx.y.z)
```

| Branch | Cut from | Merges into | How |
|---|---|---|---|
| `feature/<name>`, `fix/<name>` | `develop` | `develop` | Pull request, squash or merge. The branch is deleted on merge. |
| `release/x.y.z` | `develop` | `main` | Pull request. **Never merged by hand** — see below. |
| `develop`, `main` | — | — | Protected: no direct pushes, no force pushes, no deletion. |

Names are lowercase: letters, digits, `.`, `_` and `-`. A `release/` branch is a snapshot of
`develop` and carries no commits of its own: a fix for a release lands on `develop` through a
`fix/` branch and a new release branch is cut.

The **Branch Policy** workflow checks all of this on every pull request and is a required check
on `develop` and `main`.

## Releasing

1. `git switch develop && git pull && git switch -c release/1.4.0 && git push -u origin release/1.4.0`
   — Jenkins deploys the branch to **homologation**.
2. Open a pull request `release/1.4.0 → main`.
3. When every GitHub check on the pull request passes, Jenkins deploys to **production**. On
   success it sets the `deploy/production` status, merges the pull request with a merge commit,
   creates the tag and GitHub release `v1.4.0`, and deletes the release branch.
4. If the production deploy fails, Jenkins rolls back to the previous image and the pull request
   stays open. Fix on `develop`, then cut a new release.

The repository owner can bypass these rules. That is for emergencies, not for routine work.

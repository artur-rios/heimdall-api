# Project Board Operations

The Heimdall backlog is tracked on **GitHub Project #7** (private, owner `artur-rios`), and
each use case has one issue whose title starts with its UC number (e.g. `UC-03: Update Scope`).

The board's `Status` field drives the workflow lifecycle: **Todo → In Progress → Testing → Done**.

> Reminder from SKILL.md: the **only** status change to make without asking is `Todo → In Progress`.
> Moving to Testing, and to Done, each require the user's approval first.

## Known IDs (as of skill authoring)

These rarely change, but verify with the discovery commands below if an edit fails.

| Thing | Value |
| --- | --- |
| Project number | `7` |
| Owner | `artur-rios` |
| Project ID | `PVT_kwHOAgOUtM4BeVUr` |
| Status field ID | `PVTSSF_lAHOAgOUtM4BeVUrzhYwWz0` |
| Status option — Todo | `f75ad846` |
| Status option — In Progress | `47fc9ee4` |
| Status option — Testing | `2ac58cf7` |
| Status option — Done | `98236657` |

## 1. Find the issue for a UC

```bash
gh issue list --repo artur-rios/heimdall-api --search "UC-03 in:title" \
  --state all --json number,title -q '.[] | "#\(.number) \(.title)"'
```

## 2. Find the board item ID for that issue

`gh project item-edit` needs the **project item** ID, not the issue number. Look it up by the issue's
number (replace `4` with the issue number):

```bash
gh project item-list 7 --owner artur-rios --format json --limit 100 \
  -q '.items[] | select(.content.number == 4) | .id'
```

## 3. Change the Status

Set the Status single-select option (replace `ITEM_ID` and the option ID as needed):

```bash
gh project item-edit \
  --project-id "PVT_kwHOAgOUtM4BeVUr" \
  --id "ITEM_ID" \
  --field-id "PVTSSF_lAHOAgOUtM4BeVUrzhYwWz0" \
  --single-select-option-id "47fc9ee4"   # 47fc9ee4 = In Progress
```

Swap the `--single-select-option-id` for the target column: `47fc9ee4` (In Progress), `2ac58cf7`
(Testing), `98236657` (Done).

## 4. Close the issue (Step 8, after approval)

If the PR merged with a `Closes #<n>` reference, the issue closes automatically — just confirm. To
close explicitly:

```bash
gh issue close <n> --repo artur-rios/heimdall-api --reason completed
```

## Rediscovering IDs if they ever change

```bash
# Project ID + Status field ID + option IDs
gh project field-list 7 --owner artur-rios --format json \
  -q '.fields[] | select(.name=="Status") | {id, options: .options}'
```

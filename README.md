# Jira Exporter

## Getting started

Create `appsettings.local.json` in the project directory. The file is gitignored;
`appsettings.json` is the template.

```json
{
    "Jira": {
        "BaseApiUrl": "https://<jira-host>/rest/api/latest",
        "Username": "user@example.com",
        "Password": "...",
        "ProjectQuery": "type in ('<scoping-issue-type>') AND Team = <team>",
        "Fields": {
            "estimation": "customfield_XXXXX",
            "prodDate": "customfield_XXXXX"
        }
    },
    "Email": {
        "AutodiscoverAddress": "user@example.com",
        "ReportRecipient": "recipient@example.com"
    }
}
```

Settings are read in order: template, then the local file, then environment
variables — `JIRA_USERNAME`, `JIRA_PASSWORD`, `JIRA_BASE_API_URL`,
`JIRA_PROJECT_QUERY`, `EMAIL_AUTODISCOVER_ADDRESS`,
`EMAIL_REPORT_RECIPIENT`. If a required setting is missing the program says which
one and exits.

Then:

```
dotnet run -- fetch
```

## Commands

| Command | What it does |
|---|---|
| `fetch` | Reads issues from Jira and writes them to `data/` |
| `report` | Builds Excel and JSON reports from previously fetched data |
| `all` | `fetch`, then `report`, then emails the report |
| `checkforupdates` | Emails what changed since the last snapshot |

`fetch` and `report` run anywhere. `all` and `checkforupdates` send mail through
Exchange Web Services as the signed-in Windows account, and autodiscover resolves
the server through a Windows system library, so those two need Windows.

## Change tracking

`fetch` reads every work package the query matches and writes the snapshot.
`checkforupdates` is the one meant to run on a schedule: it reports the difference
against that snapshot instead of re-reading everything.

A run makes two searches — the scoping query, and one `key in (...)` batch for the
project issues — and then fetches in full only the work packages whose `updated`
timestamp actually moved. New and removed work packages fall out of the same
comparison, so a newly created scoping issue is reported rather than missed.

The differences go out as an HTML table in the mail body, with no attachment. The
snapshot is rewritten only after the mail has been sent, so a failed send leaves
the same changes to be reported on the next run rather than losing them.

Compared fields are the report's own columns, minus the ones that carry no signal:
identity (`ProjectKey`, `Proposal Scoping Task`), timestamps that always move
(`Created`, `Updated`, `Proposal Scoping Created`), `Latest Date` (derived from
the phase dates it would duplicate), `Summary`, `Error Message`, and the relation
counts (`Links`, `Subtasks`). That leaves status, resolution, both assignees,
issue type, estimate, budget, the scoping issue's resolved date and the phase dates.

## Field mapping

Custom field IDs differ between Jira installations, so they are not hardcoded. The
code refers to fields by logical name — `estimation`, `prodDate`,
`actualUatStart` — and `Jira.Fields` maps each to the real field in your
installation. List the available fields with:

```
GET /rest/api/latest/field
```

A name that has no entry in the map is treated as a standard Jira field
(`summary`, `status`, `assignee`) and used as-is.

## How it works

The query returns scoping issues. Each one has a parent, which is the project
issue. The two together form a `WorkPackage`, identified by
`{ProjectTask.Key}_{ProposalScopingTask.Key}`.

A project can have several scoping issues, and estimates and budgets vary between
them rather than across the project as a whole — so the work package, not the
project, is the unit the reports are built on.

## Completed projects drop out after two months

A project counts as closed when its status is in Jira's `done` category, or is
named `Done` or `Cancelled`. Its work packages are then written to
`data/done-or-cancelled-projects-and-proposals.csv`:

```
ProjectKey;ProposalScopingKey;Status;Resolved;DetectedAt
DENIZ-40348;DENIZ-42560;Done;2022-12-12;2026-09-30 10:12:16
```

`Resolved` is Jira's resolution date; `DetectedAt` is the run that noticed the
closing. For two months after `DetectedAt` the work package is still fetched and
reported, so the closing reaches whatever consumes the reports even if one report
is never delivered; after that it is skipped. A project reopened within those two
months is removed from the file and tracked again.

The file is separated by semicolons so Excel with a Turkish locale opens it in
columns. The older free-text `.txt` file is converted on the first run and kept as
`.txt.bak`; its rows have no `Resolved` date.

Later changes to a project that has dropped out are never picked up, and `fetch
--reset` deletes the file, so the next run reports every closed project again.
Whatever consumes the output should upsert rather than treat each run as the full
picture.

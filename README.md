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
        "SupportQuery": "project = <key> AND resolution = Unresolved",
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
`JIRA_PROJECT_QUERY`, `JIRA_SUPPORT_QUERY`, `EMAIL_AUTODISCOVER_ADDRESS`,
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
| `checkforupdates` | Reports which issues changed since the last fetch |

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

## Completed projects are fetched once

When a project reaches `Done` or `Cancelled` it is written to
`data/done-or-cancelled-projects-and-proposals.txt` and excluded from later runs.
It is processed one final time before being excluded, so the last snapshot of a
completed project is whatever it looked like at that moment.

Two consequences: later changes to a completed project are never picked up, and
projects completed before this tool first ran never appear at all. Whatever
consumes the output should keep its own archive rather than treating each run as
the full picture.

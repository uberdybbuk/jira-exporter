namespace FourArc.JiraExporter;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public class JiraFieldInfoAttribute(string fieldName, string subFieldName = null) : Attribute
{
    public string FieldName { get; } = fieldName;
    public string SubFieldName { get; } = subFieldName;
}

// A single Jira issue link, kept with its type and direction so different link types
// (Relates, Request To Epic, ...) can be told apart during later analysis.
public class JiraIssueLink
{
    public string LinkType { get; set; }   // e.g. "Relates", "Request To Epic"
    public string Direction { get; set; }  // "inward" or "outward"
    public string Relation { get; set; }   // the link type's phrase for this direction, e.g. "is sliced by"
    public string Key { get; set; }        // the linked issue's key
    public string Summary { get; set; }    // the linked issue's summary
    public string Status { get; set; }     // the linked issue's status
    public string IssueType { get; set; }  // the linked issue's issue type
}

// A single sub-task of an issue, with just the bits needed for later analysis.
public class JiraSubtask
{
    public string Key { get; set; }        // the sub-task's key
    public string Summary { get; set; }    // fields.summary
    public string Status { get; set; }     // fields.status.name
    public string IssueType { get; set; }  // fields.issuetype.name
}

public class JiraIssue(string key)
{
    public string Key { get; } = key;

    [JiraFieldInfo("parent", "key")]
    public string ParentKey { get; set; }

    [JiraFieldInfo("issuetype", "name")]
    public string IssueType { get; set; }

    [JiraFieldInfo("summary")]
    public string Summary { get; set; }

    [JiraFieldInfo("description")]
    public string Description { get; set; }

    [JiraFieldInfo("assignee", "displayName")]
    public string AssigneeDisplayName { get; set; }

    [JiraFieldInfo("assignee", "emailAddress")]
    public string AssigneeOrgEmail { get; set; }

    [JiraFieldInfo("assignee", "emailAddress")]
    public string AssigneeEmail { get; set; }

    [JiraFieldInfo("reporter", "displayName")]
    public string Reporter { get; set; }

    [JiraFieldInfo("status", "name")]
    public string Status { get; set; }

    // The status's category key: "new", "indeterminate" or "done". Read from the status
    // object, which is nested too deep for JiraFieldInfo.
    public string StatusCategory { get; set; }

    // Closed by category, so a done-category status with any name ("Closed", "Rejected")
    // counts. The two names are kept for statuses whose category says otherwise and for
    // snapshots written before StatusCategory was captured.
    public bool IsClosed => StatusCategory == "done" || Status is "Done" or "Cancelled";

    [JiraFieldInfo("resolution", "name")]
    public string Resolution { get; set; }

    [JiraFieldInfo("created")]
    public DateTime Created { get; set; }

    [JiraFieldInfo("updated")]
    public DateTime Updated { get; set; }

    // Empty until the issue is resolved, and cleared again if it is reopened.
    [JiraFieldInfo("resolutiondate")]
    public DateTime? Resolved { get; set; }

    [JiraFieldInfo("team", "name")]
    public string Team { get; set; }

    [JiraFieldInfo("company", "value")]
    public string Company { get; set; }

    [JiraFieldInfo("level3Team")]
    public string Level3Team { get; set; }

    [JiraFieldInfo("initiative")]
    public string Initiative { get; set; }

    [JiraFieldInfo("program")]
    public string Program { get; set; }

    [JiraFieldInfo("group")]
    public string Group { get; set; }

    [JiraFieldInfo("groupManager")]
    public string GroupManager { get; set; }

    [JiraFieldInfo("sponsor")]
    public string Sponsor { get; set; }

    [JiraFieldInfo("bankProjectManagementDepartment", "value")]
    public string BankProjectManagementDepartment { get; set; }

    [JiraFieldInfo("estimation")]
    public decimal? Estimation { get; set; }

    [JiraFieldInfo("salesForceBudget")]
    public decimal? SalesForceBudget { get; set; }

    [JiraFieldInfo("salesForceStatus", "value")]
    public string SalesForceStatus { get; set; }

    [JiraFieldInfo("actualScopeStart")]
    public DateOnly? ActualScopeStartDate { get; set; }

    [JiraFieldInfo("actualScopeEnd")]
    public DateOnly? ActualScopeEndDate { get; set; }

    [JiraFieldInfo("actualPlannedStart")]
    public DateOnly? ActualPlannedStartDate { get; set; }

    [JiraFieldInfo("actualAnalysisStart")]
    public DateOnly? ActualAnalysisStartDate { get; set; }

    [JiraFieldInfo("actualAnalysisEnd")]
    public DateOnly? ActualAnalysisEndDate { get; set; }

    [JiraFieldInfo("developmentStart")]
    public DateOnly? DevelopmentStartDate { get; set; }

    [JiraFieldInfo("actualDevelopmentStart")]
    public DateOnly? ActualDevelopmentStartDate { get; set; }

    [JiraFieldInfo("actualDevelopmentEnd")]
    public DateOnly? ActualDevelopmentEndDate { get; set; }

    [JiraFieldInfo("uatEnd")]
    public DateOnly? UATEndDate { get; set; }

    [JiraFieldInfo("uatStart")]
    public DateOnly? UATStartDate { get; set; }

    [JiraFieldInfo("actualUatEnd")]
    public DateOnly? ActualUATEndDate { get; set; }

    [JiraFieldInfo("actualUatStart")]
    public DateOnly? ActualUATStartDate { get; set; }

    [JiraFieldInfo("actualPreProdStart")]
    public DateOnly? ActualPreProdStartDate { get; set; }

    [JiraFieldInfo("actualPreProdEnd")]
    public DateOnly? ActualPreProdEndDate { get; set; }

    [JiraFieldInfo("actualSecurityApprovalEnd")]
    public DateOnly? ActualSecurityApprovalEndDate { get; set; }

    [JiraFieldInfo("actualProdDate")]
    public DateOnly? ActualProdDate { get; set; }

    [JiraFieldInfo("prodDate")]
    public DateOnly? ProdDate { get; set; }

    // Every issue link on this task, captured with type and direction so different link
    // types can be analysed later. The "main project" shows up here as an inward link.
    public List<JiraIssueLink> IssueLinks { get; set; } = [];

    // Sub-tasks of this issue, populated only when 'subtasks' was requested.
    public List<JiraSubtask> Subtasks { get; set; } = [];

    // The main project/epic this task is linked to, taken from its first inward issue link.
    public JiraIssueLink MainProjectLink => IssueLinks.FirstOrDefault(l => l.Direction == "inward");
    public string MainProjectKey => MainProjectLink?.Key;
    public string MainProjectSummary => MainProjectLink?.Summary;
    public string MainProjectLinkType => MainProjectLink?.LinkType;

    // Logical field names of every property carrying JiraFieldInfo. Translating them to
    // real Jira field names is JiraSettings.ResolveField's job.
    public static string[] GetCustomFields(string filter = null)
    {
        var customFields = typeof(JiraIssue).GetProperties()
            .Where(p => p.Name.Contains(filter ?? ""))
            .Where(p => p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false).Length > 0)
            .Select(p => (JiraFieldInfoAttribute)p.GetCustomAttributes(typeof(JiraFieldInfoAttribute), false)[0])
            .Select(attr => attr.FieldName)
            .ToArray();
        return customFields;
    }
}

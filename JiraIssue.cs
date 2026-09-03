namespace FourArc.JiraExporter;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public class JiraFieldInfoAttribute(string fieldName, string subFieldName = null) : Attribute
{
    public string FieldName { get; } = fieldName;
    public string SubFieldName { get; } = subFieldName;
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

    [JiraFieldInfo("resolution", "name")]
    public string Resolution { get; set; }

    [JiraFieldInfo("created")]
    public DateTime Created { get; set; }

    [JiraFieldInfo("updated")]
    public DateTime Updated { get; set; }

    [JiraFieldInfo("team", "name")]
    public string Team { get; set; }

    [JiraFieldInfo("company", "value")]
    public string Company { get; set; }

    [JiraFieldInfo("level3Team")]
    public string Level3Team { get; set; }

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

    public HashSet<string> InwardLinkedIssueKeys { get; } = [];

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

namespace FourArc.JiraExporter;

public class WorkPackage
{
    public WorkPackage(JiraIssue projectTask, JiraIssue proposalScopingTask)
    {
        ProjectTask = projectTask;
        ProposalScopingTask = proposalScopingTask;
    }

    public JiraIssue ProjectTask { get; set; }
    public JiraIssue ProposalScopingTask { get; set; }

    public string UniqueId => $"{ProjectTask?.Key}_{ProposalScopingTask?.Key}";

    public List<string> ErrorMessages { get; } = [];
}

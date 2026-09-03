using Microsoft.Exchange.WebServices.Data;

namespace FourArc.JiraExporter;

// Sends mail through Exchange Web Services using the signed-in Windows account.
public class ExchangeEmailService
{
    private readonly ExchangeService _service;

    public ExchangeEmailService(string autodiscoverEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autodiscoverEmail);

        _service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
        {
            UseDefaultCredentials = true
        };

        _service.AutodiscoverUrl(autodiscoverEmail, url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    }
    public void SendEmail(string to, string subject, string body)
    {
        var email = new EmailMessage(_service);
        email.ToRecipients.Add(to);
        email.Subject = subject;
        email.Body = new MessageBody(body);
        email.SendAndSaveCopy();
    }
    public void SendHtmlEmail(string to, string subject, string html)
    {
        var email = new EmailMessage(_service);
        email.ToRecipients.Add(to);
        email.Subject = subject;
        email.Body = new MessageBody(BodyType.HTML, html);
        email.SendAndSaveCopy();
    }
    public void SendEmailWithAttachment(string to, string subject, string body, string attachmentPath)
    {
        var email = new EmailMessage(_service);
        email.ToRecipients.Add(to);
        email.Subject = subject;
        email.Body = new MessageBody(body);
        email.Attachments.AddFileAttachment(attachmentPath);
        email.SendAndSaveCopy();
    }
    public static void Send(string to, string subject, string body, string autodiscoverEmail)
    {
        var emailService = new ExchangeEmailService(autodiscoverEmail);
        emailService.SendEmail(to, subject, body);
    }
    public static void SendHtml(string to, string subject, string html, string autodiscoverEmail)
    {
        var emailService = new ExchangeEmailService(autodiscoverEmail);
        emailService.SendHtmlEmail(to, subject, html);
    }
    public static void SendWithAttachment(string to, string subject, string body, string attachmentPath, string autodiscoverEmail)
    {
        var emailService = new ExchangeEmailService(autodiscoverEmail);
        emailService.SendEmailWithAttachment(to, subject, body, attachmentPath);
    }
}
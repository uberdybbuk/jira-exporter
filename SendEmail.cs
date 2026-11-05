using Microsoft.Exchange.WebServices.Data;
using Microsoft.Extensions.Logging;

namespace FourArc.JiraExporter;

// Sends mail through Exchange Web Services using the signed-in Windows account.
public static class ExchangeEmailService
{
    // A failed send is reported as one line and returned as false; the run's result on disk stays usable.
    public static bool TrySend(EmailSettings settings, ILogger logger, string subject, string html, string attachmentPath = null)
    {
        try
        {
            var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1) { UseDefaultCredentials = true };
            service.AutodiscoverUrl(settings.From, url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            var email = new EmailMessage(service) { Subject = subject, Body = new MessageBody(BodyType.HTML, html) };
            email.ToRecipients.AddRange(settings.ToAddresses);
            email.CcRecipients.AddRange(settings.CcAddresses);
            if (attachmentPath is not null)
            {
                email.Attachments.AddFileAttachment(attachmentPath);
            }

            email.SendAndSaveCopy();
            logger.LogInformation("Sent '{Subject}' to {To}", subject, string.Join(", ", settings.ToAddresses));
            return true;
        }
        catch (Exception e)
        {
            logger.LogError("Mail could not be sent: {Reason}", e.Message);
            if (attachmentPath is not null)
            {
                logger.LogError("The file is still on disk: {File}", attachmentPath);
            }

            return false;
        }
    }
}

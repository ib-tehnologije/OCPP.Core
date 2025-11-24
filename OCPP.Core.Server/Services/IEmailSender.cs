using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
    }
}

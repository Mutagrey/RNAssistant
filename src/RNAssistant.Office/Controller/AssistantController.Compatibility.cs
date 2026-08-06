using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public Task<ModelCompatibilityResponse> TestModelCompatibilityAsync(CancellationToken cancellationToken)
        {
            return new ModelCompatibilityService(_llmCompletion).TestAsync(
                _settingsService.Load(),
                cancellationToken);
        }
    }
}

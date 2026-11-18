using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Optional, fixed language analysis. No model-authored JavaScript, Office
    // access, source evaluation, filesystem or network capability is granted.
    public interface IPowerQuerySyntaxAnalyzer
    {
        bool IsAvailable { get; }
        Task<PowerQuerySyntaxAnalysis> AnalyzeAsync(PowerQuerySyntaxRequest request, CancellationToken token);
    }
}

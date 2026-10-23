using System;

namespace RNAssistant.Harness
{
    public static class V8UnknownEffectFixtureBridge
    {
        public static void Run() { Program.RunUnknownEffectFixture(); }
    }

    internal static partial class Program
    {
        internal static void RunUnknownEffectFixture()
        {
            AgentExcelUnknownContinuesThroughInspection();
            Console.WriteLine("PASS Excel unknown effect survives inspection, remaining work and finish");
        }
    }
}

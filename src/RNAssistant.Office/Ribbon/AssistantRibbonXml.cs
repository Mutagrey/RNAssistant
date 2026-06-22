namespace RNAssistant.Office.Ribbon
{
    public static class AssistantRibbonXml
    {
        public static string Create(string hostName)
        {
            return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<customUI xmlns=""http://schemas.microsoft.com/office/2009/07/customui"" onLoad=""OnRibbonLoad"">
  <ribbon>
    <tabs>
      <tab id=""rnAssistantTab"" label=""RN Assistant"">
        <group id=""rnAssistantMain"" label=""Agent"">
          <button id=""openAssistant"" label=""Open Assistant"" size=""large"" imageMso=""HappyFace"" onAction=""OpenAssistant"" />
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";
        }
    }
}

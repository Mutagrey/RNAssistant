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
        <group id=""rnAssistantMain"" label=""Assistant"">
          <button id=""openAssistant"" label=""Open Assistant"" size=""large"" imageMso=""HappyFace"" onAction=""OpenAssistant"" />
          <button id=""summarize"" label=""Summarize"" imageMso=""ReviewNewComment"" onAction=""Summarize"" />
          <button id=""explainSelection"" label=""Explain Selection"" imageMso=""ResearchPane"" onAction=""ExplainSelection"" />
          <button id=""draftRewrite"" label=""Draft / Rewrite"" imageMso=""CreateMailRule"" onAction=""DraftRewrite"" />
          <button id=""runSkill"" label=""Run Skill"" imageMso=""MacroPlay"" onAction=""RunSkill"" />
        </group>
        <group id=""rnAssistantManage"" label=""Manage"">
          <button id=""settings"" label=""Settings"" imageMso=""AdpPrimaryKey"" onAction=""OpenSettings"" />
          <button id=""context"" label=""Context"" imageMso=""FileDocumentInspect"" onAction=""OpenContext"" />
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";
        }
    }
}


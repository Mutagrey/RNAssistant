using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        internal IReadOnlyList<WorkspaceToolContribution> CaptureTools(WorkspaceDescriptor workspace)
        {
            var tools=new List<WorkspaceToolContribution>();
            lock(_sync)
            {
                var registrations=_store.List();
                foreach(var plugin in registrations)
                {
                    if(plugin.Hidden)continue;
                    if(plugin.SourceWorkspaceId==workspace.WorkspaceId)
                    {
                        tools.Add(Tool("plugins.check","Validate and retain this source plugin candidate. Does not activate it or verify business behavior.",
                            JObject.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),false,new PluginTool(this,plugin.PluginId,"check",null)));
                        tools.Add(Tool("plugins.apply","Activate this source plugin's last checked candidate. Runtime pins the candidate before confirmation; a new check invalidates that approval. Existing operations retain their old release.",
                            JObject.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),true,new PluginTool(this,plugin.PluginId,"apply",null)));
                    }
                    if(!plugin.Enabled || plugin.ActiveReleaseId==null)continue;
                    var release=_store.ReadRelease(new PluginReleaseIdentity(plugin.PluginId,plugin.ActiveReleaseId));
                    foreach(var command in PluginPackageFiles.ParseManifest(release.ManifestJson).Commands)
                        tools.Add(Tool("plugins."+plugin.PluginId+"."+command.Id,command.Description,command.ArgumentsSchema,command.WritesData,
                            new PluginTool(this,plugin.PluginId,command.Id,plugin.ActiveReleaseId),true));
                }
            }
            return tools;
        }
        private static WorkspaceToolContribution Tool(string id,string description,JObject schema,bool write,IToolHandler handler,bool execute=false)
        {
            var policy=new ToolPolicy(write?ToolEffect.Write:ToolEffect.Read,write?ToolVerification.Tool:ToolVerification.None,
                write,false,write?new[]{ChatModes.Agent}:new[]{ChatModes.Agent,ChatModes.Chat,ChatModes.Plan},write?1:0,execute);
            return new WorkspaceToolContribution { Entry=new ToolCatalogEntry {Id=id,Name=id,Description=description,
                ArgumentSchemaJson=schema.ToString(Formatting.None),Policy=policy,Binding=new ToolBinding(id,scope:((PluginTool)handler).BindingScope,host:"Common"),
                Host="Common",Enabled=true,BuiltIn=true,AgentCanRun=true,MutatesLocalState=write,RequiresConfirmation=write},Handler=handler };
        }
        private sealed class PluginTool : IManagedMutationToolHandler, IReadOnlyToolHandler, IPreparableToolHandler
        {
            private readonly PluginApplicationService _service;private readonly string _plugin,_command,_release;
            private sealed class Prepared
            {
                public string CandidateId { get; set; }
                public string ProjectId { get; set; }
            }
            public string BindingScope { get { return _release == null ? "plugin-source:" + _plugin : "plugin-release:" + _release; } }
            public PluginTool(PluginApplicationService service,string plugin,string command,string release)
            {_service=service;_plugin=plugin;_command=command;_release=release;}
            public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var prepared = new Prepared();
                lock (_service._sync)
                {
                    _service.RequireOpen();
                    if (_release == null && _command == "apply")
                    {
                        prepared.CandidateId = _service._candidates.SingleOrDefault(item => item.Value.Package.PluginId == _plugin).Key;
                        _service.RequireCandidate(_plugin, prepared.CandidateId);
                    }
                    else if (_release != null)
                    {
                        var active = _service.RequireRegistration(_plugin);
                        if (!active.Enabled || active.ActiveReleaseId != _release)
                            throw new PluginException("plugin_version_changed", "Refresh the command catalog before continuing.");
                        PluginDataGrant grant;
                        if (_service._data.TryGetValue(_plugin + ":" + _release, out grant))
                        { grant.Lifetime.Token.ThrowIfCancellationRequested(); prepared.ProjectId = grant.ProjectId; }
                    }
                }
                return Task.FromResult(new ToolPreparationResult(ToolResult.Ok("Plugin operation prepared."), JsonConvert.SerializeObject(prepared)));
            }
            public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context,CancellationToken token)
            {
                PluginInstance instance=null;var possible=false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var prepared = JsonConvert.DeserializeObject<Prepared>(context.PreparedStateJson ?? throw new PluginException("plugin_preparation_missing", "Runtime preparation is required."));
                    if(_release==null && _command=="check")
                    {
                        var checkedPackage = _service.Check(_plugin);
                        return new ToolHandlerResult(ToolResult.Ok("Candidate retained; it is not active.", JsonConvert.SerializeObject(
                            new PluginCheckSummary { Files = checkedPackage.Files, Bytes = checkedPackage.Bytes, Checks = checkedPackage.Checks })), ToolEffectEvidence.None);
                    }
                    if(_release==null && _command=="apply")
                    {
                        var candidate=prepared.CandidateId;
                        lock(_service._sync){_service.RequireCandidate(_plugin,candidate);}
                        context.MarkDispatchPossible();possible=true;
                        var result=_service.Apply(new PluginApplyRequest{PluginId=_plugin,CandidateId=candidate});
                        return new ToolHandlerResult(ToolResult.Ok("Active publication verified.",JsonConvert.SerializeObject(new PluginPublicationSummary { Name = result.Name, Enabled = result.Enabled })),ToolEffectEvidence.VerifiedChange);
                    }
                    instance=_service.CaptureCommandContext(_plugin, prepared.ProjectId, true);
                    if(instance.Release.ReleaseId!=_release)throw new PluginException("plugin_version_changed","The captured command publication changed. Refresh the tool catalog.");
                    var resultCommand=await _service.InvokeAsync(instance,_command,context.Execution.Call.ArgumentsJson,token,onDispatch:()=>{context.MarkDispatchPossible();possible=true;});
                    var writes=instance.Manifest.Commands.Single(value=>value.Id==_command).WritesData;
                    return new ToolHandlerResult(ToolResult.Ok("Command completed.",resultCommand.Json),
                        writes ? (resultCommand.HadVerifiedWrites?ToolEffectEvidence.VerifiedChange:ToolEffectEvidence.VerifiedNoChange) : ToolEffectEvidence.None);
                }
                catch(Exception error)
                {
                    var unknown=possible || (error as PluginException)?.Code=="effect_unknown";
                    return new ToolHandlerResult(unknown?ToolResult.Unknown(error.Message):ToolResult.Error(error.Message),unknown?ToolEffectEvidence.Unknown:ToolEffectEvidence.None);
                }
                finally {if(instance!=null)await _service.CloseInstanceAsync(instance.Id);}
            }
        }
    }
}

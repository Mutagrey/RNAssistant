using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Agent;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WithPluginPaths(Action<AppDataPaths> test)
        {
            WithTempPaths(paths => test(paths.Root.StartsWith("/var/",StringComparison.Ordinal)
                ? AppDataPaths.CreateForRoot("/private" + paths.Root) : paths));
        }
        private sealed class PluginPicker : IPluginProjectPicker
        {
            public string Root;
            public Task<PluginDataSelection> PickAsync(CancellationToken token){return Task.FromResult(new PluginDataSelection{RootPath=Root,Writable=true});}
        }
        private sealed class PluginExecutor : IPluginCommandExecutor
        {
            public Func<PluginInstance,CancellationToken,Task<string>> Execute;
            public Task<string> ExecuteAsync(PluginInstance instance,PluginCommandDefinition command,string input,CancellationToken token,Action<string> progress)
            {return Execute(instance,token);}
        }
        private static PluginItemDto PluginFixture(PluginApplicationService service,AppDataPaths paths,bool data=true)
        {
            var item=service.Create(new PluginCreateRequest{Name="Test plugin",RootPath=Path.Combine(paths.Root,"source"),Description="Test"});
            if(data){var file=Path.Combine(item.SourceRoot,"plugin.json");var json=JObject.Parse(File.ReadAllText(file));json["capabilities"]=new JArray("project.read","project.write");File.WriteAllText(file,json.ToString());}
            var candidate=service.Check(item.PluginId);return service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
        }
        private static PluginInstance PluginOpen(PluginApplicationService service,string id,string candidate=null)
        {var view=service.Open(new PluginOpenRequest{PluginId=id,CandidateId=candidate});return service.FindInstance(new Uri(view.EntryUrl).GetLeftPart(UriPartial.Authority));}
        private static Task<PluginFrameResponse> PluginRequest(PluginApplicationService service,PluginInstance instance,PluginMethod method,string path=null,string project=null,string handle=null)
        {return service.DispatchAsync(instance,new PluginFrameRequest{Version=1,RequestId=Guid.NewGuid().ToString("N"),Method=method,Path=path,ProjectId=project,Handle=handle},CancellationToken.None);}
        private static void PluginFailure(Action action,string code)
        {try{action();throw new Exception("Expected "+code);}catch(PluginException error){AssertEqual(code,error.Code,"plugin refusal");}}

        private static void PluginsPublishAndSeparateAuthority()
        {
            WithPluginPaths(paths=>{
                var wire = new ChatMessage { Role = "assistant", ProtocolMessage = true, ResponseProtocolVersion = 7,
                    Content = "{\"message\":\"Читаю исходник\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"README.md\"}}]}",
                    ToolCallId = "call-1", ToolName = "common.resources_read",
                    AcceptedCallOrigin = new AcceptedToolCallOrigin("step-1", "attempt-1", 0) };
                var presented = PluginApplicationService.PresentWorkspaceMessage(wire);
                AssertEqual("Читаю исходник", presented.Text, "source chat presents the model message, not protocol JSON");
                AssertEqual("common.resources_read", presented.Actions.Single(), "source chat retains the action name without arguments");
                using(var service=new PluginApplicationService(paths))
                {
                    var item=PluginFixture(service,paths);var active=PluginOpen(service,item.PluginId);
                    PluginFailure(()=>paths.ClearRuntimeData(),"plugins_busy");
                    AssertEqual("puzzle",item.Icon,"published plugin exposes its manifest icon");
                    AssertTrue(active.Release.SourceWorkspaceId!=active.Release.ReleaseId,"source and publication identities differ");
                    AssertEqual("project_required",PluginRequest(service,active,PluginMethod.List).Result.ErrorCode,"manifest grants no data access");
                    var picker=new PluginPicker{Root=item.SourceRoot};service.ProjectPicker=picker;
                    AssertEqual("source_is_not_data",PluginRequest(service,active,PluginMethod.PickProject).Result.ErrorCode,"source cannot become data");
                    picker.Root=Path.Combine(paths.Root,"plugin-preview-data");
                    AssertEqual("preview_is_not_active",PluginRequest(service,active,PluginMethod.PickProject).Result.ErrorCode,"test project cannot become active data");
                    picker.Root=Path.Combine(paths.Root,"data");Directory.CreateDirectory(picker.Root);File.WriteAllText(Path.Combine(picker.Root,"input.txt"),"original");
                    var project=PluginRequest(service,active,PluginMethod.PickProject).Result.Result.Handle;
                    AssertEqual("project_changed",PluginRequest(service,active,PluginMethod.OpenFile,"input.txt","forged").Result.ErrorCode,"foreign project refused");
                    var read=PluginRequest(service,active,PluginMethod.OpenFile,"input.txt",project).Result.Result;
                    var candidate=service.Check(item.PluginId);var preview=PluginOpen(service,item.PluginId,candidate.CandidateId);
                    var sourceStamp=service.SourceState(item.PluginId).Stamp;
                    AssertEqual("project_required",PluginRequest(service,preview,PluginMethod.List).Result.ErrorCode,"preview inherits no production grant");
                    var scratch=PluginRequest(service,preview,PluginMethod.PickProject).Result.Result.Handle;
                    AssertTrue(scratch!=project && preview.Data.DataWorkspaceId!=active.Data.DataWorkspaceId,"preview test project is independent");
                    File.WriteAllText(Path.Combine(preview.Data.Workspace.RootPath,"preview.txt"),"kept");
                    var refreshedPreview=PluginOpen(service,item.PluginId,candidate.CandidateId);
                    PluginRequest(service,refreshedPreview,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertEqual(preview.Data.DataWorkspaceId,refreshedPreview.Data.DataWorkspaceId,"preview refresh keeps its separate test project");
                    AssertTrue(File.Exists(Path.Combine(refreshedPreview.Data.Workspace.RootPath,"preview.txt")),"preview refresh keeps test data");
                    AssertEqual("not_found",PluginRequest(service,preview,PluginMethod.OpenFile,"input.txt",scratch).Result.ErrorCode,"production bytes absent from preview");
                    var oldPreviewGrant=preview.Data;
                    PluginRequest(service,preview,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertTrue(oldPreviewGrant.Lifetime.IsCancellationRequested,"reselecting preview data revokes its previous grant");
                    AssertEqual("original",Encoding.UTF8.GetString(active.ReadRangeAsync(read.Handle,0,8,CancellationToken.None).Result),"pinned bytes available");
                    var oldCode=service.ReadPackageFile(active,"app.js");File.WriteAllText(Path.Combine(item.SourceRoot,"app.js"),"changed");
                    AssertTrue(sourceStamp!=service.SourceState(item.PluginId).Stamp,"source token changes after a declared file edit");
                    candidate=service.Check(item.PluginId);service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
                    AssertTrue(oldCode.SequenceEqual(service.ReadPackageFile(active,"app.js")),"active operations retain frozen bytes");
                    var fresh=PluginOpen(service,item.PluginId);AssertEqual("project_required",PluginRequest(service,fresh,PluginMethod.List).Result.ErrorCode,"new publication receives no old data grant");
                    File.WriteAllText(Path.Combine(item.SourceRoot,"plugin.json"),"{}");PluginFailure(()=>service.Check(item.PluginId),"plugin_manifest_invalid");
                    AssertEqual(fresh.Release.ReleaseId,service.List().Items.Single().ReleaseId,"failed validation preserves active release");
                    service.DisableAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertTrue(active.Token.IsCancellationRequested && fresh.Token.IsCancellationRequested,"disable revokes instances");
                    AssertTrue(File.Exists(Path.Combine(picker.Root,"input.txt")),"disable preserves data");
                    paths.ClearRuntimeData();
                    new ResourceAuthorityStore(paths).ResetAfterRuntimeDataClear(); service.ResetAfterRuntimeDataClear();
                    AssertTrue(File.Exists(Path.Combine(picker.Root,"input.txt")) && File.Exists(Path.Combine(item.SourceRoot,"app.js")),"explicit runtime clear preserves source and data folders after plugin drain");
                    AssertEqual(0,service.List().Items.Count,"runtime clear drops only catalog state; source reconnection is explicit");
                }
            });
        }
        private static void PluginsPanelVisibility()
        {
            WithPluginPaths(paths => {
                PluginItemDto item;
                PluginInstance open;
                string sourceChat;
                using (var service = new PluginApplicationService(paths))
                {
                    item = PluginFixture(service, paths, false);
                    open = PluginOpen(service, item.PluginId);
                    sourceChat = service.ChatState(item.PluginId).SessionId;
                    service.RemoveAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertTrue(open.Token.IsCancellationRequested, "removal revokes visible instance");
                    AssertEqual(0, service.List().Items.Count, "removed plugin disappears from panel");
                    AssertEqual(item.PluginId, service.ListRemoved().Items.Single().PluginId, "removed plugin remains selectable for restoration");
                    PluginFailure(() => service.Open(new PluginOpenRequest { PluginId = item.PluginId }), "plugin_missing");
                    AssertTrue(File.Exists(Path.Combine(item.SourceRoot, "plugin.json")), "removal keeps source");
                }
                using (var service = new PluginApplicationService(paths))
                {
                    AssertEqual(item.PluginId, service.ListRemoved().Items.Single().PluginId, "panel removal survives restart");
                    var restored = service.Restore(item.PluginId);
                    AssertEqual(item.ReleaseId, restored.ReleaseId, "restoration keeps published version");
                    AssertEqual(true, restored.Enabled, "restoration keeps activation state");
                    AssertEqual(item.PluginId, service.List().Items.Single().PluginId, "restored plugin returns to panel");
                    AssertEqual(sourceChat, service.ChatState(item.PluginId).SessionId, "restoration keeps source chat");
                    PluginOpen(service, item.PluginId);
                }
            });
        }
        private static void PluginsSvgIcon()
        {
            var authoring = RNAssistant.Core.Services.WorkspaceSkillProvider.GetSkills()
                .Single(skill => skill.Id == RNAssistant.Core.Services.WorkspaceSkillProvider.PluginId);
            AssertTrue(authoring.BodyMarkdown.Contains("icon.svg") && authoring.BodyMarkdown.Contains("plugins.check"),
                "source workspace guidance owns text-authored SVG and package checking");
            WithPluginPaths(paths=>{
                using(var service=new PluginApplicationService(paths))
                {
                    var defaultParent=Path.Combine(paths.Root,"plugin-workspaces");
                    AssertEqual(defaultParent,service.List().DefaultSourceParent,"UI learns the actual default source location");
                    var automatic=service.Create(new PluginCreateRequest { Name="Default source" });
                    AssertEqual(defaultParent,Path.GetDirectoryName(automatic.SourceRoot),"creation chooses the managed plugin folder");
                    AssertTrue(Directory.Exists(automatic.SourceRoot),"default source folder is created");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Missing custom path",RootPath=" " }),"plugin_create_invalid");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Relative custom path",RootPath="relative-plugin" }),"plugin_create_invalid");
                    var emptyRoot=Path.Combine(paths.Root,"prepared-empty");Directory.CreateDirectory(emptyRoot);
                    AssertEqual(emptyRoot,service.Create(new PluginCreateRequest { Name="Empty source",RootPath=emptyRoot }).SourceRoot,
                        "creation accepts a pre-created empty folder");
                    var occupied=Path.Combine(paths.Root,"occupied");Directory.CreateDirectory(occupied);
                    File.WriteAllText(Path.Combine(occupied,"keep.txt"),"user data");
                    PluginFailure(()=>service.Create(new PluginCreateRequest { Name="Occupied",RootPath=occupied }),"target_exists");
                    AssertEqual("user data",File.ReadAllText(Path.Combine(occupied,"keep.txt")),"occupied source is never overwritten");
                    var wrapped=PluginApplicationService.ExtractModelSvg("Here is your icon:\n```svg\n<svg viewBox=\"0 0 24 24\"><path d=\"M3 3h18v18H3z\"/></svg>\n```");
                    AssertEqual(1,PluginPackageFiles.ReadIconPaths(PluginPackageFiles.Utf8.GetBytes(wrapped)).Length,
                        "a fenced model reply yields only the validated SVG document");
                    var item=service.Create(new PluginCreateRequest { Name="Sales", RootPath=Path.Combine(paths.Root,"svg-source"),
                        Description="Summarize sales", IconDescription="Three rising columns" });
                    AssertTrue(File.ReadAllText(Path.Combine(item.SourceRoot,"README.md")).Contains("Three rising columns"),
                        "the text brief stays in source guidance");
                    var manifestPath=Path.Combine(item.SourceRoot,"plugin.json");
                    var manifest=JObject.Parse(File.ReadAllText(manifestPath));
                    manifest["icon"]="icon.svg";((JArray)manifest["files"]).Add("icon.svg");
                    File.WriteAllText(manifestPath,manifest.ToString());
                    var svgPath=Path.Combine(item.SourceRoot,"icon.svg");
                    File.WriteAllText(svgPath,"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 18V6h3v12M10 18v-7h3v7M16 18V4h3v14\"/></svg>");
                    var candidate=service.Check(item.PluginId);
                    var published=service.Apply(new PluginApplyRequest { PluginId=item.PluginId, CandidateId=candidate.CandidateId });
                    AssertEqual("icon.svg",published.Icon,"SVG source becomes the published icon");
                    AssertEqual(1,published.IconPaths.Length,"published icon exposes validated paths only");
                    var catalog=service.ListIcons().Items;
                    AssertEqual(1,catalog.Count,"checking a valid icon adds it to the saved library");
                    AssertEqual(published.IconPaths[0],catalog[0].Paths[0],"library retains the validated drawing");
                    var reused=service.Create(new PluginCreateRequest { Name="Reused", RootPath=Path.Combine(paths.Root,"reused-source"), IconId=catalog[0].Id });
                    AssertEqual("icon.svg",reused.Icon,"saved icon is copied into new plugin source");
                    AssertEqual(catalog[0].Paths[0],reused.IconPaths[0],"new source owns its copy of the drawing");
                    PluginFailure(()=>service.DeleteIcon(new PluginIconDeleteRequest { Id="dataTemplates" }),"plugin_icon_protected");
                    service.DeleteIcon(new PluginIconDeleteRequest { Id=catalog[0].Id });
                    AssertEqual(0,service.ListIcons().Items.Count,"saved drawing is removable");
                    AssertEqual(catalog[0].Paths[0],service.List().Items.Single(value=>value.PluginId==reused.PluginId).IconPaths[0],
                        "deleting a saved drawing leaves plugin source intact");
                    File.WriteAllText(svgPath,"<svg viewBox=\"0 0 24 24\" onload=\"alert(1)\"><path d=\"M4 4L20 20\"/></svg>");
                    PluginFailure(()=>service.Check(item.PluginId),"plugin_icon_invalid");
                    AssertEqual(published.IconPaths[0],service.List().Items.Single(value=>value.PluginId==item.PluginId).IconPaths[0],
                        "invalid draft cannot replace the active icon");
                    PluginFailure(()=>service.SaveIcon(new PluginIconSaveRequest { Name="Unsafe", Svg="<svg viewBox=\"0 0 24 24\" onload=\"alert(1)\"><path d=\"M4 4L20 20\"/></svg>" }),"plugin_icon_invalid");
                    var manual=service.SaveIcon(new PluginIconSaveRequest { Name="Manual", Svg="<svg viewBox=\"0 0 24 24\"><path d=\"M3 3h18v18H3Z\"/></svg>" });
                    paths.ClearRuntimeData();
                    AssertEqual(manual.Id,new PluginIconLibrary(paths).List().Single().Id,
                        "saved icons survive explicit runtime-data clearing");
                }
            });
        }
        private static void PluginsCommandsPinAndCancel()
        {
            WithPluginPaths(paths=>PluginCommandsPinAndCancelAsync(paths).GetAwaiter().GetResult());
        }
        private static async Task PluginCommandsPinAndCancelAsync(AppDataPaths paths)
        {
            using(var service=new PluginApplicationService(paths))
            {
                var item=PluginFixture(service,paths,false);var context=PluginOpen(service,item.PluginId);
                var started=new TaskCompletionSource<PluginInstance>();var finish=new TaskCompletionSource<string>();
                service.Executor=new PluginExecutor{Execute=(operation,token)=>{started.TrySetResult(operation);token.Register(()=>finish.TrySetCanceled());return finish.Task;}};
                var invocation=service.InvokeAsync(context,"echo","{\"text\":\"a\"}",CancellationToken.None);var running=await started.Task;
                File.WriteAllText(Path.Combine(item.SourceRoot,"commands.js"),"new code");var candidate=service.Check(item.PluginId);
                service.Apply(new PluginApplyRequest{PluginId=item.PluginId,CandidateId=candidate.CandidateId});
                AssertEqual(context.Release.ReleaseId,running.Release.ReleaseId,"invocation pins admitted publication");
                await service.CloseInstanceAsync(context.Id);
                AssertTrue(!running.Token.IsCancellationRequested,"closing the UI leaves its command alive");
                finish.SetResult("{\"text\":\"old release completed\"}");AssertTrue((await invocation).Json.Contains("old release"),"old operation finishes across update");
                try{await service.InvokeAsync(context,"echo","{\"text\":\"a\"}",CancellationToken.None);throw new Exception("Expected revoked refusal");}catch(OperationCanceledException){}
                context=PluginOpen(service,item.PluginId);started=new TaskCompletionSource<PluginInstance>();finish=new TaskCompletionSource<string>();
                invocation=service.InvokeAsync(context,"echo","{\"text\":\"b\"}",CancellationToken.None);await started.Task;
                await service.DisableAsync(item.PluginId);try{await invocation;throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
                var source=new WorkspaceStore(paths).Open(item.SourceRoot,false,true);
                var tools=service.Conversations.AvailableToolIds(source);
                AssertTrue(tools.Contains("plugins.check") && tools.Contains("plugins.apply"),"development agent shares publication services");
            }
        }
        private static void PluginsVendorResolutionAndAdmission()
        {
            WithPluginPaths(paths=>{
                var folder=Path.Combine(paths.Root,"vendor-input");Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"module.js"),"export default 1;");File.WriteAllText(Path.Combine(folder,"LICENSE"),"MIT");
                var manifest=new VendorManifestV1{FormatVersion=1,Id="test-module",Version="1.0.0",Description="Test module",Source="local",License="MIT",
                    Licenses=new[]{"LICENSE"},Files=new[]{new VendorFileDefinition{Path="module.js",Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(folder,"module.js")))},new VendorFileDefinition{Path="LICENSE",Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(folder,"LICENSE")))}},
                    EntryPoints=new[]{new VendorEntryPoint{Path="module.js",Kind=VendorEntryKind.Module}}};
                File.WriteAllText(Path.Combine(folder,"vendor.json"),JsonConvert.SerializeObject(manifest));
                var vendors=new VendorCatalog(paths,null);vendors.Import(folder);var resolved=vendors.Resolve(manifest.Id,manifest.Version);
                VendorAdmission.RequirePlugin(resolved.Manifest);VendorAdmission.RequireExport(resolved.Manifest);
                PluginFailure(()=>VendorAdmission.RequireHtml(resolved.Manifest),"html_vendor_denied");
                var installed=Path.Combine(paths.Root,"vendors",manifest.Id,manifest.Version);
                File.WriteAllText(Path.Combine(installed,"module.js"),"export default 2;");PluginFailure(()=>vendors.Resolve(manifest.Id,manifest.Version),"vendor_changed");
                manifest.Files[0].Sha256=PluginPackageFiles.Hash(File.ReadAllBytes(Path.Combine(installed,"module.js")));File.WriteAllText(Path.Combine(installed,"vendor.json"),JsonConvert.SerializeObject(manifest));
                PluginFailure(()=>vendors.Resolve(manifest.Id,manifest.Version),"vendor_version_changed");
                manifest.Licenses=new string[0];PluginFailure(()=>VendorAdmission.RequireExport(manifest),"vendor_invalid");
            });
        }
        private static void PluginsBinaryUnknownAndRecovery()
        {
            WithPluginPaths(paths=>{
                var workspace=new WorkspaceStore(paths).Open(Path.Combine(paths.Root,"data"));var files=new WorkspaceFileService(paths);
                var staged=Path.Combine(paths.Root,"staged");var bytes=new byte[2*1024*1024];bytes[0]=255;File.WriteAllBytes(staged,bytes);
                files.FaultPoint=point=>{if(point=="after-dispatch")throw new IOException("lost receipt");};
                try{files.CommitStagedFile(workspace,"out.bin",staged,null,bytes.Length);throw new Exception("Expected unknown");}catch(WorkspaceFileException e){AssertEqual("effect_unknown",e.Code,"uncertain write");}
                files=new WorkspaceFileService(paths);
                try{files.CommitStagedFile(workspace,"out.bin",staged,null,bytes.Length);throw new Exception("Expected unresolved");}catch(WorkspaceFileException e){AssertEqual("unresolved_previous_effect",e.Code,"no automatic retry");}
                var recovery=files.ReconcileUncertain(workspace,"out.bin");AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch,recovery.Outcome,"binary inspection preserves uncertain causality");
                var snapshot=files.CaptureFile(workspace,"out.bin",bytes.Length);AssertEqual(PluginPackageFiles.Hash(bytes),snapshot.ContentSha256,"full binary read-back");
                files.FaultPoint=point=>{if(point=="after-dispatch")throw new IOException("lost directory receipt");};
                try{files.CreateDirectory(workspace,"results");throw new Exception("Expected directory unknown");}catch(WorkspaceFileException e){AssertEqual("effect_unknown",e.Code,"directory mutation is journaled");}
                files.FaultPoint=null;
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch,files.ReconcileUncertain(workspace,"results").Outcome,"directory inspection does not turn unknown causality into success");
            });
        }
        private static void PluginsResultsAndPreparedAuthority()
        {
            WithPluginPaths(paths=>PluginResultsAndPreparedAuthorityAsync(paths).GetAwaiter().GetResult());
        }
        private static async Task PluginResultsAndPreparedAuthorityAsync(AppDataPaths paths)
        {
            using(var service=new PluginApplicationService(paths))
            {
                var item=PluginFixture(service,paths);var ui=PluginOpen(service,item.PluginId);var other=PluginOpen(service,item.PluginId);
                var picker=new PluginPicker{Root=Path.Combine(paths.Root,"data")};Directory.CreateDirectory(picker.Root);File.WriteAllText(Path.Combine(picker.Root,"in.txt"),"accepted");service.ProjectPicker=picker;
                var project=(await PluginRequest(service,ui,PluginMethod.PickProject)).Result.Handle;
                await PluginRequest(service,ui,PluginMethod.OpenFile,"in.txt",project);
                File.WriteAllText(Path.Combine(picker.Root,"in.txt"),"changed!");
                service.Executor=new PluginExecutor{Execute=async (operation,token)=>{
                    var source=(await PluginRequest(service,operation,PluginMethod.OpenFile,"in.txt",project)).Result;
                    AssertEqual("accepted",Encoding.UTF8.GetString(await operation.ReadRangeAsync(source.Handle,0,8,token)),"command inherits the accepted source snapshot");
                    AssertEqual("plugin_capability_denied",(await PluginRequest(service,operation,PluginMethod.BeginWrite,"illegal.txt",project)).ErrorCode,"read command cannot write through a writable grant");
                    var writer=(await PluginRequest(service,operation,PluginMethod.BeginResult,"in.txt.canonical.json")).Result;
                    await operation.WriteChunkAsync(writer.Handle,0,Encoding.UTF8.GetBytes("{}"),token);
                    var result=(await PluginRequest(service,operation,PluginMethod.CommitResult,handle:writer.Handle)).Result;
                    return "{\"text\":"+JsonConvert.SerializeObject(result.Name)+"}";
                }};
                await service.InvokeAsync(ui,"echo","{\"text\":\"run\"}",CancellationToken.None);
                var opened=(await PluginRequest(service,ui,PluginMethod.OpenResult,"in.txt.canonical.json")).Result;
                AssertEqual("{}",Encoding.UTF8.GetString(service.ReadResult(ui,opened.Handle,0,2)),"parent can read retained command result after worker closes");
                AssertEqual("plugin_result_denied",(await PluginRequest(service,other,PluginMethod.OpenResult,"in.txt.canonical.json")).ErrorCode,"result cannot cross UI owners");
                PluginFailure(()=>service.ReadResult(other,opened.Handle,0,2),"plugin_result_denied");
                await PluginRequest(service,ui,PluginMethod.DropResult,"in.txt.canonical.json");
                AssertEqual("plugin_result_denied",(await PluginRequest(service,ui,PluginMethod.OpenResult,"in.txt.canonical.json")).ErrorCode,"discard releases result authority");
                var workspace=new WorkspaceStore(paths).Open(item.SourceRoot,false,true);
                var registry=new ToolHandlerRegistry();
                foreach(var tool in service.CaptureTools(workspace))registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(tool.Entry.Id,tool.Entry.Description,tool.Entry.ArgumentSchemaJson),tool.Entry.Policy,tool.Entry.Binding),tool.Handler);
                var runtime=new ToolRuntime(registry,"agent",false,true,(context,preparation)=>"approval");
                var checkCall=new ToolCall("check","plugins.check","{}");
                var checkedResult=await runtime.ExecuteAsync(new ToolExecutionContext(checkCall,runtime.Describe(checkCall),"run","turn","check",DateTime.UtcNow,false,5),CancellationToken.None);
                AssertTrue(!checkedResult.Result.DataJson.Contains("candidateId") && !checkedResult.Result.DataJson.Contains("releaseId"),"model sees validation facts without authority handles");
                var call=new ToolCall("apply","plugins.apply","{}");var policy=runtime.Describe(call);
                var pending=await runtime.ExecuteAsync(new ToolExecutionContext(call,policy,"run","turn","apply",DateTime.UtcNow,false,5),CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,pending.Outcome,"publication requires confirmation");
                service.Check(item.PluginId);
                var refused=await runtime.ExecuteAsync(new ToolExecutionContext(call,policy,"run","turn","apply",DateTime.UtcNow,true,5,pending.PreparedStateJson),CancellationToken.None);
                AssertTrue(!refused.MayHaveDispatched && refused.Result.Status==RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error,"rechecking invalidates the prepared candidate without publishing another");
                var before=service.CaptureCommandContext(item.PluginId,project,true);await service.CloseInstanceAsync(before.Id);
                picker.Root=Path.Combine(paths.Root,"other-data");Directory.CreateDirectory(picker.Root);await PluginRequest(service,ui,PluginMethod.PickProject);
                PluginFailure(()=>service.CaptureCommandContext(item.PluginId,project,true),"plugin_project_changed");
                AssertEqual("project_changed",(await PluginRequest(service,ui,PluginMethod.OpenFile,"in.txt",project)).ErrorCode,"stale data handles cannot follow selection");
            }
        }
        private static void PluginsDataTemplatesPackage()
        {
            WithPluginPaths(paths=>{
                var web=Path.Combine(FindHarnessRepositoryRoot(),"web");using(var service=new PluginApplicationService(paths,web))
                {
                    service.EnsureDataTemplates();var item=service.List().Items.Single();var instance=PluginOpen(service,item.PluginId);
                    AssertEqual("data-templates",item.PluginId,"ordinary shipped plugin");
                    AssertTrue(!instance.Release.Files.Any(file=>file.Path=="application.json"),"one manifest authority");
                    AssertEqual(5,instance.Manifest.Vendors.Length,"shared vendor resolution");
                    AssertTrue(service.Conversations.AvailableToolIds(new WorkspaceStore(paths).Open(item.SourceRoot)).Contains("plugins.data-templates.extract"),"chat command descriptor");
                    foreach(var vendor in service.Vendors.List())service.Vendors.Resolve(vendor.Id,vendor.Version);
                    var firstRoot=Path.Combine(paths.Root,"data-project-one");
                    var secondRoot=Path.Combine(paths.Root,"data-project-two");
                    Directory.CreateDirectory(firstRoot);Directory.CreateDirectory(secondRoot);
                    File.WriteAllText(Path.Combine(firstRoot,"source.txt"),"keep");
                    var picker=new PluginPicker{Root=firstRoot};service.ProjectPicker=picker;
                    PluginRequest(service,instance,PluginMethod.PickProject).GetAwaiter().GetResult();
                    var first=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=firstRoot}).SelectedWorkspaceId;
                    var second=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=secondRoot}).SelectedWorkspaceId;
                    AssertTrue(first!=second,"connected workspaces have separate identities");
                    var created=service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first,Title="Project discussion"});
                    AssertEqual(created.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "project chat appears under its workspace");
                    service.RenameProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=created.SessionId,Title="Renamed discussion"});
                    AssertEqual("Renamed discussion",service.ProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=created.SessionId}).State.Title,
                        "project chat title persists in workspace history");
                    PluginFailure(()=>service.ProjectChat(new ProjectChatRequest{WorkspaceId=second,SessionId=created.SessionId}),"project_chat_missing");
                    picker.Root=secondRoot;PluginRequest(service,instance,PluginMethod.PickProject).GetAwaiter().GetResult();
                    AssertEqual(0,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==second).Chats.Count,
                        "another workspace has separate chats");
                    AssertEqual(created.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "project chat history survives plugin project reselection");
                    PluginFailure(()=>service.ControlProjectChatAsync(new ProjectChatRequest {WorkspaceId=first,
                        SessionId=created.SessionId,RunId="missing",Cursor="events1.missing"},true,
                        new AppSettings(),()=>"",CancellationToken.None).GetAwaiter().GetResult(),"project_chat_stale");
                    var forgedRoot=Path.Combine(paths.Root,"unrelated-folder");Directory.CreateDirectory(forgedRoot);
                    var associationPath=Path.Combine(paths.WorkspaceDirectory,first+".json");
                    var originalAssociation=File.ReadAllText(associationPath);
                    try
                    {
                        var forgedAssociation=JObject.Parse(originalAssociation);forgedAssociation["RootPath"]=forgedRoot;
                        File.WriteAllText(associationPath,forgedAssociation.ToString());
                        try { service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first});throw new Exception("Expected connection refusal"); }
                        catch(InvalidDataException) { }
                        AssertTrue(!Directory.Exists(Path.Combine(forgedRoot,".rnassistant")),
                            "changed connection cannot create a manifest in another folder");
                    }
                    finally { File.WriteAllText(associationPath,originalAssociation); }
                    service.DeleteProjectHistory(new ProjectChatRequest{WorkspaceId=first});
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==first),
                        "project history deletion removes the workspace from the tree");
                    AssertTrue(File.Exists(Path.Combine(firstRoot,"source.txt")),"project history deletion preserves source files");
                    var missingRoot=Path.Combine(paths.Root,"missing-project");Directory.CreateDirectory(missingRoot);
                    var missing=service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=missingRoot}).SelectedWorkspaceId;
                    service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=missing,Title="Offline history"});
                    var movedRoot=missingRoot+"-moved";Directory.Move(missingRoot,movedRoot);
                    AssertTrue(!service.ProjectChats().Projects.Single(p=>p.WorkspaceId==missing).Available,
                        "missing workspace remains visible for history management");
                    service.DeleteProjectHistory(new ProjectChatRequest{WorkspaceId=missing});
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==missing) && Directory.Exists(movedRoot),
                        "unavailable workspace history can be removed without touching moved files");
                    AssertTrue(Directory.Exists(firstRoot)&&Directory.Exists(secondRoot),"project chat actions preserve data folders");
                    service.RemoveAsync(item.PluginId).GetAwaiter().GetResult();
                    AssertEqual(0,service.List().Items.Count,"shipped plugin can leave the rail");
                    AssertEqual(item.PluginId,service.ListRemoved().Items.Single().PluginId,"shipped plugin remains restorable");
                    AssertTrue(!service.ProjectChats().Projects.Any(p=>p.WorkspaceId==first),"hidden workspace stays out of tree after plugin removal");
                    AssertEqual(first,service.ConnectProjectWorkspace(new ProjectConnectionRequest{RootPath=firstRoot}).SelectedWorkspaceId,
                        "reconnecting a workspace restores its identity");
                    AssertEqual(0,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Count,
                        "reconnected workspace has empty history");
                    AssertTrue(!new WorkspaceStore(paths).OpenConnected(first).ReadOnly,"connected workspace chats can edit files");
                    var independent=service.CreateProjectChat(new ProjectChatRequest{WorkspaceId=first,Title="Independent chat"});
                    AssertEqual(independent.SessionId,service.ProjectChats().Projects.Single(p=>p.WorkspaceId==first).Chats.Single().Id,
                        "workspace chat remains usable after plugin removal");
                    service.DeleteProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=independent.SessionId});
                    PluginFailure(()=>service.DeleteProjectChat(new ProjectChatRequest{WorkspaceId=first,SessionId=independent.SessionId}),
                        "project_chat_missing");
                    AssertEqual(item.ReleaseId,service.Restore(item.PluginId).ReleaseId,"restoring shipped plugin keeps publication");
                }
            });
        }
    }
}

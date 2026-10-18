using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.WebView
{
    // Only this transport sees WebView frames. No plugin message is forwarded to
    // AssistantWebBridge, and the command controller never binds an Office session.
    public sealed class PluginWebViewHost : IPluginCommandExecutor, IPluginProjectPicker, IDisposable
    {
        private readonly PluginApplicationService _service;
        private readonly CoreWebView2 _main;
        private readonly IntPtr _parent;
        private readonly string _assets;
        private readonly SynchronizationContext _ui;
        private readonly List<Transport> _transports = new List<Transport>();
        private readonly HashSet<Task> _runs = new HashSet<Task>();
        private bool _closed;
        public Task Drained { get; private set; } = Task.CompletedTask;
        public PluginWebViewHost(PluginApplicationService service, CoreWebView2 main, IntPtr parent, string webRoot)
        {
            _service=service; _main=main; _parent=parent; _assets=Path.Combine(webRoot,"plugin-host");
            _ui=SynchronizationContext.Current ?? throw new InvalidOperationException("UI dispatcher required.");
            _transports.Add(new Transport(service,main,_assets,null));
            service.Executor=this; service.ProjectPicker=this;
        }
        public bool CanNavigateFrame(string url)
        {
            Uri uri; return Uri.TryCreate(url,UriKind.Absolute,out uri) && _service.FindInstance(uri.GetLeftPart(UriPartial.Authority))?.EntryUrl == url;
        }
        public Task<PluginDataSelection> PickAsync(CancellationToken token)
        {
            return OnUi(async () => {
                token.ThrowIfCancellationRequested();
                using(var dialog=new FolderBrowserDialog { Description="Выберите отдельную папку данных плагина. Плагин сможет читать и изменять файлы только в этой папке.", ShowNewFolderButton=true })
                { return dialog.ShowDialog()==DialogResult.OK ? new PluginDataSelection {RootPath=dialog.SelectedPath,Writable=true} : null; }
            });
        }
        public Task<string> ExecuteAsync(PluginInstance operation, PluginCommandDefinition command, string argumentsJson, CancellationToken token, Action<string> progress)
        {
            var run=OnUi(() => ExecuteOnUi(operation,command,argumentsJson,token,progress));
            lock(_runs)_runs.Add(run);
            run.ContinueWith(done=>{lock(_runs)_runs.Remove(done);},TaskScheduler.Default);
            return run;
        }
        private async Task<string> ExecuteOnUi(PluginInstance operation, PluginCommandDefinition command, string argumentsJson, CancellationToken token, Action<string> progress)
        {
            if(_closed) throw new ObjectDisposedException(nameof(PluginWebViewHost));
            CoreWebView2Controller controller=null; Transport transport=null;
            var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(token.Register(() => _ui.Post(ignored => completion.TrySetCanceled(),null)))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var options=_main.Environment.CreateCoreWebView2ControllerOptions(); options.ProfileName="RNAssistantPlugins"; options.IsInPrivateModeEnabled=true;
                    controller=await _main.Environment.CreateCoreWebView2ControllerAsync(_parent,options);
                    token.ThrowIfCancellationRequested();
                    if(_closed) throw new OperationCanceledException();
                    controller.IsVisible=false; controller.Bounds=new Rectangle(0,0,1,1);
                    var core=controller.CoreWebView2;
                    transport=new Transport(_service,core,_assets,operation); _transports.Add(transport);
                    var entry=operation.Origin+"/.host/runner.html";
                    core.NavigationStarting+=(s,e)=> { if(e.Uri!=entry) {e.Cancel=true;completion.TrySetException(new PluginException("plugin_navigation_denied","Command navigation denied."));} };
                    core.NavigationCompleted+=(s,e)=> { if(!e.IsSuccess) completion.TrySetException(new PluginException("plugin_load_failed","Command host failed to load.")); };
                    core.ProcessFailed+=(s,e)=>completion.TrySetException(new PluginException("plugin_process_failed","Command browser process failed."));
                    core.FrameNavigationStarting+=(s,e)=>e.Cancel=true;
                    core.NewWindowRequested+=(s,e)=>e.Handled=true;
                    core.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                    core.DownloadStarting+=(s,e)=>e.Cancel=true;
                    core.ScriptDialogOpening+=(s,e)=>e.Accept();
                    var ready=false;
                    core.WebMessageReceived+=async (s,e)=> {
                        try
                        {
                            if(e.Source!=entry || completion.Task.IsCompleted) return;
                            var envelope=Decode<PluginWorkerEnvelope>(e.TryGetWebMessageAsString());
                            switch(envelope.Type)
                            {
                                case "ready":
                                    if(ready) throw new PluginException("plugin_protocol_invalid","Repeated command start."); ready=true;
                                    core.PostWebMessageAsJson(JsonConvert.SerializeObject(new PluginWorkerStart {Entry=command.Entry,Command=command.Id,ArgumentsJson=argumentsJson})); break;
                                case "rpc":
                                    var response=await Task.Run(()=>_service.DispatchAsync(operation,envelope.Request,token));
                                    if(!completion.Task.IsCompleted) core.PostWebMessageAsJson(JsonConvert.SerializeObject(response)); break;
                                case "progress": if(ready) progress?.Invoke((envelope.Message ?? "").Substring(0,Math.Min(2000,(envelope.Message ?? "").Length))); break;
                                case "complete": if(!ready) throw new PluginException("plugin_protocol_invalid","Command did not start."); completion.TrySetResult(envelope.Json); break;
                                case "failed": completion.TrySetException(new PluginException("plugin_command_failed",envelope.Message ?? "Command failed.")); break;
                                default: throw new PluginException("plugin_protocol_invalid","Unknown worker message.");
                            }
                        }
                        catch(Exception error) { completion.TrySetException(error); }
                    };
                    core.Settings.IsWebMessageEnabled=true; core.Settings.AreHostObjectsAllowed=false;
                    core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.AreDevToolsEnabled=false;
                    core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
                    core.Navigate(entry);
                    return await completion.Task;
                }
                finally
                {
                    // Closing the controller terminates Worker execution independently
                    // of JS cooperation; native file operations drain before completion.
                    if(transport!=null) {transport.Dispose();_transports.Remove(transport);}
                    controller?.Close();
                    if(transport!=null) await transport.Drained;
                }
            }
        }
        private Task<T> OnUi<T>(Func<Task<T>> action)
        {
            var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui.Post(async ignored=> {try{result.TrySetResult(await action());}catch(OperationCanceledException){result.TrySetCanceled();}catch(Exception e){result.TrySetException(e);}},null);
            return result.Task;
        }
        public void Dispose()
        {
            if(_closed)return;_closed=true;_service.Dispose();
            foreach(var transport in _transports.ToArray())transport.Dispose();
            Task[] runs;lock(_runs)runs=_runs.ToArray();
            Drained=Task.WhenAll(_transports.Select(value=>value.Drained).Concat(runs.Select(run=>run.ContinueWith(done=> { var observed=done.Exception; },TaskScheduler.Default))).Concat(new[]{_service.CloseAsync()}));
        }
        private static T Decode<T>(string json)
        {
            if(json==null || json.Length>1024*1024)throw new PluginException("plugin_message_limit","Plugin message exceeds its limit.");
            return JsonConvert.DeserializeObject<T>(json,new JsonSerializerSettings {MissingMemberHandling=MissingMemberHandling.Error,MaxDepth=32});
        }
        private sealed class Transport : IDisposable
        {
            private readonly PluginApplicationService _service;
            private readonly CoreWebView2 _core;
            private readonly string _assets;
            private readonly PluginInstance _operation;
            private readonly SynchronizationContext _ui = SynchronizationContext.Current;
            private readonly Dictionary<CoreWebView2Frame,PluginInstance> _frames=new Dictionary<CoreWebView2Frame,PluginInstance>();
            private readonly HashSet<string> _claimed=new HashSet<string>();
            private readonly HashSet<Stream> _bodies=new HashSet<Stream>();
            private readonly object _bodySync=new object();
            private long _bodyBytes;
            private int _pending;
            private bool _closed;
            private readonly TaskCompletionSource<bool> _drained=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Drained {get{return _drained.Task;}}
            public Transport(PluginApplicationService service,CoreWebView2 core,string assets,PluginInstance operation)
            {
                _service=service;_core=core;_assets=assets;_operation=operation;
                core.AddWebResourceRequestedFilter(operation==null ? "https://*.rnassistant-plugin.invalid/*" : "*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested+=OnResource;
                if(operation==null)core.FrameCreated+=OnFrame;
            }
            private void OnFrame(object sender,CoreWebView2FrameCreatedEventArgs args)
            {
                var frame=args.Frame;
                frame.NavigationStarting+=(s,e)=> {
                    if(_closed){e.Cancel=true;return;}
                    Uri uri;
                    var instance=Uri.TryCreate(e.Uri,UriKind.Absolute,out uri) ? _service.FindInstance(uri.GetLeftPart(UriPartial.Authority)) : null;
                    PluginInstance bound;
                    if(_frames.TryGetValue(frame,out bound)) { if(instance!=bound || e.Uri!=bound.EntryUrl){e.Cancel=true;_ = _service.CloseInstanceAsync(bound.Id);} return; }
                    if(instance==null)return; // existing HTML material sandbox has its own policy
                    if(instance.CommandOperation || e.Uri!=instance.EntryUrl || !_claimed.Add(instance.Id)){e.Cancel=true;return;}
                    _frames.Add(frame,instance);
                };
                frame.WebMessageReceived+=async (s,e)=> {
                    PluginInstance instance;
                    if(_closed || !_frames.TryGetValue(frame,out instance) || e.Source!=instance.EntryUrl || instance.Token.IsCancellationRequested)return;
                    if(_pending>=64)return;
                    _pending++;
                    try
                    {
                        var request=Decode<PluginFrameRequest>(e.TryGetWebMessageAsString());
                        var response=await Task.Run(()=>_service.DispatchAsync(instance,request,instance.Token, message=> _ui.Post(ignored=> {
                            try { if(!_closed && !instance.Token.IsCancellationRequested)
                                frame.PostWebMessageAsJson(JsonConvert.SerializeObject(new PluginFrameResponse {RequestId=request.RequestId,Progress=message})); }
                            catch (Exception error) { Diagnostics.RuntimeLog.Error("Plugin progress delivery failed.", error); }
                        },null)));
                        if(!_closed && !instance.Token.IsCancellationRequested)frame.PostWebMessageAsJson(JsonConvert.SerializeObject(response));
                    }
                    catch(Exception error) { if(!_closed) Diagnostics.RuntimeLog.Error("Plugin frame message failed.",error); }
                    finally{Finished();}
                };
                frame.Destroyed+=async (s,e)=> {
                    try { PluginInstance instance;
                        if(_frames.TryGetValue(frame,out instance)){_frames.Remove(frame);await _service.CloseInstanceAsync(instance.Id);} }
                    catch (Exception error) { Diagnostics.RuntimeLog.Error("Plugin frame cleanup failed.", error); }
                };
            }
            private async void OnResource(object sender,CoreWebView2WebResourceRequestedEventArgs e)
            {
                Uri uri;
                if(!Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out uri))return;
                if(_operation==null && !uri.Host.EndsWith(".rnassistant-plugin.invalid",StringComparison.Ordinal))return;
                var deferral=e.GetDeferral();_pending++;
                try
                {
                    if(_closed || _pending>64)throw new PluginException("plugin_busy","Plugin transport is unavailable.");
                    var instance=_operation ?? _service.FindInstance(uri.GetLeftPart(UriPartial.Authority));
                    if(instance==null || instance.Token.IsCancellationRequested || uri.GetLeftPart(UriPartial.Authority)!=instance.Origin)
                        throw new PluginException("plugin_origin_denied","Unbound plugin origin.");
                    var path=Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); byte[] bytes; string mime;
                    var query=Query(uri.Query);
                    if(path.StartsWith(".result/download/",StringComparison.Ordinal) && e.Request.Method=="GET" && uri.Query.Length==0)
                    {
                        var download=await Task.Run(()=>_service.DownloadResult(instance,path.Substring(17)));
                        e.Response=_core.Environment.CreateWebResourceResponse(download,200,"OK","Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=result.canonical.json\r\nCache-Control: no-store\r\n");return;
                    }
                    if(path.StartsWith(".result/read/",StringComparison.Ordinal) && e.Request.Method=="GET")
                    { bytes=await Task.Run(()=>_service.ReadResult(instance,path.Substring(13),Number(query,"offset"),checked((int)Number(query,"count"))));mime="application/octet-stream"; }
                    else if(path.StartsWith(".data/read/",StringComparison.Ordinal) && e.Request.Method=="GET")
                    { bytes=await instance.ReadRangeAsync(path.Substring(11),Number(query,"offset"),checked((int)Number(query,"count")),instance.Token);mime="application/octet-stream"; }
                    else if((path.StartsWith(".data/write/",StringComparison.Ordinal) || path.StartsWith(".result/write/",StringComparison.Ordinal)) && e.Request.Method=="POST")
                    {
                        var origin=e.Request.Headers.Contains("Origin") ? e.Request.Headers.GetHeader("Origin") : null;
                        if(origin!=instance.Origin)throw new PluginException("plugin_origin_denied","Foreign write origin.");
                        using(var buffer=new MemoryStream()) {
                            var chunk=new byte[16384];int read;var content=e.Request.Content;
                            if(content==null)throw new PluginException("plugin_payload_invalid","Write body required.");
                            while((read=content.Read(chunk,0,chunk.Length))>0){if(buffer.Length+read>PluginInstance.MaximumChunkBytes)throw new PluginException("plugin_payload_limit","Chunk too large.");buffer.Write(chunk,0,read);}
                            await instance.WriteChunkAsync(path.Substring(path.StartsWith(".result/",StringComparison.Ordinal)?14:12),Number(query,"offset"),buffer.ToArray(),instance.Token);
                        }
                        bytes=new byte[0];mime="application/octet-stream";
                    }
                    else
                    {
                        if(e.Request.Method!="GET" || uri.Query.Length!=0)throw new PluginException("plugin_request_denied","Only exact package GETs are allowed.");
                        if(path.StartsWith(".host/",StringComparison.Ordinal))
                        {
                            var name=path.Substring(6);
                            if(!new[]{"sdk.js","worker.js","runner.js","runner.html"}.Contains(name) || !instance.CommandOperation && name!="sdk.js")
                                throw new PluginException("plugin_request_denied","Host asset denied.");
                            bytes=PluginPackageFiles.Read(_assets,name);
                        }
                        else bytes=await Task.Run(()=>_service.ReadPackageFile(instance,path));
                        mime=PluginStore.Mime(path);
                    }
                    var headers="Content-Type: "+mime+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\r\n";
                    e.Response=_core.Environment.CreateWebResourceResponse(Body(bytes),200,"OK",headers);
                }
                catch(Exception error)
                {
                    if(!_closed)e.Response=_core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Plugin request refused","Cache-Control: no-store\r\n");
                    Diagnostics.RuntimeLog.Error("Plugin resource refused.",error);
                }
                finally{deferral.Complete();Finished();}
            }
            private Stream Body(byte[] bytes)
            {
                lock(_bodySync)
                {
                    if(bytes.Length==0)return new MemoryStream();
                    if(_bodyBytes+bytes.Length>64L*1024*1024)throw new PluginException("plugin_response_limit","Unconsumed plugin responses exceed their bound.");
                    Stream body=null;body=new ResponseBody(bytes,()=>{lock(_bodySync){if(_bodies.Remove(body))_bodyBytes-=bytes.Length;}});
                    _bodies.Add(body);_bodyBytes+=bytes.Length;return body;
                }
            }
            private static Dictionary<string,string> Query(string query)
            {
                var result=new Dictionary<string,string>();
                foreach(var item in query.TrimStart('?').Split(new[]{'&'},StringSplitOptions.RemoveEmptyEntries)){var parts=item.Split('=');if(parts.Length!=2 || result.ContainsKey(parts[0]))throw new PluginException("plugin_query_invalid","Invalid range query.");result.Add(parts[0],parts[1]);}return result;
            }
            private static long Number(Dictionary<string,string> values,string key)
            {string text;long number;if(!values.TryGetValue(key,out text)||!long.TryParse(text,out number)||number<0)throw new PluginException("plugin_range_invalid","Invalid range.");return number;}
            private void Finished(){_pending--;if(_closed && _pending==0)_drained.TrySetResult(true);}
            public void Dispose()
            {
                if(_closed)return;_closed=true;_core.WebResourceRequested-=OnResource;_core.FrameCreated-=OnFrame;
                foreach(var instance in _frames.Values)instance.Dispose();
                Stream[] bodies;lock(_bodySync)bodies=_bodies.ToArray();foreach(var body in bodies)body.Dispose();
                if(_pending==0)_drained.TrySetResult(true);
            }
        }
        private sealed class ResponseBody : MemoryStream
        {
            private Action _release;
            public ResponseBody(byte[] bytes,Action release):base(bytes,false){_release=release;}
            public override int Read(byte[] buffer,int offset,int count){var read=base.Read(buffer,offset,count);if(Position==Length)Release();return read;}
            private void Release(){Interlocked.Exchange(ref _release,null)?.Invoke();}
            protected override void Dispose(bool disposing){Release();base.Dispose(disposing);}
        }
    }
}
